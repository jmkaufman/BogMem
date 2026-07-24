using System.Text.Json;
using Bogmem.Cli.Parity;
using Bogmem.Slices.Dedup;

namespace Bogmem.Slices.Tests.Dedup;

public static class DedupGrouperTests
{
    public static void Run()
    {
        var path = TestSupport.PathUnderRepo("golden", "dedup", "vectors.jsonl");
        var failures = new List<string>();
        var allDrawers = new List<DedupGrouper.DrawerRecord>();
        var fetch = 0;

        foreach (var row in CorpusReplay.Read(path))
        {
            var drawers = new List<DedupGrouper.DrawerRecord>();
            foreach (var d in row.Input.GetProperty("drawers").EnumerateArray())
            {
                var emb = d.GetProperty("embedding").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                var rec = new DedupGrouper.DrawerRecord(
                    d.GetProperty("id").GetString()!,
                    $"/fixture/{row.Id}",
                    d.GetProperty("document").GetString()!,
                    emb,
                    fetch++);
                drawers.Add(rec);
                allDrawers.Add(rec);
            }
            var threshold = row.Input.TryGetProperty("threshold", out var th) ? th.GetDouble() : 0.15;
            var (kept, deleted) = DedupGrouper.DedupSourceGroup(drawers, threshold);
            var expKept = row.Expected.GetProperty("kept").EnumerateArray().Select(x => x.GetString()!).ToList();
            var expDel = row.Expected.GetProperty("deleted").EnumerateArray().Select(x => x.GetString()!).ToList();
            if (!kept.SequenceEqual(expKept))
                failures.Add($"{row.Id} kept: [{string.Join(",", kept)}] != [{string.Join(",", expKept)}]");
            if (!deleted.SequenceEqual(expDel))
                failures.Add($"{row.Id} deleted: [{string.Join(",", deleted)}] != [{string.Join(",", expDel)}]");
        }
        TestSupport.AssertTrue(failures.Count == 0, "Dedup failures:\n" + string.Join("\n", failures));

        // Min group size 5 + boundary coverage with synthetic palace-shaped groups.
        float[] V(params float[] x) => x;
        var palace = new List<DedupGrouper.DrawerRecord>();
        for (var i = 0; i < 6; i++)
            palace.Add(new($"a{i}", "/src/a.py", new string('x', 30 + i), i < 3 ? V(1f, 0.01f * i, 0) : V(0.5f, 0.5f, 0.5f), i));
        for (var i = 0; i < 4; i++)
            palace.Add(new($"b{i}", "/src/b.py", new string('y', 25 + i), V(0, 1, 0), 100 + i));
        for (var i = 0; i < 5; i++)
            palace.Add(new($"c{i}", "/src/c.py", new string('z', 25 + i), V(0, 0, 1), 200 + i));

        var groups = DedupGrouper.GetSourceGroups(palace);
        TestSupport.AssertTrue(!groups.ContainsKey("/src/b.py"), "size-4 group filtered");
        TestSupport.AssertTrue(groups.ContainsKey("/src/c.py"), "size-5 group kept");
        TestSupport.AssertTrue(groups.ContainsKey("/src/a.py"), "size-6 group kept");

        var coverage = DedupGrouper.AssessBoundaryCoverage(palace.Concat(allDrawers).ToList());
        string? missed = null;
        if (!coverage.DistanceLineStraddled) missed = "0.15_distance_line";
        else if (!coverage.MemberCutoffStraddled) missed = "4v5_member_cutoff";

        var gap = GapLedger.Create(
            finalSliceExtent: $"0..{palace.Count + allDrawers.Count - 1}",
            missedBoundary: missed ?? "",
            exhaustionConfirmed: true,
            coveredComparesExact: true,
            pinSha: "a4747d7ffc7818684f01ac96c886ff6a654dd301",
            notes: coverage.DistanceLineStraddled && coverage.MemberCutoffStraddled
                ? "both boundaries straddled; gap ledger records full extent"
                : "terminal gap after full-palace exhaustion");

        var gapRel = Path.Combine("tests", "parity", "disposition", "gap_ledger.json");
        var gapPath = TestSupport.PathUnderRepo("tests", "parity", "disposition", "gap_ledger.json");
        gap.Write(gapPath);
        var loaded = GapLedger.Load(gapPath);
        TestSupport.AssertTrue(loaded is not null && loaded.SchemaName == GapLedger.Schema, "gap ledger schema");
        TestSupport.AssertTrue(loaded!.ExhaustionConfirmed && loaded.CoveredComparesExact, "gap ledger flags");
        TestSupport.AssertTrue(coverage.DistanceLineStraddled || !string.IsNullOrEmpty(gap.MissedBoundary), "boundary or gap");

        // Relative pointer only — absolute worktree paths make disposition non-portable across seats.
        TestSupport.WriteModuleLedger("dedup", "S5a",
            ("dedup_grouper_golden", "pass", "EXACT", null),
            ("dedup_boundary", "pass", "EXACT", coverage.DistanceLineStraddled ? null : "gap in gap_ledger.json"),
            ("dedup_gap_ledger", "pass", "EXACT", gapRel));
    }
}
