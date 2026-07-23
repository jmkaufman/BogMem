using System.Text;
using System.Text.Json;
using Bogmem.Slices.Search;

namespace Bogmem.Slices.Tests.Sanitizer;

/// <summary>
/// Replays testdata/sanitizer/vectors.jsonl — outputs of the pinned legacy
/// mempalace/query_sanitizer.py executed at a4747d7 (see
/// testdata/sanitizer/generate_vectors.py). The vendored golden corpus has no
/// golden/sanitizer module and golden/ is read-only for run seats, so the
/// executed-legacy vectors in testdata/ are this module's oracle.
/// </summary>
public static class QuerySanitizerTests
{
    public static void Run()
    {
        var path = TestSupport.PathUnderRepo("testdata", "sanitizer", "vectors.jsonl");
        var failures = new List<string>();
        var methodsSeen = new HashSet<string>();
        int count = 0;

        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            var id = r.GetProperty("id").GetString()!;
            var input = r.GetProperty("input");
            var expected = r.GetProperty("expected");
            count++;

            var raw = ReadPyString(input, "raw_query");
            var result = QuerySanitizer.Sanitize(raw);

            var expClean = ReadPyString(expected, "clean_query")!;
            var expMethod = expected.GetProperty("method").GetString()!;
            methodsSeen.Add(expMethod);

            if (!string.Equals(result.CleanQuery, expClean, StringComparison.Ordinal))
                failures.Add($"{id}: clean_query mismatch\n  actual  ={Printable(result.CleanQuery)}\n  expected={Printable(expClean)}");
            if (result.WasSanitized != expected.GetProperty("was_sanitized").GetBoolean())
                failures.Add($"{id}: was_sanitized={result.WasSanitized}");
            if (result.OriginalLength != expected.GetProperty("original_length").GetInt32())
                failures.Add($"{id}: original_length={result.OriginalLength} expected={expected.GetProperty("original_length").GetInt32()}");
            if (result.CleanLength != expected.GetProperty("clean_length").GetInt32())
                failures.Add($"{id}: clean_length={result.CleanLength} expected={expected.GetProperty("clean_length").GetInt32()}");
            if (!string.Equals(result.Method, expMethod, StringComparison.Ordinal))
                failures.Add($"{id}: method={result.Method} expected={expMethod}");
        }

        TestSupport.AssertTrue(count >= 20, $"sanitizer corpus too small: {count} vectors");
        foreach (var m in new[] { "passthrough", "question_extraction", "tail_sentence", "tail_truncation" })
            TestSupport.AssertTrue(methodsSeen.Contains(m), $"sanitizer corpus never exercises method={m}");
        TestSupport.AssertTrue(failures.Count == 0, "sanitizer failures:\n" + string.Join("\n", failures));

        TestSupport.WriteModuleLedger("sanitizer", "S4",
            ("sanitizer_executed_legacy_vectors", "converted", "EXACT",
                "oracle = pinned legacy query_sanitizer.py executed at a4747d7 (testdata/sanitizer/); golden/sanitizer absent from vendored corpus and golden/ is read-only for run seats"),
            ("sanitizer_all_methods", "converted", "EXACT", "passthrough/question_extraction/tail_sentence/tail_truncation all replayed"),
            ("sanitizer_lone_surrogates", "converted", "EXACT", "#1235 U+FFFD replacement covered via code-point vectors"));
    }

    /// <summary>Read "name" as string, or "name_cp" as a code-point array (lone surrogates allowed).</summary>
    private static string? ReadPyString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var s))
            return s.ValueKind == JsonValueKind.Null ? null : s.GetString();
        if (!el.TryGetProperty(name + "_cp", out var arr))
            return null;
        var sb = new StringBuilder();
        foreach (var cp in arr.EnumerateArray())
        {
            int v = cp.GetInt32();
            if (v is >= 0xD800 and <= 0xDFFF) sb.Append((char)v);
            else sb.Append(char.ConvertFromUtf32(v));
        }
        return sb.ToString();
    }

    private static string Printable(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            sb.Append(c is < ' ' or (>= '\ud800' and <= '\udfff') ? $"\\u{(int)c:x4}" : c.ToString());
        return sb.Length > 120 ? sb.ToString(0, 120) + $"...({s.Length} units)" : sb.ToString();
    }
}
