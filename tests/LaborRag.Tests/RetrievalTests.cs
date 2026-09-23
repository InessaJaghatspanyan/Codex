using System.Text.Json;
using LaborRag.Core;

namespace LaborRag.Tests;

public class RetrievalTests
{
    private static readonly Lazy<SearchIndex> Sample = new(() => new SearchIndex(TestData.SampleChunks()));

    [Fact]
    public void InflectedQueryFindsArticle()
    {
        // "արձակուրդի" (genitive) should still match the vacation article.
        Assert.Equal("4", Sample.Value.Search("ամենամյա արձակուրդի տևողությունը", 3)[0].Chunk.Article);
    }

    [Fact]
    public void ArticleReferenceIsPinned()
    {
        Assert.Equal("5", Sample.Value.Search("ինչ է ասում հոդված 5-ը", 3)[0].Chunk.Article);
        Assert.Equal(new[] { "3.1", "2" }, SearchIndex.FindArticleRefs("see Article 3.1 and ст. 2"));
    }

    [Fact]
    public void ExtraQueriesHelp()
    {
        Assert.Equal("3.1", Sample.Value.Search("probation period", 2, ["փորձաշրջան"])[0].Chunk.Article);
    }

    [Fact]
    public void NoMatchReturnsNothing()
    {
        Assert.Empty(Sample.Value.Search("qwerty zzz", 5));
    }

    [Fact]
    public void IndexRoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var chunks = TestData.SampleChunks();
        IndexStore.Save(dir, chunks);
        Assert.Equal(chunks, IndexStore.Load(dir));
    }

    public static IEnumerable<object[]> ReferenceCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData.Path("reference_search.json")));
        foreach (var c in doc.RootElement.EnumerateArray())
            if (c.TryGetProperty("query", out var q)) yield return [q.GetString()!];
    }

    private static readonly Lazy<SearchIndex> Reference = new(() => new SearchIndex(TestData.ReferenceChunks()));

    /// <summary>Same chunks, same query: rankings and scores must match the Python implementation.</summary>
    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void MatchesPythonRankings(string query)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData.Path("reference_search.json")));
        var c = doc.RootElement.EnumerateArray().First(e => e.TryGetProperty("query", out var q) && q.GetString() == query);
        var extra = c.GetProperty("extra").EnumerateArray().Select(e => e.GetString()!).ToList();
        var hits = Reference.Value.Search(query, 8, extra);
        Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()), hits.Select(h => h.Chunk.Id));
        var scores = c.GetProperty("scores").EnumerateArray().Select(e => e.GetDouble()).ToList();
        for (var i = 0; i < hits.Count; i++) Assert.Equal(scores[i], hits[i].Score, 4);
    }

    [Fact]
    public void TokenizerMatchesPython()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData.Path("reference_search.json")));
        var c = doc.RootElement.EnumerateArray().First(e => e.TryGetProperty("tokenize", out _));
        Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()),
                     SearchIndex.Tokenize(c.GetProperty("tokenize").GetString()!));
    }
}
