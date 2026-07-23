using System.Text.RegularExpressions;

namespace Bogmem.Slices.Search;

/// <summary>
/// Search ranking: Okapi-BM25 (k1=1.5, b=0.75), hybrid 0.6/0.4 convex combo,
/// MCP closet rank boosts, stable ordinal tie-breaks.
/// Ported from mempalace.searcher at LEGACY_COMMIT.
/// </summary>
public static partial class Searcher
{
    public const double K1 = 1.5;
    public const double B = 0.75;
    public const double VectorWeight = 0.6;
    public const double Bm25Weight = 0.4;
    public static readonly double[] ClosetRankBoosts = [0.40, 0.25, 0.15, 0.08, 0.04];
    public const double ClosetDistanceCap = 1.5;

    [GeneratedRegex(@"\w{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public sealed class SearchHit
    {
        public SearchHit(
            string id,
            string text,
            double? distance,
            string? authoredAt = null,
            string? sourceFile = null,
            IReadOnlyDictionary<string, object?>? metadata = null)
        {
            Id = id;
            Text = text;
            Distance = distance;
            AuthoredAt = authoredAt;
            SourceFile = sourceFile;
            Metadata = metadata;
        }

        public string Id { get; }
        public string Text { get; }
        public double? Distance { get; set; }
        public string? AuthoredAt { get; }
        public string? SourceFile { get; }
        public IReadOnlyDictionary<string, object?>? Metadata { get; }
        public double Bm25Score { get; set; }
        public double HybridScore { get; set; }
        public double ClosetBoost { get; set; }
        public string MatchedVia { get; set; } = "drawer";
    }

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return TokenRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();
    }

    public static IReadOnlyList<double> Bm25Scores(string query, IReadOnlyList<string> documents, double k1 = K1, double b = B)
    {
        var nDocs = documents.Count;
        var queryTerms = Tokenize(query).ToHashSet(StringComparer.Ordinal);
        if (queryTerms.Count == 0 || nDocs == 0) return Enumerable.Repeat(0.0, nDocs).ToList();

        var tokenized = documents.Select(d => Tokenize(d)).ToList();
        var docLens = tokenized.Select(t => t.Count).ToList();
        if (docLens.All(l => l == 0)) return Enumerable.Repeat(0.0, nDocs).ToList();
        var avgdl = docLens.Sum() / (double)nDocs;
        if (avgdl == 0) avgdl = 1.0;

        var df = queryTerms.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        foreach (var toks in tokenized)
        {
            foreach (var term in toks.Distinct(StringComparer.Ordinal).Where(queryTerms.Contains))
                df[term]++;
        }

        var idf = queryTerms.ToDictionary(
            t => t,
            t => Math.Log((nDocs - df[t] + 0.5) / (df[t] + 0.5) + 1),
            StringComparer.Ordinal);

        var scores = new List<double>(nDocs);
        for (var i = 0; i < nDocs; i++)
        {
            var toks = tokenized[i];
            var dl = docLens[i];
            if (dl == 0) { scores.Add(0); continue; }
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in toks)
            {
                if (queryTerms.Contains(t))
                    tf[t] = tf.GetValueOrDefault(t) + 1;
            }
            double score = 0;
            foreach (var (term, freq) in tf)
            {
                var num = freq * (k1 + 1);
                var den = freq + k1 * (1 - b + b * dl / avgdl);
                score += idf[term] * num / den;
            }
            scores.Add(score);
        }
        return scores;
    }

    public static double DistanceToSimilarity(double? distance, string metric = "cosine")
    {
        if (distance is null) return 0.0;
        var m = (metric ?? "cosine").ToLowerInvariant();
        return m switch
        {
            "l2" => 1.0 / (1.0 + Math.Max(0.0, distance.Value)),
            "ip" => 1.0 / (1.0 + Math.Exp(Math.Min(60.0, distance.Value))),
            _ => Math.Max(0.0, 1.0 - distance.Value),
        };
    }

