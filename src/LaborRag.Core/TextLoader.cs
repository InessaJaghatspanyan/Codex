using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Paragraph;

namespace LaborRag.Core;

/// <summary>Extracts plain text from .txt/.md, .html/.htm, .pdf and .docx files.</summary>
public static partial class TextLoader
{
    public static string Load(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".md" => File.ReadAllText(path, Encoding.UTF8),
        ".html" or ".htm" => LoadHtml(File.ReadAllText(path, Encoding.UTF8)),
        ".pdf" => LoadPdf(path),
        ".docx" => LoadDocx(path),
        _ => throw new NotSupportedException($"Unsupported file type: {path}"),
    };

    private static readonly HashSet<string> BlockTags =
    [
        "P", "DIV", "BR", "LI", "TR", "TD", "TH", "H1", "H2", "H3", "H4", "H5", "H6",
        "TABLE", "UL", "OL", "SECTION", "ARTICLE", "HEADER", "FOOTER", "BLOCKQUOTE", "PRE",
    ];

    /// <summary>Visible text with a line break around each block element (like a browser).</summary>
    public static string LoadHtml(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        foreach (var el in doc.QuerySelectorAll("script, style, noscript").ToList()) el.Remove();
        var sb = new StringBuilder();
        void Walk(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IText text) { sb.Append(text.Data); continue; }
                if (child is not IElement el) continue;
                var block = BlockTags.Contains(el.TagName);
                if (block) sb.Append('\n');
                Walk(el);
                if (block) sb.Append('\n');
            }
        }
        Walk(doc.Body ?? (INode)doc.DocumentElement);
        return sb.ToString();
    }

    private static string LoadDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        return body is null ? "" : string.Join('\n', body.Descendants<WordText>().Select(p => p.InnerText));
    }

    private static string LoadPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        return CleanPdfPages(pdf.GetPages().Select(PageLines).ToList());
    }

    /// <summary>
    /// Rebuilds a page's lines from glyph positions: letters are grouped by baseline and
    /// ordered left to right. Only real space glyphs become spaces. Justified text
    /// spreads letters apart (e.g. "1 0 )"), so gap-based spacing would corrupt numbers.
    /// A line holding only a space glyph comes out empty and separates blocks.
    /// </summary>
    private static List<string> PageLines(Page page)
    {
        var rows = new List<(double Y, List<Letter> Letters)>();
        foreach (var letter in page.Letters.OrderByDescending(l => l.StartBaseLine.Y))
        {
            var tolerance = Math.Max(letter.PointSize, 1) * 0.2;
            if (rows.Count > 0 && Math.Abs(rows[^1].Y - letter.StartBaseLine.Y) <= tolerance)
                rows[^1].Letters.Add(letter);
            else
                rows.Add((letter.StartBaseLine.Y, [letter]));
        }
        return rows.SelectMany(r => SplitColumns(r.Letters)).ToList();
    }

    /// <summary>
    /// Orders a baseline's letters left to right and splits it where a wide horizontal
    /// gap separates side-by-side text blocks (e.g. the first-page metadata box).
    /// Faux-bold text draws every glyph twice at a tiny offset; the copy is dropped.
    /// </summary>
    private static IEnumerable<string> SplitColumns(List<Letter> letters)
    {
        var sb = new StringBuilder();
        Letter? prev = null;
        foreach (var l in letters.OrderBy(x => x.StartBaseLine.X))
        {
            if (prev is not null)
            {
                var dx = l.StartBaseLine.X - prev.StartBaseLine.X;
                if (l.Value == prev.Value && dx < Math.Max(prev.Width * 0.3, 0.2)) continue;
                if (l.StartBaseLine.X - prev.EndBaseLine.X > 3 * Math.Max(l.PointSize, 1))
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            sb.Append(l.Value);
            prev = l;
        }
        yield return sb.ToString();
    }

    // Metadata box that arlis.am/IRTEK PDFs print on the first page (adoption, signing,
    // entry-into-force dates). It lands mid-article, so it is moved to the preamble.
    [GeneratedRegex(@"^(?:ՀՀ Ազգային Ժողով|Ընդունվել է\.|Ստորագրվել է\.|ՈՒժի մեջ է մտել\.|Ուժի մեջ է մտել\.)")]
    private static partial Regex PdfMetadataRe();

    // A line that starts a new logical line in hard-wrapped PDF text.
    [GeneratedRegex(@"^(?:\d+(?:\.\d+)*[.)]\s|\d+(?:\.\d+)*\)|\(|Հոդված\s|ԳԼՈՒԽ\s|ԲԱԺԻՆ\s)")]
    private static partial Regex LineStartRe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRe();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRe();

    [GeneratedRegex(@"^[\d. -]+$")]
    private static partial Regex DateLineRe();

    private static string NormLine(string line) => WhitespaceRe().Replace(line.Replace(' ', ' '), " ").Trim();

    /// <summary>Removes running headers/footers and page numbers, then undoes hard line wraps.</summary>
    public static string CleanPdfPages(IReadOnlyList<IEnumerable<string>> pages)
    {
        var pageLines = pages.Select(p => p.Select(NormLine).ToList()).ToList();

        // Lines repeated on most pages (numbers masked) are running headers/footers.
        var counts = new Dictionary<string, int>();
        foreach (var lines in pageLines)
            foreach (var key in lines.Where(l => l.Length > 0).Select(l => DigitsRe().Replace(l, "#")).Distinct())
                counts[key] = counts.GetValueOrDefault(key) + 1;
        var threshold = Math.Max(3, pages.Count / 2);
        var boilerplate = counts.Where(kv => kv.Value >= threshold).Select(kv => kv.Key).ToHashSet();

        var body = new List<string>();
        var metadata = new List<string>();
        foreach (var page in pageLines)
        {
            var lines = page;
            while (lines.Count > 0 && (lines[^1].Length == 0 || lines[^1].All(char.IsDigit)))
                lines = lines[..^1]; // trailing page number
            foreach (var ln in lines)
            {
                if (boilerplate.Contains(DigitsRe().Replace(ln, "#"))) continue;
                if (PdfMetadataRe().IsMatch(ln)) { metadata.Add(ln); continue; }
                body.Add(ln);
            }
        }

        // Blank lines separate blocks (headings, articles); within a block, join wrapped
        // lines unless the next one starts a numbered part, a note, or a heading.
        var output = new List<string>();
        var joinable = false;
        foreach (var ln in body)
        {
            if (ln.Length == 0)
            {
                output.Add("");
                joinable = false;
            }
            else if (joinable && !LineStartRe().IsMatch(ln))
            {
                output[^1] += " " + ln;
            }
            else
            {
                output.Add(ln);
                joinable = true;
            }
        }
        var head = new List<string>();
        if (output.Count > 0 && DateLineRe().IsMatch(output[0]))
        {
            head.Add(output[0]);
            output.RemoveAt(0);
        }
        return string.Join('\n', head.Concat(metadata).Concat(output));
    }
}
