using System.Text.Json;
using Bogmem.Harness;
using Bogmem.Slices.Config;
using Bogmem.Slices.Ids;

internal sealed record ParityRunResult(bool Passed, int CheckedCount);

internal static class ParityRunner
{
    public static ParityRunResult Run(string startDirectory, string module, string reportPath)
    {
        var root = FindCorpusRoot(startDirectory);
        var findings = new List<ParityFinding>();
        var gapLedger = FindGapLedgerPointer(root);
        var corpusPath = Path.Combine(root, "golden", module, "vectors.jsonl");

        if (!File.Exists(corpusPath))
        {
            findings.Add(new("corpus_missing", "EXACT", false, $"No frozen corpus for module '{module}'."));
            ParityReportWriter.Write(reportPath, SliceId(module), findings, gapLedger);
            return new(false, findings.Count);
        }

        try
        {
            switch (module)
            {
                case "config":
                    RunConfig(corpusPath, findings);
                    break;
                case "ids":
                    RunIds(corpusPath, findings);
                    break;
                default:
                    findings.Add(new("unsupported_module", "EXACT", false, $"CLI parity comparisons are implemented for config and ids; '{module}' is not wired yet."));
                    break;
            }
        }
        catch (Exception ex)
        {
            findings.Add(new("parity_exception", "EXACT", false, ex.Message));
        }

        ParityReportWriter.Write(reportPath, SliceId(module), findings, gapLedger);
        return new(findings.Count > 0 && findings.All(x => x.Passed), findings.Count);
    }

    private static void RunConfig(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var file = row.Input.GetProperty("file");
            var env = ReadStringMap(row.Input.GetProperty("env"));
            var actual = ConfigResolver.Resolve(file.ValueKind == JsonValueKind.Null ? null : file, env);
            var expected = row.Expected;

            var mismatches = new List<string>();
            Check("backend", actual.Backend, expected.GetProperty("backend").GetString(), mismatches);
            Check("chunk_size", actual.ChunkSize, expected.GetProperty("chunk_size").GetInt32(), mismatches);
            Check("chunk_overlap", actual.ChunkOverlap, expected.GetProperty("chunk_overlap").GetInt32(), mismatches);
            Check("min_chunk_size", actual.MinChunkSize, expected.GetProperty("min_chunk_size").GetInt32(), mismatches);
            Check("min_chunk_size_explicit", actual.MinChunkSizeExplicit, expected.GetProperty("min_chunk_size_explicit").GetBoolean(), mismatches);
            var expectedPalace = expected.TryGetProperty("palace_path", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
            Check("palace_path", actual.PalacePath, expectedPalace, mismatches);

            findings.Add(new(row.Id, "EXACT", mismatches.Count == 0, mismatches.Count == 0 ? null : string.Join("; ", mismatches)));
        }
    }

    private static void RunIds(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var input = row.Input;
            var expected = row.Expected;
            var kind = input.GetProperty("kind").GetString()!;
            string actual;
            string? preimage = null;

            switch (kind)
            {
                case "drawer_chunk":
                    actual = IdRecipes.MakeDrawerIdFromChunk(
                        input.GetProperty("wing").GetString()!,
                        input.GetProperty("room").GetString()!,
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("chunk_index").GetInt32());
                    preimage = IdRecipes.BuildPreimage([
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("chunk_index").GetInt32().ToString()]);
                    break;
                case "drawer_content":
                    actual = IdRecipes.MakeDrawerIdFromContent(
                        input.GetProperty("wing").GetString()!,
                        input.GetProperty("room").GetString()!,
                        input.GetProperty("content").GetString()!);
                    preimage = IdRecipes.BuildPreimage([
                        input.GetProperty("wing").GetString()!,
                        input.GetProperty("room").GetString()!,
                        input.GetProperty("content").GetString()!]);
                    break;
                case "convo_drawer":
                    actual = IdRecipes.MakeConvoDrawerId(
                        input.GetProperty("wing").GetString()!,
                        input.GetProperty("room").GetString()!,
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("extract_mode").GetString()!,
                        input.GetProperty("chunk_index").GetInt32());
                    preimage = IdRecipes.BuildPreimage([
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("extract_mode").GetString()!,
                        input.GetProperty("chunk_index").GetInt32().ToString()]);
                    break;
                case "convo_sentinel":
                    actual = IdRecipes.MakeConvoSentinelId(
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("extract_mode").GetString()!);
                    preimage = IdRecipes.BuildPreimage([
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("extract_mode").GetString()!]);
                    break;
                case "triple":
                    var vfEl = input.GetProperty("valid_from");
                    string? vf = vfEl.ValueKind == JsonValueKind.Null ? null : vfEl.GetString();
                    if (vf == "None") vf = null;
                    actual = IdRecipes.MakeTripleId(
                        input.GetProperty("sub_id").GetString()!,
                        input.GetProperty("predicate").GetString()!,
                        input.GetProperty("obj_id").GetString()!,
                        vf,
                        input.GetProperty("recorded_at").GetString()!);
                    preimage = IdRecipes.BuildPreimage([vf, input.GetProperty("recorded_at").GetString()!]);
                    break;
                case "delimited":
                    var parts = new List<object?>();
                    foreach (var p in input.GetProperty("parts").EnumerateArray())
                        parts.Add(p.ValueKind == JsonValueKind.Null ? null : p.GetString());
                    actual = IdRecipes.DelimitedSha256(parts, expected.GetProperty("id").GetString()!.Length);
                    preimage = IdRecipes.BuildPreimage(parts);
                    break;
                case "tunnel":
                    actual = IdRecipes.CanonicalTunnelId(
                        input.GetProperty("source_wing").GetString()!,
                        input.GetProperty("source_room").GetString()!,
                        input.GetProperty("target_wing").GetString()!,
                        input.GetProperty("target_room").GetString()!);
                    break;
                default:
                    findings.Add(new(row.Id, "EXACT", false, $"Unknown ID fixture kind '{kind}'."));
                    continue;
            }

            var mismatches = new List<string>();
            Check("id", actual, expected.GetProperty("id").GetString(), mismatches);
            if (expected.TryGetProperty("preimage", out var pe) && pe.ValueKind == JsonValueKind.String && preimage is not null)
                Check("preimage", preimage, pe.GetString(), mismatches);
            findings.Add(new(row.Id, "EXACT", mismatches.Count == 0, mismatches.Count == 0 ? null : string.Join("; ", mismatches)));
        }
    }

    private static Dictionary<string, string?> ReadStringMap(JsonElement element)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
            map[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
        return map;
    }

    private static void Check<T>(string name, T actual, T expected, List<string> mismatches)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            mismatches.Add($"{name}: actual={actual ?? (object)"<null>"} expected={expected ?? (object)"<null>"}");
    }

    private static string FindCorpusRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "golden")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return startDirectory;
    }

    private static string? FindGapLedgerPointer(string root)
    {
        var path = Path.Combine(root, "tests", "parity", "disposition", "gap_ledger.json");
        return File.Exists(path) ? Path.GetRelativePath(root, path) : null;
    }

    private static string SliceId(string module) => module switch
    {
        "config" => "S8",
        "ids" => "S1",
        _ => module
    };
}
