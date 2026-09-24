# Labor Code of RA — search and Q&A (.NET)

Ask questions about the **Labor Code of the Republic of Armenia**
(ՀՀ աշխատանքային օրենսգիրք) and get back the exact provisions of the Code that answer
them, each linked to its article. Built with C# / .NET 8 and ASP.NET Core. It uses no AI
service and no API keys, so it costs nothing to run beyond hosting.

```
question ──► glossary (EN/RU → Armenian terms) ──► hybrid article search ──► best-matching provisions
                                                    BM25 + char n-grams        quoted, with their article
```

- **Article-level index.** The Code is split on `Հոդված N.` headings, keeping each
  article's Section/Chapter. English (`Article`) and Russian (`Статья`) translations are
  parsed too.
- **Clean PDF extraction.** Lines are rebuilt from glyph positions. The loader removes the
  arlis.am/IRTEK page footers and page numbers, moves the first-page metadata box to the
  preamble, rejoins hard-wrapped lines, and drops the doubled glyphs that the PDF uses to
  fake bold text.
- **Search designed for Armenian.** Armenian words are heavily inflected
  (աշխատող / աշխատողի / աշխատողների). Word matching uses a light Armenian stemmer and is
  combined with character n-gram TF-IDF over article text and titles; the rankings are
  merged with reciprocal rank fusion. Mentioning an article (`հոդված 139`, `article 139`,
  `ст. 139`) always pulls that article in.
- **Answers are quotes.** The best articles are split into provisions (numbered parts, list
  items with the sentence that introduces them, sentences of long paragraphs), and the
  provisions that best match the question are shown with the matching words highlighted.
  Clicking one opens the full article at that provision.
- **English and Russian questions** work through a built-in glossary of about 50 labor-law
  terms (vacation/отпуск → արձակուրդ, overtime/сверхурочные → արտաժամյա աշխատանք, …).
  Armenian questions match best.

## Deploy to Render

The repo includes a `Dockerfile` and a `render.yaml` Blueprint. The Docker build parses
`data/raw/` into the search index, so every deploy ships the current text of the Code.

1. Open **https://render.com/deploy?repo=https://github.com/InessaJaghatspanyan/Codex**
   and sign in to Render (a GitHub login works).
2. Click **Apply**. The first build takes a few minutes. The site is then live at
   `https://labor-code-ra.onrender.com`, or a similar name if that one is taken.

No settings or keys are needed. Pushing to the repository redeploys automatically. The
free Render plan sleeps after 15 minutes without traffic, so the first visit after a
pause takes about a minute; the paid Starter plan keeps it running.

## Run locally

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
alias laborrag="dotnet src/LaborRag/bin/Debug/net8.0/laborrag.dll"

laborrag ingest data/raw/                  # Parsed 291 chunks covering 288 articles.
laborrag ask "Քանի՞ օր է ամենամյա նվազագույն արձակուրդը"
laborrag ask "How is overtime work paid?"
laborrag search "աշխատանքային պայմանագրի լուծում" -k 5
laborrag article 139
laborrag serve                             # web interface on http://localhost:5000
```

Or with Docker: `docker build -t labor-code-ra . && docker run -p 8080:8080 labor-code-ra`.

## The text of the Code

`data/raw/150003.pdf` is the arlis.am/IRTEK PDF of the version valid
**10.07.2026 – 01.01.2027** (288 articles: 1–266 plus inserted articles such as 3.1).
When the Code is amended, download the new consolidated version from **arlis.am**
(search «Աշխատանքային օրենսգիրք») as PDF, DOCX or HTML, replace the file in `data/raw/`,
and push. Render then rebuilds the index. Local runs need `laborrag ingest data/raw/` again.

## Configuration

| Env var | Default | Purpose |
|---|---|---|
| `LABOR_RAG_INDEX` | `data/index` | Index directory (or `--index`) |
| `PORT` | none | Port to listen on (set by Render) |

## Layout

```
src/LaborRag.Core/     library: text loading (PDF/HTML/DOCX), article parser, hybrid
                       search, Armenian stemmer, EN/RU glossary, provision finder
src/LaborRag/          the `laborrag` app: CLI commands + ASP.NET Core web API
  wwwroot/index.html   the web page (plain HTML/CSS/JS, no build step)
tests/LaborRag.Tests/  xUnit tests; Data/ holds a synthetic sample and reference output
                       from the original Python implementation
Dockerfile, render.yaml
```

Run the tests with `dotnet test`. They include questions in all three languages checked
against the real Code (for example, the annual-leave question must quote Article 159's
"20 աշխատանքային օր"), and parser checks against the real PDF.

## Disclaimer

This tool shows quotes from the Code found by automatic search; it is not legal advice.
Always check against the official text on arlis.am.
