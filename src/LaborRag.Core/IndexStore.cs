using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LaborRag.Core;

/// <summary>Persists parsed chunks as JSON Lines; the search structures are rebuilt on load.</summary>
public static class IndexStore
{
    public const string FileName = "chunks.jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Armenian readable
    };

    public static void Save(string directory, IEnumerable<Chunk> chunks)
    {
        Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(Path.Combine(directory, FileName), false, new UTF8Encoding(false));
        foreach (var c in chunks) writer.WriteLine(JsonSerializer.Serialize(c, Json));
    }

    public static List<Chunk> Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No index at {directory}. Run `laborrag ingest <files>` first.", path);
        return File.ReadLines(path, Encoding.UTF8)
            .Where(l => l.Trim().Length > 0)
            .Select(l => JsonSerializer.Deserialize<Chunk>(l, Json)!)
            .ToList();
    }
}
