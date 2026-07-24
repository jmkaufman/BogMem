using Bogmem.Graph;

namespace Bogmem.Slices.Tests.GraphMemory;

public static class ActorGraphTests
{
    public static void Run()
    {
        WindowAggregatesWeightedEvidenceIdempotently();
        WindowSupportsConcurrentProducersAndBoundsFanOut();
        BogDbRoundTripPreservesTemporalEvidence();
        LeidenSeparatesDenseConnectedCommunities();
    }

    private static void WindowSupportsConcurrentProducersAndBoundsFanOut()
    {
        var start = DateTimeOffset.Parse("2026-07-23T00:00:00Z");
        var window = new ActorGraphWindow(start, start.AddHours(1));
        Parallel.For(0, 100, index =>
            window.Observe(new(
                $"concurrent-{index}",
                start.AddSeconds(index),
                ["alice", "bob"])));

        var edge = window.Snapshot().Edges.Single();
        TestSupport.AssertEqual(100, window.ObservationCount, "concurrent observation count");
        TestSupport.AssertEqual(100, edge.ObservationCount, "concurrent edge evidence count");
        TestSupport.AssertEqual(100d, edge.Weight, "concurrent edge weight");

        var bounded = new ActorGraphWindow(start, start.AddHours(1), maxActorsPerObservation: 3);
        TestSupport.AssertThrows<ArgumentException>(
            () => bounded.Observe(new(
                "too-wide",
                start.AddMinutes(1),
                ["one", "two", "three", "four"])),
            "actor fan-out limit");
    }

    private static void WindowAggregatesWeightedEvidenceIdempotently()
    {
        var start = DateTimeOffset.Parse("2026-07-23T00:00:00Z");
        var window = new ActorGraphWindow(start, start.AddHours(1));
        window.RememberActor(new("alice", "account", "Alice"));

        var first = new CoActivityObservation(
            "event-1", start.AddMinutes(5), ["alice", "bob", "carol"], 2, "shared-endpoint");
        TestSupport.AssertTrue(window.Observe(first), "first observation should be accepted");
        TestSupport.AssertTrue(!window.Observe(first), "identical replay should be a no-op");
        window.Observe(new("event-2", start.AddMinutes(10), ["bob", "alice"], 0.5));

        var snapshot = window.Snapshot();
        TestSupport.AssertEqual(3, snapshot.Actors.Count, "actor count");
        TestSupport.AssertEqual(3, snapshot.Edges.Count, "three pair projection");
        var aliceBob = snapshot.Edges.Single(edge =>
            edge.SourceId == "alice" && edge.TargetId == "bob");
        TestSupport.AssertEqual(2.5, aliceBob.Weight, "aggregated edge weight");
        TestSupport.AssertEqual(2, aliceBob.ObservationCount, "edge evidence count");
        TestSupport.AssertEqual("Alice", snapshot.Neighbors("bob").First().Actor.DisplayName, "metadata preserved");
        var reach = snapshot.Traverse("carol", maxHops: 2);
        TestSupport.AssertEqual(2, reach.Count, "two-hop traversal count");
        TestSupport.AssertEqual(1, reach.Single(item => item.Actor.Id == "alice").Hops, "direct traversal hop");
        TestSupport.AssertEqual(2d, reach.Single(item => item.Actor.Id == "alice").PathStrength, "direct traversal strength");

        TestSupport.AssertThrows<InvalidOperationException>(
            () => window.Observe(first with { Weight = 3 }),
            "conflicting observation replay");
        TestSupport.AssertThrows<ArgumentOutOfRangeException>(
            () => window.Observe(new("late", start.AddHours(1), ["alice", "bob"])),
            "half-open window");
    }

    private static void BogDbRoundTripPreservesTemporalEvidence()
    {
        var root = TestKit.TempDir("actor-graph");
        var path = Path.Combine(root, "graph");
        var start = DateTimeOffset.Parse("2026-07-23T00:00:00Z");
        try
        {
            using (var store = new BogDbActorGraphStore(path))
            {
                store.UpsertActor(new("acct-a", "account", "Account A"));
                TestSupport.AssertTrue(
                    store.Observe(new("one", start.AddMinutes(1), ["acct-a", "acct-b"], 1.25, "transfer")),
                    "durable observation insert");
                TestSupport.AssertTrue(
                    !store.Observe(new("one", start.AddMinutes(1), ["acct-b", "acct-a"], 1.25, "transfer")),
                    "normalized durable replay");

                var liveFirstHour = store.Snapshot(start, start.AddHours(1));
                TestSupport.AssertEqual(2, liveFirstHour.Actors.Count, "live participant count");
                TestSupport.AssertEqual(1, liveFirstHour.Edges.Count, "live edge count");

                store.Observe(new("later", start.AddHours(2), ["acct-a", "acct-c"], 4));
            }

            using var reopened = new BogDbActorGraphStore(path);
            var firstHour = reopened.Snapshot(start, start.AddHours(1));
            TestSupport.AssertEqual(2, firstHour.Actors.Count, "windowed participant count");
            TestSupport.AssertEqual(1, firstHour.Edges.Count, "windowed edge count");
            TestSupport.AssertEqual(1.25, firstHour.Edges[0].Weight, "durable edge weight");
            TestSupport.AssertEqual("Account A", firstHour.Actors.Single(a => a.Id == "acct-a").DisplayName, "durable actor metadata");

            var full = reopened.Snapshot(start, start.AddHours(3));
            TestSupport.AssertEqual(3, full.Actors.Count, "full-window actor count");
            TestSupport.AssertEqual(2, full.Edges.Count, "full-window edge count");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void LeidenSeparatesDenseConnectedCommunities()
    {
        var start = DateTimeOffset.Parse("2026-07-23T00:00:00Z");
        var window = new ActorGraphWindow(start, start.AddHours(1));
        var offset = 0;
        foreach (var pair in Triangle("a", "b", "c").Concat(Triangle("x", "y", "z")))
            window.Observe(new($"dense-{offset++}", start.AddMinutes(offset), [pair.Left, pair.Right], 5));
        window.Observe(new("bridge", start.AddMinutes(20), ["c", "x"], 0.05));

        var detector = new LeidenCommunityDetector();
        var first = detector.Detect(window.Snapshot());
        var second = detector.Detect(window.Snapshot());

        TestSupport.AssertEqual(LeidenCommunityDetector.AlgorithmId, first.Algorithm, "algorithm identity");
        TestSupport.AssertEqual(2, first.Communities.Count, "dense cluster count");
        TestSupport.AssertEqual(first.CommunityOf("a"), first.CommunityOf("b"), "left cluster assignment");
        TestSupport.AssertEqual(first.CommunityOf("b"), first.CommunityOf("c"), "left cluster assignment");
        TestSupport.AssertEqual(first.CommunityOf("x"), first.CommunityOf("z"), "right cluster assignment");
        TestSupport.AssertTrue(first.CommunityOf("a") != first.CommunityOf("x"), "weak bridge should not merge clusters");
        TestSupport.AssertTrue(first.Modularity > 0, "partition should have positive modularity");
        TestSupport.AssertTrue(
            first.Assignments.OrderBy(pair => pair.Key).SequenceEqual(second.Assignments.OrderBy(pair => pair.Key)),
            "community detection should be deterministic");
    }

    private static IEnumerable<(string Left, string Right)> Triangle(
        string first,
        string second,
        string third)
    {
        yield return (first, second);
        yield return (first, third);
        yield return (second, third);
    }
}
