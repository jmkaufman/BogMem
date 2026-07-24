using System.Text.Json;
using BogDb.Core.Main;
using Bogmem.Graph;
using Bogmem.Slices.Embedding;
using Bogmem.Slices.Storage;
using BogQueryResult = BogDb.Core.Main.QueryResult.QueryResult;

namespace Bogmem.Slices.Runtime;

public sealed record PalaceManifest(
    string PalaceId,
    string Name,
    int SchemaVersion,
    string CreatedAt,
    IReadOnlyList<string> Capabilities);

public sealed record PalaceRuntimeStatus(
    string Status,
    string PalaceId,
    string PalaceName,
    int SchemaVersion,
    string DatabasePath,
    string Backend,
    int Drawers,
    int Wings,
    int Rooms,
    string RetrievalMode,
    string EmbeddingModel,
    int Actors,
    int Observations,
    IReadOnlyList<string> Capabilities);

/// <summary>
/// Owns exactly one BogDB database and composes all memory capabilities for one
/// named palace. A Coliseum supervises multiple runtime instances; each MCP
/// service binds to one runtime and therefore one stable palace identity.
/// </summary>
public sealed class PalaceRuntime : IDisposable
{
    public const int CurrentSchemaVersion = 1;
    private const string ManifestTable = "BogMemPalace";
    private static readonly string[] CurrentCapabilities =
    [
        "drawer-recall",
        "actor-graph",
        "graph-traversal",
        "community-detection",
        "mcp",
    ];

    private readonly BogDatabase _database;
    private bool _disposed;

    private PalaceRuntime(
        string databasePath,
        string? requestedName,
        IMemoryEmbedder? embedder)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A palace path is required.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
        _database = BogDatabase.Open(DatabasePath);

        try
        {
            Manifest = EnsureManifest(requestedName);
            Memory = new BogDbMemoryStore(_database, DatabasePath, embedder);
            Graph = new BogDbActorGraphStore(_database, DatabasePath);
        }
        catch
        {
            Graph?.Dispose();
            Memory?.Dispose();
            _database.Dispose();
            throw;
        }
    }

    public string DatabasePath { get; }
    public PalaceManifest Manifest { get; } = null!;
    public BogDbMemoryStore Memory { get; } = null!;
    public BogDbActorGraphStore Graph { get; } = null!;

    public static PalaceRuntime Open(
        string databasePath,
        string? name = null,
        IMemoryEmbedder? embedder = null) =>
        new(databasePath, name, embedder);

    public PalaceRuntimeStatus Status()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var memory = Memory.Status();
        var graph = Graph.Status();
        return new(
            "ok",
            Manifest.PalaceId,
            Manifest.Name,
            Manifest.SchemaVersion,
            DatabasePath,
            memory.Backend,
            memory.Drawers,
            memory.Wings,
            memory.Rooms,
            memory.RetrievalMode,
            memory.EmbeddingModel,
            graph.Actors,
            graph.Observations,
            Manifest.Capabilities);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Graph.Dispose();
        Memory.Dispose();
        _database.Dispose();
        _disposed = true;
    }

    private PalaceManifest EnsureManifest(string? requestedName)
    {
        using var connection = new BogConnection(_database);
        if (!connection.HasTable(ManifestTable))
            QueryOrThrow(
                connection,
                $"CREATE NODE TABLE {ManifestTable}(" +
                "id STRING PRIMARY KEY, name STRING, schema_version INT64, " +
                "created_at STRING, capabilities STRING)");

        var existing = QueryOrThrow(
            connection,
            $"MATCH (p:{ManifestTable}) RETURN " +
            "p.id, p.name, p.schema_version, p.created_at, p.capabilities LIMIT 1");
        if (existing.HasNext())
        {
            var row = existing.GetNext();
            var manifest = new PalaceManifest(
                row.GetString(0) ?? throw new InvalidOperationException("Palace manifest has no ID."),
                row.GetString(1) ?? "palace",
                checked((int)row.GetInt64(2)),
                row.GetString(3) ?? "",
                ParseCapabilities(row.GetString(4)));
            if (manifest.SchemaVersion > CurrentSchemaVersion)
                throw new InvalidOperationException(
                    $"Palace schema {manifest.SchemaVersion} is newer than supported schema {CurrentSchemaVersion}.");
            if (!string.IsNullOrWhiteSpace(requestedName) &&
                !string.Equals(manifest.Name, requestedName.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Palace '{manifest.PalaceId}' is named '{manifest.Name}', not '{requestedName.Trim()}'.");
            if (!manifest.Capabilities.SequenceEqual(CurrentCapabilities, StringComparer.Ordinal))
            {
                connection.UpsertNodeById(
                    ManifestTable,
                    manifest.PalaceId,
                    new Dictionary<string, object>
                    {
                        ["id"] = manifest.PalaceId,
                        ["name"] = manifest.Name,
                        ["schema_version"] = (long)manifest.SchemaVersion,
                        ["created_at"] = manifest.CreatedAt,
                        ["capabilities"] = JsonSerializer.Serialize(CurrentCapabilities),
                    });
                manifest = manifest with { Capabilities = CurrentCapabilities };
            }
            return manifest;
        }

        var name = string.IsNullOrWhiteSpace(requestedName)
            ? DefaultName(DatabasePath)
            : requestedName.Trim();
        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        var id = Guid.CreateVersion7().ToString("N");
        var capabilities = JsonSerializer.Serialize(CurrentCapabilities);
        connection.UpsertNodeById(
            ManifestTable,
            id,
            new Dictionary<string, object>
            {
                ["id"] = id,
                ["name"] = name,
                ["schema_version"] = (long)CurrentSchemaVersion,
                ["created_at"] = createdAt,
                ["capabilities"] = capabilities,
            });
        return new(id, name, CurrentSchemaVersion, createdAt, CurrentCapabilities);
    }

    private static IReadOnlyList<string> ParseCapabilities(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return CurrentCapabilities;
        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? CurrentCapabilities;
        }
        catch (JsonException)
        {
            return CurrentCapabilities;
        }
    }

    private static string DefaultName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "palace" : name;
    }

    private static BogQueryResult QueryOrThrow(
        BogConnection connection,
        string query,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var result = parameters is null
            ? connection.Query(query)
            : connection.Query(query, parameters);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"BogDB palace query failed: {result.ErrorMessage}\n{query}");
        return result;
    }
}
