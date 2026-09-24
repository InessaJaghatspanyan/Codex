using LaborRag;
using LaborRag.Core;

// laborrag [--index DIR] <command> ...
//   ingest <files/dirs...>   parse .txt/.html/.pdf/.docx and build the index
//   search <query> [-k N] [--rewrite] [--full]
//   article <number>
//   ask <question>           the provisions that best answer the question
//   serve                    web interface (default when no command is given)
var argList = args.ToList();
var indexDir = TakeOption(argList, "--index") ?? Environment.GetEnvironmentVariable("LABOR_RAG_INDEX") ?? "data/index";
var hasCommand = argList.Count > 0 && !argList[0].StartsWith('-');
var command = hasCommand ? argList[0] : "serve";
if (hasCommand) argList.RemoveAt(0);

try
{
    switch (command)
    {
        case "serve":
            IndexStore.Load(indexDir); // fail fast if the index is missing
            await WebApp.Build(argList.ToArray(), indexDir).RunAsync();
            return 0;
        case "ingest":
            return Cli.Ingest(argList, indexDir);
        case "search":
            return Cli.Search(argList, indexDir);
        case "article":
            return Cli.Article(argList, indexDir);
        case "ask":
            return Cli.Ask(argList, indexDir);
        default:
            Console.Error.WriteLine($"Unknown command '{command}'. Commands: ingest, search, article, ask, serve.");
            return 2;
    }
}
catch (FileNotFoundException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static string? TakeOption(List<string> list, string name)
{
    var i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count) return null;
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

namespace LaborRag
{
    public static class Cli
    {
        public static int Ingest(List<string> args, string indexDir)
        {
            if (args.Count == 0)
            {
                Console.Error.WriteLine("usage: laborrag ingest <files or directories...>");
                return 2;
            }
            var chunks = CodeParser.LoadCorpus(args);
            if (chunks.Count == 0)
            {
                Console.Error.WriteLine("No text found in the given files.");
                return 1;
            }
            var articles = chunks.Where(c => c.Article is not null).Select(c => c.Article).Distinct().Count();
            Console.WriteLine($"Parsed {chunks.Count} chunks covering {articles} articles.");
            if (articles == 0)
                Console.Error.WriteLine("Warning: no 'Հոդված N.' headings were detected; check the source file's formatting.");
            IndexStore.Save(indexDir, chunks);
            Console.WriteLine($"Index written to {indexDir}/");
            return 0;
        }

        public static int Search(List<string> args, string indexDir)
        {
            var k = IntOption(args, "-k", 8);
            var full = Flag(args, "--full");
            if (args.Count == 0) return Usage("search <query>");
            var query = string.Join(' ', args);
            var index = new SearchIndex(IndexStore.Load(indexDir));
            PrintHits(index.Search(query, k, Glossary.Expand(query)), full);
            return 0;
        }

        public static int Article(List<string> args, string indexDir)
        {
            if (args.Count == 0) return Usage("article <number>");
            var chunks = new SearchIndex(IndexStore.Load(indexDir)).GetArticle(args[0]);
            if (chunks.Count == 0)
            {
                Console.Error.WriteLine($"Article {args[0]} is not in the index.");
                return 1;
            }
            foreach (var c in chunks)
            {
                Console.WriteLine(c.Heading);
                if (c.Chapter.Length > 0) Console.WriteLine(c.Chapter);
                Console.WriteLine();
                Console.WriteLine(c.Text);
                Console.WriteLine();
            }
            return 0;
        }

        public static int Ask(List<string> args, string indexDir)
        {
            if (args.Count == 0) return Usage("ask <question>");
            var question = string.Join(' ', args);
            var result = PassageFinder.Answer(new SearchIndex(IndexStore.Load(indexDir)), question);
            if (result.Expansions.Count > 0) Console.WriteLine($"(searched also for: {string.Join(", ", result.Expansions)})");
            if (result.Passages.Count == 0)
            {
                Console.WriteLine("No matching provisions found.");
                return 0;
            }
            foreach (var p in result.Passages)
            {
                Console.WriteLine($"\n{p.Chunk.Heading}");
                if (p.Context is not null) Console.WriteLine($"  [{p.Context}]");
                Console.WriteLine($"  «{p.Text.Replace("\n", "\n   ")}»");
            }
            Console.WriteLine("\nRelated articles: " + string.Join(", ", result.Articles.Select(h => h.Chunk.Article ?? "preamble").Distinct()));
            return 0;
        }

        private static void PrintHits(IEnumerable<Hit> hits, bool full)
        {
            foreach (var h in hits)
            {
                Console.WriteLine($"\n[{h.Score:0.00}] {h.Chunk.Heading}");
                if (h.Chunk.Chapter.Length > 0) Console.WriteLine($"       {h.Chunk.Chapter}");
                var body = h.Chunk.Text.Replace('\n', ' ');
                if (!full && body.Length > 300) body = body[..297] + "...";
                Console.WriteLine($"       {body}");
            }
        }

        private static bool Flag(List<string> args, string name) => args.Remove(name);

        private static int IntOption(List<string> args, string name, int fallback)
        {
            var i = args.IndexOf(name);
            if (i < 0 || i + 1 >= args.Count || !int.TryParse(args[i + 1], out var v)) return fallback;
            args.RemoveRange(i, 2);
            return v;
        }

        private static int Usage(string usage)
        {
            Console.Error.WriteLine($"usage: laborrag {usage}");
            return 2;
        }
    }
}

public partial class Program;
