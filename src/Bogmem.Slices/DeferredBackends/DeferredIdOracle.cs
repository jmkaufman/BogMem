using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bogmem.Slices.DeferredBackends;

/// <summary>
/// Deferred-backend expected-ID oracle for qdrant/milvus/pgvector.
/// Emits UUID5 (RFC-4122-v5, NAMESPACE_URL) IDs for staging under testdata/deferred_ids/
/// (vendored golden/ is read-only). No comparison runs this pass — oracle staging only (S13).
/// </summary>
public static class DeferredIdOracle
{
    /// <summary>RFC-4122 UUID namespace URL (6ba7b811-9dad-11d1-80b4-00c04fd430c8).</summary>
    public static readonly Guid NamespaceUrl = Guid.Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

    public sealed record OracleRow(string Name, string Backend, string Input, string Id, string Notes);

    public static Guid Uuid5(Guid ns, string name)
    {
        var nsBytes = ns.ToByteArray();
        SwapUuidEndianness(nsBytes);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[nsBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(nsBytes, 0, data, 0, nsBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, data, nsBytes.Length, nameBytes.Length);
        var hash = SHA1.HashData(data);
        var bytes = new byte[16];
        Array.Copy(hash, 0, bytes, 0, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant RFC 4122
        SwapUuidEndianness(bytes);
        return new Guid(bytes);
    }

    private static void SwapUuidEndianness(byte[] guid)
    {
        void Swap(int a, int b) => (guid[a], guid[b]) = (guid[b], guid[a]);
        Swap(0, 3); Swap(1, 2);
        Swap(4, 5); Swap(6, 7);
    }

    public static string ExpectedId(string backend, string input) =>
        Uuid5(NamespaceUrl, input).ToString();

    public static IReadOnlyList<OracleRow> DefaultOracleRows() =>
    [
        new("qdrant_uuid5", "qdrant", "drawer:/path/a1:0",
            ExpectedId("qdrant", "drawer:/path/a1:0"),
            "RFC-4122-v5 uuid; deferred from S1 comparison"),
        new("milvus_id", "milvus", "milvus:doc1",
            ExpectedId("milvus", "milvus:doc1"),
            "surrogatepass hasher deferred; uuid5 oracle placeholder"),
        new("pgvector_id", "pgvector", "pg:doc2",
            ExpectedId("pgvector", "pg:doc2"),
            "surrogatepass hasher deferred; uuid5 oracle placeholder"),
    ];

    public static void EmitOracleJsonl(string path, IEnumerable<OracleRow>? rows = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        rows ??= DefaultOracleRows();
        using var w = new StreamWriter(path);
        foreach (var r in rows)
        {
            w.WriteLine(JsonSerializer.Serialize(new
            {
                id = r.Name,
                input = new { backend = r.Backend, value = r.Input },
                expected = new { id = r.Id, notes = r.Notes },
            }));
        }
    }
}
