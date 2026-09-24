using LaborRag.Core;

namespace LaborRag.Tests;

public class ParserTests
{
    [Fact]
    public void SplitsArticlesWithStructure()
    {
        var chunks = TestData.SampleChunks();
        var byArticle = chunks.Where(c => c.Article is not null).ToDictionary(c => c.Article!);
        Assert.Equal(new[] { "1", "2", "3", "3.1", "4", "5" }, byArticle.Keys);
        Assert.Null(chunks[0].Article); // preamble line
        Assert.Equal("Աշխատանքային պայմանագրի կնքումը", byArticle["3"].Title);
        Assert.Equal("Գլուխ 2. ԱՇԽԱՏԱՆՔԱՅԻՆ ՊԱՅՄԱՆԱԳԻՐ", byArticle["3"].Chapter);
        Assert.Equal("Բաժին 1. ԸՆԴՀԱՆՈՒՐ ԴՐՈՒՅԹՆԵՐ", byArticle["1"].Section);
        Assert.Equal("Ամենամյա արձակուրդ", byArticle["4"].Title); // title on the next line
        Assert.Contains("ամենամյա վճարովի արձակուրդ", byArticle["4"].Text);
    }

    [Fact]
    public void RecognisesEnglishAndRussianHeadings()
    {
        var en = CodeParser.SplitArticles("Article 12. Working time\nNormal hours are 40 per week.");
        var ru = CodeParser.SplitArticles("Статья 12. Рабочее время\nНормальная продолжительность 40 часов.");
        Assert.Equal("12", en[0].Article);
        Assert.Equal("12", ru[0].Article);
        Assert.Equal("Working time", en[0].Title);
    }

    [Fact]
    public void SplitsLongArticlesIntoParts()
    {
        var body = string.Join('\n', Enumerable.Range(1, 7).Select(n => $"{n}. " + string.Concat(Enumerable.Repeat("բառ ", 400))));
        var chunks = CodeParser.SplitArticles("Հոդված 7. Երկար հոդված\n" + body);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.Equal("7", c.Article));
        Assert.Equal(Enumerable.Range(1, chunks.Count), chunks.Select(c => c.Part));
        Assert.Equal(chunks.Count, chunks.Select(c => c.Id).Distinct().Count());
        Assert.EndsWith("(մաս 1)", chunks[0].Heading);
    }

    [Fact]
    public void CleanPdfPagesStripsFootersAndUnwraps()
    {
        const string footer = "ՀԱՅԱՍՏԱՆԻ ՀԱՆՐԱՊԵՏՈՒԹՅԱՆ ԱՇԽԱՏԱՆՔԱՅԻՆ ՕՐԵՆՍԳԻՐՔ\n© 1996 - 2026, PDF 23.09.2026";
        string[] pages =
        [
            "01.01.2026 - 01.01.2027\n \nՀոդված 1. Առաջին\n \n1. Սա երկար\nտող է:\nՀՀ Ազգային Ժողով, Օրենսգիրք\nԸնդունվել է. 09.11.2004\n" + footer + "1",
            "2. Երկրորդ մասը\nշարունակվում է ( այսուհետ` X) 1- ին մասով:\n \n" + footer + "2",
            "Հոդված 2. Երկրորդ\n \nՏեքստ:\n" + footer + "13",
        ];
        var chunks = CodeParser.SplitArticles(TextLoader.CleanPdfPages(pages.Select(p => p.Split('\n')).ToList()));
        Assert.StartsWith("01.01.2026 - 01.01.2027\n", chunks[0].Text);
        Assert.Contains("Ընդունվել է. 09.11.2004", chunks[0].Text); // metadata moved to the preamble
        Assert.Equal("1. Սա երկար տող է:\n2. Երկրորդ մասը շարունակվում է (այսուհետ` X) 1-ին մասով:", chunks[1].Text);
        Assert.Equal("Տեքստ:", chunks[2].Text);
        Assert.DoesNotContain(chunks, c => c.Text.Contains('©'));
    }

    [Fact]
    public void LoadsHtmlWithLineBreaksBetweenBlocks()
    {
        var text = TextLoader.LoadHtml(
            "<html><head><style>p{}</style></head><body><h2>Հոդված 1. Վերնագիր</h2><p>1. Առաջին մաս</p>" +
            "<script>alert(1)</script><p>2. Երկրորդ մաս</p></body></html>");
        var chunks = CodeParser.SplitArticles(text);
        Assert.Equal("Վերնագիր", chunks[0].Title);
        Assert.Equal("1. Առաջին մաս\n2. Երկրորդ մաս", chunks[0].Text);
    }
}

/// <summary>The uploaded Code PDF, checked against the original Python parser's output.</summary>
public class RealPdfTests
{
    // Articles where the C# extraction is deliberately different: pypdf repeated phrases
    // from faux-bold (double-drawn) glyphs in 57 and 160, scrambled the signature block
    // in 266, and added a stray space before a comma in 17.1.
    private static readonly HashSet<string> CorrectedIds = ["art-17.1", "art-57", "art-160", "art-266"];

    private static readonly Lazy<List<Chunk>> Parsed = new(() => CodeParser.LoadCorpus([TestData.CodePdf]));

    [Fact]
    public void MatchesReferenceStructure()
    {
        var reference = TestData.ReferenceChunks();
        var parsed = Parsed.Value;
        Assert.Equal(291, parsed.Count);
        Assert.Equal(288, parsed.Where(c => c.Article is not null).Select(c => c.Article).Distinct().Count());
        Assert.Equal(reference.Select(c => (c.Id, c.Article, c.Title, c.Chapter, c.Section, c.Part)),
                     parsed.Select(c => (c.Id, c.Article, c.Title, c.Chapter, c.Section, c.Part)));
    }

    [Fact]
    public void MatchesReferenceText()
    {
        var reference = TestData.ReferenceChunks().ToDictionary(c => c.Id);
        foreach (var c in Parsed.Value.Where(c => !CorrectedIds.Contains(c.Id)))
            Assert.True(reference[c.Id].Text == c.Text, $"text differs for {c.Id}");
    }

    [Fact]
    public void RemovesFauxBoldDuplicates()
    {
        var art57 = Parsed.Value.Single(c => c.Id == "art-57").Text;
        Assert.Contains("մի մասի վերաբերյալ:", art57);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(art57, "49-րդ հոդվածի 3-րդ մասով"));
        Assert.DoesNotContain("պպաա", art57);
    }

    [Fact]
    public void PreambleStartsWithEdition()
    {
        var preamble = Parsed.Value[0];
        Assert.Null(preamble.Article);
        Assert.StartsWith("10.07.2026 - 01.01.2027\n", preamble.Text);
    }
}
