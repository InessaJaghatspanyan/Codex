"""Hybrid retriever: BM25 over words + TF-IDF over character n-grams (+ optional dense).

Armenian is highly inflected (աշխատող, աշխատողի, աշխատողների, ...), so exact word
matching alone misses a lot. Character n-grams inside word boundaries absorb
most suffix variation without an Armenian stemmer. Rankings are merged with
reciprocal rank fusion, and an explicit "Հոդված 139" / "article 139" in the
query pins that article to the top.
"""

from __future__ import annotations

import json
import math
import os
import re
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from sklearn.feature_extraction.text import TfidfVectorizer

from .parser import Chunk

WORD_RE = re.compile(r"\w+", re.UNICODE)
ARTICLE_REF_RE = re.compile(
    r"(?:հոդված|հոդվածի|հոդվածը|հոդվածում|հոդ\.|article|art\.|статья|статьи|статье|ст\.)\s*(\d+(?:\.\d+)*)",
    re.IGNORECASE,
)
RRF_K = 60


def tokenize(text: str) -> list[str]:
    return [t for t in WORD_RE.findall(text.lower()) if len(t) > 1 or t.isdigit()]


def find_article_refs(text: str) -> list[str]:
    return ARTICLE_REF_RE.findall(text)


class BM25:
    def __init__(self, docs: list[list[str]], k1: float = 1.5, b: float = 0.75):
        self.k1, self.b = k1, b
        self.tfs = [Counter(d) for d in docs]
        self.lens = np.array([len(d) for d in docs], dtype=float)
        self.avgdl = float(self.lens.mean()) if len(docs) else 0.0
        df = Counter(t for d in docs for t in set(d))
        n = len(docs)
        self.idf = {t: math.log(1 + (n - f + 0.5) / (f + 0.5)) for t, f in df.items()}

    def scores(self, query: list[str]) -> np.ndarray:
        out = np.zeros(len(self.tfs))
        for i, tf in enumerate(self.tfs):
            norm = self.k1 * (1 - self.b + self.b * self.lens[i] / (self.avgdl or 1))
            for t in query:
                if t in tf:
                    f = tf[t]
                    out[i] += self.idf[t] * f * (self.k1 + 1) / (f + norm)
        return out


@dataclass
class Hit:
    chunk: Chunk
    score: float


def _doc_text(c: Chunk) -> str:
    # Titles and chapter names carry a lot of signal; repeat the title to weight it.
    return "\n".join([c.heading, c.title, c.chapter, c.text])


class Index:
    def __init__(self, chunks: list[Chunk], embeddings: np.ndarray | None = None):
        self.chunks = chunks
        texts = [_doc_text(c) for c in chunks]
        self.bm25 = BM25([tokenize(t) for t in texts])
        self.char_vec = TfidfVectorizer(
            analyzer="char_wb", ngram_range=(3, 5), lowercase=True, sublinear_tf=True
        )
        self.char_mat = self.char_vec.fit_transform(texts)
        self.embeddings = embeddings
        self.by_article: dict[str, list[int]] = {}
        for i, c in enumerate(chunks):
            if c.article:
                self.by_article.setdefault(c.article, []).append(i)

    # ---- persistence -------------------------------------------------------

    def save(self, directory: Path) -> None:
        directory.mkdir(parents=True, exist_ok=True)
        with open(directory / "chunks.jsonl", "w", encoding="utf-8") as f:
            for c in self.chunks:
                f.write(json.dumps(c.to_dict(), ensure_ascii=False) + "\n")
        if self.embeddings is not None:
            np.save(directory / "embeddings.npy", self.embeddings)
        elif (directory / "embeddings.npy").exists():
            (directory / "embeddings.npy").unlink()

    @classmethod
    def load(cls, directory: Path) -> "Index":
        path = directory / "chunks.jsonl"
        if not path.exists():
            raise FileNotFoundError(
                f"No index at {directory}. Run `labor-rag ingest <files>` first."
            )
        with open(path, encoding="utf-8") as f:
            chunks = [Chunk.from_dict(json.loads(line)) for line in f if line.strip()]
        emb_path = directory / "embeddings.npy"
        emb = np.load(emb_path) if emb_path.exists() else None
        return cls(chunks, emb)

    # ---- search ------------------------------------------------------------

    def search(self, query: str, k: int = 8, extra_queries: list[str] | None = None) -> list[Hit]:
        queries = [query] + [q for q in (extra_queries or []) if q.strip()]
        rankings: list[np.ndarray] = []

        def add(scores: np.ndarray) -> None:
            # Only chunks that actually match get rank credit; otherwise a query with
            # no overlap (e.g. English against Armenian text) would promote noise.
            order = np.argsort(-scores)
            rankings.append(order[scores[order] > 0])

        for q in queries:
            add(self.bm25.scores(tokenize(q)))
            add((self.char_mat @ self.char_vec.transform([q]).T).toarray().ravel())
        if self.embeddings is not None:
            qv = embed_queries(queries)
            if qv is not None:
                for v in qv:
                    add(self.embeddings @ v)

        fused = np.zeros(len(self.chunks))
        for ranking in rankings:
            for rank, idx in enumerate(ranking[:100]):
                fused[idx] += 1.0 / (RRF_K + rank + 1)

        # Explicitly referenced articles always make the cut.
        pinned: list[int] = []
        for q in queries:
            for ref in find_article_refs(q):
                for idx in self.by_article.get(ref, []):
                    if idx not in pinned:
                        pinned.append(idx)
        order = pinned + [
            int(i) for i in np.argsort(-fused) if fused[i] > 0 and int(i) not in pinned
        ]
        top = max(fused.max(), 1e-9) if len(fused) else 1.0
        return [
            Hit(self.chunks[i], 1.0 if i in pinned else float(fused[i] / top))
            for i in order[: max(k, len(pinned))]
        ]

    def get_article(self, number: str) -> list[Chunk]:
        return [self.chunks[i] for i in self.by_article.get(number, [])]


# ---- optional dense embeddings (Voyage) ------------------------------------

VOYAGE_MODEL = os.environ.get("LABOR_RAG_VOYAGE_MODEL", "voyage-multilingual-2")


def _voyage_client():
    if not os.environ.get("VOYAGE_API_KEY"):
        return None
    try:
        import voyageai
    except ImportError:
        return None
    return voyageai.Client()


def _normalize(m: np.ndarray) -> np.ndarray:
    return m / np.clip(np.linalg.norm(m, axis=1, keepdims=True), 1e-12, None)


def embed_documents(chunks: list[Chunk]) -> np.ndarray | None:
    client = _voyage_client()
    if client is None:
        return None
    texts = [_doc_text(c)[:16000] for c in chunks]
    vectors: list[list[float]] = []
    for start in range(0, len(texts), 64):
        res = client.embed(texts[start : start + 64], model=VOYAGE_MODEL, input_type="document")
        vectors.extend(res.embeddings)
    return _normalize(np.array(vectors, dtype=np.float32))


def embed_queries(queries: list[str]) -> np.ndarray | None:
    client = _voyage_client()
    if client is None:
        return None
    res = client.embed(queries, model=VOYAGE_MODEL, input_type="query")
    return _normalize(np.array(res.embeddings, dtype=np.float32))
