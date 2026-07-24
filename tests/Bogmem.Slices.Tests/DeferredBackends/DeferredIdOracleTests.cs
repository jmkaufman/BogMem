using Bogmem.Cli.Parity;
using Bogmem.Slices.DeferredBackends;

namespace Bogmem.Slices.Tests.DeferredBackends;

public static class DeferredIdOracleTests
{
    public static void Run()
    {
        var path = TestSupport.PathUnderRepo("golden", "deferred_ids", "vectors.jsonl");
        var failures = new List<string>();
        foreach (var row in CorpusReplay.Read(path))
        {
            var backend = row.Input.GetProperty("backend").GetString()!;
            var value = row.Input.GetProperty("value").GetString()!;
            var expected = row.Expected.GetProperty("id").GetString()!;
            var actual = DeferredIdOracle.ExpectedId(backend, value);
            var cmp = ComparisonRules.Exact(actual, expected);
            if (!cmp.Passed) failures.Add($"{row.Id}: actual={actual} expected={expected}");
        }
        TestSupport.AssertTrue(failures.Count == 0, "DeferredIdOracle failures:\n" + string.Join("\n", failures));

        // Stage oracle under testdata/ (golden is read-only).
        var outPath = TestSupport.PathUnderRepo("testdata", "deferred_ids", "oracle.jsonl");
        DeferredIdOracle.EmitOracleJsonl(outPath);
        TestSupport.AssertTrue(File.Exists(outPath), "oracle staged");

        TestSupport.WriteModuleLedger("deferred_ids", "S13",
            ("deferred_oracle_staged", "not_applicable", "PLACEHOLDER", "deferred pass, oracle staged"),
            ("deferred_uuid5_match", "pass", "EXACT", "oracle matches golden vectors; no comparison pass"));
    }
}
