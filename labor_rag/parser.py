"""Load the Labor Code from disk and split it into article-level chunks.

The Code is structured as Sections (ԲԱԺԻՆ) > Chapters (ԳԼՈՒԽ) > Articles (Հոդված).
Articles are the natural retrieval unit: they are what lawyers cite and what
answers should point back to. English ("Article", "Chapter", "Section") and
Russian ("Статья", "Глава", "Раздел") translations are recognised too.
"""

from __future__ import annotations

import re
from dataclasses import asdict, dataclass, field
from pathlib import Path

ARTICLE_RE = re.compile(
    r"^\s*(?:Հոդված|ՀՈԴՎԱԾ|Article|ARTICLE|Статья|СТАТЬЯ)\s+"
    r"(\d+(?:\.\d+)*)\s*[.։:]?\s*(.*)$"
)
CHAPTER_RE = re.compile(
    r"^\s*(?:ԳԼՈՒԽ|Գլուխ|CHAPTER|Chapter|ГЛАВА|Глава)\s+(\d+(?:\.\d+)*)\s*[.։:]?\s*(.*)$"
)
SECTION_RE = re.compile(
    r"^\s*(?:ԲԱԺԻՆ|Բաժին|SECTION|Section|PART|Part|РАЗДЕЛ|Раздел)\s+(\d+(?:\.\d+)*)\s*[.։:]?\s*(.*)$"
)
# A numbered paragraph ("1.", "2)") inside an article — used to split long articles.
PART_RE = re.compile(r"^\s*\d+[.)]\s+")

MAX_CHUNK_CHARS = 4000


@dataclass
class Chunk:
    id: str
    article: str | None
    title: str
    chapter: str
    section: str
    text: str
    source: str
    part: int = 0
    extra: dict = field(default_factory=dict)

    @property
    def heading(self) -> str:
        if self.article is None:
            return self.title or "Preamble"
        label = f"Հոդված {self.article}. {self.title}".strip()
        return f"{label} (մաս {self.part})" if self.part else label

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: dict) -> "Chunk":
        return cls(**d)


def load_text(path: Path) -> str:
    """Extract plain text from .txt/.md, .html/.htm, .pdf or .docx."""
    suffix = path.suffix.lower()
    if suffix in {".txt", ".md"}:
        return path.read_text(encoding="utf-8")
    if suffix in {".html", ".htm"}:
        from bs4 import BeautifulSoup

        soup = BeautifulSoup(path.read_bytes(), "html.parser")
        for tag in soup(["script", "style"]):
            tag.decompose()
        return soup.get_text("\n")
    if suffix == ".pdf":
        from pypdf import PdfReader

        return "\n".join(page.extract_text() or "" for page in PdfReader(path).pages)
    if suffix == ".docx":
        import docx

        return "\n".join(p.text for p in docx.Document(path).paragraphs)
    raise ValueError(f"Unsupported file type: {path}")


def _clean_lines(text: str) -> list[str]:
    text = text.replace(" ", " ").replace("\r", "")
    lines = [re.sub(r"[ \t]+", " ", ln).strip() for ln in text.split("\n")]
    return [ln for ln in lines if ln]


def _next_title(lines: list[str], i: int, inline: str) -> tuple[str, int]:
    """Headings sometimes put the title on the following line."""
    if inline:
        return inline, i
    if i + 1 < len(lines) and not any(
        r.match(lines[i + 1]) for r in (ARTICLE_RE, CHAPTER_RE, SECTION_RE)
    ):
        return lines[i + 1], i + 1
    return "", i


def split_articles(text: str, source: str = "") -> list[Chunk]:
    lines = _clean_lines(text)
    chunks: list[Chunk] = []
    section = chapter = ""
    current: dict | None = None
    preamble: list[str] = []

    def flush() -> None:
        if current and current["body"]:
            chunks.extend(_make_chunks(current, source))

    i = 0
    while i < len(lines):
        line = lines[i]
        if m := SECTION_RE.match(line):
            title, i = _next_title(lines, i, m.group(2))
            section = f"Բաժին {m.group(1)}. {title}".strip()
        elif m := CHAPTER_RE.match(line):
            title, i = _next_title(lines, i, m.group(2))
            chapter = f"Գլուխ {m.group(1)}. {title}".strip()
        elif m := ARTICLE_RE.match(line):
            flush()
            title, i = _next_title(lines, i, m.group(2))
            current = {
                "article": m.group(1),
                "title": title,
                "chapter": chapter,
                "section": section,
                "body": [],
            }
        elif current is None:
            preamble.append(line)
        else:
            current["body"].append(line)
        i += 1
    flush()

    if preamble:
        chunks.insert(
            0,
            Chunk(
                id=f"{Path(source).stem or 'doc'}:preamble",
                article=None,
                title="Preamble",
                chapter="",
                section="",
                text="\n".join(preamble),
                source=source,
            ),
        )
    return _dedupe_ids(chunks)


def _make_chunks(art: dict, source: str) -> list[Chunk]:
    body: list[str] = art["body"]
    groups: list[list[str]] = [[]]
    size = 0
    for line in body:
        # Start a new group at a numbered paragraph once the current one is big.
        if size > MAX_CHUNK_CHARS and PART_RE.match(line):
            groups.append([])
            size = 0
        groups[-1].append(line)
        size += len(line)
    multi = len(groups) > 1
    return [
        Chunk(
            id=f"art-{art['article']}" + (f"-p{n}" if multi else ""),
            article=art["article"],
            title=art["title"],
            chapter=art["chapter"],
            section=art["section"],
            text="\n".join(g),
            source=source,
            part=n if multi else 0,
        )
        for n, g in enumerate(groups, start=1)
    ]


def _dedupe_ids(chunks: list[Chunk]) -> list[Chunk]:
    seen: dict[str, int] = {}
    for c in chunks:
        if c.id in seen:
            seen[c.id] += 1
            c.id = f"{c.id}~{seen[c.id]}"
        else:
            seen[c.id] = 0
    return chunks


def load_corpus(paths: list[Path]) -> list[Chunk]:
    files: list[Path] = []
    for p in paths:
        if p.is_dir():
            files.extend(
                sorted(f for f in p.rglob("*") if f.is_file() and not f.name.startswith("."))
            )
        else:
            files.append(p)
    chunks: list[Chunk] = []
    for f in files:
        chunks.extend(split_articles(load_text(f), source=f.name))
    return _dedupe_ids(chunks)
