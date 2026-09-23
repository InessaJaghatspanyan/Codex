"""Claude calls: query rewriting and grounded, cited answers."""

from __future__ import annotations

import os
from dataclasses import dataclass, field

import anthropic
from pydantic import BaseModel, Field

from .index import Hit

MODEL = os.environ.get("LABOR_RAG_MODEL", "claude-opus-5")
REWRITE_MODEL = os.environ.get("LABOR_RAG_REWRITE_MODEL", MODEL)
EFFORT = os.environ.get("LABOR_RAG_EFFORT", "high")
FALLBACK_BETA = "server-side-fallback-2026-07-01"

ANSWER_SYSTEM = """\
You are a legal research assistant for the Labor Code of the Republic of Armenia \
(ՀՀ աշխատանքային օրենսգիրք).

You are given excerpts from the Code as documents. Each document is one article \
(or part of one); its title names the article.

Rules:
- Answer only from the provided documents. If they do not contain the answer, say so \
plainly and suggest which topic or article the user could look up; do not fill gaps \
from memory of the law, other laws, or other countries' law.
- Cite the specific articles (and parts, where visible) that support each statement.
- Reply in the language the user wrote in. Quote Armenian legal terms in the original \
where the translation could be ambiguous.
- If the excerpts may be outdated or amended, or the question depends on facts you \
don't have (contract terms, collective agreements, employer type), say what \
would change the answer.
- This is legal information, not legal advice; say so briefly when the user asks \
what they personally should do."""

REWRITE_SYSTEM = """\
You turn user questions about the Labor Code of the Republic of Armenia into search \
queries for an Armenian-language index of the Code. Produce Armenian search queries \
using the terminology of the Code (e.g. աշխատանքային պայմանագիր, արձակուրդ, \
աշխատանքային ժամանակ, աշխատավարձ, աշխատանքից ազատում, գործատու, աշխատող). \
Put any article numbers the user mentions into article_refs."""


class SearchPlan(BaseModel):
    queries: list[str] = Field(
        description="1-3 short Armenian search queries covering the question's legal concepts"
    )
    article_refs: list[str] = Field(
        default_factory=list, description="Article numbers explicitly mentioned, e.g. '139'"
    )


@dataclass
class Citation:
    article_heading: str
    cited_text: str
    document_index: int = -1


@dataclass
class Answer:
    text: str
    citations: list[Citation] = field(default_factory=list)
    refused: bool = False


_client: anthropic.Anthropic | None = None


def client() -> anthropic.Anthropic:
    global _client
    if _client is None:
        _client = anthropic.Anthropic()
    return _client


def rewrite_query(question: str) -> SearchPlan:
    """Translate/expand the question into Armenian search queries.

    Falls back to the raw question if the call fails, so retrieval still works.
    """
    try:
        response = client().messages.parse(
            model=REWRITE_MODEL,
            max_tokens=2000,
            system=REWRITE_SYSTEM,
            output_config={"effort": "low"},
            messages=[{"role": "user", "content": question}],
            output_format=SearchPlan,
        )
    except anthropic.APIError:
        return SearchPlan(queries=[])
    if response.stop_reason == "refusal" or response.parsed_output is None:
        return SearchPlan(queries=[])
    return response.parsed_output


def _documents(hits: list[Hit]) -> list[dict]:
    docs = []
    for h in hits:
        c = h.chunk
        context = " / ".join(x for x in (c.section, c.chapter) if x)
        doc = {
            "type": "document",
            "source": {"type": "text", "media_type": "text/plain", "data": c.text},
            "title": c.heading,
            "citations": {"enabled": True},
        }
        if context:
            doc["context"] = context
        docs.append(doc)
    return docs


def answer(question: str, hits: list[Hit], history: list[dict] | None = None) -> Answer:
    """Ask Claude to answer using only the retrieved articles, with citations.

    `history` holds earlier turns as plain {"role", "content": str} messages; the
    retrieved documents are attached only to the current question.
    """
    if not hits:
        return Answer("No relevant articles were found in the index.")

    # Older turns stay text-only; only the current question carries the documents.
    messages = list(history or [])
    messages.append(
        {"role": "user", "content": [*_documents(hits), {"type": "text", "text": question}]}
    )

    response = client().beta.messages.create(
        model=MODEL,
        max_tokens=16000,
        system=ANSWER_SYSTEM,
        thinking={"type": "adaptive"},
        output_config={"effort": EFFORT},
        messages=messages,
        betas=[FALLBACK_BETA],
        fallbacks="default",
    )

    if response.stop_reason == "refusal":
        return Answer("The model declined to answer this request.", refused=True)

    titles = [h.chunk.heading for h in hits]
    parts: list[str] = []
    citations: list[Citation] = []
    for block in response.content:
        if block.type != "text":
            continue
        parts.append(block.text)
        marks = []
        for cit in getattr(block, "citations", None) or []:
            heading = getattr(cit, "document_title", None) or titles[cit.document_index]
            citations.append(Citation(heading, cit.cited_text, cit.document_index))
            marks.append(len(citations))
        if marks:
            parts.append("".join(f"[{n}]" for n in marks))
    return Answer("".join(parts).strip(), citations)
