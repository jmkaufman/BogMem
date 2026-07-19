using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Harness;
using Bogmem.Slices.Storage;

namespace Bogmem.Slices.Tests.Storage;

/// <summary>S6 — tunnels/hallways/sidecar byte-exact serialization + sqlite_exact row-set and scoring.</summary>
public static class GraphStoreTests
{
    public static void Run(TestDispositionLedgerWriter ledger)
    {
        var result = new SuiteResult("storage");
        RunGraphFiles(result, ledger);
        RunSqliteExact(result, ledger);
        result.ThrowIfFailed();
    }

    private static void RunGraphFiles(SuiteResult result, TestDispositionLedgerWriter ledger)
    {
        foreach (var (id, input, expected) in TestKit.Vectors("graph_files"))
        {
            var kind = input.GetProperty("kind").GetString();
            switch (kind)
            {
                case "tunnels":
                {
                    var records = JsonNode.Parse(input.GetProperty("records").GetRawText());
                    var content = GraphStore.SerializeTunnels(records);
                    bool ok = content == expected.GetProperty("content").GetString();
                    // Also exercise the atomic write path: final bytes exact, tmp removed.
                    var dir = TestKit.TempDir("tunnels");
                    try
                    {
                        var file = Path.Combine(dir, "tunnels.json");
                        GraphStore.SaveTunnels(file, JsonNode.Parse(input.GetProperty("records").GetRawText()));
                        ok = ok && File.ReadAllText(file, Encoding.UTF8) == expected.GetProperty("content").GetString()
                                && !File.Exists(file + ".tmp");
                    }
                    finally { Directory.Delete(dir, recursive: true); }
                    result.Check(id, ok, "tunnels.json content mismatch");
                    ledger.Append(id, "graph_files", ok ? "converted" : "failed", "EXACT", slice: "S6");
                    break;
                }
                case "hallways":
                {
                    var records = JsonNode.Parse(input.GetProperty("records").GetRawText());
                    var content = GraphStore.SerializeHallways(records);
                    bool ok = content == expected.GetProperty("content").GetString();
                    var dir = TestKit.TempDir("hallways");
                    try
                    {
                        var file = Path.Combine(dir, "hallways.json");
                        GraphStore.SaveHallways(file, JsonNode.Parse(input.GetProperty("records").GetRawText()));
                        ok = ok && File.ReadAllText(file, Encoding.UTF8) == expected.GetProperty("content").GetString()
                                && Directory.GetFiles(dir, ".hallways-*.tmp").Length == 0;
                    }
                    finally { Directory.Delete(dir, recursive: true); }
                    result.Check(id, ok, "hallways.json content mismatch");
                    ledger.Append(id, "graph_files", ok ? "converted" : "failed", "EXACT", slice: "S6");
                    break;
                }
                case "sidecar":
                {
                    bool ok = RunSidecar(input, expected, out string detail);
                    result.Check(id, ok, detail);
                    ledger.Append(id, "graph_files", ok ? "converted" : "failed", "EXACT", slice: "S6");
                    break;
                }
                default:
                    result.Fail(id, $"unknown fixture kind {kind}");
                    break;
            }
        }
    }

