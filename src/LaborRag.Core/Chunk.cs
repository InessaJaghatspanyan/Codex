using System.Text.Json.Serialization;

namespace LaborRag.Core;

/// <summary>One retrieval unit: an article of the Code, or one part of a long article.</summary>
public sealed record Chunk
{
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("article")] public string? Article { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("chapter")] public string Chapter { get; init; } = "";
    [JsonPropertyName("section")] public string Section { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("part")] public int Part { get; init; }

    /// <summary>Display title, e.g. "Հոդված 139. Աշխատաժամանակի տևողությունը".</summary>
    [JsonIgnore]
    public string Heading
    {
        get
        {
            if (Article is null) return Title.Length > 0 ? Title : "Preamble";
            var label = $"Հոդված {Article}. {Title}".Trim();
            return Part > 0 ? $"{label} (մաս {Part})" : label;
        }
    }
}
