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
