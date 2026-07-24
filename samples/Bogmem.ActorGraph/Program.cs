using Bogmem.Graph;

var start = DateTimeOffset.Parse("2026-07-23T00:00:00Z");
var window = new ActorGraphWindow(start, start.AddMinutes(15));

// An upstream workflow owns normalization. BogMem receives explicit,
// replay-safe observations from transfers, shared endpoints, devices, or
// whatever "co-activity" means in the caller's domain.
ObserveGroup(window, "payment-burst", ["acct-101", "acct-102", "acct-103"], 3, start.AddMinutes(1));
ObserveGroup(window, "shared-device", ["acct-101", "acct-102"], 2, start.AddMinutes(3));
ObserveGroup(window, "payment-burst", ["acct-201", "acct-202", "acct-203"], 3, start.AddMinutes(5));
ObserveGroup(window, "shared-device", ["acct-202", "acct-203"], 2, start.AddMinutes(7));
ObserveGroup(window, "weak-cross-group-signal", ["acct-103", "acct-201"], 0.05, start.AddMinutes(9));

var graph = window.Snapshot();
var partition = new LeidenCommunityDetector().Detect(graph);

Console.WriteLine(
    $"Window [{graph.WindowStart:HH:mm}, {graph.WindowEnd:HH:mm}) " +
    $"actors={graph.Actors.Count} edges={graph.Edges.Count}");
foreach (var community in partition.Communities)
    Console.WriteLine(
        $"community {community.Id}: {string.Join(", ", community.ActorIds)} " +
        $"(internal weight {community.InternalWeight:F2})");

Console.WriteLine();
Console.WriteLine("Neighbors of acct-101:");
foreach (var neighbor in graph.Neighbors("acct-101"))
    Console.WriteLine(
        $"  {neighbor.Actor.Id}: weight={neighbor.Weight:F2}, " +
        $"evidence={neighbor.ObservationCount}");

static void ObserveGroup(
    ActorGraphWindow window,
    string source,
    IReadOnlyList<string> actorIds,
    double weight,
    DateTimeOffset occurredAt)
{
    var id = $"{source}:{occurredAt.ToUnixTimeMilliseconds()}";
    window.Observe(new(
        id,
        occurredAt,
        actorIds,
        weight,
        source,
        new ObservationProvenance(
            Source: "ftt",
            WorkflowId: "social-signal-window",
            RunId: "demo-run",
            SignalType: source)));
}
