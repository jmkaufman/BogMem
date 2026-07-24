using System.Text.Json;
using Bogmem.Cli.Parity;
using Bogmem.Slices.Wal;

namespace Bogmem.Slices.Tests.Wal;

public static class WalWriterTests
{
    public static void Run()
    {
        var path = TestSupport.PathUnderRepo("golden", "wal", "vectors.jsonl");
        var frozen = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var writer = new WalWriter(clock: () => frozen);
        var failures = new List<string>();

        foreach (var row in CorpusReplay.Read(path))
        {
            var input = row.Input;
            var op = input.GetProperty("operation").GetString()!;
            var parameters = new Dictionary<string, object?>();
            foreach (var p in input.GetProperty("params").EnumerateObject())
                parameters[p.Name] = JsonElementToObject(p.Value);

            object? result = null;
            if (input.TryGetProperty("result", out var resEl))
                result = JsonElementToObject(resEl);

            var line = writer.FormatEntry(op, parameters, result, frozen);
            var expected = row.Expected.GetProperty("line").GetString()!;
            var cmp = ComparisonRules.Exact(line, expected);
            if (!cmp.Passed)
                failures.Add($"{row.Id}:\n  actual  ={line}\n  expected={expected}");
        }

        TestSupport.AssertTrue(failures.Count == 0, "WAL failures:\n" + string.Join("\n", failures));
        TestSupport.WriteModuleLedger("wal", "S10",
            ("wal_jsonl_exact", "pass", "EXACT", null),
            ("wal_redaction", "pass", "EXACT", "covered_by_golden"));
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        _ => el.GetRawText(),
    };
}
