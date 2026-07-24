using Bogmem.Graph;
using Bogmem.Slices.Embedding;
using Bogmem.Slices.Runtime;

namespace Bogmem.Slices.Tests.Runtime;

public static class PalaceRuntimeTests
{
    public static void Run()
    {
        var root = TestKit.TempDir("palace-runtime");
        var path = Path.Combine(root, "social-signals-palace");
        var occurredAt = DateTimeOffset.Parse("2026-07-23T12:00:00Z");
        string palaceId;
        try
        {
            using (var runtime = PalaceRuntime.Open(
                       path,
                       "social-signals",
                       new LexicalHashEmbedder()))
            {
                palaceId = runtime.Manifest.PalaceId;
                TestSupport.AssertTrue(!string.IsNullOrWhiteSpace(palaceId), "stable palace ID");
                TestSupport.AssertEqual("social-signals", runtime.Manifest.Name, "palace name");
                runtime.Memory.Add("social-signals", "signals", "Coordinated activity signal.");
                runtime.Graph.Observe(new(
                    "signal:1",
                    occurredAt,
                    ["account-a", "account-b"],
                    2,
                    "shared-endpoint",
                    new ObservationProvenance(
                        Source: "ftt",
                        WorkflowId: "social-signal-ingest",
                        RunId: "run-42",
                        ArtifactId: "artifact-7",
                        SignalType: "co-activity")));

                var status = runtime.Status();
                TestSupport.AssertEqual(1, status.Drawers, "composed drawer count");
                TestSupport.AssertEqual(2, status.Actors, "composed actor count");
                TestSupport.AssertEqual(1, status.Observations, "composed observation count");
                TestSupport.AssertTrue(
                    status.Capabilities.Contains("actor-graph", StringComparer.Ordinal),
                    "graph capability");
            }

            using (var reopened = PalaceRuntime.Open(path, embedder: new LexicalHashEmbedder()))
            {
                TestSupport.AssertEqual(palaceId, reopened.Manifest.PalaceId, "manifest ID survives reopen");
                TestSupport.AssertEqual("social-signals", reopened.Manifest.Name, "manifest name survives reopen");
                TestSupport.AssertEqual(1, reopened.Status().Drawers, "drawer survives runtime reopen");
                TestSupport.AssertEqual(1, reopened.Status().Observations, "observation survives runtime reopen");
                TestSupport.AssertTrue(
                    !reopened.Graph.Observe(new(
                        "signal:1",
                        occurredAt,
                        ["account-b", "account-a"],
                        2,
                        "shared-endpoint",
                        new ObservationProvenance(
                            Source: "ftt",
                            WorkflowId: "social-signal-ingest",
                            RunId: "run-42",
                            ArtifactId: "artifact-7",
                            SignalType: "co-activity"))),
                    "provenance-bearing replay is idempotent");
            }

            TestSupport.AssertThrows<InvalidOperationException>(
                () =>
                {
                    using var _ = PalaceRuntime.Open(
                        path,
                        "wrong-palace",
                        new LexicalHashEmbedder());
                },
                "existing palace name is immutable");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