    private static bool RunSidecar(JsonElement input, JsonElement expected, out string detail)
    {
        var dir = TestKit.TempDir("sidecar");
        try
        {
            var path = Path.Combine(dir, GraphStore.EmbedderSidecarFilename);
            if (input.TryGetProperty("preexisting", out var pre) && pre.ValueKind == JsonValueKind.String)
                File.WriteAllText(path, pre.GetString()!, new UTF8Encoding(false));

            var reads = new List<EmbedderIdentity?>();
            foreach (var op in input.GetProperty("ops").EnumerateArray())
            {
                var verb = op[0].GetString();
                var collection = op[1].GetString();
                if (verb == "write")
                    GraphStore.WriteEmbedderSidecar(path, collection,
                        new EmbedderIdentity(op[2].GetString()!, op[3].GetInt32()));
                else
                    reads.Add(GraphStore.ReadEmbedderSidecar(path, collection));
            }

            var expReads = expected.GetProperty("reads");
            if (reads.Count != expReads.GetArrayLength()) { detail = "read count mismatch"; return false; }
            int i = 0;
            foreach (var e in expReads.EnumerateArray())
            {
                var got = reads[i++];
                if (e.ValueKind == JsonValueKind.Null)
                {
                    if (got is not null) { detail = $"read {i - 1}: expected null, got {got}"; return false; }
                }
                else if (got is null
                    || got.ModelName != e.GetProperty("model_name").GetString()
                    || got.Dimension != e.GetProperty("dimension").GetInt32())
                {
                    detail = $"read {i - 1} mismatch: got {got}";
                    return false;
                }
            }

            var expContent = expected.GetProperty("content");
            if (expContent.ValueKind == JsonValueKind.Null)
            {
                if (File.Exists(path)) { detail = "sidecar file should not exist"; return false; }
            }
            else
            {
                var actual = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
                if (actual != expContent.GetString()) { detail = $"content mismatch: {actual}"; return false; }
            }
            detail = "";
            return true;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void RunSqliteExact(SuiteResult result, TestDispositionLedgerWriter ledger)
    {
        var store = new SqliteExactStore();
        foreach (var (id, input, expected) in TestKit.Vectors("sqlite_exact"))
        {
            if (id == "rowset")
            {
                foreach (var doc in input.GetProperty("docs").EnumerateArray())
                    UpsertFrom(store, doc);
                UpsertFrom(store, input.GetProperty("upsert"));

                var rows = store.RowSet();
                var exp = expected.GetProperty("rows");
                bool ok = rows.Count == exp.GetArrayLength();
                int i = 0;
                foreach (var e in exp.EnumerateArray())
                {
                    if (!ok) break;
                    var r = rows[i++];
                    ok = r.Id == e.GetProperty("id").GetString()
                        && r.Document == e.GetProperty("document").GetString()
                        && r.MetadataJson == e.GetProperty("metadata_json").GetString()
                        && r.Dim == e.GetProperty("dim").GetInt32()
                        && Convert.ToHexString(r.Embedding) == e.GetProperty("embedding_hex").GetString();
                }
                result.Check(id, ok, "row-set mismatch");
                ledger.Append(id, "sqlite_exact", ok ? "converted" : "failed", "EXACT", slice: "S6");
                continue;
            }

            // Query vector.
            var embedding = input.GetProperty("embedding").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            int nResults = input.GetProperty("n_results").GetInt32();
            JsonObject? where = input.TryGetProperty("where", out var w) && w.ValueKind == JsonValueKind.Object
                ? (JsonObject)JsonNode.Parse(w.GetRawText())! : null;
            var hits = store.Query(embedding, nResults, where);

            var expIds = expected.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var expDocs = expected.GetProperty("documents").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var expDists = expected.GetProperty("distances").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var expMetas = expected.GetProperty("metadatas").EnumerateArray().ToArray();
            var expEmbeds = expected.GetProperty("embeddings").EnumerateArray()
                .Select(a => a.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToArray();

            bool qok = hits.Count == expIds.Length;
            for (int i = 0; qok && i < hits.Count; i++)
            {
                qok = hits[i].Id == expIds[i]
                    && hits[i].Document == expDocs[i]
                    && TestKit.DoubleBitsEqual(hits[i].Distance, expDists[i])
                    && SqliteExactStore.MetadataJson(hits[i].Metadata)
                        == SqliteExactStore.MetadataJson((JsonObject)JsonNode.Parse(expMetas[i].GetRawText())!)
                    && hits[i].Embedding.Length == expEmbeds[i].Length
                    && hits[i].Embedding.Zip(expEmbeds[i]).All(p => TestKit.DoubleBitsEqual(p.First, p.Second));
                if (!qok)
                    result.Fail(id, $"hit {i}: id={hits[i].Id} dist={hits[i].Distance:R} expected id={expIds[i]} dist={expDists[i]:R}");
            }
            if (qok) result.Pass();
            else if (hits.Count != expIds.Length) result.Fail(id, $"hit count {hits.Count} != {expIds.Length}");
            ledger.Append(id, "sqlite_exact", qok ? "converted" : "failed", "EXACT", slice: "S6");
        }
    }

    private static void UpsertFrom(SqliteExactStore store, JsonElement doc)
    {
        var id = doc[0].GetString()!;
        var document = doc[1].GetString()!;
        var metadata = (JsonObject)JsonNode.Parse(doc[2].GetRawText())!;
        var embedding = doc[3].EnumerateArray().Select(x => x.GetDouble()).ToArray();
        store.Upsert(id, document, metadata, embedding);
    }
}
