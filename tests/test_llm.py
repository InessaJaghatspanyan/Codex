"""Exercise the answer/rewrite plumbing against a fake client (no network)."""

from pathlib import Path
from types import SimpleNamespace as NS

from labor_rag import llm
from labor_rag.index import Index
from labor_rag.parser import load_corpus

FIXTURE = Path(__file__).parent / "fixtures" / "sample_code.txt"


class FakeMessages:
    def __init__(self, response):
        self.response = response
        self.calls = []

    def create(self, **kwargs):
        self.calls.append(kwargs)
        return self.response

    parse = create


def _client(response):
    msgs = FakeMessages(response)
    return NS(messages=msgs, beta=NS(messages=msgs)), msgs


def test_answer_builds_cited_documents(monkeypatch):
    hits = Index(load_corpus([FIXTURE])).search("ամենամյա արձակուրդ", k=2)
    cit = NS(document_index=0, document_title=hits[0].chunk.heading, cited_text="վճարովի արձակուրդ")
    response = NS(
        stop_reason="end_turn",
        content=[
            NS(type="thinking", thinking=""),
            NS(type="text", text="Yes, annual paid leave is granted.", citations=[cit]),
        ],
    )
    fake, msgs = _client(response)
    monkeypatch.setattr(llm, "_client", fake)

    ans = llm.answer("Is there paid vacation?", hits)

    assert ans.text == "Yes, annual paid leave is granted.[1]"
    assert ans.citations[0].article_heading == "Հոդված 4. Ամենամյա արձակուրդ"
    call = msgs.calls[0]
    assert call["fallbacks"] == "default"
    content = call["messages"][-1]["content"]
    assert [b["type"] for b in content] == ["document", "document", "text"]
    assert content[0]["citations"] == {"enabled": True}
    assert content[0]["title"] == hits[0].chunk.heading


def test_answer_handles_refusal(monkeypatch):
    fake, _ = _client(NS(stop_reason="refusal", content=[]))
    monkeypatch.setattr(llm, "_client", fake)
    hits = Index(load_corpus([FIXTURE])).search("աշխատավարձ", k=1)
    assert llm.answer("q", hits).refused


def test_rewrite_returns_plan(monkeypatch):
    plan = llm.SearchPlan(queries=["փորձաշրջան"], article_refs=["3.1"])
    fake, _ = _client(NS(stop_reason="end_turn", parsed_output=plan))
    monkeypatch.setattr(llm, "_client", fake)
    assert llm.rewrite_query("How long is probation?") == plan
