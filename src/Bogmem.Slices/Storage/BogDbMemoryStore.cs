using System.Collections;
using BogDb.Core.Main;
using Bogmem.Slices.Embedding;
using Bogmem.Slices.Ids;
using Bogmem.Slices.Search;

namespace Bogmem.Slices.Storage;

/// <summary>
/// Persistent BogDB drawer backend. Retrieval is intentionally an exact scan
/// for the first product slice: it stays correct across writes and reopen while
/// BogDB's HNSW lifecycle is hardened. Stored vectors make a later ANN cutover
/// a backend concern rather than a schema migration.
/// </summary>
public sealed class BogDbMemoryStore : IMemoryStore
{
    private const string Table = "Drawer";
    private readonly object _gate = new();
    private readonly BogDatabase _database;
    private readonly BogConnection _connection;
    private readonly LexicalHashEmbedder _embedder = new();
    private bool _disposed;

    public string DatabasePath { get; }

    public BogDbMemoryStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A BogDB palace path is required.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
        _database = BogDatabase.Open(DatabasePath);
        _connection = new BogConnection(_database);
        EnsureSchema();
    }

    public AddDrawerResult Add(string wing, string room, string content, string? sourceFile = null, string addedBy = "mcp")
    {
        wing = Required(wing, nameof(wing));
        room = Required(room, nameof(room));
        content = Required(content, nameof(content), trim: false);
        addedBy = string.IsNullOrWhiteSpace(addedBy) ? "mcp" : addedBy.Trim();
        sourceFile = sourceFile?.Trim() ?? "";
        var id = IdRecipes.MakeDrawerIdFromContent(wing, room, content);

        lock (_gate)
        {
            ThrowIfDisposed();
            var existing = GetCore(id);
            if (existing is not null) return new(existing, Created: false);
            var drawer = new MemoryDrawer(
                id, wing, room, content, sourceFile, addedBy,
                DateTimeOffset.UtcNow.ToString("O"), IdRecipes.IdRecipe, "manual", _embedder.Embed(content));
            Upsert(drawer);
            return new(drawer, Created: true);
        }
    }

    public SourceReplaceResult ReplaceSource(
        string wing,
        string room,
        string sourceFile,
        IReadOnlyList<string> chunks,
        string addedBy = "mempalace",
        bool dryRun = false)
    {
        wing = Required(wing, nameof(wing));
        room = Required(room, nameof(room));
        sourceFile = Required(sourceFile, nameof(sourceFile));
        ArgumentNullException.ThrowIfNull(chunks);
        addedBy = string.IsNullOrWhiteSpace(addedBy) ? "mempalace" : addedBy.Trim();

        var filedAt = DateTimeOffset.UtcNow.ToString("O");
        var expected = chunks.Select((content, chunkIndex) =>
        {
            content = Required(content, $"{nameof(chunks)}[{chunkIndex}]", trim: false);
            return new MemoryDrawer(
                IdRecipes.MakeDrawerIdFromChunk(wing, room, sourceFile, chunkIndex),
                wing,
                room,
                content,
                sourceFile,
                addedBy,
                filedAt,
                IdRecipes.IdRecipe,
                "project",
                _embedder.Embed(content));
        }).ToArray();

        lock (_gate)
        {
            ThrowIfDisposed();
            var current = ReadAll()
                .Where(d => string.Equals(d.SourceFile, sourceFile, StringComparison.Ordinal))
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToArray();
            var expectedById = expected.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            var changed = current.Length != expectedById.Length ||
                          current.Where((drawer, index) => !EquivalentSourceDrawer(drawer, expectedById[index])).Any();
            if (!changed || dryRun)
                return new(sourceFile, current.Length, expected.Length, changed, Applied: false);

            _connection.ExecuteWriteTransaction(() =>
            {
                var deleted = _connection.Query(
                    "MATCH (d:Drawer) WHERE d.source_file = $source DELETE d",
                    new Dictionary<string, object?> { ["source"] = sourceFile });
                if (!deleted.IsSuccess)
                    throw new InvalidOperationException($"BogDB query failed: {deleted.ErrorMessage}");
                foreach (var drawer in expected)
                    _connection.UpsertNodeById(Table, drawer.Id, ToProperties(drawer));
            });
            return new(sourceFile, current.Length, expected.Length, Changed: true, Applied: true);
        }
    }

    public MemoryDrawer? Get(string id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return GetCore(Required(id, nameof(id)));
        }
    }

    public IReadOnlyList<MemoryDrawer> List(string? wing = null, string? room = null, int limit = 20, int offset = 0)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and 100");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        lock (_gate)
        {
            ThrowIfDisposed();
            return ReadAll()
                .Where(d => wing is null || string.Equals(d.Wing, wing, StringComparison.Ordinal))
                .Where(d => room is null || string.Equals(d.Room, room, StringComparison.Ordinal))
                .OrderByDescending(d => d.FiledAt, StringComparer.Ordinal)
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .Skip(offset)
                .Take(limit)
                .ToArray();
        }
    }

    public IReadOnlyList<MemorySearchResult> Search(
        string query, int limit = 5, string? wing = null, string? room = null,
        string? sourceFile = null, double maxDistance = 0)
    {
        query = Required(query, nameof(query));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and 100");

        lock (_gate)
        {
            ThrowIfDisposed();
            var candidates = ReadAll()
                .Where(d => wing is null || string.Equals(d.Wing, wing, StringComparison.Ordinal))
                .Where(d => room is null || string.Equals(d.Room, room, StringComparison.Ordinal))
                .Where(d => sourceFile is null || string.Equals(d.SourceFile, sourceFile.Trim(), StringComparison.Ordinal))
                .ToArray();
            if (candidates.Length == 0) return [];

            var queryVector = _embedder.Embed(query);
            var hits = candidates.Select(d => new Searcher.SearchHit(
                d.Id, d.Content, CosineDistance(queryVector, d.Embedding), d.FiledAt, d.SourceFile,
                new Dictionary<string, object?> { ["wing"] = d.Wing, ["room"] = d.Room })).ToArray();
            var ranked = Searcher.HybridRank(hits, query);
            var byId = candidates.ToDictionary(d => d.Id, StringComparer.Ordinal);
            return ranked
                .Where(h => maxDistance <= 0 || (h.Distance is double distance && distance <= maxDistance))
                .Take(limit)
                .Select(h => new MemorySearchResult(byId[h.Id], h.Distance ?? 1.0, h.HybridScore, h.Bm25Score))
                .ToArray();
        }
    }

    public MemoryDrawer? Update(string id, string? content = null, string? wing = null, string? room = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var current = GetCore(Required(id, nameof(id)));
            if (current is null) return null;
            var nextContent = content is null ? current.Content : Required(content, nameof(content), trim: false);
            var updated = current with
            {
                Wing = wing is null ? current.Wing : Required(wing, nameof(wing)),
                Room = room is null ? current.Room : Required(room, nameof(room)),
                Content = nextContent,
                Embedding = content is null ? current.Embedding : _embedder.Embed(nextContent),
            };
            Upsert(updated);
            return updated;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            id = Required(id, nameof(id));
            if (GetCore(id) is null) return false;
            Execute("MATCH (d:Drawer) WHERE d.id = $id DELETE d", new Dictionary<string, object?> { ["id"] = id });
            return true;
        }
    }

    public int DeleteMany(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var unique = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unique.Length == 0) return 0;

        lock (_gate)
        {
            ThrowIfDisposed();
            var existing = ReadAll().Select(drawer => drawer.Id).ToHashSet(StringComparer.Ordinal);
            var targets = unique.Where(existing.Contains).ToArray();
            if (targets.Length == 0) return 0;
            _connection.ExecuteWriteTransaction(() =>
            {
                foreach (var id in targets)
                {
                    var result = _connection.Query(
                        "MATCH (d:Drawer) WHERE d.id = $id DELETE d",
                        new Dictionary<string, object?> { ["id"] = id });
                    if (!result.IsSuccess)
                        throw new InvalidOperationException($"BogDB query failed: {result.ErrorMessage}");
                }
            });
            return targets.Length;
        }
    }

    public int DeleteBySource(string sourceFile, bool dryRun = true)
    {
        sourceFile = Required(sourceFile, nameof(sourceFile));
        lock (_gate)
        {
            ThrowIfDisposed();
            var count = ReadAll().Count(d => string.Equals(d.SourceFile, sourceFile, StringComparison.Ordinal));
            if (!dryRun && count > 0)
                Execute("MATCH (d:Drawer) WHERE d.source_file = $source DELETE d", new Dictionary<string, object?> { ["source"] = sourceFile });
            return count;
        }
    }

    public MemoryStoreStatus Status()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var drawers = ReadAll();
            return new(
                "bogdb", DatabasePath, drawers.Count,
                drawers.Select(d => d.Wing).Distinct(StringComparer.Ordinal).Count(),
                drawers.Select(d => (d.Wing, d.Room)).Distinct().Count(),
                "exact-hybrid-lexical", LexicalHashEmbedder.Identity);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _connection.Dispose();
            _database.Dispose();
            _disposed = true;
        }
    }

    private void EnsureSchema()
    {
        if (!_connection.HasTable(Table))
        {
            Execute(
                "CREATE NODE TABLE Drawer(" +
                "id STRING PRIMARY KEY, wing STRING, room STRING, content STRING, " +
                "source_file STRING, added_by STRING, filed_at STRING, id_recipe STRING, " +
                "origin STRING, embedding FLOAT[])");
            return;
        }

        var originProbe = _connection.Query("MATCH (d:Drawer) RETURN d.origin LIMIT 0");
        if (originProbe.IsSuccess) return;
        var migration = _connection.Query("ALTER TABLE Drawer ADD origin STRING DEFAULT 'legacy'");
        if (!migration.IsSuccess)
            throw new InvalidOperationException($"BogDB schema migration failed: {migration.ErrorMessage}");
    }

    private void Upsert(MemoryDrawer drawer)
    {
        _connection.Graph().AddNode(Table, drawer.Id, ToProperties(drawer)).Commit();
    }

    private static Dictionary<string, object> ToProperties(MemoryDrawer drawer) => new()
    {
        ["id"] = drawer.Id,
        ["wing"] = drawer.Wing,
        ["room"] = drawer.Room,
        ["content"] = drawer.Content,
        ["source_file"] = drawer.SourceFile,
        ["added_by"] = drawer.AddedBy,
        ["filed_at"] = drawer.FiledAt,
        ["id_recipe"] = drawer.IdRecipe,
        ["origin"] = drawer.Origin,
        ["embedding"] = drawer.Embedding.ToArray(),
    };

    private static bool EquivalentSourceDrawer(MemoryDrawer current, MemoryDrawer expected) =>
        string.Equals(current.Id, expected.Id, StringComparison.Ordinal) &&
        string.Equals(current.Wing, expected.Wing, StringComparison.Ordinal) &&
        string.Equals(current.Room, expected.Room, StringComparison.Ordinal) &&
        string.Equals(current.Content, expected.Content, StringComparison.Ordinal) &&
        string.Equals(current.SourceFile, expected.SourceFile, StringComparison.Ordinal) &&
        string.Equals(current.AddedBy, expected.AddedBy, StringComparison.Ordinal) &&
        string.Equals(current.IdRecipe, expected.IdRecipe, StringComparison.Ordinal) &&
        string.Equals(current.Origin, expected.Origin, StringComparison.Ordinal);

    private MemoryDrawer? GetCore(string id)
    {
        var result = Execute(
            Projection + " WHERE d.id = $id RETURN " + ReturnFields,
            new Dictionary<string, object?> { ["id"] = id });
        return result.FirstOrDefault();
    }

    private List<MemoryDrawer> ReadAll() => Execute(Projection + " RETURN " + ReturnFields);

    private List<MemoryDrawer> Execute(string query, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var result = _connection.Query(query, parameters);
        if (!result.IsSuccess) throw new InvalidOperationException($"BogDB query failed: {result.ErrorMessage}");
        if (!query.TrimStart().StartsWith("MATCH", StringComparison.OrdinalIgnoreCase)) return [];
        return result.Select(row => new MemoryDrawer(
            row.GetString(0) ?? "",
            row.GetString(1) ?? "",
            row.GetString(2) ?? "",
            row.GetString(3) ?? "",
            row.GetString(4) ?? "",
            row.GetString(5) ?? "",
            row.GetString(6) ?? "",
            row.GetString(7) ?? "",
            row.GetString(8) ?? "legacy",
            ToFloatList(row.GetValue(9)))).ToList();
    }

    private const string Projection = "MATCH (d:Drawer)";
    private const string ReturnFields = "d.id, d.wing, d.room, d.content, d.source_file, d.added_by, d.filed_at, d.id_recipe, d.origin, d.embedding";

    private static IReadOnlyList<float> ToFloatList(object? value)
    {
        if (value is not IEnumerable sequence || value is string) return [];
        return sequence.Cast<object?>().Select(Convert.ToSingle).ToArray();
    }

    private static double CosineDistance(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        var count = Math.Min(left.Count, right.Count);
        for (var i = 0; i < count; i++)
        {
            dot += (double)left[i] * right[i];
            leftNorm += (double)left[i] * left[i];
            rightNorm += (double)right[i] * right[i];
        }
        if (leftNorm == 0 || rightNorm == 0) return leftNorm == rightNorm ? 0 : 1;
        return 1 - Math.Clamp(dot / Math.Sqrt(leftNorm * rightNorm), -1, 1);
    }

    private static string Required(string value, string parameterName, bool trim = true)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{parameterName} is required", parameterName);
        return trim ? value.Trim() : value;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
