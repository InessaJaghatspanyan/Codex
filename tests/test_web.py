from pathlib import Path
from types import SimpleNamespace as NS

import pytest

pytest.importorskip("fastapi")
from fastapi.testclient import TestClient

from labor_rag import llm, web
from labor_rag.index import Index
from labor_rag.parser import load_corpus

FIXTURE = Path(__file__).parent / "fixtures" / "sample_code.txt"


@pytest.fixture
def client(tmp_path, monkeypatch):
    Index(load_corpus([FIXTURE])).save(tmp_path)
    monkeypatch.setattr(web, "INDEX_DIR", tmp_path)
    web.get_index.cache_clear()
    yield TestClient(web.app)
    web.get_index.cache_clear()


def test_page_and_info(client):
    assert "ՀՀ աշխատանքային օրենսգիրք" in client.get("/").text
    info = client.get("/api/info").json()
    assert info["articles"] == 6


def test_search_and_article(client):
    hits = client.post("/api/search", json={"query": "ամենամյա արձակուրդ"}).json()["hits"]
    assert hits[0]["article"] == "4"
    assert client.get("/api/article/3.1").json()["chunks"][0]["heading"].startswith("Հոդված 3.1")
    assert client.get("/api/article/999").status_code == 404


def test_ask_maps_citations_to_articles(client, monkeypatch):
    monkeypatch.setattr(llm, "rewrite_query", lambda q: llm.SearchPlan(queries=["փորձաշրջան"]))

    def fake_answer(question, hits, history):
        assert history == [{"role": "user", "content": "hi"}, {"role": "assistant", "content": "hello"}]
        idx = next(i for i, h in enumerate(hits) if h.chunk.article == "3.1")
        return llm.Answer("Պայմանագրով։[1]", [llm.Citation(hits[idx].chunk.heading, "Փորձաշրջանի", idx)])

    monkeypatch.setattr(llm, "answer", fake_answer)
    data = client.post(
        "/api/ask",
        json={
            "question": "How long is probation?",
            "history": [{"role": "user", "content": "hi"}, {"role": "assistant", "content": "hello"}],
        },
    ).json()
    assert data["answer"] == "Պայմանագրով։[1]"
    assert data["citations"][0]["article"] == "3.1"
    assert data["hits"]


def test_ask_rejects_bad_history_role(client):
    r = client.post("/api/ask", json={"question": "x", "history": [{"role": "system", "content": "x"}]})
    assert r.status_code == 422
