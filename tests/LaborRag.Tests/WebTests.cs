using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LaborRag.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LaborRag.Tests;

/// <summary>Stands in for Claude so the API can be tested without network access.</summary>
internal sealed class FakeAssistant : IAssistant
{
    public List<IReadOnlyList<Turn>> Histories { get; } = [];

    public Task<SearchPlan> RewriteQueryAsync(string question, CancellationToken ct = default) =>
        Task.FromResult(new SearchPlan(["փորձաշրջան"], []));

    public Task<Answer> AnswerAsync(string question, IReadOnlyList<Hit> hits, IReadOnlyList<Turn> history, CancellationToken ct = default)
    {
        Histories.Add(history);
        var i = hits.ToList().FindIndex(h => h.Chunk.Article == "3.1");
        return Task.FromResult(new Answer("Պայմանագրով։[1]", [new Citation(hits[i].Chunk.Heading, "Փորձաշրջանի", i)]));
    }
}

public sealed class WebTests : IDisposable
{
    private readonly string _indexDir = Directory.CreateTempSubdirectory().FullName;
    private readonly FakeAssistant _assistant = new();

    public WebTests() => IndexStore.Save(_indexDir, TestData.SampleChunks());

    public void Dispose() => Directory.Delete(_indexDir, true);

    private HttpClient Client(string? accessCode = "secret-code", int dailyLimit = 100)
    {
        var settings = new WebSettings
        {
            IndexDir = _indexDir,
            AccessCode = accessCode,
            DailyQuestionLimit = dailyLimit,
            LlmConfigured = true,
        };
        Environment.SetEnvironmentVariable("LABOR_RAG_INDEX", _indexDir);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<WebSettings>();
            s.AddSingleton(settings);
            s.RemoveAll<DailyQuota>();
            s.AddSingleton(new DailyQuota(dailyLimit));
            s.RemoveAll<IAssistant>();
            s.AddSingleton<IAssistant>(_assistant);
        }));
        return factory.CreateClient();
    }

    private static object AskBody(string question = "How long is probation?") => new
    {
        question,
        history = new[]
        {
            new { role = "assistant", content = "dropped: history must start with a user turn" },
            new { role = "user", content = "hi" },
            new { role = "assistant", content = "hello" },
        },
    };

    [Fact]
    public async Task ServesPageAndInfo()
    {
        var client = Client();
        Assert.Contains("ՀՀ աշխատանքային օրենսգիրք", await client.GetStringAsync("/"));
        var info = await client.GetFromJsonAsync<JsonElement>("/api/info");
        Assert.Equal(6, info.GetProperty("articles").GetInt32());
        Assert.True(info.GetProperty("auth").GetBoolean());
    }

    [Fact]
    public async Task SearchAndArticleNeedNoCode()
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
    public async Task AskRequiresTheAccessCode()
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/ask", AskBody())).StatusCode);
        client.DefaultRequestHeaders.Add("x-access-code", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth", new { })).StatusCode);
        Assert.Empty(_assistant.Histories);
    }

    [Fact]
    public async Task AskMapsCitationsToArticles()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("x-access-code", "secret-code");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth", new { })).StatusCode);

        var response = await client.PostAsJsonAsync("/api/ask", AskBody());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Պայմանագրով։[1]", data.GetProperty("answer").GetString());
        Assert.Equal("3.1", data.GetProperty("citations")[0].GetProperty("article").GetString());
        Assert.True(data.GetProperty("hits").GetArrayLength() > 0);
        // The leading assistant turn is dropped so history starts with the user.
        Assert.Equal([new Turn("user", "hi"), new Turn("assistant", "hello")], _assistant.Histories.Single());
    }

    [Fact]
    public async Task AskIsOpenWithoutConfiguredCode()
    {
        var client = Client(accessCode: null);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/ask", AskBody())).StatusCode);
        var info = await client.GetFromJsonAsync<JsonElement>("/api/info");
        Assert.False(info.GetProperty("auth").GetBoolean());
    }

    [Fact]
    public async Task AskValidatesInput()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("x-access-code", "secret-code");
        var badRole = new { question = "x", history = new[] { new { role = "system", content = "x" } } };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/ask", badRole)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/api/ask", new { question = new string('ա', 4001) })).StatusCode);
    }

    [Fact]
    public async Task DailyLimitStopsQuestions()
    {
        var client = Client(dailyLimit: 1);
        client.DefaultRequestHeaders.Add("x-access-code", "secret-code");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/ask", AskBody())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/ask", AskBody())).StatusCode);
    }
}
