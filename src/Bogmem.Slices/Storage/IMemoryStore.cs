using System.Text.Json.Serialization;

namespace Bogmem.Slices.Storage;

public sealed record MemoryDrawer(
    string Id,
    string Wing,
    string Room,
    string Content,
    string SourceFile,
    string AddedBy,
    string FiledAt,
    string IdRecipe,
    string Origin,
    [property: JsonIgnore] IReadOnlyList<float> Embedding);

public sealed record AddDrawerResult(MemoryDrawer Drawer, bool Created);

public sealed record SourceReplaceResult(
    string SourceFile,
    int PreviousDrawers,
    int CurrentDrawers,
    bool Changed,
    bool Applied);

public sealed record MemorySearchResult(MemoryDrawer Drawer, double Distance, double Score, double Bm25Score);

public sealed record MemoryStoreStatus(
    string Backend,
    string DatabasePath,
    int Drawers,
    int Wings,
    int Rooms,
    string RetrievalMode,
    string EmbeddingModel);

/// <summary>
/// Product-facing storage boundary for complete drawer records and their
/// lifecycle. Persistence backends can be swapped without leaking a vendor
/// result shape into MCP or the CLI.
/// </summary>
public interface IMemoryStore : IDisposable
{
    AddDrawerResult Add(string wing, string room, string content, string? sourceFile = null, string addedBy = "mcp");
    SourceReplaceResult ReplaceSource(
        string wing,
        string room,
        string sourceFile,
        IReadOnlyList<string> chunks,
        string addedBy = "mempalace",
        bool dryRun = false);
    MemoryDrawer? Get(string id);
    IReadOnlyList<MemoryDrawer> List(string? wing = null, string? room = null, int limit = 20, int offset = 0);
    IReadOnlyList<MemorySearchResult> Search(
        string query,
        int limit = 5,
        string? wing = null,
        string? room = null,
        string? sourceFile = null,
        double maxDistance = 0);
    MemoryDrawer? Update(string id, string? content = null, string? wing = null, string? room = null);
    bool Delete(string id);
    int DeleteMany(IReadOnlyCollection<string> ids);
    int DeleteBySource(string sourceFile, bool dryRun = true);
    MemoryStoreStatus Status();
}
