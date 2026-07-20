using System.Text.Json;
using Bogmem.Slices.Dates;

namespace Bogmem.Slices.Tests.Dates;

/// <summary>
/// Replays every vector in golden/dates against ContentDateExtractor.
/// Per golden/dates/manifest.json, rows with a non-null mtime_epoch_utc are
/// materialized as real files with that mtime and interpreted in UTC; all
/// other rows use the vector's source_file path verbatim (no file on disk,
/// so the mtime tier stays unreachable for them).
/// </summary>
public static class ContentDateExtractorTests
{
    public static void Run()
    {
        var failures = new List<string>();
        var tiersSeen = new HashSet<int>();
        int count = 0;
        var tempRoot = TestKit.TempDir("dates");

        try
        {
            foreach (var (id, input, expected) in TestKit.Vectors("dates"))
            {
                count++;
                var sourceFile = input.GetProperty("source_file").GetString()!;
                var content = input.GetProperty("content").GetString()!;

                string path = sourceFile;
                if (input.TryGetProperty("mtime_epoch_utc", out var mt) && mt.ValueKind == JsonValueKind.Number)
                {
                    path = Path.Combine(tempRoot, sourceFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, content);
                    File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(mt.GetDouble()));
                }

                var (iso, tier) = ContentDateExtractor.ExtractWithTier(path, content);
                var expIso = TestKit.OptString(expected, "iso");
                int expTier = expected.GetProperty("tier").GetInt32();
                tiersSeen.Add(expTier);

                if (!string.Equals(iso, expIso, StringComparison.Ordinal))
                    failures.Add($"{id}: iso={iso ?? "null"} expected={expIso ?? "null"}");
                if ((int)tier != expTier)
                    failures.Add($"{id}: tier={(int)tier} expected={expTier}");
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
        }

        TestSupport.AssertTrue(count == 18, $"golden/dates corpus drift: {count} vectors, expected 18");
        for (int tier = 1; tier <= 5; tier++)
            TestSupport.AssertTrue(tiersSeen.Contains(tier), $"golden/dates never exercises tier {tier}");
        TestSupport.AssertTrue(failures.Count == 0, "dates failures:\n" + string.Join("\n", failures));

        TestSupport.WriteModuleLedger("dates", "S2",
            ("dates_golden_replay", "covered_by_golden", "EXACT", "all 18 golden/dates vectors replayed, iso + tier exact"),
            ("dates_mtime_utc", "covered_by_golden", "EXACT", "mtime vector materialized on disk and interpreted in UTC per manifest"),
            ("dates_fuzzy_gate", "covered_by_golden", "EXACT", "PR #1584 hallucination-gate cases (junk filename, version string, partial year) return None"));
    }
}
