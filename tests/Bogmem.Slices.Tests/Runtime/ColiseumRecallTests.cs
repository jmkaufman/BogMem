using Bogmem.Graph;
using Bogmem.Slices.Embedding;
using Bogmem.Slices.Runtime;

namespace Bogmem.Slices.Tests.Runtime;

public static class ColiseumRecallTests
{
    public static void Run()
    {
        var root = TestKit.TempDir("coliseum-recall");
        var registry = new PalaceRegistry(Path.Combine(root, "registry.json"));
        var alphaPath = Path.Combine(root, "alpha");
        var betaPath = Path.Combine(root, "beta");
        var missingPath = Path.Combine(root, "missing");
        var occurredAt = DateTimeOffset.Parse("2026-07-23T12:15:00Z");
        string alphaId;
        string betaId;
        string missingId;
        try
        {
            using (var alpha = PalaceRuntime.Open(
                       alphaPath,
                       "alpha",
                       new LexicalHashEmbedder()))
            {
                alphaId = alpha.Manifest.PalaceId;
                alpha.Memory.Add(
                    "shared",
                    "decisions",
                    "Authentication tokens use a short-lived signed envelope.");
                alpha.Memory.Add(
                    "shared",
                    "decisions",
                    "Alpha retains audit events for thirty days.");
                alpha.Graph.Observe(new(
                    "alpha:signal:1",
                    occurredAt,
                    ["account-root", "account-alpha"],
                    Weight: 3,
                    Context: "shared-endpoint"));
                registry.Register(alpha);
            }

            using (var beta = PalaceRuntime.Open(
                       betaPath,
                       "beta",
                       new LexicalHashEmbedder()))
            {
                betaId = beta.Manifest.PalaceId;
                // The same logical drawer ID may exist in multiple palaces. It
                // must remain two sourced hits rather than being deduplicated.
                beta.Memory.Add(
                    "shared",
                    "decisions",
                    "Authentication tokens use a short-lived signed envelope.");
                beta.Memory.Add(
                    "shared",
                    "decisions",
                    "Beta rotates signing keys every seven days.");
                beta.Graph.Observe(new(
                    "beta:signal:1",
                    occurredAt,
                    ["account-root", "account-beta"],
                    Weight: 2,
                    Context: "shared-endpoint"));
                registry.Register(beta);
            }

            using (var missing = PalaceRuntime.Open(
                       missingPath,
                       "missing",
                       new LexicalHashEmbedder()))
            {
                missingId = missing.Manifest.PalaceId;
                registry.Register(missing);
            }
            Directory.Delete(missingPath, recursive: true);

            var recall = new ColiseumRecall(registry);
            var result = recall.Search(
                "short-lived authentication token envelope",
                limit: 4,
                perPalaceLimit: 2,
                maxDistance: 2);
            TestSupport.AssertEqual(
                ColiseumRecall.RankingAlgorithm,
                result.Ranking,
                "federated ranking identity");
            TestSupport.AssertEqual(3, result.PalacesRequested, "all registered palaces requested");
            TestSupport.AssertEqual(2, result.PalacesSucceeded, "missing palace is a partial failure");
            TestSupport.AssertEqual(1, result.Failures.Count, "partial failure count");
            TestSupport.AssertEqual(missingId, result.Failures[0].PalaceId, "partial failure provenance");
            TestSupport.AssertEqual(4, result.Hits.Count, "global recall limit");
            TestSupport.AssertEqual(1, result.Hits[0].LocalRank, "first rank tier");
            TestSupport.AssertEqual(1, result.Hits[1].LocalRank, "rank interleaves palaces");
            TestSupport.AssertEqual(
                2,
                result.Hits.Take(2).Select(hit => hit.PalaceId).Distinct().Count(),
                "first tier includes both healthy palaces");
            TestSupport.AssertEqual(
                2,
                result.Hits
                    .Where(hit => hit.Drawer.Content.StartsWith(
                        "Authentication tokens",
                        StringComparison.Ordinal))
                    .Count(),
                "same drawer identity remains sourced per palace");

            var alphaOnly = recall.Search(
                "authentication token",
                ["alpha"],
                limit: 5,
                perPalaceLimit: 5,
                maxDistance: 2);
            TestSupport.AssertEqual(1, alphaOnly.PalacesRequested, "selector scopes federation");
            TestSupport.AssertTrue(
                alphaOnly.Hits.All(hit => hit.PalaceId == alphaId),
                "selected recall does not query another palace");

            var explicitlyEmpty = recall.Search(
                "authentication token",
                []);
            TestSupport.AssertEqual(
                0,
                explicitlyEmpty.PalacesRequested,
                "explicit empty selection queries no palaces");
            TestSupport.AssertEqual(
                0,
                explicitlyEmpty.Hits.Count,
                "explicit empty selection returns no hits");

            TestSupport.AssertThrows<ArgumentException>(
                () => recall.Search("query", ["not-registered"]),
                "unknown palace selector is a request error");

            var neighbors = recall.Neighbors(
                "account-root",
                occurredAt.AddMinutes(-5),
                occurredAt.AddMinutes(5),
                limit: 10);
            TestSupport.AssertEqual(2, neighbors.Hits.Count, "neighbors recalled across healthy palaces");
            TestSupport.AssertTrue(
                neighbors.Hits.Any(hit =>
                    hit.PalaceId == alphaId &&
                    hit.Actor.Id == "account-alpha"),
                "alpha graph provenance");
            TestSupport.AssertTrue(
                neighbors.Hits.Any(hit =>
                    hit.PalaceId == betaId &&
                    hit.Actor.Id == "account-beta"),
                "beta graph provenance");
            TestSupport.AssertTrue(
                neighbors.Hits.All(hit => hit.LocalRank == 1),
                "local graph ranks remain independent");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
