using LaborRag.Core;

namespace LaborRag;

public sealed record SearchRequest(string? Query, int? K);

public sealed record AskRequest(string? Question);

public static class WebApp
{
    public const int MaxQuestionChars = 4000;

    public static WebApplication Build(string[] args, string indexDir)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Render (and most PaaS hosts) pass the port to listen on in $PORT.
        if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        builder.Services.AddSingleton(_ => new SearchIndex(IndexStore.Load(indexDir)));

        var app = builder.Build();
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

    private static void MapApi(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/info", (SearchIndex index) =>
        {
            var preamble = index.Chunks.FirstOrDefault(c => c.Article is null)?.Text ?? "";
            return Results.Json(new
            {
                articles = index.ArticleCount,
                chunks = index.Chunks.Count,
                version = preamble.Split('\n', 2)[0],
            });
        });

        api.MapPost("/search", (SearchRequest req, SearchIndex index) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query) || req.Query.Length > MaxQuestionChars)
                return Error(422, $"query is required (max {MaxQuestionChars} characters).");
            var k = Math.Clamp(req.K ?? 8, 1, 20);
            return Results.Json(new { hits = index.Search(req.Query, k, Glossary.Expand(req.Query)).Select(ChunkJson) });
        });

        api.MapGet("/article/{number}", (string number, SearchIndex index) =>
        {
            var chunks = index.GetArticle(number);
            return chunks.Count == 0
                ? Error(404, $"Article {number} is not in the index.")
                : Results.Json(new { chunks = chunks.Select(c => ChunkJson(new Hit(c, 1.0))) });
        });

        // Answers with the provisions of the Code that best match the question (no LLM).
        api.MapPost("/ask", (AskRequest req, SearchIndex index) =>
        {
            if (string.IsNullOrWhiteSpace(req.Question) || req.Question.Length > MaxQuestionChars)
                return Error(422, $"question is required (max {MaxQuestionChars} characters).");
            var result = PassageFinder.Answer(index, req.Question);
            return Results.Json(new
            {
                passages = result.Passages.Select(p => new
                {
                    article = p.Chunk.Article,
                    heading = p.Chunk.Heading,
                    chapter = p.Chunk.Chapter,
                    context = p.Context,
                    text = p.Text,
                }),
                hits = result.Articles.Select(ChunkJson),
                expansions = result.Expansions,
            });
        });
    }
}
