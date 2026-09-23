# Labor Code of RA — RAG

Question answering over the **Labor Code of the Republic of Armenia**
(ՀՀ աշխատանքային օրենսգիրք), with answers grounded in, and cited to, specific articles.

```
question ──► Claude query rewrite ──► hybrid retrieval ──► Claude answer with citations
             (→ Armenian legal terms)   BM25 + char n-grams     (cites Հոդված N)
                                        (+ Voyage embeddings)
```

- **Article-level chunking.** The source is split on `Հոդված N.` headings, keeping each
  article's Section/Chapter. Long articles are split at numbered parts. English
  (`Article`) and Russian (`Статья`) translations are parsed too.
- **Retrieval designed for Armenian.** Armenian words are heavily inflected
  (աշխատող / աշխատողի / աշխատողների), so word-level BM25 is combined with
  character n-gram TF-IDF, which matches across endings without a stemmer. The rankings
  are merged with reciprocal rank fusion. Mentioning an article (`հոդված 139`, `article 139`,
  `ст. 139`) always pulls that article in.
- **Questions in any language.** Claude rewrites the question into Armenian legal search
  terms before retrieval, so English or Russian questions still match the Armenian text.
- **Cited answers.** The retrieved articles are passed to Claude as documents with
  citations enabled. Each answer lists the articles and the exact sentences it relied on,
  and the prompt tells Claude to say so when the retrieved text doesn't answer the question.

## Setup

```bash
python -m venv .venv && source .venv/bin/activate
pip install -e ".[dev]"            # add ",voyage" for dense embeddings
export ANTHROPIC_API_KEY=...        # or `ant auth login`
```

## 1. Get the text of the Code

To update or replace the text, download the current consolidated
version from the official legal information system **arlis.am** (search for
«Աշխատանքային օրենսգիրք»), then save the page as HTML, or download the DOCX/PDF, into
`data/raw/`. `.txt`, `.html`, `.pdf` and `.docx` are all supported.

The repo currently has `data/raw/150003.pdf`: the arlis.am/IRTEK PDF of the version
valid **10.07.2026 – 01.01.2027**, which parses into 288 articles (1–266 plus inserted
articles such as 3.1). The PDF loader strips the running page footers and page
numbers, moves the first-page metadata box (adoption and entry-into-force dates)
to the preamble, and rejoins hard-wrapped lines.

> The Code has been amended many times. Re-download and re-ingest after amendments,
> and check which version your answers came from.

## 2. Build the index

```bash
labor-rag ingest data/raw/
# Parsed 291 chunks covering 288 articles.
```

If `VOYAGE_API_KEY` is set and the `voyage` extra is installed, dense embeddings
(`voyage-multilingual-2`) are added to the hybrid search automatically. Skip them with
`--no-embeddings`.

## 3. Use it

```bash
# One question (any language)
labor-rag ask "Քանի՞ օր է ամենամյա նվազագույն արձակուրդը"
labor-rag ask "What notice must an employer give before dismissing an employee?"
labor-rag ask "Сколько длится испытательный срок?" --show-context

# Interactive session with follow-up questions
labor-rag chat

# Retrieval only, no LLM: useful for debugging and costs nothing
labor-rag search "աշխատանքային պայմանագրի լուծում" -k 5
labor-rag article 139
```

`python -m labor_rag ...` works the same way.

### Web interface

```bash
pip install -e ".[web]"
labor-rag serve                   # http://127.0.0.1:8000/
labor-rag serve --host 0.0.0.0    # reachable from other machines on your network
```

A single page with three modes:

- **Հարցնել · Ask:** questions in any language, answered with numbered citations.
  Click a citation to open the article with the quoted sentence highlighted.
  Follow-up questions keep the conversation context.
- **Որոնել · Search:** retrieval only, with no LLM call.
- **Հոդված · Article:** open any article by number.

If no Anthropic credentials are configured, the page opens in Search mode and says so.
The JSON API it uses is also available directly: `POST /api/ask`, `POST /api/search`,
`GET /api/article/{number}`, `GET /api/info`.

The server has no authentication. Before exposing it beyond your own network, put it
behind a login, because every question costs Anthropic API usage.

## Configuration

| Env var | Default | Purpose |
|---|---|---|
| `LABOR_RAG_INDEX` | `data/index` | Index directory (or `--index`) |
| `LABOR_RAG_MODEL` | `claude-opus-5` | Model that writes answers |
| `LABOR_RAG_REWRITE_MODEL` | same as above | Model that rewrites queries (a cheaper model works well here) |
| `LABOR_RAG_EFFORT` | `high` | Answer effort: `low`, `medium`, `high`, `xhigh` or `max` |
| `VOYAGE_API_KEY` | unset | Turns on dense embeddings |
| `LABOR_RAG_VOYAGE_MODEL` | `voyage-multilingual-2` | Embedding model |

Answers are requested with server-side refusal fallbacks (`fallbacks: "default"`), so
if the primary model declines a request, the API reruns it on a fallback model.

## Layout

```
labor_rag/parser.py   file loading + article/chapter/section splitting
labor_rag/index.py    BM25 + char n-gram TF-IDF + optional Voyage, RRF fusion, persistence
labor_rag/llm.py      Claude: query rewriting (structured output) and cited answers
labor_rag/cli.py      ingest / search / article / ask / chat / serve
labor_rag/web.py      FastAPI server for the web interface
labor_rag/static/     the web page (plain HTML/CSS/JS, no build step)
tests/                unit tests on a synthetic fixture (not real law text)
```

Run the tests with `pytest`.

## Disclaimer

This tool provides legal information, not legal advice. Always check answers against the
official text on arlis.am.
