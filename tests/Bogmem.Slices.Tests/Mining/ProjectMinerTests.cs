using Bogmem.Slices.Mining;
using Bogmem.Slices.Storage;

namespace Bogmem.Slices.Tests.Mining;

public static class ProjectMinerTests
{
    public static void Run()
    {
        var scratch = Directory.CreateTempSubdirectory("bogmem-project-miner-").FullName;
        var palace = Directory.CreateTempSubdirectory("bogmem-project-miner-palace-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(scratch, "docs"));
            Directory.CreateDirectory(Path.Combine(scratch, "bin"));
            var source = Path.Combine(scratch, "docs", "architecture.md");
            File.WriteAllText(source, LongText("BogDB keeps project memory durable and searchable."));
            File.WriteAllText(Path.Combine(scratch, "bin", "generated.cs"), LongText("this generated file must be skipped"));
            File.WriteAllText(Path.Combine(scratch, "package-lock.json"), LongText("this lock file must be skipped"));

            using var store = new BogDbMemoryStore(palace);
            var miner = new ProjectMiner(store);
            var dryRun = miner.Mine(new ProjectMineRequest(scratch, Wing: "bogmem", DryRun: true));
            TestSupport.AssertTrue(dryRun.FilesDiscovered == 1 && dryRun.FilesChanged == 1, "dry-run should plan one source");
            TestSupport.AssertTrue(dryRun.DrawersPlanned > 0 && dryRun.DrawersWritten == 0, "dry-run must not write");
            TestSupport.AssertTrue(store.Status().Drawers == 0, "dry-run palace should remain empty");

            var first = miner.Mine(new ProjectMineRequest(scratch, Wing: "bogmem", Agent: "test"));
            TestSupport.AssertTrue(first.FilesChanged == 1 && first.DrawersWritten > 1, "first mine should write chunks");
            var firstCount = store.Status().Drawers;
            TestSupport.AssertTrue(firstCount == first.DrawersWritten, "stored drawer count should match summary");
            TestSupport.AssertTrue(store.Search("durable searchable memory", wing: "bogmem").Count > 0, "mined content should be searchable");

            var repeat = miner.Mine(new ProjectMineRequest(scratch, Wing: "bogmem", Agent: "test"));
            TestSupport.AssertTrue(repeat.FilesUnchanged == 1 && repeat.DrawersWritten == 0, "unchanged rerun should perform no writes");
            TestSupport.AssertTrue(store.Status().Drawers == firstCount, "unchanged rerun should preserve count");

            File.WriteAllText(source, LongText("The revised design uses atomic source replacement."));
            var changed = miner.Mine(new ProjectMineRequest(scratch, Wing: "bogmem", Agent: "test"));
            TestSupport.AssertTrue(changed.FilesChanged == 1 && changed.DrawersWritten > 0, "edited source should be replaced");
            TestSupport.AssertTrue(store.Search("revised atomic replacement", wing: "bogmem")[0].Drawer.Content.Contains("revised design", StringComparison.Ordinal),
                "search should see revised content");
            TestSupport.AssertTrue(store.List(wing: "bogmem", limit: 100).All(d => !d.Content.Contains("durable and searchable", StringComparison.Ordinal)),
                "replacement should remove stale chunks");

            File.WriteAllText(source, "too short");
            var emptied = miner.Mine(new ProjectMineRequest(scratch, Wing: "bogmem", Agent: "test"));
            TestSupport.AssertTrue(emptied.FilesChanged == 1 && emptied.DrawersWritten == 0, "shortened source should plan zero current drawers");
            TestSupport.AssertTrue(store.Status().Drawers == 0, "shortened source should remove stale drawers");
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            if (Directory.Exists(palace)) Directory.Delete(palace, recursive: true);
        }
    }

    private static string LongText(string sentence) => string.Join("\n\n", Enumerable.Repeat(sentence, 60));
}
