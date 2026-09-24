using System.Text.RegularExpressions;

namespace LaborRag.Core;

/// <summary>A provision of an article; <see cref="Context"/> is the sentence that introduces a list item.</summary>
public sealed record Passage(Chunk Chunk, string Text, string? Context, double Score);

public sealed record QuestionResult(IReadOnlyList<Passage> Passages, IReadOnlyList<Hit> Articles, IReadOnlyList<string> Expansions);

/// <summary>A provision candidate: what is shown, and the sentence that introduces it.</summary>
public sealed record Provision(string Text, string? Context);

/// <summary>
/// Answers a question without an LLM: finds the most relevant articles, splits them into
/// provisions (numbered parts, list items and sentences) and returns the provisions that
/// best match the question, each linked to its article.
/// </summary>
public static partial class PassageFinder
{
    // Armenian sentences end with "։" (or ":" in many texts); list items end with ";" or ".".
    [GeneratedRegex(@"(?<=[։:;])\s+(?=\S)")]
    private static partial Regex SentenceBreakRe();

    // "1)", "2.1)": a list item inside a part.
    [GeneratedRegex(@"^\d+(?:\.\d+)*\)")]
    private static partial Regex ItemRe();

    private const int MinPassageChars = 25;
    private const int MaxContextChars = 220;
    private const int MaxListChars = 900;

    public static QuestionResult Answer(SearchIndex index, string question, int articles = 6, int passages = 4)
    {
        var expansions = Glossary.Expand(question);
        var hits = index.Search(question, articles, expansions);
        if (hits.Count == 0) return new QuestionResult([], [], expansions);

        // Every provision of the top articles becomes a candidate. A list item is scored
        // together with its lead-in, so "the notice must state:" gives its items context.
        var candidates = new List<Chunk>();
        var owners = new List<(Chunk Chunk, Provision Provision)>();
        foreach (var hit in hits)
        {
            foreach (var p in SplitProvisions(hit.Chunk.Text))
            {
                var scored = p.Context is null ? p.Text : p.Context + " " + p.Text;
                candidates.Add(hit.Chunk with { Id = $"{hit.Chunk.Id}#{candidates.Count}", Text = scored });
                owners.Add((hit.Chunk, p));
            }
        }
        if (candidates.Count == 0) return new QuestionResult([], hits, expansions);

        // Rank provisions with the same hybrid scoring, then favour ones from the
        // better-ranked articles so a stray matching sentence can't outrank them.
        var articleRank = hits.Select((h, i) => (h.Chunk.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var ranked = new SearchIndex(candidates).Search(question, candidates.Count, expansions)
            .Select(h =>
            {
                var (owner, provision) = owners[int.Parse(h.Chunk.Id[(h.Chunk.Id.LastIndexOf('#') + 1)..])];
                var score = h.Score * (1.0 - 0.08 * articleRank[owner.Id]);
                return new Passage(owner, provision.Text, provision.Context, score);
            })
            .OrderByDescending(p => p.Score)
            .ToList();

        // Keep the best provisions: at most two per article, and none contained in one
        // already picked (a lead-in with its list vs. one of its items).
        var picked = new List<Passage>();
        foreach (var p in ranked)
        {
            if (picked.Count(x => x.Chunk.Id == p.Chunk.Id) >= 2) continue;
            if (picked.Any(x => x.Chunk.Id == p.Chunk.Id && (x.Text.Contains(p.Text) || p.Text.Contains(x.Text)))) continue;
            picked.Add(p);
            if (picked.Count == passages) break;
        }
        return new QuestionResult(picked, hits, expansions);
    }

    /// <summary>
    /// Splits article text into provisions. Long paragraphs are split into sentences; a
    /// list item keeps the sentence that introduces it as context; an introducing
    /// sentence (ending in "`" or ":") is also offered together with its items.
    /// </summary>
    public static List<Provision> SplitProvisions(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Amendment notes like "(115-րդ հոդ. փոփ. ...)" are not provisions.
            .Where(l => !(l.StartsWith('(') && l.EndsWith(')')))
            .ToList();
        var result = new List<Provision>();
        string? leadIn = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (ItemRe().IsMatch(line))
            {
                result.Add(new Provision(line, leadIn is null ? null : Shorten(leadIn)));
                continue;
            }
            leadIn = line;
            var items = lines.Skip(i + 1).TakeWhile(l => ItemRe().IsMatch(l)).ToList();
            if (items.Count > 0 && IsLeadIn(line))
            {
                var list = line;
                foreach (var item in items)
                {
                    if (list.Length + item.Length > MaxListChars) { list += "\n…"; break; }
                    list += "\n" + item;
                }
                result.Add(new Provision(list, null));
                continue;
            }
            var sentences = line.Length > 400 ? SentenceBreakRe().Split(line) : [line];
            foreach (var s in sentences.Select(s => s.Trim()))
            {
                if (s.Length >= MinPassageChars) result.Add(new Provision(s, null));
                else if (result.Count > 0 && s.Length > 0 && result[^1].Context is null)
                    result[^1] = result[^1] with { Text = result[^1].Text + " " + s };
            }
        }
        return result;
    }

    private static bool IsLeadIn(string line) => line.EndsWith('`') || line.EndsWith(':') || line.EndsWith('։');

    private static string Shorten(string s) => s.Length <= MaxContextChars ? s : "…" + s[^MaxContextChars..].TrimStart();
}
