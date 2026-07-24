using System.Text.Json;
using Bogmem.Cli;

namespace Bogmem.Slices.Tests.Cli;

/// <summary>
/// Exercises the documented CLI exit-code contract in-process:
/// 0 = parity holds, 1 = parity mismatch, 2 = usage/corpus error.
/// </summary>
public static class CliExitCodeTests
{
    public static void Run()
    {
        var failures = new List<string>();
        var scratch = Directory.CreateTempSubdirectory("bogmem-cli-exit").FullName;
        try
        {
            Expect(failures, "unknown module -> 2", 2,
                Invoke(["parity", "nosuchmodule"], TestKit.Root));
            Expect(failures, "unknown option -> 2", 2,
                Invoke(["parity", "ids", "--bogus"], TestKit.Root));
            Expect(failures, "missing option value -> 2", 2,
                Invoke(["parity", "ids", "--golden"], TestKit.Root));
            Expect(failures, "unknown command -> 2", 2,
                Invoke(["frobnicate"], TestKit.Root));
            Expect(failures, "nonexistent --golden -> 2", 2,
                Invoke(["parity", "ids", "--golden", Path.Combine(scratch, "does-not-exist")],
                    TestKit.Root, out var goldenErr));
            if (!goldenErr.Contains("does not exist"))
                failures.Add($"nonexistent --golden stderr should name the missing directory, got: {goldenErr.Trim()}");

            // An existing --golden directory that has no corpus for the module is a
            // corpus error (2), never a silent pass against the default corpus.
            var emptyGolden = Path.Combine(scratch, "empty-golden");
            Directory.CreateDirectory(emptyGolden);
            Expect(failures, "empty --golden dir -> 2", 2,
                Invoke(["parity", "ids", "--golden", emptyGolden], TestKit.Root));

            Expect(failures, "wired module against default corpus -> 0", 0,
                Invoke(["parity", "ids", "--report", Path.Combine(scratch, "ids_report.json")], TestKit.Root));
            Expect(failures, "explicit --golden pointing at the real corpus -> 0", 0,
                Invoke(["parity", "ids",
                        "--golden", Path.Combine(TestKit.Root, "golden"),
                        "--report", Path.Combine(scratch, "ids_report2.json")], TestKit.Root));

            // Doctored corpus with a wrong expected id must be a parity mismatch (1).
            var badGolden = Path.Combine(scratch, "bad-golden", "ids");
            Directory.CreateDirectory(badGolden);
            File.WriteAllText(Path.Combine(badGolden, "vectors.jsonl"),
                """{"id": "doctored", "input": {"kind": "tunnel", "source_wing": "a", "source_room": "b", "target_wing": "c", "target_room": "d"}, "expected": {"id": "not-the-real-id"}}""" + "\n");
            Expect(failures, "doctored corpus -> 1", 1,
                Invoke(["parity", "ids",
                        "--golden", Path.Combine(scratch, "bad-golden"),
                        "--report", Path.Combine(scratch, "bad_report.json")], TestKit.Root));

            Expect(failures, "--help -> 0", 0, Invoke(["--help"], TestKit.Root));

            var productPalace = Path.Combine(scratch, "product-palace");
            var init = InvokeCapture(
                ["init", "--palace", productPalace, "--name", "cli-test-palace"],
                TestKit.Root);
            Expect(failures, "product init -> 0", 0, init.Code);
            string initializedPalaceId;
            using (var initJson = JsonDocument.Parse(init.Stdout))
            {
                initializedPalaceId = initJson.RootElement.GetProperty("palaceId").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(initializedPalaceId)) failures.Add("product init palace ID");
                if (initJson.RootElement.GetProperty("palaceName").GetString() != "cli-test-palace")
                    failures.Add("product init palace name");
            }
            var add = InvokeCapture([
                "add", "--palace", productPalace,
                "--wing", "bogmem", "--room", "backend",
                "--content", "BogDB is the durable local memory backend."
            ], TestKit.Root);
            Expect(failures, "product add -> 0", 0, add.Code);
            using (var addJson = JsonDocument.Parse(add.Stdout))
                if (!addJson.RootElement.GetProperty("created").GetBoolean()) failures.Add("product add should create");

            var status = InvokeCapture(["status", "--palace", productPalace], TestKit.Root);
            Expect(failures, "product status -> 0", 0, status.Code);
            using (var statusJson = JsonDocument.Parse(status.Stdout))
            {
                if (statusJson.RootElement.GetProperty("backend").GetString() != "bogdb") failures.Add("product status backend");
                if (statusJson.RootElement.GetProperty("drawers").GetInt32() != 1) failures.Add("product status drawer count");
                if (statusJson.RootElement.GetProperty("palaceId").GetString() != initializedPalaceId)
                    failures.Add("product status stable palace ID");
            }

            var registryPath = Path.Combine(scratch, "registry", "palaces.json");
            var register = InvokeCapture([
                "registry", "register",
                "--palace", productPalace,
                "--registry", registryPath
            ], TestKit.Root);
            Expect(failures, "registry register -> 0", 0, register.Code);
            using (var registerJson = JsonDocument.Parse(register.Stdout))
                if (registerJson.RootElement.GetProperty("palaceId").GetString() != initializedPalaceId)
                    failures.Add("registry register palace ID");

            var registryList = InvokeCapture([
                "registry", "list", "--registry", registryPath
            ], TestKit.Root);
            Expect(failures, "registry list -> 0", 0, registryList.Code);
            using (var listJson = JsonDocument.Parse(registryList.Stdout))
            {
                if (listJson.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
                    failures.Add("registry schema version");
                if (listJson.RootElement.GetProperty("palaces").GetArrayLength() != 1)
                    failures.Add("registry list count");
            }

            var resolve = InvokeCapture([
                "registry", "resolve", "cli-test-palace", "--registry", registryPath
            ], TestKit.Root);
            Expect(failures, "registry resolve -> 0", 0, resolve.Code);
            using (var resolveJson = JsonDocument.Parse(resolve.Stdout))
                if (resolveJson.RootElement.GetProperty("databasePath").GetString() != Path.GetFullPath(productPalace))
                    failures.Add("registry resolve path");

            var secondPalace = Path.Combine(scratch, "second-palace");
            var secondInit = InvokeCapture([
                "init", "--palace", secondPalace, "--name", "cli-second-palace"
            ], TestKit.Root);
            Expect(failures, "second palace init -> 0", 0, secondInit.Code);
            var secondAdd = InvokeCapture([
                "add", "--palace", secondPalace,
                "--wing", "bogmem", "--room", "backend",
                "--content", "A second palace also retains durable BogDB memory."
            ], TestKit.Root);
            Expect(failures, "second palace add -> 0", 0, secondAdd.Code);
            var secondRegister = InvokeCapture([
                "registry", "register",
                "--palace", secondPalace,
                "--registry", registryPath
            ], TestKit.Root);
            Expect(failures, "second registry register -> 0", 0, secondRegister.Code);

            var federatedRecall = InvokeCapture([
                "recall", "durable BogDB memory",
                "--registry", registryPath,
                "--limit", "2",
                "--per-palace-limit", "1",
                "--max-distance", "2"
            ], TestKit.Root);
            Expect(failures, "federated recall -> 0", 0, federatedRecall.Code);
            using (var recallJson = JsonDocument.Parse(federatedRecall.Stdout))
            {
                var hits = recallJson.RootElement.GetProperty("hits");
                if (recallJson.RootElement.GetProperty("palacesSucceeded").GetInt32() != 2)
                    failures.Add("federated recall palace count");
                if (hits.GetArrayLength() != 2 ||
                    hits.EnumerateArray()
                        .Select(hit => hit.GetProperty("palaceId").GetString())
                        .Distinct()
                        .Count() != 2)
                    failures.Add("federated recall should interleave sourced palace hits");
            }

            var search = InvokeCapture(["search", "durable BogDB memory", "--palace", productPalace], TestKit.Root);
            Expect(failures, "product search -> 0", 0, search.Code);
            using (var searchJson = JsonDocument.Parse(search.Stdout))
                if (searchJson.RootElement.GetProperty("results").GetArrayLength() != 1) failures.Add("product search should retrieve drawer");

            var mineSource = Path.Combine(scratch, "mine-source");
            Directory.CreateDirectory(mineSource);
            File.WriteAllText(Path.Combine(mineSource, "notes.md"),
                string.Join("\n\n", Enumerable.Repeat("CLI mining stores project notes in BogDB.", 40)));
            var dryMine = InvokeCapture([
                "mine", mineSource, "--palace", productPalace, "--wing", "cli_project", "--dry-run"
            ], TestKit.Root);
            Expect(failures, "product mine dry-run -> 0", 0, dryMine.Code);
            using (var dryMineJson = JsonDocument.Parse(dryMine.Stdout))
                if (dryMineJson.RootElement.GetProperty("drawersWritten").GetInt32() != 0)
                    failures.Add("product mine dry-run should not write");

            var mine = InvokeCapture([
                "mine", mineSource, "--palace", productPalace, "--wing", "cli_project"
            ], TestKit.Root);
            Expect(failures, "product mine -> 0", 0, mine.Code);
            using (var mineJson = JsonDocument.Parse(mine.Stdout))
                if (mineJson.RootElement.GetProperty("drawersWritten").GetInt32() == 0)
                    failures.Add("product mine should write project drawers");

            File.Delete(Path.Combine(mineSource, "notes.md"));
            var syncPreview = InvokeCapture([
                "sync", mineSource, "--palace", productPalace, "--wing", "cli_project"
            ], TestKit.Root);
            Expect(failures, "product sync preview -> 0", 0, syncPreview.Code);
            using (var syncJson = JsonDocument.Parse(syncPreview.Stdout))
            {
                if (!syncJson.RootElement.GetProperty("dryRun").GetBoolean() ||
                    syncJson.RootElement.GetProperty("missing").GetInt32() == 0 ||
                    syncJson.RootElement.GetProperty("removedDrawers").GetInt32() != 0)
                    failures.Add("product sync preview should report missing drawers without deleting");
            }

            Expect(failures, "product sync unscoped apply -> 2", 2,
                Invoke(["sync", "--palace", productPalace, "--wing", "cli_project", "--apply"], TestKit.Root));
            var syncApply = InvokeCapture([
                "sync", mineSource, "--palace", productPalace, "--wing", "cli_project", "--apply"
            ], TestKit.Root);
            Expect(failures, "product sync apply -> 0", 0, syncApply.Code);
            using (var syncJson = JsonDocument.Parse(syncApply.Stdout))
                if (syncJson.RootElement.GetProperty("removedDrawers").GetInt32() == 0)
                    failures.Add("product sync apply should remove missing project drawers");

            // The canonical full-corpus gate is one aggregate invocation. It must
            // run every leaf module and produce an aggregate report, rather than
            // being rejected as an unknown module before dispatch.
            var allReport = Path.Combine(scratch, "all_report.json");
            Expect(failures, "parity all -> 0", 0,
                Invoke(["parity", "all", "--report", allReport], TestKit.Root));
            if (!File.Exists(allReport))
                failures.Add("parity all should write its aggregate report");
            else
            {
                using var allJson = JsonDocument.Parse(File.ReadAllText(allReport));
                if (allJson.RootElement.GetProperty("slice").GetString() != "ALL")
                    failures.Add("parity all report should identify slice ALL");
                if (allJson.RootElement.GetProperty("findings").GetArrayLength() == 0)
                    failures.Add("parity all report should contain leaf findings");
            }

            // Every advertised module must replay green through the CLI path, so
            // the CLI verdict and the test-suite verdict can never disagree.
            foreach (var module in ParityRunner.KnownModules)
                Expect(failures, $"parity {module} -> 0", 0,
                    Invoke(["parity", module, "--report", Path.Combine(scratch, $"pr_{module}.json")], TestKit.Root));

            Directory.Delete(secondPalace, recursive: true);
            var unavailableRecall = InvokeCapture([
                "recall", "durable memory",
                "--registry", registryPath,
                "--palaces", "cli-second-palace"
            ], TestKit.Root);
            Expect(failures, "all selected recall palaces unavailable -> 1", 1, unavailableRecall.Code);
            using (var unavailableJson = JsonDocument.Parse(unavailableRecall.Stdout))
                if (unavailableJson.RootElement.GetProperty("failures").GetArrayLength() != 1)
                    failures.Add("unavailable federated recall should return sourced failure");

            var unregister = InvokeCapture([
                "registry", "unregister", initializedPalaceId, "--registry", registryPath
            ], TestKit.Root);
            Expect(failures, "registry unregister -> 0", 0, unregister.Code);
            Expect(failures, "registry unregister missing -> 1", 1,
                Invoke([
                    "registry", "unregister", initializedPalaceId,
                    "--registry", registryPath
                ], TestKit.Root));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join("; ", failures));
        Console.WriteLine($"    cli exit-code contract + {ParityRunner.KnownModules.Length}-module replay: ok");
    }

    private static int Invoke(string[] args, string cwd) => Invoke(args, cwd, out _);

    private sealed record Invocation(int Code, string Stdout, string Stderr);

    private static Invocation InvokeCapture(string[] args, string cwd)
    {
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        var code = CliMain.Run(args, outWriter, errWriter, cwd);
        return new(code, outWriter.ToString(), errWriter.ToString());
    }

    private static int Invoke(string[] args, string cwd, out string stderr)
    {
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        var code = CliMain.Run(args, outWriter, errWriter, cwd);
        stderr = errWriter.ToString();
        return code;
    }

    private static void Expect(List<string> failures, string label, int expected, int actual)
    {
        if (actual != expected)
            failures.Add($"{label}: expected exit {expected}, got {actual}");
    }
}
