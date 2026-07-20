using System.Text.Json;

namespace Bogmem.Slices.Tests;

/// <summary>Shared plumbing for the golden-corpus replay tests.</summary>
public static class TestKit
{
    private static string? _root;

    /// <summary>Worktree root — the directory containing golden/ (walk up from cwd).</summary>
    public static string Root
    {
        get
        {
            if (_root is not null) return _root;
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "golden")))
                dir = dir.Parent;
            _root = dir?.FullName ?? throw new InvalidOperationException("golden/ corpus not found above cwd");
            return _root;
        }
    }

    public static IEnumerable<(string Id, JsonElement Input, JsonElement Expected)> Vectors(string module)
    {
        var path = Path.Combine(Root, "golden", module, "vectors.jsonl");
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            yield return (r.GetProperty("id").GetString()!, r.GetProperty("input").Clone(), r.GetProperty("expected").Clone());
        }
    }

    public static string? OptString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static int? OptInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    public static bool IsNullOrMissing(JsonElement el, string name) =>
        !el.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null;

    /// <summary>Bit-exact double equality (distances, widened float32 embeddings).</summary>
    public static bool DoubleBitsEqual(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    /// <summary>|a - b| in float32 ULPs after (double)(float) narrowing.</summary>
    public static long FloatUlpDelta(double actual, double expected)
    {
        float a = (float)actual, e = (float)expected;
        if (a == e) return 0;
        if (float.IsNaN(a) || float.IsNaN(e)) return long.MaxValue;
        long ai = BitConverter.SingleToInt32Bits(a), ei = BitConverter.SingleToInt32Bits(e);
        if (ai < 0) ai = int.MinValue - ai;
        if (ei < 0) ei = int.MinValue - ei;
        return Math.Abs(ai - ei);
    }

    public static string TempDir(string tag)
    {
        var path = Path.Combine(Root, "tests", ".tmp", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>Collects per-vector verdicts; a suite throws at the end if any failed.</summary>
public sealed class SuiteResult(string suite)
{
    public string Suite { get; } = suite;
    public List<string> Failures { get; } = [];
    public int Passed { get; private set; }

    public void Pass() => Passed++;

    public void Fail(string id, string detail) => Failures.Add($"{Suite}/{id}: {detail}");

    public void Check(string id, bool ok, string detail)
    {
        if (ok) Pass();
        else Fail(id, detail);
    }

    public void ThrowIfFailed()
    {
        if (Failures.Count > 0)
            throw new InvalidOperationException(
                $"{Suite}: {Failures.Count} failure(s), {Passed} passed\n" + string.Join("\n", Failures.Take(25)));
        Console.WriteLine($"  {Suite}: {Passed} checks passed");
    }
}
