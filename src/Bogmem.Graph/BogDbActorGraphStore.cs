using BogDb.Core.Main;
using System.Text.Json;
using BogQueryResult = BogDb.Core.Main.QueryResult.QueryResult;

namespace Bogmem.Graph;

/// <summary>
/// Durable temporal actor memory. BogDB stores observations as event nodes and
/// actor participation as graph relationships; snapshots derive weighted
/// undirected co-activity without discarding the original evidence.
/// </summary>
public sealed class BogDbActorGraphStore : IDisposable
{
    private const string ActorTable = "BogMemActor";
    private const string ObservationTable = "BogMemObservation";
    private const string ParticipationTable = "BOGMEM_PARTICIPATED_IN";

    private readonly object _gate = new();
    private readonly BogDatabase _database;
    private readonly BogConnection _connection;
    private readonly int _maxActorsPerObservation;
    private readonly bool _ownsDatabase;
    private bool _disposed;

    /// <summary>Creates or opens a persistent BogDB graph.</summary>
    public BogDbActorGraphStore(
        string databasePath,
        int maxActorsPerObservation = ActorGraphWindow.DefaultMaxActorsPerObservation)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A BogDB path is required.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
        _maxActorsPerObservation = ValidateActorLimit(maxActorsPerObservation);
        _database = BogDatabase.Open(DatabasePath);
        _ownsDatabase = true;
        _connection = new BogConnection(_database);
        EnsureSchema();
    }

    private BogDbActorGraphStore(int maxActorsPerObservation)
    {
        DatabasePath = ":memory:";
        _maxActorsPerObservation = ValidateActorLimit(maxActorsPerObservation);
        _database = BogDatabase.CreateInMemory();
        _ownsDatabase = true;
        _connection = new BogConnection(_database);
        EnsureSchema();
    }

    /// <summary>
    /// Attaches graph memory to a database owned by a wider palace runtime.
    /// Disposing this store closes only its connection.
    /// </summary>
    public BogDbActorGraphStore(
        BogDatabase database,
        string databasePath,
        int maxActorsPerObservation = ActorGraphWindow.DefaultMaxActorsPerObservation)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        DatabasePath = string.IsNullOrWhiteSpace(databasePath) ? ":shared:" : databasePath;
        _maxActorsPerObservation = ValidateActorLimit(maxActorsPerObservation);
        _ownsDatabase = false;
        _connection = new BogConnection(_database);
        EnsureSchema();
    }

    public string DatabasePath { get; }

    /// <summary>Creates a disposable store with no filesystem state.</summary>
    public static BogDbActorGraphStore CreateInMemory(
        int maxActorsPerObservation = ActorGraphWindow.DefaultMaxActorsPerObservation) =>
        new(maxActorsPerObservation);

    public void UpsertActor(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var id = ActorGraphWindow.Required(actor.Id, nameof(actor.Id));
        var kind = string.IsNullOrWhiteSpace(actor.Kind) ? "actor" : actor.Kind.Trim();
        var displayName = string.IsNullOrWhiteSpace(actor.DisplayName) ? "" : actor.DisplayName.Trim();

        lock (_gate)
        {
            ThrowIfDisposed();
            _connection.UpsertNodeById(
                ActorTable,
                id,
                new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["kind"] = kind,
                    ["display_name"] = displayName,
                });
        }
    }

    /// <summary>
    /// Adds an event once. Replaying byte-equivalent normalized content is a
    /// no-op; reusing an ID for different evidence is rejected.
    /// </summary>
    public bool Observe(CoActivityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var normalized = Normalize(observation, _maxActorsPerObservation);
        var fingerprint = ActorGraphWindow.Fingerprint(normalized);

        lock (_gate)
        {
            ThrowIfDisposed();
            var existing = QueryOrThrow(
                $"MATCH (o:{ObservationTable}) WHERE o.id = $id RETURN o.fingerprint",
                new Dictionary<string, object?> { ["id"] = normalized.Id });
            if (existing.HasNext())
            {
                var existingFingerprint = existing.GetNext().GetString(0);
                if (!string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Observation '{normalized.Id}' was replayed with different actors, time, weight, or context.");
                return false;
            }

            _connection.ExecuteWriteTransaction(() =>
            {
                foreach (var actorId in normalized.ActorIds)
                {
                    QueryOrThrow(
                        $"MERGE (a:{ActorTable} {{id:$actorId}}) " +
                        "ON CREATE SET a.kind = 'actor', a.display_name = ''",
                        new Dictionary<string, object?> { ["actorId"] = actorId });
                }

                _connection.UpsertNodeById(
                    ObservationTable,
                    normalized.Id,
                    new Dictionary<string, object>
                    {
                        ["id"] = normalized.Id,
                        ["occurred_at"] = normalized.OccurredAt.ToUniversalTime().ToUnixTimeMilliseconds(),
                        ["weight"] = normalized.Weight,
                        ["context"] = normalized.Context ?? "",
                        ["provenance"] = normalized.Provenance is null
                            ? ""
                            : JsonSerializer.Serialize(normalized.Provenance),
                        ["fingerprint"] = fingerprint,
                    });

                foreach (var actorId in normalized.ActorIds)
                {
                    _connection.UpsertRelationshipById(
                        ParticipationTable,
                        actorId,
                        normalized.Id,
                        []);
                }
            });
            return true;
        }
    }

    public ActorGraphSnapshot Snapshot(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        double minimumEdgeWeight = 0,
        bool includeIsolatedActors = false)
    {
        if (windowEnd <= windowStart)
            throw new ArgumentException("The graph window end must be after its start.", nameof(windowEnd));

        lock (_gate)
        {
            ThrowIfDisposed();
            var window = new ActorGraphWindow(windowStart, windowEnd, _maxActorsPerObservation);
            var actors = ReadActors();
            var participants = ReadObservationParticipants(windowStart, windowEnd);

            foreach (var observation in participants
                         .GroupBy(row => row.ObservationId, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var first = observation.First();
                var actorIds = observation
                    .Select(row => row.ActorId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();
                window.Observe(new(
                    first.ObservationId,
                    DateTimeOffset.FromUnixTimeMilliseconds(first.OccurredAt),
                    actorIds,
                    first.Weight,
                    string.IsNullOrEmpty(first.Context) ? null : first.Context));
            }

            var includedActorIds = participants
                .Select(row => row.ActorId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var actor in actors.Where(actor =>
                         includeIsolatedActors || includedActorIds.Contains(actor.Id)))
                window.RememberActor(actor);

            return window.Snapshot(minimumEdgeWeight);
        }
    }

    public ActorGraphStoreStatus Status()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return new(
                "bogdb",
                DatabasePath,
                Count($"MATCH (a:{ActorTable}) RETURN count(a)"),
                Count($"MATCH (o:{ObservationTable}) RETURN count(o)"));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _connection.Dispose();
            if (_ownsDatabase) _database.Dispose();
            _disposed = true;
        }
    }

    private void EnsureSchema()
    {
        if (!_connection.HasTable(ActorTable))
            QueryOrThrow(
                $"CREATE NODE TABLE {ActorTable}(" +
                "id STRING PRIMARY KEY, kind STRING, display_name STRING)");
        if (!_connection.HasTable(ObservationTable))
            QueryOrThrow(
                $"CREATE NODE TABLE {ObservationTable}(" +
                "id STRING PRIMARY KEY, occurred_at INT64, weight DOUBLE, context STRING, " +
                "provenance STRING, fingerprint STRING)");
        else
        {
            var provenanceProbe = _connection.Query(
                $"MATCH (o:{ObservationTable}) RETURN o.provenance LIMIT 0");
            if (!provenanceProbe.IsSuccess)
                QueryOrThrow(
                    $"ALTER TABLE {ObservationTable} ADD provenance STRING DEFAULT ''");
        }
        if (!_connection.HasTable(ParticipationTable))
            QueryOrThrow(
                $"CREATE REL TABLE {ParticipationTable}(" +
                $"FROM {ActorTable} TO {ObservationTable})");
    }

    private Actor[] ReadActors()
    {
        var result = QueryOrThrow(
            $"MATCH (a:{ActorTable}) RETURN a.id, a.kind, a.display_name ORDER BY a.id");
        var actors = new List<Actor>();
        while (result.HasNext())
        {
            var row = result.GetNext();
            var id = row.GetString(0);
            if (!string.IsNullOrEmpty(id))
                actors.Add(new(
                    id,
                    row.GetString(1) ?? "actor",
                    string.IsNullOrEmpty(row.GetString(2)) ? null : row.GetString(2)));
        }
        return actors.ToArray();
    }

    private ObservationParticipant[] ReadObservationParticipants(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var result = QueryOrThrow(
            $"MATCH (a:{ActorTable})-[:{ParticipationTable}]->(o:{ObservationTable}) " +
            "WHERE o.occurred_at >= $windowStart AND o.occurred_at < $windowEnd " +
            "RETURN o.id, o.occurred_at, o.weight, o.context, a.id " +
            "ORDER BY o.occurred_at, o.id, a.id",
            new Dictionary<string, object?>
            {
                ["windowStart"] = windowStart.ToUniversalTime().ToUnixTimeMilliseconds(),
                ["windowEnd"] = windowEnd.ToUniversalTime().ToUnixTimeMilliseconds(),
            });
        var rows = new List<ObservationParticipant>();
        while (result.HasNext())
        {
            var row = result.GetNext();
            rows.Add(new(
                row.GetString(0) ?? "",
                row.GetInt64(1),
                row.GetDouble(2),
                row.GetString(3),
                row.GetString(4) ?? ""));
        }
        return rows.ToArray();
    }

    private int Count(string query)
    {
        var result = QueryOrThrow(query);
        return result.HasNext()
            ? (int)Math.Min(result.GetNext().GetInt64(0), int.MaxValue)
            : 0;
    }

    private BogQueryResult QueryOrThrow(
        string query,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var result = parameters is null ? _connection.Query(query) : _connection.Query(query, parameters);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"BogDB graph query failed: {result.ErrorMessage}\n{query}");
        return result;
    }

    private static CoActivityObservation Normalize(
        CoActivityObservation observation,
        int maxActorsPerObservation)
    {
        var id = ActorGraphWindow.Required(observation.Id, nameof(observation.Id));
        ArgumentNullException.ThrowIfNull(observation.ActorIds);
        var actorIds = observation.ActorIds
            .Select((actorId, index) =>
                ActorGraphWindow.Required(actorId, $"{nameof(observation.ActorIds)}[{index}]"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(actorId => actorId, StringComparer.Ordinal)
            .ToArray();
        if (actorIds.Length == 0)
            throw new ArgumentException("An observation requires at least one actor.", nameof(observation));
        if (actorIds.Length > maxActorsPerObservation)
            throw new ArgumentException(
                $"Observation '{id}' has {actorIds.Length} actors; the configured limit is {maxActorsPerObservation}.",
                nameof(observation));
        if (!double.IsFinite(observation.Weight) || observation.Weight <= 0)
            throw new ArgumentOutOfRangeException(nameof(observation), "Observation weight must be finite and positive.");

        return observation with
        {
            Id = id,
            ActorIds = actorIds,
            Context = observation.Context?.Trim(),
            Provenance = ActorGraphWindow.Normalize(observation.Provenance),
        };
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static int ValidateActorLimit(int value) =>
        value < 2 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;

    private sealed record ObservationParticipant(
        string ObservationId,
        long OccurredAt,
        double Weight,
        string? Context,
        string ActorId);
}
