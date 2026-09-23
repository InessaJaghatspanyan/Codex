using System.Text.Json;
using LaborRag.Core;

namespace LaborRag.Tests;

internal static class TestData
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Data", name);

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "LaborRag.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    public static string CodePdf => System.IO.Path.Combine(RepoRoot(), "data", "raw", "150003.pdf");

    public static List<Chunk> SampleChunks() => CodeParser.LoadCorpus([Path("sample_code.txt")]);

    /// <summary>Chunks produced by the original Python implementation (pypdf) from the same PDF.</summary>
    public static List<Chunk> ReferenceChunks() =>
        JsonSerializer.Deserialize<List<Chunk>>(File.ReadAllText(Path("reference_chunks.json")))!;
}
