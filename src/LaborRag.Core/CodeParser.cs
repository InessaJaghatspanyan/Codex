using System.Text.RegularExpressions;

namespace LaborRag.Core;

/// <summary>
/// Splits the Labor Code into article-level chunks. The Code is structured as
/// Sections (ԲԱԺԻՆ) &gt; Chapters (ԳԼՈՒԽ) &gt; Articles (Հոդված). Articles are what
/// lawyers cite and what answers should point back to. English ("Article") and
/// Russian ("Статья") translations are recognised too.
/// </summary>
public static partial class CodeParser
{
    public const int MaxChunkChars = 4000;

    [GeneratedRegex(@"^\s*(?:Հոդված|ՀՈԴՎԱԾ|Article|ARTICLE|Статья|СТАТЬЯ)\s+(\d+(?:\.\d+)*)\s*[.։:]?\s*(.*)$")]
    private static partial Regex ArticleRe();

    [GeneratedRegex(@"^\s*(?:ԳԼՈՒԽ|Գլուխ|CHAPTER|Chapter|ГЛАВА|Глава)\s+(\d+(?:\.\d+)*)\s*[.։:]?\s*(.*)$")]
    private static partial Regex ChapterRe();

    [GeneratedRegex(@"^\s*(?:ԲԱԺԻՆ|Բաժին|SECTION|Section|PART|Part|РАЗДЕЛ|Раздел)\s+(\d+(?:\.\d+)*)\s*[.։:]?\s*(.*)$")]
    private static partial Regex SectionRe();

    // A numbered paragraph ("1.", "2)") inside an article; long articles split there.
    [GeneratedRegex(@"^\s*\d+[.)]\s+")]
    private static partial Regex PartRe();

    // Extraction artifacts: "1- ին" -> "1-ին", "( այսուհետ" -> "(այսուհետ".
    [GeneratedRegex(@"(\d)- (?=[Ա-և])")]
    private static partial Regex HyphenSpaceRe();

    [GeneratedRegex(@"\( +")]
    private static partial Regex OpenParenSpaceRe();

    [GeneratedRegex(@" +\)")]
    private static partial Regex CloseParenSpaceRe();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex BlanksRe();

    public static List<Chunk> LoadCorpus(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith('.'))
                    .Order(StringComparer.Ordinal));
            }
            else if (File.Exists(p))
            {
                files.Add(p);
            }
            else
            {
                throw new FileNotFoundException($"No such file or directory: {p}", p);
            }
        }
        var chunks = files.SelectMany(f => SplitArticles(TextLoader.Load(f), Path.GetFileName(f))).ToList();
        return DedupeIds(chunks);
    }

    public static List<string> CleanLines(string text)
    {
        text = text.Replace(' ', ' ').Replace("\r", "");
        text = HyphenSpaceRe().Replace(text, "$1-");
        text = OpenParenSpaceRe().Replace(text, "(");
        text = CloseParenSpaceRe().Replace(text, ")");
        return text.Split('\n')
            .Select(ln => BlanksRe().Replace(ln, " ").Trim())
            .Where(ln => ln.Length > 0)
            .ToList();
    }

    private static bool IsHeading(string line) =>
        ArticleRe().IsMatch(line) || ChapterRe().IsMatch(line) || SectionRe().IsMatch(line);

    /// <summary>Headings sometimes put the title on the following line.</summary>
    private static (string Title, int Index) NextTitle(List<string> lines, int i, string inline)
    {
        if (inline.Length > 0) return (inline, i);
        if (i + 1 < lines.Count && !IsHeading(lines[i + 1])) return (lines[i + 1], i + 1);
        return ("", i);
    }

    private sealed class Draft
    {
        public required string Article;
        public required string Title;
        public required string Chapter;
        public required string Section;
        public List<string> Body { get; } = [];
    }

    public static List<Chunk> SplitArticles(string text, string source = "")
    {
        var lines = CleanLines(text);
        var chunks = new List<Chunk>();
        var preamble = new List<string>();
        string section = "", chapter = "";
        Draft? current = null;

        void Flush()
        {
            if (current is { Body.Count: > 0 }) chunks.AddRange(MakeChunks(current, source));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            Match m;
            if ((m = SectionRe().Match(line)).Success)
            {
                (var title, i) = NextTitle(lines, i, m.Groups[2].Value);
                section = $"Բաժին {m.Groups[1].Value}. {title}".Trim();
            }
            else if ((m = ChapterRe().Match(line)).Success)
            {
                (var title, i) = NextTitle(lines, i, m.Groups[2].Value);
                chapter = $"Գլուխ {m.Groups[1].Value}. {title}".Trim();
            }
            else if ((m = ArticleRe().Match(line)).Success)
            {
                Flush();
                (var title, i) = NextTitle(lines, i, m.Groups[2].Value);
                current = new Draft { Article = m.Groups[1].Value, Title = title, Chapter = chapter, Section = section };
            }
            else if (current is null)
            {
                preamble.Add(line);
            }
            else
            {
                current.Body.Add(line);
            }
        }
        Flush();

        if (preamble.Count > 0)
        {
            var stem = Path.GetFileNameWithoutExtension(source);
            chunks.Insert(0, new Chunk
            {
                Id = $"{(stem.Length > 0 ? stem : "doc")}:preamble",
                Title = "Preamble",
                Text = string.Join('\n', preamble),
                Source = source,
            });
        }
        return DedupeIds(chunks);
    }

    private static IEnumerable<Chunk> MakeChunks(Draft art, string source)
    {
        var groups = new List<List<string>> { new() };
        var size = 0;
        foreach (var line in art.Body)
        {
            // Start a new group at a numbered paragraph once the current one is big.
            if (size > MaxChunkChars && PartRe().IsMatch(line))
            {
                groups.Add([]);
                size = 0;
            }
            groups[^1].Add(line);
            size += line.Length;
        }
        var multi = groups.Count > 1;
        return groups.Select((g, n) => new Chunk
        {
            Id = $"art-{art.Article}" + (multi ? $"-p{n + 1}" : ""),
            Article = art.Article,
            Title = art.Title,
            Chapter = art.Chapter,
            Section = art.Section,
            Text = string.Join('\n', g),
            Source = source,
            Part = multi ? n + 1 : 0,
        });
    }

    private static List<Chunk> DedupeIds(List<Chunk> chunks)
    {
        var seen = new Dictionary<string, int>();
        foreach (var c in chunks)
        {
            if (seen.TryGetValue(c.Id, out var n))
            {
                seen[c.Id] = n + 1;
                c.Id = $"{c.Id}~{n + 1}";
            }
            else
            {
                seen[c.Id] = 0;
            }
        }
        return chunks;
    }
}
