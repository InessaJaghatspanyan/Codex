from pathlib import Path

from labor_rag.index import Index, find_article_refs
from labor_rag.parser import load_corpus

FIXTURE = Path(__file__).parent / "fixtures" / "sample_code.txt"


def _index() -> Index:
    return Index(load_corpus([FIXTURE]))


def test_inflected_query_finds_article():
    # "արձակուրդի" (genitive) should still match the vacation article.
    hits = _index().search("ամենամյա արձակուրդի տևողությունը", k=3)
    assert hits[0].chunk.article == "4"


def test_article_reference_is_pinned():
    hits = _index().search("ինչ է ասում հոդված 5-ը", k=3)
    assert hits[0].chunk.article == "5"
    assert find_article_refs("see Article 3.1 and ст. 2") == ["3.1", "2"]


def test_extra_queries_help():
    hits = _index().search("probation period", k=2, extra_queries=["փորձաշրջան"])
    assert hits[0].chunk.article == "3.1"


def test_save_and_load_roundtrip(tmp_path):
    idx = _index()
    idx.save(tmp_path)
    loaded = Index.load(tmp_path)
    assert [c.id for c in loaded.chunks] == [c.id for c in idx.chunks]
    assert loaded.get_article("3")[0].title == idx.get_article("3")[0].title


def test_no_match_returns_nothing():
    assert _index().search("qwerty zzz", k=5) == []
