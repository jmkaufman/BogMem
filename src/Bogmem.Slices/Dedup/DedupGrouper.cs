namespace Bogmem.Slices.Dedup;

/// <summary>
/// Dedup grouping: cosine-distance threshold 0.15, min group size 5,
/// exact group membership + intra-group order via ordinal compare over fetch order.
/// Ported from mempalace.dedup (get_source_groups + greedy dedup_source_group).
///
/// Important parity quirk: legacy queries the full collection with
/// n_results=min(len(kept), 5). The candidate itself often occupies a neighbor
/// slot (dist≈0) and is not in the kept set, so a near-dup of a single kept
/// drawer can survive when n_results=1 — matching the frozen triple_cluster row.
/// </summary>
public static class DedupGrouper
{
    public const double DefaultThreshold = 0.15;
    public const int MinDrawersToCheck = 5;

    public sealed record DrawerRecord(string Id, string SourceFile, string Document, float[]? Embedding = null, int FetchIndex = 0);

    public sealed record GroupResult(string SourceFile, IReadOnlyList<string> MemberIdsInOrder, IReadOnlyList<string> KeptIds, IReadOnlyList<string> DeletedIds);

    public sealed record BoundaryCoverage(bool DistanceLineStraddled, bool MemberCutoffStraddled, IReadOnlyList<(string A, string B, double Distance)> SampledDistances);

    /// <summary>
    /// Group drawers by source_file preserving Python fetch order within each group.
    /// Only returns groups with &gt;= minCount members.
    /// </summary>
    public static IReadOnlyDictionary<string, List<DrawerRecord>> GetSourceGroups(
        IReadOnlyList<DrawerRecord> drawers,
        int minCount = MinDrawersToCheck,
        string? sourcePattern = null)
    {
        var groups = new Dictionary<string, List<DrawerRecord>>(StringComparer.Ordinal);
        foreach (var d in drawers.OrderBy(x => x.FetchIndex))
        {
            var src = string.IsNullOrEmpty(d.SourceFile) ? "unknown" : d.SourceFile;
            if (sourcePattern is not null &&
                src.IndexOf(sourcePattern, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!groups.TryGetValue(src, out var list))
            {
                list = [];
                groups[src] = list;
            }
            list.Add(d);
        }
        return groups.Where(kv => kv.Value.Count >= minCount)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    public static double CosineDistance(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 2.0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 1.0;
        var sim = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        return 1.0 - sim;
    }

    /// <summary>
    /// Greedy dedup within one source group: sort by doc length desc;
    /// keep if not too similar (distance &lt; threshold) to any already-kept
    /// among the top min(kept,5) nearest neighbors in the full group.
    /// </summary>
    public static (IReadOnlyList<string> Kept, IReadOnlyList<string> Deleted) DedupSourceGroup(
        IReadOnlyList<DrawerRecord> group,
        double threshold = DefaultThreshold)
    {
        // Length desc; preserve original relative order among equal lengths (stable).
        var items = group
            .Select((x, i) => (Item: x, Ord: i))
            .OrderBy(x => x.Ord)
            .OrderByDescending(x => (x.Item.Document ?? "").Length)
            .Select(x => x.Item)
            .ToList();

        var kept = new List<DrawerRecord>();
        var toDelete = new List<string>();
        var allWithEmb = group.Where(d => d.Embedding is not null).ToList();

        foreach (var item in items)
        {
            var doc = item.Document ?? "";
            if (doc.Length < 20)
            {
                toDelete.Add(item.Id);
                continue;
            }
            if (kept.Count == 0)
            {
                kept.Add(item);
                continue;
            }

            var isDup = false;
            if (item.Embedding is not null && allWithEmb.Count > 0)
            {
                var nResults = Math.Min(kept.Count, 5);
                // Simulate col.query over the full group: nearest neighbors by cosine distance.
                var neighbors = allWithEmb
                    .Select(o => (o.Id, Dist: CosineDistance(item.Embedding, o.Embedding!)))
                    .OrderBy(x => x.Dist)
                    .ThenBy(x => x.Id, StringComparer.Ordinal)
                    .Take(nResults)
                    .ToList();

                var keptIds = kept.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var (rid, dist) in neighbors)
                {
                    if (keptIds.Contains(rid) && dist < threshold)
                    {
                        isDup = true;
                        break;
                    }
                }
            }

            if (isDup) toDelete.Add(item.Id);
            else kept.Add(item);
        }

        return (kept.Select(k => k.Id).ToList(), toDelete);
    }

    public static IReadOnlyList<GroupResult> GroupAndDedup(
        IReadOnlyList<DrawerRecord> drawers,
        double threshold = DefaultThreshold,
        int minCount = MinDrawersToCheck)
    {
        var groups = GetSourceGroups(drawers, minCount);
        return groups
            .OrderByDescending(g => g.Value.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var members = g.Value.Select(d => d.Id).ToList();
                var (kept, deleted) = DedupSourceGroup(g.Value, threshold);
                return new GroupResult(g.Key, members, kept, deleted);
            })
            .ToList();
    }

    /// <summary>
    /// Boundary-coverage guard: 0.15 distance straddle + 4v5 member cutoff.
    /// </summary>
    public static BoundaryCoverage AssessBoundaryCoverage(
        IReadOnlyList<DrawerRecord> drawers,
        double threshold = DefaultThreshold)
    {
        var distances = new List<(string A, string B, double Distance)>();
        var withEmb = drawers.Where(d => d.Embedding is not null).ToList();
        for (var i = 0; i < withEmb.Count; i++)
        {
            for (var j = i + 1; j < withEmb.Count; j++)
            {
                var dist = CosineDistance(withEmb[i].Embedding!, withEmb[j].Embedding!);
                distances.Add((withEmb[i].Id, withEmb[j].Id, dist));
            }
        }

        var straddle015 = distances.Any(d => d.Distance < threshold) &&
                          distances.Any(d => d.Distance >= threshold);
        var groups = GetSourceGroups(drawers, minCount: 1);
        var sizes = groups.Values.Select(g => g.Count).ToList();
        var straddle4v5 = (sizes.Any(s => s == 4) && sizes.Any(s => s >= 5))
            || (sizes.Any(s => s < MinDrawersToCheck) && sizes.Any(s => s >= MinDrawersToCheck));

        return new BoundaryCoverage(straddle015, straddle4v5, distances);
    }
}
