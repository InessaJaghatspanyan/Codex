using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using NonBeta = Anthropic.Models.Messages;

namespace LaborRag.Core;

public sealed record Turn(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record Citation(string Heading, string CitedText, int DocumentIndex);

public sealed record Answer(string Text, IReadOnlyList<Citation> Citations, bool Refused = false);

public sealed record SearchPlan(
    [property: JsonPropertyName("queries")] IReadOnlyList<string> Queries,
    [property: JsonPropertyName("article_refs")] IReadOnlyList<string> ArticleRefs)
{
    public static readonly SearchPlan Empty = new([], []);
}

/// <summary>The two Claude calls: query rewriting and grounded, cited answers.</summary>
public interface IAssistant
{
    Task<SearchPlan> RewriteQueryAsync(string question, CancellationToken ct = default);
    Task<Answer> AnswerAsync(string question, IReadOnlyList<Hit> hits, IReadOnlyList<Turn> history, CancellationToken ct = default);
}

public sealed record AssistantOptions
{
    public string Model { get; init; } = "claude-opus-5";
    public string? RewriteModel { get; init; }
    public string Effort { get; init; } = "high";

    public static AssistantOptions FromEnvironment() => new()
    {
        Model = Env("LABOR_RAG_MODEL") ?? "claude-opus-5",
        RewriteModel = Env("LABOR_RAG_REWRITE_MODEL"),
        Effort = Env("LABOR_RAG_EFFORT") ?? "high",
    };

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

public sealed class ClaudeAssistant(AnthropicClient client, AssistantOptions options) : IAssistant
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    public static Prompts Prompts { get; } = Prompts.Load();

    public async Task<SearchPlan> RewriteQueryAsync(string question, CancellationToken ct = default)
    {
        try
        {
            var response = await client.Messages.Create(new NonBeta.MessageCreateParams
            {
                Model = options.RewriteModel ?? options.Model,
                MaxTokens = 2000,
                System = Prompts.RewriteSystem,
                OutputConfig = new NonBeta.OutputConfig
                {
                    Effort = NonBeta.Effort.Low,
                    Format = new NonBeta.JsonOutputFormat { Schema = Prompts.SearchPlanSchema },
                },
                Messages = [new() { Role = NonBeta.Role.User, Content = question }],
            }, ct);
            if (response.StopReason == "refusal") return SearchPlan.Empty;
            var json = string.Concat(response.Content.Select(b => b.Value).OfType<NonBeta.TextBlock>().Select(t => t.Text));
            return JsonSerializer.Deserialize<SearchPlan>(json) ?? SearchPlan.Empty;
        }
        catch (Exception e) when (e is not OperationCanceledException && !IsAuthError(e))
        {
            // Retrieval still works on the raw question.
            Console.Error.WriteLine($"query rewrite failed; using the raw question: {e.Message}");
            return SearchPlan.Empty;
        }
    }

    public async Task<Answer> AnswerAsync(
        string question, IReadOnlyList<Hit> hits, IReadOnlyList<Turn> history, CancellationToken ct = default)
    {
        if (hits.Count == 0) return new Answer("No relevant articles were found in the index.", []);

        var content = new List<BetaContentBlockParam>();
        foreach (var hit in hits)
        {
            var c = hit.Chunk;
            var context = string.Join(" / ", new[] { c.Section, c.Chapter }.Where(s => s.Length > 0));
            content.Add(new BetaRequestDocumentBlock
            {
                Source = new BetaPlainTextSource { Data = c.Text },
                Title = c.Heading,
                Context = context.Length > 0 ? context : null,
                Citations = new BetaCitationsConfigParam { Enabled = true },
            });
        }
        content.Add(new BetaTextBlockParam { Text = question });

        // Older turns stay text-only; only the current question carries the documents.
        var messages = history
            .Select(t => new BetaMessageParam { Role = t.Role == "assistant" ? Role.Assistant : Role.User, Content = t.Content })
            .ToList();
        messages.Add(new BetaMessageParam { Role = Role.User, Content = content });

        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = 16000,
            System = Prompts.AnswerSystem,
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(options.Effort) },
            Messages = messages,
            Betas = [FallbackBeta],
            Fallbacks = new Default(), // "default": Anthropic picks the fallback per refusal category
        }, ct);

        if (response.StopReason == "refusal") return new Answer("The model declined to answer this request.", [], Refused: true);

        var parts = new List<string>();
        var citations = new List<Citation>();
        foreach (var block in response.Content)
        {
            if (!block.TryPickText(out var text)) continue;
            parts.Add(text.Text);
            var marks = new List<int>();
            foreach (var cit in text.Citations ?? [])
            {
                if (!cit.TryPickCitationCharLocation(out var loc)) continue;
                var index = (int)loc.DocumentIndex;
                var heading = loc.DocumentTitle ?? (index < hits.Count ? hits[index].Chunk.Heading : "");
                citations.Add(new Citation(heading, loc.CitedText, index));
                marks.Add(citations.Count);
            }
            if (marks.Count > 0) parts.Add(string.Concat(marks.Select(n => $"[{n}]")));
        }
        return new Answer(string.Concat(parts).Trim(), citations);
    }

    private static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.High,
    };

    public static bool IsAuthError(Exception e) =>
        e is Anthropic.Exceptions.AnthropicUnauthorizedException;
}

/// <summary>Prompts shared by every entry point (embedded prompts.json).</summary>
public sealed record Prompts(string AnswerSystem, string RewriteSystem, Dictionary<string, JsonElement> SearchPlanSchema)
{
    public static Prompts Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaborRag.Core.prompts.json")
            ?? throw new InvalidOperationException("prompts.json resource missing");
        var doc = JsonDocument.Parse(stream).RootElement;
        string Get(string key) => doc.GetProperty(key).GetString()!;
        var schema = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["queries"] = new { type = "array", items = new { type = "string" }, description = Get("rewrite_queries_description") },
                ["article_refs"] = new { type = "array", items = new { type = "string" }, description = Get("rewrite_article_refs_description") },
            }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "queries", "article_refs" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };
        return new Prompts(Get("answer_system"), Get("rewrite_system"), schema);
    }
}
