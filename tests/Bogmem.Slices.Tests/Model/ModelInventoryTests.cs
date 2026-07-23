using System.Text;
using System.Text.Json;
using Bogmem.Slices.Embedding;

namespace Bogmem.Slices.Tests.Model;

/// <summary>
/// Replays golden/model (bogmem.golden.model_meta.v1) through ModelInventory:
/// the parsed inventory must agree with the module manifest, and the golden
/// sha256 digests must actually drive cache verification (missing and
/// corrupted cache files are both detected against the golden digests).
/// </summary>
public static class ModelInventoryTests
{
    public static void Run()
    {
        var failures = new List<string>();
        var inventories = new List<ModelInventory>();

        foreach (var (id, input, expected) in TestKit.Vectors("model"))
            inventories.Add(ModelInventory.FromVector(id, input, expected));

        TestSupport.AssertTrue(inventories.Count == 1, $"golden/model corpus drift: {inventories.Count} vectors, expected 1");
        var inv = inventories[0];

        // Inventory content must agree with golden/model/manifest.json.
        using var manifestDoc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestKit.Root, "golden", "model", "manifest.json")));
        var manifest = manifestDoc.RootElement;
        if (manifest.GetProperty("model").GetString() != inv.ModelId)
            failures.Add($"model id {inv.ModelId} != manifest {manifest.GetProperty("model").GetString()}");
        if (manifest.GetProperty("dims").GetInt32() != inv.Dims)
            failures.Add($"dims {inv.Dims} != manifest {manifest.GetProperty("dims").GetInt32()}");
        if (inv.EfName != "default")
            failures.Add($"ef_name {inv.EfName} != default");
        if (inv.Dims != 384)
            failures.Add($"dims {inv.Dims} != 384 (all-MiniLM-L6-v2)");
        // The capture inventories the unpacked onnx/ cache plus the downloaded
        // onnx.tar.gz archive itself.
        foreach (var rel in inv.Files.Keys)
            if (!rel.StartsWith("onnx/", StringComparison.Ordinal) && rel != "onnx.tar.gz")
                failures.Add($"unexpected inventory path shape: {rel}");
        if (!inv.Files.ContainsKey("onnx/model.onnx"))
            failures.Add("inventory is missing onnx/model.onnx — the embedding weights themselves");

        var tempRoot = TestKit.TempDir("model");
        try
        {
            // Empty cache: every golden digest must surface as a missing file.
            var missing = inv.VerifyCacheDir(tempRoot);
            if (missing.Count != inv.Files.Count || missing.Any(c => c.Present || c.Matches))
                failures.Add("empty cache dir did not report every inventoried file as missing");

            // Corrupted cache: bytes that differ from the capture must fail the
            // golden digest comparison, and the reported actual hash must be the
            // real sha256 of what is on disk.
            var firstRel = inv.Files.Keys.First();
            var corrupted = Path.Combine(tempRoot, firstRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(corrupted)!);
            var bytes = Encoding.UTF8.GetBytes("not the pinned model bytes");
            File.WriteAllBytes(corrupted, bytes);
            using var ms = new MemoryStream(bytes);
            var expectedActual = ModelInventory.Sha256Hex(ms);

            var check = inv.VerifyCacheDir(tempRoot).Single(c => c.RelativePath == firstRel);
            if (!check.Present || check.Matches)
                failures.Add($"corrupted {firstRel} not flagged: present={check.Present} matches={check.Matches}");
            if (check.ActualSha256 != expectedActual)
                failures.Add($"hasher disagrees with independent sha256 for {firstRel}");
            if (check.ExpectedSha256 != inv.Files[firstRel])
                failures.Add($"check lost the golden digest for {firstRel}");
            if (inv.IsCacheVerified(tempRoot))
                failures.Add("IsCacheVerified passed a corrupted cache");

            // Matching bytes must verify — proved with a synthetic inventory row
            // shaped exactly like the corpus (the golden digests themselves have
            // no reproducible preimage in this worktree; the model blobs are not
            // vendored).
            var synthDigest = expectedActual;
            using var synthDoc = JsonDocument.Parse(
                $"{{\"input\":{{\"ef_name\":\"default\"}},\"expected\":{{\"dims\":384,\"files\":{{\"{firstRel}\":\"{synthDigest}\"}}}}}}");
            var synth = ModelInventory.FromVector("synthetic", synthDoc.RootElement.GetProperty("input"),
                synthDoc.RootElement.GetProperty("expected"));
            if (!synth.IsCacheVerified(tempRoot))
                failures.Add("matching bytes did not verify against their own sha256");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
        }

        TestSupport.AssertTrue(failures.Count == 0, "model failures:\n" + string.Join("\n", failures));

        TestSupport.WriteModuleLedger("model", "S3",
            ("model_meta_golden_replay", "covered_by_golden", "EXACT", "golden/model inventory parsed; id/dims/ef_name agree with module manifest"),
            ("model_cache_verification", "covered_by_golden", "EXACT", "golden sha256 digests drive missing/corrupt cache detection; hasher cross-checked"),
            ("model_bytes_not_vendored", "not_applicable", "PLACEHOLDER", "the ~90MB ONNX blobs are not vendored; live-cache byte verification runs out-of-band via ModelInventory.IsCacheVerified"));
    }
}
