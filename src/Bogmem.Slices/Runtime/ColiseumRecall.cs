using Bogmem.Graph;
using Bogmem.Slices.Storage;

namespace Bogmem.Slices.Runtime;

public sealed record PalaceRecallFailure(
    string PalaceId,
    string PalaceName,
    string ErrorClass,
    string Message);

public sealed record PalaceRecallSource(
    string PalaceId,
    string PalaceName,
    string RetrievalMode,
    string EmbeddingModel,
    int Hits);

public sealed record PalaceRecallHit(
    int FederatedRank,
    int LocalRank,
    string PalaceId,
    string PalaceName,
    MemoryDrawer Drawer,
    double Distance,
    double LocalScore,
    double Bm25Score);

public sealed record ColiseumRecallResult(
    string Query,
    string Ranking,
    int PalacesRequested,
    int PalacesSucceeded,
    IReadOnlyList<PalaceRecallSource> Sources,
    IReadOnlyList<PalaceRecallHit> Hits,
    IReadOnlyList<PalaceRecallFailure> Failures);

public sealed record PalaceNeighborSource(
    string PalaceId,
    string PalaceName,
    int Hits);

public sealed record PalaceNeighborHit(
    int FederatedRank,
    int LocalRank,
    string PalaceId,
    string PalaceName,
    Actor Actor,
    double Weight,
    int ObservationCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

public sealed record ColiseumNeighborResult(
    string ActorId,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    string Ranking,
    int PalacesRequested,
    int PalacesSucceeded,
    IReadOnlyList<PalaceNeighborSource> Sources,
    IReadOnlyList<PalaceNeighborHit> Hits,
    IReadOnlyList<PalaceRecallFailure> Failures);

/// <summary>
/// Read-only federation over independently owned palace runtimes. Results are
/// interleaved by local rank because scores produced by different embedding
/// models or palace-local BM25 populations are not globally comparable.
/// </summary>
public sealed class ColiseumRecall
{
    public const string RankingAlgorithm = "local-rank-interleave-v1";

    private readonly PalaceRegistry _registry;

    public ColiseumRecall(PalaceRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public ColiseumRecallResult Search(
        string query,
        IReadOnlyList<string>? palaceSelectors = null,
        int limit = 10,
        int perPalaceLimit = 5,
        string? wing = null,
        string? room = null,
        string? sourceFile = null,
        double maxDistance = 0)
    {
        query = Required(query, nameof(query));
        ValidateLimits(limit, perPalaceLimit);
        if (!double.IsFinite(maxDistance) || maxDistance < 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxDistance),
                "Maximum distance must be finite and non-negative.");

        var entries = SelectPalaces(palaceSelectors);
        var failures = new List<PalaceRecallFailure>();
        var sources = new List<PalaceRecallSource>();
        var candidates = new List<UnrankedRecallHit>();
        foreach (var entry in entries)
        {
            try
            {
                using var runtime = Open(entry);
                var status = runtime.Memory.Status();
                var localHits = runtime.Memory.Search(
                    query,
                    perPalaceLimit,
                    Optional(wing),
                    Optional(room),
                    Optional(sourceFile),
                    maxDistance);
                sources.Add(new(
                    entry.PalaceId,
                    entry.Name,
                    status.RetrievalMode,
                    status.EmbeddingModel,
                    localHits.Count));
                candidates.AddRange(localHits.Select((hit, index) =>
                    new UnrankedRecallHit(entry, index + 1, hit)));
            }
            catch (Exception ex)
            {
                failures.Add(Failure(entry, ex));
            }
        }

        var federatedHits = candidates
            .OrderBy(hit => hit.LocalRank)
            .ThenByDescending(hit => hit.Result.Score)
            .ThenBy(hit => hit.Entry.Name, StringComparer.Ordinal)
            .ThenBy(hit => hit.Result.Drawer.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select((hit, index) => new PalaceRecallHit(
                index + 1,
                hit.LocalRank,
                hit.Entry.PalaceId,
                hit.Entry.Name,
                hit.Result.Drawer,
                hit.Result.Distance,
                hit.Result.Score,
                hit.Result.Bm25Score))
            .ToArray();

        return new(
            query,
            RankingAlgorithm,
            entries.Count,
            sources.Count,
            sources,
            federatedHits,
            failures);
    }

    public ColiseumNeighborResult Neighbors(
        string actorId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyList<string>? palaceSelectors = null,
        int limit = 100,
        int perPalaceLimit = 100,
        double minimumEdgeWeight = 0)
    {
        actorId = Required(actorId, nameof(actorId));
        ValidateLimits(limit, perPalaceLimit);
        if (windowEnd <= windowStart)
            throw new ArgumentException(
                "The graph window end must be after its start.",
                nameof(windowEnd));
        if (!double.IsFinite(minimumEdgeWeight) || minimumEdgeWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumEdgeWeight));

        var entries = SelectPalaces(palaceSelectors);
        var failures = new List<PalaceRecallFailure>();
        var sources = new List<PalaceNeighborSource>();
        var candidates = new List<UnrankedNeighborHit>();
        foreach (var entry in entries)
        {
            try
            {
                using var runtime = Open(entry);
                var localHits = runtime.Graph
                    .Snapshot(windowStart, windowEnd, minimumEdgeWeight)
                    .Neighbors(actorId, minimumEdgeWeight, perPalaceLimit);
                sources.Add(new(entry.PalaceId, entry.Name, localHits.Count));
                candidates.AddRange(localHits.Select((hit, index) =>
                    new UnrankedNeighborHit(entry, index + 1, hit)));
            }
            catch (Exception ex)
            {
                failures.Add(Failure(entry, ex));
            }
        }

        var federatedHits = candidates
            .OrderBy(hit => hit.LocalRank)
            .ThenByDescending(hit => hit.Neighbor.Weight)
            .ThenBy(hit => hit.Entry.Name, StringComparer.Ordinal)
            .ThenBy(hit => hit.Neighbor.Actor.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select((hit, index) => new PalaceNeighborHit(
                index + 1,
                hit.LocalRank,
                hit.Entry.PalaceId,
                hit.Entry.Name,
                hit.Neighbor.Actor,
                hit.Neighbor.Weight,
                hit.Neighbor.ObservationCount,
                hit.Neighbor.FirstSeen,
                hit.Neighbor.LastSeen))
            .ToArray();

        return new(
            actorId,
            windowStart,
            windowEnd,
            RankingAlgorithm,
            entries.Count,
            sources.Count,
            sources,
            federatedHits,
            failures);
    }

    private PalaceRuntime Open(PalaceRegistryEntry entry)
        => _registry.OpenPalace(entry.PalaceId);

    private IReadOnlyList<PalaceRegistryEntry> SelectPalaces(
        IReadOnlyList<string>? selectors)
    {
        var registered = _registry.Snapshot().Palaces;
        if (selectors is null)
            return registered;

        var entries = new List<PalaceRegistryEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selector in selectors)
        {
            var value = Required(selector, nameof(selectors));
            var entry = registered.FirstOrDefault(candidate =>
                            string.Equals(
                                candidate.PalaceId,
                                value,
                                StringComparison.Ordinal))
                        ?? registered.FirstOrDefault(candidate =>
                            string.Equals(
                                candidate.Name,
                                value,
                                StringComparison.Ordinal))
                        ?? throw new ArgumentException(
                            $"Palace '{value}' is not registered in " +
                            $"'{_registry.RegistryPath}'.",
                            nameof(selectors));
            if (ids.Add(entry.PalaceId)) entries.Add(entry);
        }
        return entries;
    }

    private static void ValidateLimits(int limit, int perPalaceLimit)
    {
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "Limit must be between 1 and 1000.");
        if (perPalaceLimit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(perPalaceLimit),
                "Per-palace limit must be between 1 and 100.");
    }

    private static PalaceRecallFailure Failure(
        PalaceRegistryEntry entry,
        Exception exception) =>
        new(
            entry.PalaceId,
            entry.Name,
            exception.GetType().Name,
            exception.Message);

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                "A non-empty value is required.",
                parameterName)
            : value.Trim();

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record UnrankedRecallHit(
        PalaceRegistryEntry Entry,
        int LocalRank,
        MemorySearchResult Result);

    private sealed record UnrankedNeighborHit(
        PalaceRegistryEntry Entry,
        int LocalRank,
        ActorNeighbor Neighbor);
}
