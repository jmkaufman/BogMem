using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Slices.Embedding;
using Bogmem.Slices.Runtime;

namespace Bogmem.Slices.Tests.Runtime;

public static class PalaceRegistryTests
{
    public static void Run()
    {
        var root = TestKit.TempDir("palace-registry");
        var registryPath = Path.Combine(root, "coliseum", "registry.json");
        var alphaPath = Path.Combine(root, "palaces", "alpha");
        var betaPath = Path.Combine(root, "palaces", "beta");
        try
        {
            var registry = new PalaceRegistry(registryPath);
            string alphaId;
            string betaId;
            string registeredAt;
            using (var alpha = PalaceRuntime.Open(
                       alphaPath,
                       "alpha",
                       new LexicalHashEmbedder()))
            {
                alphaId = alpha.Manifest.PalaceId;
                var entry = registry.Register(alpha);
                registeredAt = entry.RegisteredAt;
                TestSupport.AssertEqual(alphaId, entry.PalaceId, "registered palace ID");
                TestSupport.AssertEqual(
                    Path.GetFullPath(alphaPath),
                    entry.DatabasePath,
                    "registered absolute path");
            }

            var reopened = new PalaceRegistry(registryPath);
            TestSupport.AssertEqual(
                alphaId,
                reopened.Resolve("alpha").PalaceId,
                "resolve palace by unique name");
            TestSupport.AssertEqual(
                "alpha",
                reopened.Resolve(alphaId).Name,
                "resolve palace by stable ID");
            TestSupport.AssertEqual(
                1,
                reopened.Snapshot().Palaces.Count,
                "registry survives reopen");

            using (var routed = reopened.OpenPalace(
                       "alpha",
                       new LexicalHashEmbedder()))
                TestSupport.AssertEqual(
                    alphaId,
                    routed.Manifest.PalaceId,
                    "routed open verifies identity");

            using (var alpha = PalaceRuntime.Open(
                       alphaPath,
                       embedder: new LexicalHashEmbedder()))
            {
                var refreshed = reopened.Register(alpha);
                TestSupport.AssertEqual(
                    registeredAt,
                    refreshed.RegisteredAt,
                    "re-register preserves original registration time");
            }

            using (var beta = PalaceRuntime.Open(
                       betaPath,
                       "beta",
                       new LexicalHashEmbedder()))
            {
                betaId = beta.Manifest.PalaceId;
                reopened.Register(beta);
            }
            TestSupport.AssertEqual(
                "alpha",
                reopened.Snapshot().Palaces[0].Name,
                "registry ordering is deterministic");

            var duplicateNamePath = Path.Combine(root, "palaces", "duplicate-alpha");
            using (var duplicate = PalaceRuntime.Open(
                       duplicateNamePath,
                       "alpha",
                       new LexicalHashEmbedder()))
                TestSupport.AssertThrows<InvalidOperationException>(
                    () => reopened.Register(duplicate),
                    "registry names must be unique");

            TestSupport.AssertTrue(
                reopened.Unregister("beta", out var removed),
                "unregister by name");
            TestSupport.AssertEqual(betaId, removed!.PalaceId, "unregistered entry");
            TestSupport.AssertTrue(
                !reopened.Unregister("beta", out _),
                "unregister missing palace is a no-op");

            var relocatedPath = Path.Combine(root, "palaces", "alpha-relocated");
            Directory.Move(alphaPath, relocatedPath);
            using (var relocated = PalaceRuntime.Open(
                       relocatedPath,
                       embedder: new LexicalHashEmbedder()))
                reopened.Register(relocated);
            TestSupport.AssertEqual(
                Path.GetFullPath(relocatedPath),
                reopened.Resolve(alphaId).DatabasePath,
                "stable ID can update a relocated path");

            // A router must reject a stale path that now points at another
            // palace instead of silently serving the wrong memory.
            var document = JsonNode.Parse(File.ReadAllText(registryPath))!.AsObject();
            document["palaces"]!.AsArray()[0]!["databasePath"] = Path.GetFullPath(betaPath);
            File.WriteAllText(
                registryPath,
                document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            TestSupport.AssertThrows<InvalidOperationException>(
                () =>
                {
                    using var _ = new PalaceRegistry(registryPath).OpenPalace(
                        alphaId,
                        new LexicalHashEmbedder());
                },
                "routed open rejects identity mismatch");

            TestSupport.AssertTrue(
                !Directory.EnumerateFiles(
                        Path.GetDirectoryName(registryPath)!,
                        "*.tmp")
                    .Any(),
                "atomic registry writes leave no temporary files");

            var futurePath = Path.Combine(root, "future-registry.json");
            File.WriteAllText(futurePath, """{"schemaVersion":999,"palaces":[]}""");
            TestSupport.AssertThrows<InvalidOperationException>(
                () => new PalaceRegistry(futurePath).Snapshot(),
                "future registry schema is rejected");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
