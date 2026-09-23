"""Minimal web interface: a single HTML page plus a small JSON API."""

from __future__ import annotations

import os
from functools import lru_cache
from importlib import resources
from pathlib import Path

import anthropic
from fastapi import FastAPI, HTTPException
from fastapi.responses import HTMLResponse
from pydantic import BaseModel, Field

from .index import Hit, Index

INDEX_DIR = Path(os.environ.get("LABOR_RAG_INDEX", "data/index"))
MAX_HISTORY = 12

app = FastAPI(title="Labor Code of RA — RAG")


@lru_cache(maxsize=1)
def get_index() -> Index:
    try:
        return Index.load(INDEX_DIR)
    except FileNotFoundError as e:
        raise HTTPException(503, str(e)) from e


def _chunk_json(h: Hit) -> dict:
    c = h.chunk
    return {
        "id": c.id,
        "article": c.article,
        "heading": c.heading,
        "chapter": c.chapter,
        "section": c.section,
        "text": c.text,
        "score": round(h.score, 3),
    }


class Turn(BaseModel):
    role: str = Field(pattern="^(user|assistant)$")
    content: str


class AskRequest(BaseModel):
    question: str = Field(min_length=1, max_length=4000)
    history: list[Turn] = []
    k: int = Field(8, ge=1, le=20)
    rewrite: bool = True


class SearchRequest(BaseModel):
    query: str = Field(min_length=1, max_length=4000)
    k: int = Field(8, ge=1, le=20)


def _has_credentials() -> bool:
    """Best-effort check so the page can default to search when answers can't work."""
    if os.environ.get("ANTHROPIC_API_KEY") or os.environ.get("ANTHROPIC_AUTH_TOKEN"):
        return True
    if os.environ.get("ANTHROPIC_PROFILE") or os.environ.get("ANTHROPIC_FEDERATION_RULE_ID"):
        return True
    config = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "anthropic"
    return config.is_dir() and any(config.iterdir())


@app.get("/", response_class=HTMLResponse)
def page() -> str:
    return resources.files("labor_rag").joinpath("static/index.html").read_text("utf-8")


@app.get("/api/info")
def info() -> dict:
    index = get_index()
    preamble = next((c.text for c in index.chunks if c.article is None), "")
    return {
        "articles": len(index.by_article),
        "chunks": len(index.chunks),
        "version": preamble.split("\n", 1)[0] if preamble else "",
        "llm": _has_credentials(),
    }


@app.post("/api/search")
def search(req: SearchRequest) -> dict:
    hits = get_index().search(req.query, k=req.k)
    return {"hits": [_chunk_json(h) for h in hits]}


@app.get("/api/article/{number}")
def article(number: str) -> dict:
    chunks = get_index().get_article(number)
    if not chunks:
        raise HTTPException(404, f"Article {number} is not in the index.")
    return {"chunks": [_chunk_json(Hit(c, 1.0)) for c in chunks]}


@app.post("/api/ask")
def ask(req: AskRequest) -> dict:
    from . import llm

    index = get_index()
    try:
        extra: list[str] = []
        if req.rewrite:
            plan = llm.rewrite_query(req.question)
            extra = plan.queries + [f"Հոդված {n}" for n in plan.article_refs]
        hits = index.search(req.question, k=req.k, extra_queries=extra)
        history = [t.model_dump() for t in req.history][-MAX_HISTORY:]
        ans = llm.answer(req.question, hits, history)
    except anthropic.AuthenticationError as e:
        raise HTTPException(503, "Anthropic API key is missing or invalid.") from e
    except anthropic.APIConnectionError as e:
        raise HTTPException(502, "Could not reach the Anthropic API.") from e
    except anthropic.RateLimitError as e:
        raise HTTPException(429, "Rate limited by the Anthropic API; try again shortly.") from e
    except anthropic.APIStatusError as e:
        raise HTTPException(502, f"Anthropic API error: {e.message}") from e
    except TypeError as e:
        # The SDK raises TypeError when no credentials can be resolved at all.
        if "api_key" in str(e) or "auth" in str(e).lower():
            raise HTTPException(503, "Anthropic API key is not configured.") from e
        raise

    return {
        "answer": ans.text,
        "refused": ans.refused,
        "citations": [
            {
                "n": n,
                "heading": c.article_heading,
                "article": hits[c.document_index].chunk.article
                if 0 <= c.document_index < len(hits)
                else None,
                "text": c.cited_text,
            }
            for n, c in enumerate(ans.citations, start=1)
        ],
        "hits": [_chunk_json(h) for h in hits],
    }


def serve(index_dir: Path, host: str = "127.0.0.1", port: int = 8000) -> None:
    import uvicorn

    global INDEX_DIR
    INDEX_DIR = index_dir
    get_index.cache_clear()
    Index.load(index_dir)  # fail fast (FileNotFoundError) if the index is missing
    print(f"Open http://{host}:{port}/")
    uvicorn.run(app, host=host, port=port)
