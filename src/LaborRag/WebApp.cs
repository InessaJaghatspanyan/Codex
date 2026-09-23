using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Anthropic;
using Anthropic.Exceptions;
using LaborRag.Core;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace LaborRag;

public sealed record SearchRequest(string? Query, int? K);

public sealed record AskRequest(string? Question, List<Turn>? History, int? K, bool? Rewrite);

/// <summary>Settings read from the environment (see README, "Configuration").</summary>
public sealed record WebSettings
{
    public string IndexDir { get; init; } = "data/index";
    public string? AccessCode { get; init; }
    public int DailyQuestionLimit { get; init; } = 200;
    public bool LlmConfigured { get; init; }

    public static WebSettings FromEnvironment() => new()
    {
        IndexDir = Env("LABOR_RAG_INDEX") ?? "data/index",
        AccessCode = Env("ACCESS_CODE"),
        DailyQuestionLimit = int.TryParse(Env("DAILY_QUESTION_LIMIT"), out var n) ? n : 200,
        LlmConfigured = Env("ANTHROPIC_API_KEY") is not null || Env("ANTHROPIC_AUTH_TOKEN") is not null,
    };

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

/// <summary>Counts questions per UTC day; a cost guard, reset when the process restarts.</summary>
public sealed class DailyQuota(int limit)
{
    private readonly object _lock = new();
    private DateOnly _day;
    private int _used;

    public bool TryTake()
    {
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (today != _day) (_day, _used) = (today, 0);
            if (_used >= limit) return false;
            _used++;
            return true;
        }
    }
}

public static class WebApp
{
    public const int MaxQuestionChars = 4000;
    public const int MaxHistoryTurns = 12;
    public const int MaxTurnChars = 20000;

    public static WebApplication Build(string[] args, WebSettings settings)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Render (and most PaaS hosts) pass the port to listen on in $PORT.
        if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(_ => new SearchIndex(IndexStore.Load(settings.IndexDir)));
        builder.Services.AddSingleton(new DailyQuota(settings.DailyQuestionLimit));
        builder.Services.AddSingleton<IAssistant>(_ =>
            new ClaudeAssistant(new AnthropicClient(), AssistantOptions.FromEnvironment()));
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Behind the host's proxy, the client IP (used for rate limits) arrives in X-Forwarded-For.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("ask", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
            o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseRateLimiter();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        MapApi(app);
        return app;
    }

    private static IResult Error(int status, string detail) => Results.Json(new { detail }, statusCode: status);

    private static object ChunkJson(Hit h) => new
    {
        id = h.Chunk.Id,
        article = h.Chunk.Article,
        heading = h.Chunk.Heading,
        chapter = h.Chunk.Chapter,
        section = h.Chunk.Section,
        text = h.Chunk.Text,
        score = Math.Round(h.Score, 3),
    };

    /// <summary>True when no code is configured or the request carries the right one.</summary>
    private static bool HasAccess(HttpRequest req, WebSettings settings)
    {
        if (settings.AccessCode is null) return true;
        var given = req.Headers["x-access-code"].ToString();
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)),
            SHA256.HashData(Encoding.UTF8.GetBytes(settings.AccessCode)));
    }

    private static void MapApi(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/info", (SearchIndex index, WebSettings settings) =>
        {
            var preamble = index.Chunks.FirstOrDefault(c => c.Article is null)?.Text ?? "";
            return Results.Json(new
            {
                articles = index.ArticleCount,
                chunks = index.Chunks.Count,
                version = preamble.Split('\n', 2)[0],
                llm = settings.LlmConfigured,
                auth = settings.AccessCode is not null,
            });
        });

        api.MapPost("/search", (SearchRequest req, SearchIndex index) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query) || req.Query.Length > MaxQuestionChars)
                return Error(422, $"query is required (max {MaxQuestionChars} characters).");
            var k = Math.Clamp(req.K ?? 8, 1, 20);
            return Results.Json(new { hits = index.Search(req.Query, k).Select(ChunkJson) });
        });

        api.MapGet("/article/{number}", (string number, SearchIndex index) =>
        {
            var chunks = index.GetArticle(number);
            return chunks.Count == 0
                ? Error(404, $"Article {number} is not in the index.")
                : Results.Json(new { chunks = chunks.Select(c => ChunkJson(new Hit(c, 1.0))) });
        });

        api.MapPost("/auth", (HttpRequest req, WebSettings settings) =>
            HasAccess(req, settings) ? Results.NoContent() : Error(401, "Invalid access code."))
            .RequireRateLimiting("auth");

        api.MapPost("/ask", AskAsync).RequireRateLimiting("ask");
    }

    private static async Task<IResult> AskAsync(
        AskRequest req, HttpRequest http, SearchIndex index, IAssistant assistant,
        WebSettings settings, DailyQuota quota, ILoggerFactory logs, CancellationToken ct)
    {
        if (!HasAccess(http, settings)) return Error(401, "Invalid access code.");
        if (!settings.LlmConfigured) return Error(503, "Anthropic API key is not configured.");
        if (string.IsNullOrWhiteSpace(req.Question) || req.Question.Length > MaxQuestionChars)
            return Error(422, $"question is required (max {MaxQuestionChars} characters).");
        var history = req.History ?? [];
        if (history.Any(t => t is null || (t.Role != "user" && t.Role != "assistant") || t.Content is null))
            return Error(422, "history items need role 'user' or 'assistant' and string content.");
        if (history.Any(t => t.Content.Length > MaxTurnChars)) return Error(422, "a history item is too long.");
        var k = req.K ?? 8;
        if (k is < 1 or > 20) return Error(422, "k must be 1-20.");
        if (!quota.TryTake()) return Error(429, "The daily question limit has been reached. Please try again tomorrow.");

        var turns = history.TakeLast(MaxHistoryTurns).SkipWhile(t => t.Role != "user").ToList();
        try
        {
            var extra = new List<string>();
            if (req.Rewrite ?? true)
            {
                var plan = await assistant.RewriteQueryAsync(req.Question, ct);
                extra.AddRange(plan.Queries);
                extra.AddRange(plan.ArticleRefs.Select(n => $"Հոդված {n}"));
            }
            var hits = index.Search(req.Question, k, extra);
            var answer = await assistant.AnswerAsync(req.Question, hits, turns, ct);
            return Results.Json(new
            {
                answer = answer.Text,
                refused = answer.Refused,
                citations = answer.Citations.Select((c, i) => new
                {
                    n = i + 1,
                    heading = c.Heading,
                    article = c.DocumentIndex >= 0 && c.DocumentIndex < hits.Count ? hits[c.DocumentIndex].Chunk.Article : null,
                    text = c.CitedText,
                }),
                hits = hits.Select(ChunkJson),
            });
        }
        catch (AnthropicUnauthorizedException)
        {
            return Error(503, "The server's Anthropic API key is invalid.");
        }
        catch (AnthropicRateLimitException)
        {
            return Error(429, "Rate limited by the Anthropic API; try again shortly.");
        }
        catch (AnthropicApiException e)
        {
            logs.CreateLogger("ask").LogError(e, "Anthropic API error");
            return Error(502, "The Anthropic API returned an error; try again shortly.");
        }
    }
}
