"""Command-line interface: ingest, search, ask, chat, article."""

from __future__ import annotations

import argparse
import os
import sys
import textwrap
from pathlib import Path

from .index import Hit, Index, embed_documents
from .parser import load_corpus

DEFAULT_INDEX = Path(os.environ.get("LABOR_RAG_INDEX", "data/index"))


def cmd_ingest(args: argparse.Namespace) -> None:
    chunks = load_corpus([Path(p) for p in args.paths])
    if not chunks:
        sys.exit("No text found in the given files.")
    articles = {c.article for c in chunks if c.article}
    print(f"Parsed {len(chunks)} chunks covering {len(articles)} articles.")
    if not articles:
        print(
            "Warning: no 'Հոդված N.' headings were detected; the text is indexed "
            "as a single preamble chunk. Check the source file's formatting.",
            file=sys.stderr,
        )
    embeddings = None if args.no_embeddings else embed_documents(chunks)
    if embeddings is not None:
        print(f"Computed dense embeddings ({embeddings.shape[1]} dims).")
    Index(chunks, embeddings).save(args.index)
    print(f"Index written to {args.index}/")


def _retrieve(index: Index, question: str, k: int, rewrite: bool) -> list[Hit]:
    extra: list[str] = []
    if rewrite:
        from .llm import rewrite_query

        plan = rewrite_query(question)
        extra = plan.queries + [f"Հոդված {n}" for n in plan.article_refs]
    return index.search(question, k=k, extra_queries=extra)


def _print_hits(hits: list[Hit], full: bool = False) -> None:
    for h in hits:
        c = h.chunk
        print(f"\n[{h.score:.2f}] {c.heading}")
        if c.chapter:
            print(f"       {c.chapter}")
        body = c.text if full else textwrap.shorten(c.text.replace("\n", " "), 300)
        print(textwrap.indent(body, "       "))


def _print_answer(ans) -> None:
    print()
    print(ans.text)
    if ans.citations:
        print("\nSources:")
        for n, cit in enumerate(ans.citations, start=1):
            quote = textwrap.shorten(cit.cited_text.replace("\n", " "), 200)
            print(f"  [{n}] {cit.article_heading}: «{quote}»")


def cmd_search(args: argparse.Namespace) -> None:
    index = Index.load(args.index)
    _print_hits(_retrieve(index, args.query, args.k, args.rewrite), full=args.full)


def cmd_article(args: argparse.Namespace) -> None:
    chunks = Index.load(args.index).get_article(args.number)
    if not chunks:
        sys.exit(f"Article {args.number} is not in the index.")
    for c in chunks:
        print(c.heading)
        if c.chapter:
            print(c.chapter)
        print()
        print(c.text)
        print()


def cmd_ask(args: argparse.Namespace) -> None:
    from .llm import answer

    index = Index.load(args.index)
    hits = _retrieve(index, args.question, args.k, not args.no_rewrite)
    if args.show_context:
        _print_hits(hits)
    _print_answer(answer(args.question, hits))


def cmd_chat(args: argparse.Namespace) -> None:
    from .llm import answer

    index = Index.load(args.index)
    history: list[dict] = []
    print("Labor Code of RA — ask a question (empty line or Ctrl-D to exit).")
    while True:
        try:
            q = input("\n> ").strip()
        except EOFError:
            break
        if not q:
            break
        hits = _retrieve(index, q, args.k, not args.no_rewrite)
        ans = answer(q, hits, history)
        _print_answer(ans)
        history += [{"role": "user", "content": q}, {"role": "assistant", "content": ans.text}]
        history = history[-12:]


def cmd_serve(args: argparse.Namespace) -> None:
    try:
        from .web import serve
    except ImportError:
        sys.exit('The web interface needs extra packages: pip install -e ".[web]"')
    serve(args.index, host=args.host, port=args.port)


def main(argv: list[str] | None = None) -> None:
    p = argparse.ArgumentParser(
        prog="labor-rag", description="RAG over the Labor Code of the Republic of Armenia"
    )
    p.add_argument("--index", type=Path, default=DEFAULT_INDEX, help="index directory")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("ingest", help="parse source files and build the index")
    s.add_argument("paths", nargs="+", help=".txt/.html/.pdf/.docx files or directories")
    s.add_argument("--no-embeddings", action="store_true", help="skip Voyage embeddings")
    s.set_defaults(func=cmd_ingest)

    s = sub.add_parser("search", help="show the retrieved articles for a query (no LLM)")
    s.add_argument("query")
    s.add_argument("-k", type=int, default=8)
    s.add_argument("--rewrite", action="store_true", help="expand the query with Claude first")
    s.add_argument("--full", action="store_true", help="print full article text")
    s.set_defaults(func=cmd_search)

    s = sub.add_parser("article", help="print an article by number")
    s.add_argument("number")
    s.set_defaults(func=cmd_article)

    for name, func, help_ in (
        ("ask", cmd_ask, "answer one question"),
        ("chat", cmd_chat, "interactive Q&A"),
    ):
        s = sub.add_parser(name, help=help_)
        if name == "ask":
            s.add_argument("question")
            s.add_argument("--show-context", action="store_true")
        s.add_argument("-k", type=int, default=8, help="articles to retrieve")
        s.add_argument("--no-rewrite", action="store_true", help="skip Claude query rewriting")
        s.set_defaults(func=func)

    s = sub.add_parser("serve", help="start the web interface")
    s.add_argument("--host", default="127.0.0.1", help="use 0.0.0.0 to allow other machines")
    s.add_argument("--port", type=int, default=8000)
    s.set_defaults(func=cmd_serve)

    args = p.parse_args(argv)
    try:
        args.func(args)
    except FileNotFoundError as e:
        sys.exit(str(e))
