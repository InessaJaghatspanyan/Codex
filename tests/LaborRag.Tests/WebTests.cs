using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LaborRag.Core;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LaborRag.Tests;

public sealed class WebTests : IDisposable
{
    private readonly string _indexDir = Directory.CreateTempSubdirectory().FullName;

    public WebTests()
    {
        IndexStore.Save(_indexDir, TestData.SampleChunks());
        // Program reads the index location when the test host starts it.
        Environment.SetEnvironmentVariable("LABOR_RAG_INDEX", _indexDir);
    }

    public void Dispose() => Directory.Delete(_indexDir, true);

    private static HttpClient Client() => new WebApplicationFactory<Program>().CreateClient();

    [Fact]
    public async Task ServesPageAndInfo()
    {
        var client = Client();
        var page = await client.GetStringAsync("/");
        Assert.Contains("ՀՀ աշխատանքային օրենսգիրք", page);
        Assert.DoesNotContain("access-code", page);
        var info = await client.GetFromJsonAsync<JsonElement>("/api/info");
        Assert.Equal(6, info.GetProperty("articles").GetInt32());
    }

    [Fact]
    public async Task SearchAndArticle()
    {
        var client = Client();
        var search = await client.PostAsJsonAsync("/api/search", new { query = "ամենամյա արձակուրդ" });
        var hits = (await search.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hits");
        Assert.Equal("4", hits[0].GetProperty("article").GetString());

        var article = await client.GetFromJsonAsync<JsonElement>("/api/article/3.1");
        Assert.StartsWith("Հոդված 3.1", article.GetProperty("chunks")[0].GetProperty("heading").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/article/999")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/api/search", new { query = "" })).StatusCode);
    }

    [Fact]
    public async Task AskReturnsMatchingProvisions()
    {
        var client = Client();
        var response = await client.PostAsJsonAsync("/api/ask", new { question = "How long is the probation period?" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("փորձաշրջան", data.GetProperty("expansions").EnumerateArray().Select(e => e.GetString()));
        var first = data.GetProperty("passages")[0];
        Assert.Equal("3.1", first.GetProperty("article").GetString());
        Assert.Contains("Փորձաշրջանի տևողությունը", first.GetProperty("text").GetString());
        Assert.True(data.GetProperty("hits").GetArrayLength() > 0);
    }

    [Fact]
    public async Task AskValidatesInput()
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/api/ask", new { question = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/api/ask", new { question = new string('ա', 4001) })).StatusCode);
    }
}
