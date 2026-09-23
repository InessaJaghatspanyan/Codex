using Anthropic;
using LaborRag;
using LaborRag.Core;

// laborrag [--index DIR] <command> ...
//   ingest <files/dirs...>   parse .txt/.html/.pdf/.docx and build the index
//   search <query> [-k N] [--rewrite] [--full]
//   article <number>
//   ask <question> [-k N] [--no-rewrite] [--show-context]
//   chat [-k N] [--no-rewrite]
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
            var settings = WebSettings.FromEnvironment() with { IndexDir = indexDir };
            IndexStore.Load(indexDir); // fail fast if the index is missing
            await WebApp.Build(argList.ToArray(), settings).RunAsync();
            return 0;
        case "ingest":
            return Cli.Ingest(argList, indexDir);
        case "search":
            return await Cli.SearchAsync(argList, indexDir);
        case "article":
            return Cli.Article(argList, indexDir);
        case "ask":
            return await Cli.AskAsync(argList, indexDir);
        case "chat":
            return await Cli.ChatAsync(argList, indexDir);
        default:
            Console.Error.WriteLine($"Unknown command '{command}'. Commands: ingest, search, article, ask, chat, serve.");
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

        public static async Task<int> SearchAsync(List<string> args, string indexDir)
        {
            var k = IntOption(args, "-k", 8);
            var rewrite = Flag(args, "--rewrite");
            var full = Flag(args, "--full");
            if (args.Count == 0) return Usage("search <query>");
            var index = new SearchIndex(IndexStore.Load(indexDir));
            PrintHits(await RetrieveAsync(index, string.Join(' ', args), k, rewrite), full);
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

        public static async Task<int> AskAsync(List<string> args, string indexDir)
        {
            var k = IntOption(args, "-k", 8);
            var rewrite = !Flag(args, "--no-rewrite");
            var showContext = Flag(args, "--show-context");
            if (args.Count == 0) return Usage("ask <question>");
            var question = string.Join(' ', args);
            var index = new SearchIndex(IndexStore.Load(indexDir));
            var hits = await RetrieveAsync(index, question, k, rewrite);
            if (showContext) PrintHits(hits, false);
            PrintAnswer(await Assistant().AnswerAsync(question, hits, []));
            return 0;
        }

        public static async Task<int> ChatAsync(List<string> args, string indexDir)
        {
            var k = IntOption(args, "-k", 8);
            var rewrite = !Flag(args, "--no-rewrite");
            var index = new SearchIndex(IndexStore.Load(indexDir));
            var assistant = Assistant();
            var history = new List<Turn>();
            Console.WriteLine("Labor Code of RA — ask a question (empty line or Ctrl-D to exit).");
            while (true)
            {
                Console.Write("\n> ");
                var q = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(q)) break;
                var hits = await RetrieveAsync(index, q, k, rewrite);
                var answer = await assistant.AnswerAsync(q, hits, history);
                PrintAnswer(answer);
                history.Add(new Turn("user", q));
                history.Add(new Turn("assistant", answer.Text));
                if (history.Count > 12) history.RemoveRange(0, history.Count - 12);
            }
            return 0;
        }

        private static IAssistant Assistant() =>
            new ClaudeAssistant(new AnthropicClient(), AssistantOptions.FromEnvironment());

        private static async Task<List<Hit>> RetrieveAsync(SearchIndex index, string question, int k, bool rewrite)
        {
            var extra = new List<string>();
            if (rewrite)
            {
                var plan = await Assistant().RewriteQueryAsync(question);
                extra.AddRange(plan.Queries);
                extra.AddRange(plan.ArticleRefs.Select(n => $"Հոդված {n}"));
            }
            return index.Search(question, k, extra);
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

        private static void PrintAnswer(Answer answer)
        {
            Console.WriteLine();
            Console.WriteLine(answer.Text);
            if (answer.Citations.Count == 0) return;
            Console.WriteLine("\nSources:");
            for (var i = 0; i < answer.Citations.Count; i++)
            {
                var quote = answer.Citations[i].CitedText.Replace('\n', ' ');
                if (quote.Length > 200) quote = quote[..197] + "...";
                Console.WriteLine($"  [{i + 1}] {answer.Citations[i].Heading}: «{quote}»");
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
