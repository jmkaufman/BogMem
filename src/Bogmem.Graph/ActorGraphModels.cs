namespace Bogmem.Graph;

/// <summary>A stable participant in a graph-memory observation.</summary>
public sealed record Actor(
    string Id,
    string Kind = "actor",
    string? DisplayName = null);

/// <summary>Source lineage carried with a normalized observation.</summary>
public sealed record ObservationProvenance(
    string? Source = null,
    string? WorkflowId = null,
    string? RunId = null,
    string? ArtifactId = null,
    string? SignalType = null);

/// <summary>
/// One sourced co-activity event. A single event may involve any number of
/// actors; a graph window projects every distinct actor pair into a weighted
/// undirected edge.
/// </summary>
public sealed record CoActivityObservation(
    string Id,
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> ActorIds,
    double Weight = 1,
    string? Context = null,
    ObservationProvenance? Provenance = null);

/// <summary>An undirected edge aggregated from one or more observations.</summary>
public sealed record WeightedActorEdge(
    string SourceId,
    string TargetId,
    double Weight,
    int ObservationCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>A neighboring actor and the evidence accumulated on that edge.</summary>
public sealed record ActorNeighbor(
    Actor Actor,
    double Weight,
    int ObservationCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>An actor reached by a weighted traversal from a starting actor.</summary>
public sealed record ActorReach(
    Actor Actor,
    int Hops,
    double PathStrength);

public sealed record ActorGraphStoreStatus(
    string Backend,
    string DatabasePath,
    int Actors,
    int Observations);

/// <summary>An immutable weighted graph for a half-open time window.</summary>
public sealed class ActorGraphSnapshot
{
    private readonly IReadOnlyDictionary<string, Actor> _actorsById;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ActorNeighbor>> _neighbors;

    public ActorGraphSnapshot(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyList<Actor> actors,
        IReadOnlyList<WeightedActorEdge> edges)
    {
        if (windowEnd <= windowStart)
            throw new ArgumentException("The graph window end must be after its start.", nameof(windowEnd));

        WindowStart = windowStart;
        WindowEnd = windowEnd;
        Actors = actors.OrderBy(actor => actor.Id, StringComparer.Ordinal).ToArray();
        Edges = edges
            .OrderByDescending(edge => edge.Weight)
            .ThenBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToArray();
        _actorsById = Actors.ToDictionary(actor => actor.Id, StringComparer.Ordinal);

        var neighbors = Actors.ToDictionary(
            actor => actor.Id,
            _ => new List<ActorNeighbor>(),
            StringComparer.Ordinal);
        foreach (var edge in Edges)
        {
            if (!_actorsById.TryGetValue(edge.SourceId, out var source) ||
                !_actorsById.TryGetValue(edge.TargetId, out var target))
                throw new ArgumentException("Every edge endpoint must exist in the actor set.", nameof(edges));

            neighbors[source.Id].Add(new(
                target, edge.Weight, edge.ObservationCount, edge.FirstSeen, edge.LastSeen));
            neighbors[target.Id].Add(new(
                source, edge.Weight, edge.ObservationCount, edge.FirstSeen, edge.LastSeen));
        }

        _neighbors = neighbors.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ActorNeighbor>)pair.Value
                .OrderByDescending(neighbor => neighbor.Weight)
                .ThenBy(neighbor => neighbor.Actor.Id, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    public DateTimeOffset WindowStart { get; }
    public DateTimeOffset WindowEnd { get; }
    public IReadOnlyList<Actor> Actors { get; }
    public IReadOnlyList<WeightedActorEdge> Edges { get; }
    public double TotalEdgeWeight => Edges.Sum(edge => edge.Weight);

    public IReadOnlyList<ActorNeighbor> Neighbors(
        string actorId,
        double minimumWeight = 0,
        int limit = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("An actor ID is required.", nameof(actorId));
        if (!double.IsFinite(minimumWeight) || minimumWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumWeight));
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));

        return !_neighbors.TryGetValue(actorId.Trim(), out var neighbors)
            ? []
            : neighbors.Where(neighbor => neighbor.Weight >= minimumWeight).Take(limit).ToArray();
    }

    /// <summary>
    /// Returns actors within <paramref name="maxHops"/>. Path strength is the
    /// largest bottleneck weight among shortest paths to that actor.
    /// </summary>
    public IReadOnlyList<ActorReach> Traverse(
        string actorId,
        int maxHops = 2,
        double minimumEdgeWeight = 0,
        int limit = 100)
    {
        actorId = string.IsNullOrWhiteSpace(actorId)
            ? throw new ArgumentException("An actor ID is required.", nameof(actorId))
            : actorId.Trim();
        if (!_actorsById.ContainsKey(actorId)) return [];
        if (maxHops < 1) throw new ArgumentOutOfRangeException(nameof(maxHops));
        if (!double.IsFinite(minimumEdgeWeight) || minimumEdgeWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumEdgeWeight));
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));

        var visited = new HashSet<string>([actorId], StringComparer.Ordinal);
        var frontier = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [actorId] = double.PositiveInfinity,
        };
        var reached = new List<ActorReach>();

        for (var hops = 1; hops <= maxHops && frontier.Count > 0; hops++)
        {
            var next = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (currentId, currentStrength) in frontier)
            {
                foreach (var neighbor in _neighbors[currentId]
                             .Where(neighbor => neighbor.Weight >= minimumEdgeWeight))
                {
                    if (visited.Contains(neighbor.Actor.Id)) continue;
                    var candidateStrength = Math.Min(currentStrength, neighbor.Weight);
                    if (candidateStrength > next.GetValueOrDefault(neighbor.Actor.Id))
                        next[neighbor.Actor.Id] = candidateStrength;
                }
            }

            foreach (var (reachedId, strength) in next)
            {
                visited.Add(reachedId);
                reached.Add(new(_actorsById[reachedId], hops, strength));
            }
            frontier = next;
        }

        return reached
            .OrderBy(reach => reach.Hops)
            .ThenByDescending(reach => reach.PathStrength)
            .ThenBy(reach => reach.Actor.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }
}
