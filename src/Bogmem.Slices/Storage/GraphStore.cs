using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Slices.Common;

namespace Bogmem.Slices.Storage;

public sealed record EmbedderIdentity(string ModelName, int Dimension);

/// <summary>
/// Port of the S6 storage/serialization seams at LEGACY_COMMIT:
///
///  - tunnels.json  (palace_graph._save_tunnels): json.dump(indent=2) with
///    ensure_ascii=True, written to "&lt;file&gt;.tmp" then atomically replaced.
///    Callers hold mine_palace_lock around the write; the writer itself does
///    not lock (matching legacy layering).
///  - hallways.json (hallways save): {"schema_version": 1, "hallways": [...]}
///    with ensure_ascii=False via an mkstemp-style ".hallways-*.tmp" sibling —
///    deliberately NOT locked, replicating the legacy last-writer-wins race.
///  - embedder sidecar (backends/_sidecar): per-collection identity map,
///    ensure_ascii=False, 2-space indent, merge-preserving, never raises.
/// </summary>
public static class GraphStore
{
    public const int HallwaysSchemaVersion = 1;
    public const string EmbedderSidecarFilename = "mempalace_embedder.json";

    // ── tunnels.json ────────────────────────────────────────────────────────

    public static string SerializeTunnels(JsonNode? records) =>
        PyJson.Dumps(records ?? new JsonArray(), indent: 2, ensureAscii: true);

    public static void SaveTunnels(string tunnelFile, JsonNode? records)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(tunnelFile))!;
        Directory.CreateDirectory(parent);
        var tmpPath = tunnelFile + ".tmp";
        WriteAllTextFsync(tmpPath, SerializeTunnels(records));
        File.Move(tmpPath, tunnelFile, overwrite: true);
    }

    // ── hallways.json ───────────────────────────────────────────────────────

    public static string SerializeHallways(JsonNode? records)
    {
        var payload = new JsonObject
        {
            ["schema_version"] = HallwaysSchemaVersion,
            ["hallways"] = records ?? new JsonArray(),
        };
        return PyJson.Dumps(payload, indent: 2, ensureAscii: false);
    }

    public static void SaveHallways(string hallwayFile, JsonNode? records)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(hallwayFile))!;
        Directory.CreateDirectory(directory);
        // tempfile.mkstemp(prefix=".hallways-", suffix=".tmp", dir=directory)
        var tmpPath = Path.Combine(directory, $".hallways-{Guid.NewGuid():N}.tmp");
        try
        {
            WriteAllTextFsync(tmpPath, SerializeHallways(records));
            File.Move(tmpPath, hallwayFile, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch (IOException) { }
            throw;
        }
    }

    // ── embedder sidecar ────────────────────────────────────────────────────

    public static EmbedderIdentity? ReadEmbedderSidecar(string? path, string? collectionName)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(collectionName) || !File.Exists(path))
            return null;
        JsonNode? data;
        try { data = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        if (data is not JsonObject obj) return null;
        if (obj[collectionName] is not JsonObject entry) return null;
        var modelName = entry["model_name"];
        var nameStr = modelName?.GetValueKind() switch
        {
            JsonValueKind.String => modelName!.GetValue<string>(),
            JsonValueKind.Number => modelName!.ToJsonString(),
            _ => null,
        };
        if (string.IsNullOrEmpty(nameStr)) return null; // legacy: falsy model_name → unknown
        int dimension = entry["dimension"] switch
        {
            JsonNode d when d.GetValueKind() == JsonValueKind.Number => (int)d.GetValue<double>(),
            _ => 0,
        };
        return new EmbedderIdentity(nameStr, dimension);
    }

    public static void WriteEmbedderSidecar(string? path, string? collectionName, EmbedderIdentity? identity)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(collectionName)
            || identity is null || string.IsNullOrEmpty(identity.ModelName))
            return;
        JsonObject data = new();
        if (File.Exists(path))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is JsonObject loaded)
                    data = loaded;
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        // Re-assigning an existing key keeps its insertion position (Python dict).
        data[collectionName] = new JsonObject
        {
            ["model_name"] = identity.ModelName,
            ["dimension"] = identity.Dimension,
        };
        try
        {
            File.WriteAllText(path, PyJson.Dumps(data, indent: 2, ensureAscii: false), new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void WriteAllTextFsync(string path, string content)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var bytes = new UTF8Encoding(false).GetBytes(content);
        fs.Write(bytes);
        fs.Flush(flushToDisk: true);
    }
}
