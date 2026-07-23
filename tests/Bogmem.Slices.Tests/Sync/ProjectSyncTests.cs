using System.Diagnostics;
using Bogmem.Slices.Locking;
using Bogmem.Slices.Mining;
using Bogmem.Slices.Storage;
using Bogmem.Slices.Sync;

namespace Bogmem.Slices.Tests.Sync;

public static class ProjectSyncTests
{
    public static void Run()
    {
        var project = Directory.CreateTempSubdirectory("bogmem-project-sync-").FullName;
        var palace = Directory.CreateTempSubdirectory("bogmem-project-sync-palace-").FullName;
        var elsewhere = Directory.CreateTempSubdirectory("bogmem-project-sync-elsewhere-").FullName;
        try
        {
            RunGit(project, "init", "--quiet");
            Directory.CreateDirectory(Path.Combine(project, "src"));
            Directory.CreateDirectory(Path.Combine(project, "ignored"));
            File.WriteAllText(Path.Combine(project, ".gitignore"), "");
            var keep = Path.Combine(project, "src", "keep.md");
            var deleted = Path.Combine(project, "src", "deleted.md");
            var ignored = Path.Combine(project, "ignored", "generated.md");
            var ignoredLog = Path.Combine(project, "debug.txt");
            var reIncludedLog = Path.Combine(project, "important.txt");
            File.WriteAllText(keep, LongText("Keep this durable design note."));
            File.WriteAllText(deleted, LongText("This source will be deleted."));
            File.WriteAllText(ignored, LongText("This generated source will become ignored."));
            File.WriteAllText(ignoredLog, LongText("This log will become ignored."));
            File.WriteAllText(reIncludedLog, LongText("This important log is explicitly retained."));

            using var store = new BogDbMemoryStore(palace);
            var mine = new ProjectMiner(store).Mine(new ProjectMineRequest(project, Wing: "demo", Agent: "test"));
            TestSupport.AssertTrue(mine.FilesDiscovered == 5 && mine.DrawersWritten > 5, "fixture should mine all sources before ignore rules change");

            var manualMissing = Path.Combine(project, "manual-missing.md");
            store.Add("demo", "manual", LongText("Manual source metadata must never grant sync ownership."), manualMissing, "test");
            var outside = Path.Combine(elsewhere, "outside.md");
            File.WriteAllText(outside, LongText("This project-owned source is outside the requested scope."));
            store.ReplaceSource("demo", "general", outside, [LongText("This project-owned source is outside the requested scope.")], "test");

            File.Delete(deleted);
            File.WriteAllText(Path.Combine(project, ".gitignore"), "ignored/\n*.txt\n!important.txt\n");

            var sync = new ProjectSync(store);
            var unscopedApplyRejected = false;
            try { sync.Run(new ProjectSyncRequest(Wing: "demo", Apply: true)); }
            catch (ArgumentException) { unscopedApplyRejected = true; }
            TestSupport.AssertTrue(unscopedApplyRejected, "destructive sync must require an explicit root");

            var preview = sync.Run(new ProjectSyncRequest(project, Wing: "demo"));
            TestSupport.AssertTrue(preview.DryRun && preview.RemovedDrawers == 0, "sync should default to dry-run");
            TestSupport.AssertTrue(preview.GitIgnored > 0 && preview.Missing > 0, "preview should classify ignored and missing sources");
            TestSupport.AssertTrue(preview.Protected == 1, "manual drawer should be protected");
            TestSupport.AssertTrue(preview.OutOfScope == 1, "project drawer outside explicit root should be out of scope");
            TestSupport.AssertTrue(preview.Candidates.Any(candidate => candidate.SourceFile == FileLock.RealPath(ignored) && candidate.Reason == "gitignored"),
                "ignored directory should be a visible candidate");
            TestSupport.AssertTrue(preview.Candidates.All(candidate => candidate.SourceFile != FileLock.RealPath(reIncludedLog)),
                "negated Git ignore rule should retain important.txt");
            var before = store.Status().Drawers;
            TestSupport.AssertTrue(before > preview.GitIgnored + preview.Missing, "preview must not mutate the palace");

            var applied = sync.Run(new ProjectSyncRequest(project, Wing: "demo", Apply: true));
            TestSupport.AssertTrue(applied.RemovedDrawers == preview.GitIgnored + preview.Missing, "apply should delete exactly the previewed drawer set");
            TestSupport.AssertTrue(store.List(wing: "demo", limit: 100).Any(drawer => drawer.Origin == "manual" && drawer.SourceFile == manualMissing),
                "manual source metadata must survive apply");
            TestSupport.AssertTrue(store.List(wing: "demo", limit: 100).Any(drawer => drawer.SourceFile == keep), "kept project file should survive");
            TestSupport.AssertTrue(store.List(wing: "demo", limit: 100).Any(drawer => drawer.SourceFile == reIncludedLog),
                "negated Git ignore source should survive");

            var second = sync.Run(new ProjectSyncRequest(project, Wing: "demo", Apply: true));
            TestSupport.AssertTrue(second.RemovedDrawers == 0 && second.GitIgnored == 0 && second.Missing == 0,
                "repeated apply should be idempotent");
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
            if (Directory.Exists(palace)) Directory.Delete(palace, recursive: true);
            if (Directory.Exists(elsewhere)) Directory.Delete(elsewhere, recursive: true);
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git failed: {error}");
    }

    private static string LongText(string sentence) => string.Join("\n\n", Enumerable.Repeat(sentence, 60));
}
