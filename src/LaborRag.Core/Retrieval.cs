using System.Text.RegularExpressions;

namespace LaborRag.Core;

public sealed record Hit(Chunk Chunk, double Score);

/// <summary>
/// Hybrid retriever: BM25 over words, plus TF-IDF over character n-grams of the article
/// text and of article titles, merged with reciprocal rank fusion.
///
/// Armenian is highly inflected (աշխատող, աշխատողի, աշխատողների, ...), so exact word
/// matching alone misses a lot; character n-grams inside word boundaries absorb most
/// suffix variation without a stemmer. An explicit "Հոդված 139" / "article 139" in the
/// query pins that article to the top.
/// </summary>
public sealed partial class SearchIndex
{
    private const int RrfK = 60;

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex WordRe();

    [GeneratedRegex(@"(?:հոդված|հոդվածի|հոդվածը|հոդվածում|հոդ\.|article|art\.|статья|статьи|статье|ст\.)\s*(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleRefRe();

    private readonly Bm25 _bm25;
    private readonly CharTfidf _body;
    private readonly CharTfidf _titles;
    private readonly Dictionary<string, List<int>> _byArticle = [];

    public IReadOnlyList<Chunk> Chunks { get; }
    public int ArticleCount => _byArticle.Count;

    private readonly bool _stem;

    /// <param name="stem">Reduce Armenian words to their stems for the word-level ranking
    /// (տույժի, տույժերը → տույժ). Off reproduces the original Python ranking exactly.</param>
    public SearchIndex(IReadOnlyList<Chunk> chunks, bool stem = true)
    {
        Chunks = chunks;
        _stem = stem;
        var texts = chunks.Select(DocText).ToList();
        _bm25 = new Bm25(texts.Select(WordsFor).ToList());
        _body = new CharTfidf(texts);
        // Article titles are short and precise; ranking them separately keeps a
        // matching title from being drowned out by long, term-heavy articles.
        _titles = new CharTfidf(chunks.Select(c => TitleText(c.Title)).ToList());
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Article is not { } a) continue;
            if (!_byArticle.TryGetValue(a, out var list)) _byArticle[a] = list = [];
            list.Add(i);
        }
    }

