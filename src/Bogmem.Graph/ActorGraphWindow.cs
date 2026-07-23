using System.Security.Cryptography;
using System.Text;

namespace Bogmem.Graph;

/// <summary>
/// An idempotent, in-memory co-activity accumulator for one half-open time
/// window. This is the low-latency surface for stream processors such as
/// Undertow; the same observations can also be written to
/// <see cref="BogDbActorGraphStore"/> for durable memory.
/// </summary>
public sealed class ActorGraphWindow
{
    public const int DefaultMaxActorsPerObservation = 1_024;

    private readonly object _gate = new();
    private readonly Dictionary<string, Actor> _actors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _observationFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Target), EdgeAccumulator> _edges = [];

    public ActorGraphWindow(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int maxActorsPerObservation = DefaultMaxActorsPerObservation)
    {
        if (windowEnd <= windowStart)
            throw new ArgumentException("The graph window end must be after its start.", nameof(windowEnd));
        if (maxActorsPerObservation < 2)
            throw new ArgumentOutOfRangeException(nameof(maxActorsPerObservation));
        WindowStart = windowStart;
        WindowEnd = windowEnd;
        MaxActorsPerObservation = maxActorsPerObservation;
    }

    public DateTimeOffset WindowStart { get; }
    public DateTimeOffset WindowEnd { get; }
    public int MaxActorsPerObservation { get; }
    public int ObservationCount
    {
        get
        {
            lock (_gate) return _observationFingerprints.Count;
        }
    }

    public void RememberActor(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalized = Normalize(actor);
        lock (_gate)
        {
            if (_actors.TryGetValue(normalized.Id, out var existing))
            {
                if (existing == normalized) return;
                if (IsPlaceholder(existing))
                {
                    _actors[normalized.Id] = normalized;
                    return;
                }
                if (IsPlaceholder(normalized)) return;
                throw new InvalidOperationException(
                    $"Actor '{normalized.Id}' was already registered with different metadata.");
            }
            _actors[normalized.Id] = normalized;
        }
    }

    /// <returns><see langword="true"/> when the observation was new.</returns>
    public bool Observe(CoActivityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var id = Required(observation.Id, nameof(observation.Id));
        if (observation.OccurredAt < WindowStart || observation.OccurredAt >= WindowEnd)
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                $"Observation '{id}' falls outside [{WindowStart:O}, {WindowEnd:O}).");
        if (!double.IsFinite(observation.Weight) || observation.Weight <= 0)
            throw new ArgumentOutOfRangeException(nameof(observation), "Observation weight must be finite and positive.");
        ArgumentNullException.ThrowIfNull(observation.ActorIds);

        var actorIds = observation.ActorIds
            .Select((actorId, index) => Required(actorId, $"{nameof(observation.ActorIds)}[{index}]"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(actorId => actorId, StringComparer.Ordinal)
            .ToArray();
        if (actorIds.Length == 0)
            throw new ArgumentException("An observation requires at least one actor.", nameof(observation));
        if (actorIds.Length > MaxActorsPerObservation)
            throw new ArgumentException(
                $"Observation '{id}' has {actorIds.Length} actors; the configured limit is {MaxActorsPerObservation}.",
                nameof(observation));

        var normalized = observation with
        {
            Id = id,
            ActorIds = actorIds,
            Context = observation.Context?.Trim(),
        };
        var fingerprint = Fingerprint(normalized);
        lock (_gate)
        {
            if (_observationFingerprints.TryGetValue(id, out var existingFingerprint))
            {
                if (!string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Observation '{id}' was replayed with different actors, time, weight, or context.");
                return false;
            }

            foreach (var actorId in actorIds)
                _actors.TryAdd(actorId, new Actor(actorId));

            for (var left = 0; left < actorIds.Length; left++)
            {
                for (var right = left + 1; right < actorIds.Length; right++)
                {
                    var key = (actorIds[left], actorIds[right]);
                    if (!_edges.TryGetValue(key, out var edge))
                        _edges[key] = edge = new EdgeAccumulator(observation.OccurredAt);
                    edge.Add(observation.Weight, observation.OccurredAt);
                }
            }

            _observationFingerprints.Add(id, fingerprint);
            return true;
        }
    }

    public ActorGraphSnapshot Snapshot(double minimumEdgeWeight = 0)
    {
        if (!double.IsFinite(minimumEdgeWeight) || minimumEdgeWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumEdgeWeight));

        lock (_gate)
        {
            var edges = _edges
                .Where(pair => pair.Value.Weight >= minimumEdgeWeight)
                .Select(pair => new WeightedActorEdge(
                    pair.Key.Source,
                    pair.Key.Target,
                    pair.Value.Weight,
                    pair.Value.Count,
                    pair.Value.FirstSeen,
                    pair.Value.LastSeen))
                .ToArray();
            return new ActorGraphSnapshot(WindowStart, WindowEnd, _actors.Values.ToArray(), edges);
        }
    }

    internal static string Fingerprint(CoActivityObservation observation)
    {
        var payload = string.Join(
            "\n",
            observation.OccurredAt.ToUniversalTime().ToUnixTimeMilliseconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            observation.Weight.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            observation.Context ?? "",
            string.Join("\0", observation.ActorIds));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static Actor Normalize(Actor actor) => actor with
    {
        Id = Required(actor.Id, nameof(actor.Id)),
        Kind = string.IsNullOrWhiteSpace(actor.Kind) ? "actor" : actor.Kind.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : actor.DisplayName.Trim(),
    };

    private static bool IsPlaceholder(Actor actor) =>
        string.Equals(actor.Kind, "actor", StringComparison.Ordinal) &&
        actor.DisplayName is null;

    internal static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private sealed class EdgeAccumulator(DateTimeOffset occurredAt)
    {
        public double Weight { get; private set; }
        public int Count { get; private set; }
        public DateTimeOffset FirstSeen { get; private set; } = occurredAt;
        public DateTimeOffset LastSeen { get; private set; } = occurredAt;

        public void Add(double weight, DateTimeOffset occurredAt)
        {
            Weight += weight;
            Count++;
            if (occurredAt < FirstSeen) FirstSeen = occurredAt;
            if (occurredAt > LastSeen) LastSeen = occurredAt;
        }
    }
}
