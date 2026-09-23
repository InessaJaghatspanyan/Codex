# Labor Code of RA — RAG (.NET)

Question answering over the **Labor Code of the Republic of Armenia**
(ՀՀ աշխատանքային օրենսգիրք), with answers grounded in, and cited to, specific articles.
Built with C# / .NET 8, ASP.NET Core and the official Anthropic C# SDK.

```
question ──► Claude query rewrite ──► hybrid retrieval ──► Claude answer with citations
             (→ Armenian legal terms)   BM25 + char n-grams     (cites Հոդված N)
```

- **Article-level chunking.** The Code is split on `Հոդված N.` headings, keeping each
  article's Section/Chapter. Long articles are split at numbered parts. English
  (`Article`) and Russian (`Статья`) translations are parsed too.
- **Clean PDF extraction.** Lines are rebuilt from glyph positions. The loader removes the
  arlis.am/IRTEK page footers and page numbers, moves the first-page metadata box to the
  preamble, rejoins hard-wrapped lines, and drops the doubled glyphs that the PDF uses to
  fake bold text.
- **Retrieval designed for Armenian.** Armenian words are heavily inflected
  (աշխատող / աշխատողի / աշխատողների), so word-level BM25 is combined with character
  n-gram TF-IDF over article text and titles, which matches across endings without a
  stemmer. The rankings are merged with reciprocal rank fusion. Mentioning an article
  (`հոդված 139`, `article 139`, `ст. 139`) always pulls that article in.
- **Questions in any language.** Claude rewrites the question into Armenian legal search
  terms before retrieval, so English or Russian questions still match the Armenian text.
- **Cited answers.** The retrieved articles are passed to Claude as documents with
  citations enabled. Each answer links to the articles and the exact sentences it relied
  on, and Claude is told to say so when the retrieved text doesn't answer the question.

## Deploy to Render

The repo includes a `Dockerfile` and a `render.yaml` Blueprint. The Docker build parses
`data/raw/` into the search index, so every deploy ships the current text of the Code.

1. Open **https://render.com/deploy?repo=https://github.com/InessaJaghatspanyan/Codex**
   and sign in to Render (a GitHub login works).
2. Render reads `render.yaml` and asks for **`ANTHROPIC_API_KEY`**. Paste your key
   from console.anthropic.com.
3. Click **Apply**. The first build takes a few minutes. The site is then live at
   `https://labor-code-ra.onrender.com`, or a similar name if that one is taken.
4. In the service's **Environment** tab, open **`ACCESS_CODE`**. Render generated a random
   one, which you can replace with something easier to share. Give this code to the people
   who should be able to ask questions.

Search and article lookup are open to everyone; asking Claude requires the access code.
Each IP address can ask 10 questions a minute and try the code 10 times every 5 minutes.
`DAILY_QUESTION_LIMIT` (default 200) caps the total number of questions per day, which
bounds your Anthropic costs.

The free Render plan sleeps after 15 minutes without traffic, so the first visit after a
pause takes about a minute. The paid Starter plan keeps it running. Pushing to the
repository redeploys automatically.

## Run locally

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
alias laborrag="dotnet src/LaborRag/bin/Debug/net8.0/laborrag.dll"

laborrag ingest data/raw/                  # Parsed 291 chunks covering 288 articles.
export ANTHROPIC_API_KEY=...

laborrag ask "Քանի՞ օր է ամենամյա նվազագույն արձակուրդը"
laborrag ask "What notice must an employer give before dismissing an employee?" --show-context
laborrag chat                              # interactive, keeps follow-up context
laborrag search "աշխատանքային պայմանագրի լուծում" -k 5   # retrieval only, free
laborrag article 139
laborrag serve                             # web interface on http://localhost:5000
```

Or with Docker: `docker build -t labor-code-ra . && docker run -p 8080:8080 -e ANTHROPIC_API_KEY=... labor-code-ra`.

## The text of the Code

`data/raw/150003.pdf` is the arlis.am/IRTEK PDF of the version valid
**10.07.2026 – 01.01.2027** (288 articles: 1–266 plus inserted articles such as 3.1).
When the Code is amended, download the new consolidated version from **arlis.am**
(search «Աշխատանքային օրենսգիրք») as PDF, DOCX or HTML, replace the file in `data/raw/`,
and push. Render then rebuilds the index. Local runs need `laborrag ingest data/raw/` again.

## Configuration

| Env var | Default | Purpose |
|---|---|---|
| `ANTHROPIC_API_KEY` | none | Required for answers; without it only search works |
| `ACCESS_CODE` | unset (open) | Code required to ask questions in the web interface |
| `DAILY_QUESTION_LIMIT` | `200` | Maximum questions per UTC day (cost guard) |
| `LABOR_RAG_MODEL` | `claude-opus-5` | Model that writes answers |
| `LABOR_RAG_REWRITE_MODEL` | same as above | Model that rewrites queries (a cheaper model works well here) |
| `LABOR_RAG_EFFORT` | `high` | Answer effort: `low`, `medium`, `high`, `xhigh` or `max` |
| `LABOR_RAG_INDEX` | `data/index` | Index directory (or `--index`) |
| `PORT` | none | Port to listen on (set by Render) |

Answers are requested with server-side refusal fallbacks (`fallbacks: "default"`), so
if the primary model declines a request, the API reruns it on a fallback model.

## Layout

```
src/LaborRag.Core/     library: text loading (PDF/HTML/DOCX), article parser,
                       hybrid search, index storage, Claude calls, prompts.json
src/LaborRag/          the `laborrag` app: CLI commands + ASP.NET Core web API
  wwwroot/index.html   the web page (plain HTML/CSS/JS, no build step)
tests/LaborRag.Tests/  xUnit tests; Data/ holds the synthetic sample and reference
                       output from the original Python implementation
Dockerfile, render.yaml
```

Run the tests with `dotnet test`. The search tests check that rankings and scores match
the original Python implementation exactly on 20 reference queries. The parser tests
check the real PDF against the Python parser's output article by article.

## Disclaimer

This tool provides legal information, not legal advice. Always check answers against the
official text on arlis.am.
