using System.Collections;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BogDb.Core.Main;
using BogDb.Core.Main.QueryResult;
using BogDb.Extensions.FTS;
using BogDb.Extensions.Vector;
using Bogmem.Slices.Embedding;
using Bogmem.Slices.Ids;
using Bogmem.Slices.Search;

namespace Bogmem.Slices.Storage;

/// <summary>
/// Persistent BogDB drawer backend. BogDB owns the maintained HNSW and BM25
/// indexes; a numeric-key search projection bridges Drawer string IDs to the
/// FTS extension without leaking that implementation detail through IMemoryStore.
/// </summary>
public sealed class BogDbMemoryStore : IMemoryStore
{
    private const string Table = "Drawer";
    private const string SearchTable = "DrawerSearch";
    private const string VectorIndex = "drawer_embedding_hnsw";
    private const string FtsIndex = "drawer_content_fts";
    private const int UnscopedOverfetch = 8;
    private readonly object _gate = new();
    private readonly BogDatabase _database;
    private readonly BogConnection _connection;
    private readonly FtsExtension _ftsExtension;
    private readonly IMemoryEmbedder _embedder;
    private bool _disposed;

    public string DatabasePath { get; }

    public BogDbMemoryStore(string databasePath, IMemoryEmbedder? embedder = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A BogDB palace path is required.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
        _embedder = embedder ?? MemoryEmbedderFactory.CreateDefault();
        _database = BogDatabase.Open(DatabasePath);
        new VectorExtension().Load(_database);
        _ftsExtension = new FtsExtension();
        _ftsExtension.Load(_database);
        _connection = new BogConnection(_database);
        EnsureSchema();
        ReconcileSearchProjection();
        EnsureRetrievalIndexes();
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
        var normalizedChunks = chunks
            .Select((content, chunkIndex) => Required(content, $"{nameof(chunks)}[{chunkIndex}]", trim: false))
            .ToArray();
        var expectedMetadata = normalizedChunks.Select((content, chunkIndex) =>
            new MemoryDrawer(
                IdRecipes.MakeDrawerIdFromChunk(wing, room, sourceFile, chunkIndex),
                wing,
                room,
                content,
                sourceFile,
                addedBy,
                filedAt,
                IdRecipes.IdRecipe,
                "project",
                [])).ToArray();

        lock (_gate)
        {
            ThrowIfDisposed();
            var current = ReadAll()
                .Where(d => string.Equals(d.SourceFile, sourceFile, StringComparison.Ordinal))
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToArray();
            var expectedById = expectedMetadata.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            var changed = current.Length != expectedById.Length ||
                          current.Where((drawer, index) => !EquivalentSourceDrawer(drawer, expectedById[index])).Any();
            if (!changed || dryRun)
                return new(sourceFile, current.Length, expectedMetadata.Length, changed, Applied: false);

            var embeddings = _embedder.EmbedMany(normalizedChunks);
            if (embeddings.Count != normalizedChunks.Length)
                throw new InvalidOperationException(
                    $"The embedder returned {embeddings.Count} vectors for {normalizedChunks.Length} chunks.");
            var expected = expectedMetadata
                .Select((drawer, chunkIndex) => drawer with { Embedding = embeddings[chunkIndex] })
                .ToArray();

            _connection.ExecuteWriteTransaction(() =>
            {
                foreach (var drawer in current)
                    DeleteSearchProjection(drawer.Id);
                QueryOrThrow(
                    "MATCH (d:Drawer) WHERE d.source_file = $source DELETE d",
                    new Dictionary<string, object?> { ["source"] = sourceFile });
                foreach (var drawer in expected)
                    UpsertCore(drawer);
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
            var queryVector = _embedder.Embed(query);
            var isScoped = wing is not null || room is not null || sourceFile is not null;
            var candidateLimit = isScoped
                ? CountDrawers()
                : Math.Max(limit * UnscopedOverfetch, 64);
            if (candidateLimit == 0) return [];

            var distances = QueryVectorIndex(queryVector, candidateLimit);
            var bm25Scores = QueryFtsIndex(query, candidateLimit);
            var candidates = isScoped
                ? ReadScoped(wing, room, sourceFile)
                : distances.Keys
                    .Concat(bm25Scores.Keys)
                    .Distinct(StringComparer.Ordinal)
                    .Select(GetCore)
                    .Where(drawer => drawer is not null)
                    .Select(drawer => drawer!)
                    .ToArray();
            if (candidates.Length == 0) return [];

            var byId = candidates.ToDictionary(d => d.Id, StringComparer.Ordinal);
            var maxBm25 = bm25Scores
                .Where(pair => byId.ContainsKey(pair.Key))
                .Select(pair => pair.Value)
                .DefaultIfEmpty(0)
                .Max();

            return byId.Values
                .Where(drawer => distances.ContainsKey(drawer.Id) || bm25Scores.ContainsKey(drawer.Id))
                .Select(drawer =>
                {
                    var distance = distances.GetValueOrDefault(drawer.Id, 1.0);
                    var bm25 = bm25Scores.GetValueOrDefault(drawer.Id);
                    var normalizedBm25 = maxBm25 > 0 ? bm25 / maxBm25 : 0;
                    var score = Searcher.VectorWeight * Searcher.DistanceToSimilarity(distance) +
                                Searcher.Bm25Weight * normalizedBm25;
                    return new MemorySearchResult(drawer, distance, score, Math.Round(bm25, 3));
                })
                .Where(hit => maxDistance <= 0 || hit.Distance <= maxDistance)
                .OrderByDescending(hit => hit.Score)
                .ThenByDescending(hit => hit.Drawer.FiledAt, StringComparer.Ordinal)
                .ThenBy(hit => hit.Drawer.Id, StringComparer.Ordinal)
                .Take(limit)
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
            _connection.ExecuteWriteTransaction(() =>
            {
                DeleteSearchProjection(id);
                QueryOrThrow("MATCH (d:Drawer) WHERE d.id = $id DELETE d", new Dictionary<string, object?> { ["id"] = id });
            });
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
                    DeleteSearchProjection(id);
                    QueryOrThrow(
                        "MATCH (d:Drawer) WHERE d.id = $id DELETE d",
                        new Dictionary<string, object?> { ["id"] = id });
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
            var targets = ReadAll()
                .Where(d => string.Equals(d.SourceFile, sourceFile, StringComparison.Ordinal))
                .Select(d => d.Id)
                .ToArray();
            var count = targets.Length;
            if (!dryRun && count > 0)
                _connection.ExecuteWriteTransaction(() =>
                {
                    foreach (var id in targets)
                        DeleteSearchProjection(id);
                    QueryOrThrow(
                        "MATCH (d:Drawer) WHERE d.source_file = $source DELETE d",
                        new Dictionary<string, object?> { ["source"] = sourceFile });
                });
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
                "bogdb-hnsw-bm25-hybrid", _embedder.Identity);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _connection.Dispose();
            _database.Dispose();
            _embedder.Dispose();
            _disposed = true;
        }
    }

    private void EnsureSchema()
    {
        if (!_connection.HasTable(Table))
        {
            QueryOrThrow(
                "CREATE NODE TABLE Drawer(" +
                "id STRING PRIMARY KEY, wing STRING, room STRING, content STRING, " +
                "source_file STRING, added_by STRING, filed_at STRING, id_recipe STRING, " +
                "origin STRING, embedding FLOAT[], embedding_model STRING)");
        }
        else
        {
            var originProbe = _connection.Query("MATCH (d:Drawer) RETURN d.origin LIMIT 0");
            if (!originProbe.IsSuccess)
                QueryOrThrow("ALTER TABLE Drawer ADD origin STRING DEFAULT 'legacy'", context: "schema migration");

            var embeddingModelProbe = _connection.Query("MATCH (d:Drawer) RETURN d.embedding_model LIMIT 0");
            if (!embeddingModelProbe.IsSuccess)
                QueryOrThrow(
                    $"ALTER TABLE Drawer ADD embedding_model STRING DEFAULT '{LexicalHashEmbedder.Identity}'",
                    context: "embedding identity migration");
        }

        if (!_connection.HasTable(SearchTable))
            QueryOrThrow(
                "CREATE NODE TABLE DrawerSearch(" +
                "id INT64 PRIMARY KEY, drawer_id STRING, content STRING)");
    }

    private void Upsert(MemoryDrawer drawer)
    {
        _connection.ExecuteWriteTransaction(() => UpsertCore(drawer));
    }

    private void UpsertCore(MemoryDrawer drawer)
    {
        _connection.UpsertNodeById(Table, drawer.Id, ToProperties(drawer));
        UpsertSearchProjection(drawer);
    }

    private void UpsertSearchProjection(MemoryDrawer drawer)
    {
        var searchId = SearchId(drawer.Id);
        _connection.UpsertNodeById(
            SearchTable,
            searchId.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, object>
            {
                ["id"] = searchId,
                ["drawer_id"] = drawer.Id,
                ["content"] = drawer.Content,
            });
    }

    private void DeleteSearchProjection(string drawerId) =>
        QueryOrThrow(
            "MATCH (s:DrawerSearch) WHERE s.drawer_id = $drawerId DELETE s",
            new Dictionary<string, object?> { ["drawerId"] = drawerId });

    private void ReconcileSearchProjection()
    {
        var drawers = ReadAll();
        var collisions = drawers
            .GroupBy(drawer => SearchId(drawer.Id))
            .FirstOrDefault(group => group.Select(drawer => drawer.Id).Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (collisions is not null)
            throw new InvalidOperationException(
                $"Drawer FTS key collision between: {string.Join(", ", collisions.Select(drawer => drawer.Id))}");

        var existing = ReadSearchProjection();
        var identities = ReadEmbeddingIdentities();
        var wantedIds = drawers.Select(drawer => drawer.Id).ToHashSet(StringComparer.Ordinal);
        var normalized = drawers.ToArray();
        var reembedIndexes = drawers
            .Select((drawer, index) => new { Drawer = drawer, Index = index })
            .Where(item =>
                item.Drawer.Embedding.Count != _embedder.Dimensions ||
                !identities.TryGetValue(item.Drawer.Id, out var identity) ||
                !string.Equals(identity, _embedder.Identity, StringComparison.Ordinal))
            .Select(item => item.Index)
            .ToArray();
        var migratedEmbeddings = _embedder.EmbedMany(
            reembedIndexes.Select(index => drawers[index].Content).ToArray());
        if (migratedEmbeddings.Count != reembedIndexes.Length)
            throw new InvalidOperationException(
                $"The embedder returned {migratedEmbeddings.Count} vectors for {reembedIndexes.Length} drawers.");
        for (var index = 0; index < reembedIndexes.Length; index++)
        {
            var drawerIndex = reembedIndexes[index];
            normalized[drawerIndex] = normalized[drawerIndex] with { Embedding = migratedEmbeddings[index] };
        }

        var needsWrite = existing.Keys.Any(id => !wantedIds.Contains(id));
        for (var index = 0; index < normalized.Length && !needsWrite; index++)
        {
            var drawer = normalized[index];
            needsWrite =
                drawer.Embedding.Count != drawers[index].Embedding.Count ||
                !identities.TryGetValue(drawer.Id, out var identity) ||
                !string.Equals(identity, _embedder.Identity, StringComparison.Ordinal) ||
                !existing.TryGetValue(drawer.Id, out var document) ||
                !string.Equals(document.Content, drawer.Content, StringComparison.Ordinal) ||
                document.SearchId != SearchId(drawer.Id);
        }
        if (!needsWrite) return;

        _connection.ExecuteWriteTransaction(() =>
        {
            foreach (var orphan in existing.Keys.Where(id => !wantedIds.Contains(id)))
                DeleteSearchProjection(orphan);
            for (var index = 0; index < normalized.Length; index++)
            {
                var drawer = normalized[index];
                if (drawers[index].Embedding.Count != _embedder.Dimensions ||
                    !identities.TryGetValue(drawer.Id, out var identity) ||
                    !string.Equals(identity, _embedder.Identity, StringComparison.Ordinal))
                    _connection.UpsertNodeById(Table, drawer.Id, ToProperties(drawer));
                UpsertSearchProjection(drawer);
            }
        });
    }

    private void EnsureRetrievalIndexes()
    {
        QueryOrThrow(
            $"CALL create_vector_index('{Table}', '{VectorIndex}', 'embedding', 'cosine', skip_if_exists := true) RETURN *",
            context: "vector index creation");

        if (!_ftsExtension.Indexes.ContainsKey(FtsIndex))
            QueryOrThrow(
                $"CALL CREATE_FTS_INDEX('{SearchTable}', '{FtsIndex}', ['content'], k1 := 1.5, b := 0.75) RETURN *",
                context: "FTS index creation");
    }

    private Dictionary<string, double> QueryVectorIndex(IReadOnlyList<float> embedding, int limit)
    {
        var result = QueryOrThrow(
            $"CALL query_vector_index('{Table}', '{VectorIndex}', $embedding, $limit) RETURN *",
            new Dictionary<string, object?>
            {
                ["embedding"] = embedding.ToArray(),
                ["limit"] = limit,
            },
            "vector search");
        var distances = new Dictionary<string, double>(StringComparer.Ordinal);
        while (result.HasNext())
        {
            var row = result.GetNext();
            var id = row.GetString(1);
            if (!string.IsNullOrEmpty(id))
                distances[id] = row.GetDouble(2);
        }
        return distances;
    }

    private Dictionary<string, double> QueryFtsIndex(string query, int limit)
    {
        var result = QueryOrThrow(
            $"CALL QUERY_FTS_INDEX('{FtsIndex}', $query, $limit) RETURN *",
            new Dictionary<string, object?>
            {
                ["query"] = query,
                ["limit"] = limit,
            },
            "full-text search");
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        while (result.HasNext())
        {
            var row = result.GetNext();
            var drawerId = GetDrawerIdForSearchId(row.GetInt64(0));
            if (!string.IsNullOrEmpty(drawerId))
                scores[drawerId] = row.GetDouble(1);
        }
        return scores;
    }

    private string? GetDrawerIdForSearchId(long searchId)
    {
        var result = QueryOrThrow(
            "MATCH (s:DrawerSearch) WHERE s.id = $searchId RETURN s.drawer_id",
            new Dictionary<string, object?> { ["searchId"] = searchId });
        return result.HasNext() ? result.GetNext().GetString(0) : null;
    }

    private Dictionary<string, SearchProjection> ReadSearchProjection()
    {
        var result = QueryOrThrow("MATCH (s:DrawerSearch) RETURN s.id, s.drawer_id, s.content");
        var documents = new Dictionary<string, SearchProjection>(StringComparer.Ordinal);
        while (result.HasNext())
        {
            var row = result.GetNext();
            var drawerId = row.GetString(1);
            if (!string.IsNullOrEmpty(drawerId))
                documents[drawerId] = new SearchProjection(row.GetInt64(0), row.GetString(2) ?? "");
        }
        return documents;
    }

    private Dictionary<string, string> ReadEmbeddingIdentities()
    {
        var result = QueryOrThrow("MATCH (d:Drawer) RETURN d.id, d.embedding_model");
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        while (result.HasNext())
        {
            var row = result.GetNext();
            var drawerId = row.GetString(0);
            if (!string.IsNullOrEmpty(drawerId))
                identities[drawerId] = row.GetString(1) ?? "";
        }
        return identities;
    }

    private static long SearchId(string drawerId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(drawerId));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(digest) & long.MaxValue;
        return value == 0 ? 1 : (long)value;
    }

    private Dictionary<string, object> ToProperties(MemoryDrawer drawer) => new()
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
        ["embedding_model"] = _embedder.Identity,
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

    private MemoryDrawer[] ReadScoped(string? wing, string? room, string? sourceFile)
    {
        // BogDB 1.3 secondary metadata indexes can retain duplicate/stale hits
        // across delete-then-upsert source replacement. Keep this scoped path
        // on the correct scan until that lifecycle is hardened upstream.
        var predicates = new List<string>();
        var parameters = new Dictionary<string, object?>();
        if (wing is not null)
        {
            predicates.Add("d.wing = $wing");
            parameters["wing"] = wing;
        }
        if (room is not null)
        {
            predicates.Add("d.room = $room");
            parameters["room"] = room;
        }
        if (sourceFile is not null)
        {
            predicates.Add("d.source_file = $sourceFile");
            parameters["sourceFile"] = sourceFile.Trim();
        }

        var where = predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates);
        return Execute(Projection + where + " RETURN " + ReturnFields, parameters)
            .DistinctBy(drawer => drawer.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private int CountDrawers()
    {
        var result = QueryOrThrow("MATCH (d:Drawer) RETURN count(*)");
        if (!result.HasNext()) return 0;
        return (int)Math.Min(result.GetNext().GetInt64(0), int.MaxValue);
    }

    private List<MemoryDrawer> Execute(string query, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var result = QueryOrThrow(query, parameters);
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

    private QueryResult QueryOrThrow(
        string query,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string? context = null)
    {
        var result = _connection.Query(query, parameters);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"BogDB {context ?? "query"} failed: {result.ErrorMessage}");
        return result;
    }

    private const string Projection = "MATCH (d:Drawer)";
    private const string ReturnFields = "d.id, d.wing, d.room, d.content, d.source_file, d.added_by, d.filed_at, d.id_recipe, d.origin, d.embedding";

    private static IReadOnlyList<float> ToFloatList(object? value)
    {
        if (value is not IEnumerable sequence || value is string) return [];
        return sequence.Cast<object?>().Select(Convert.ToSingle).ToArray();
    }

    private static string Required(string value, string parameterName, bool trim = true)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{parameterName} is required", parameterName);
        return trim ? value.Trim() : value;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record SearchProjection(long SearchId, string Content);
}