    /// <summary>CLI search path: hybrid re-rank without closet boost.</summary>
    public static List<SearchHit> HybridRank(IReadOnlyList<SearchHit> results, string query, string metric = "cosine",
        double vectorWeight = VectorWeight, double bm25Weight = Bm25Weight)
    {
        if (results.Count == 0) return [];
        var docs = results.Select(r => r.Text).ToList();
        var bm25Raw = Bm25Scores(query, docs);
        var maxBm25 = bm25Raw.DefaultIfEmpty(0).Max();
        var bm25Norm = maxBm25 > 0
            ? bm25Raw.Select(s => s / maxBm25).ToList()
            : Enumerable.Repeat(0.0, results.Count).ToList();

        var scored = new List<(double Score, int Ordinal, SearchHit Hit)>(results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var vecSim = DistanceToSimilarity(r.Distance, metric);
            r.Bm25Score = Math.Round(bm25Raw[i], 3);
            r.HybridScore = vectorWeight * vecSim + bm25Weight * bm25Norm[i];
            scored.Add((r.HybridScore, i, r));
        }

        // Higher score first; authored_at desc (newer first) for ties; stable over input ordinal.
        // OrderBy is stable; we pre-sort by ordinal then apply score/date keys.
        return scored
            .OrderBy(x => x.Ordinal)
            .OrderByDescending(x => x.Hit.AuthoredAt ?? x.Hit.Metadata?.GetValueOrDefault("authored_at") as string ?? "", StringComparer.Ordinal)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Hit)
            .ToList();
    }

    /// <summary>MCP search_memories Stage-1: closet rank boosts then order by effective distance.</summary>
    public static List<SearchHit> RankWithClosetBoost(
        IReadOnlyList<SearchHit> drawerHits,
        IReadOnlyList<(string SourceFile, double Distance)> closetHitsByRank,
        double maxDistance = 0.0)
    {
        // Closets already ordered by rank (distance ascending from query). First occurrence wins.
        var boostBySource = new Dictionary<string, (int Rank, double Dist)>(StringComparer.Ordinal);
        for (var rank = 0; rank < closetHitsByRank.Count; rank++)
        {
            var (src, dist) = closetHitsByRank[rank];
            if (!string.IsNullOrEmpty(src) && !boostBySource.ContainsKey(src))
                boostBySource[src] = (rank, dist);
        }

        var scored = new List<(SearchHit Hit, int Ordinal)>();
        for (var i = 0; i < drawerHits.Count; i++)
        {
            var hit = drawerHits[i];
            if (maxDistance > 0.0 && hit.Distance is double d && d > maxDistance) continue;
            var boost = 0.0;
            var matchedVia = "drawer";
            var source = hit.SourceFile ?? "";
            if (boostBySource.TryGetValue(source, out var c) &&
                c.Dist <= ClosetDistanceCap &&
                c.Rank < ClosetRankBoosts.Length)
            {
                boost = ClosetRankBoosts[c.Rank];
                matchedVia = "drawer+closet";
            }
            var rawDist = hit.Distance ?? 0.0;
            var effectiveDist = Math.Max(0.0, Math.Min(2.0, rawDist - boost));
            hit.ClosetBoost = boost;
            hit.MatchedVia = matchedVia;
            hit.HybridScore = DistanceToSimilarity(effectiveDist);
            hit.Distance = effectiveDist;
            scored.Add((hit, i));
        }

        // Stable OrderBy over ordinal pre-sort: effective distance asc, then id ordinal.
        return scored
            .OrderBy(x => x.Ordinal)
            .OrderBy(x => x.Hit.Id, StringComparer.Ordinal)
            .OrderBy(x => x.Hit.Distance ?? 2.0)
            .Select(x => x.Hit)
            .ToList();
    }

    public static List<SearchHit> SearchCli(string query, IReadOnlyList<SearchHit> candidates, string metric = "cosine") =>
        HybridRank(candidates, query, metric);

    public static List<SearchHit> SearchMcp(
        string query,
        IReadOnlyList<SearchHit> drawerHits,
        IReadOnlyList<(string SourceFile, double Distance)> closetHits,
        double maxDistance = 0.0,
        string metric = "cosine")
    {
        var boosted = RankWithClosetBoost(drawerHits, closetHits, maxDistance);
        return HybridRank(boosted, query, metric);
    }
}