    public static List<string> Tokenize(string text) =>
        WordRe().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 1 || char.IsDigit(t[0]))
            .ToList();

    // Stemmed titles let "տույժերը" (title) match "տույժի" (question).
    private string TitleText(string text) =>
        _stem ? string.Join(' ', Tokenize(text).Select(ArmenianStemmer.Stem)) : text;

    private List<string> WordsFor(string text) =>
        _stem ? Tokenize(text).Select(ArmenianStemmer.Stem).ToList() : Tokenize(text);

    /// <summary>
    /// Removes the Armenian question, exclamation and emphasis marks (՞ ՜ ՛), which are
    /// written inside words: "Կարո՞ղ" must search as "Կարող", not as "Կարո" + "ղ".
    /// </summary>
    public static string StripArmenianMarks(string text) =>
        text.Replace("\u055E", "").Replace("\u055C", "").Replace("\u055B", "");

    public static List<string> FindArticleRefs(string text) =>
        ArticleRefRe().Matches(text).Select(m => m.Groups[1].Value).ToList();

    private static string DocText(Chunk c) => string.Join('\n', c.Heading, c.Title, c.Chapter, c.Text);

    public IReadOnlyList<Chunk> GetArticle(string number) =>
        _byArticle.TryGetValue(number, out var idx) ? idx.Select(i => Chunks[i]).ToList() : [];

    public List<Hit> Search(string query, int k = 8, IEnumerable<string>? extraQueries = null)
    {
        var queries = new List<string> { StripArmenianMarks(query) };
        queries.AddRange((extraQueries ?? []).Where(q => !string.IsNullOrWhiteSpace(q)));

        var rankings = new List<int[]>();
        void Add(double[] scores) =>
            // Only chunks that actually match get rank credit; otherwise a query with no
            // overlap (e.g. English against Armenian text) would promote noise.
            rankings.Add(Enumerable.Range(0, scores.Length)
                .Where(i => scores[i] > 0)
                .OrderByDescending(i => scores[i]) // stable: ties keep index order
                .ToArray());

        foreach (var q in queries)
        {
            Add(_bm25.Scores(WordsFor(q)));
            Add(_body.Scores(q));
            Add(_titles.Scores(TitleText(q)));
        }

        var fused = new double[Chunks.Count];
        foreach (var ranking in rankings)
            for (var rank = 0; rank < Math.Min(100, ranking.Length); rank++)
                fused[ranking[rank]] += 1.0 / (RrfK + rank + 1);

        // Explicitly referenced articles always make the cut.
        var pinned = new List<int>();
        foreach (var q in queries)
            foreach (var reference in FindArticleRefs(q))
                if (_byArticle.TryGetValue(reference, out var idx))
                    foreach (var i in idx.Where(i => !pinned.Contains(i)))
                        pinned.Add(i);

        var rest = Enumerable.Range(0, fused.Length)
            .Where(i => fused[i] > 0 && !pinned.Contains(i))
            .OrderByDescending(i => fused[i]);
        var top = Math.Max(fused.Length > 0 ? fused.Max() : 0, 1e-9);
        return pinned.Concat(rest)
            .Take(Math.Max(k, pinned.Count))
            .Select(i => new Hit(Chunks[i], pinned.Contains(i) ? 1.0 : fused[i] / top))
            .ToList();
    }

    private sealed class Bm25
    {
        private const double K1 = 1.5, B = 0.75;
        private readonly List<Dictionary<string, int>> _tfs;
        private readonly int[] _lens;
        private readonly double _avgdl;
        private readonly Dictionary<string, double> _idf = [];

        public Bm25(List<List<string>> docs)
        {
            _tfs = docs.Select(d => d.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count())).ToList();
            _lens = docs.Select(d => d.Count).ToArray();
            _avgdl = docs.Count > 0 ? _lens.Average() : 0;
            var df = new Dictionary<string, int>();
            foreach (var tf in _tfs)
                foreach (var t in tf.Keys)
                    df[t] = df.GetValueOrDefault(t) + 1;
            var n = docs.Count;
            foreach (var (t, f) in df) _idf[t] = Math.Log(1 + (n - f + 0.5) / (f + 0.5));
        }

        public double[] Scores(List<string> query)
        {
            var output = new double[_tfs.Count];
            for (var i = 0; i < _tfs.Count; i++)
            {
                var norm = K1 * (1 - B + B * _lens[i] / (_avgdl == 0 ? 1 : _avgdl));
                foreach (var t in query)
                    if (_tfs[i].TryGetValue(t, out var f))
                        output[i] += _idf[t] * f * (K1 + 1) / (f + norm);
            }
            return output;
        }
    }

    /// <summary>
    /// Matches scikit-learn's TfidfVectorizer(analyzer="char_wb", ngram_range=(3, 5),
    /// lowercase=True, sublinear_tf=True) with its defaults (smooth idf, l2 norm).
    /// </summary>
    private sealed partial class CharTfidf
    {
        private readonly Dictionary<string, double> _idf = [];
        // n-gram -> (doc index, weight) postings for fast sparse dot products.
        private readonly Dictionary<string, List<(int Doc, double Weight)>> _postings = [];
        private readonly int _size;

        [GeneratedRegex(@"\s\s+")]
        private static partial Regex MultiSpaceRe();

        public CharTfidf(List<string> texts)
        {
            _size = texts.Count;
            var counts = texts.Select(Count).ToList();
            var df = new Dictionary<string, int>();
            foreach (var m in counts)
                foreach (var g in m.Keys)
                    df[g] = df.GetValueOrDefault(g) + 1;
            foreach (var (g, f) in df) _idf[g] = Math.Log((1.0 + _size) / (1.0 + f)) + 1;
            for (var i = 0; i < counts.Count; i++)
            {
                foreach (var (g, w) in Weigh(counts[i]))
                {
                    if (!_postings.TryGetValue(g, out var list)) _postings[g] = list = [];
                    list.Add((i, w));
                }
            }
        }

        private static Dictionary<string, int> Count(string text)
        {
            var counts = new Dictionary<string, int>();
            foreach (var g in Ngrams(text)) counts[g] = counts.GetValueOrDefault(g) + 1;
            return counts;
        }

        private static IEnumerable<string> Ngrams(string text, int minN = 3, int maxN = 5)
        {
            var normalized = MultiSpaceRe().Replace(text.ToLowerInvariant(), " ");
            foreach (var word in normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = " " + word + " ";
                for (var n = minN; n <= maxN; n++)
                {
                    var offset = 0;
                    yield return w.Substring(offset, Math.Min(n, w.Length - offset));
                    while (offset + n < w.Length)
                    {
                        offset++;
                        yield return w.Substring(offset, Math.Min(n, w.Length - offset));
                    }
                    if (offset == 0) break; // a short word is counted once
                }
            }
        }

        private Dictionary<string, double> Weigh(Dictionary<string, int> counts)
        {
            var v = new Dictionary<string, double>();
            var norm = 0.0;
            foreach (var (g, c) in counts)
            {
                if (!_idf.TryGetValue(g, out var idf)) continue; // outside the fitted vocabulary
                var w = (1 + Math.Log(c)) * idf;
                v[g] = w;
                norm += w * w;
            }
            norm = Math.Sqrt(norm);
            if (norm > 0) foreach (var g in v.Keys.ToList()) v[g] /= norm;
            return v;
        }

        public double[] Scores(string query)
        {
            var output = new double[_size];
            foreach (var (g, qw) in Weigh(Count(query)))
                if (_postings.TryGetValue(g, out var list))
                    foreach (var (doc, dw) in list)
                        output[doc] += qw * dw;
            return output;
        }
    }
}
