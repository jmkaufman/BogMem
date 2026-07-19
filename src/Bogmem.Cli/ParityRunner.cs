using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Harness;
using Bogmem.Slices.Config;
using Bogmem.Slices.Dedup;
using Bogmem.Slices.DeferredBackends;
using Bogmem.Slices.Ids;
using Bogmem.Slices.Mcp;
using Bogmem.Slices.Search;
using Bogmem.Slices.Wal;

public sealed record ParityRunResult(bool Passed, int CheckedCount);

public sealed class CorpusException : Exception
{
    public CorpusException(string message) : base(message) { }
}

public static partial class ParityRunner
{
    public static readonly string[] KnownModules =
    [
        "config", "ids", "wal", "search", "dedup", "mcp", "deferred_ids",
        "chunks", "dates", "dynamics", "locks", "spellcheck", "storage",
        "model", "embedding", "chroma", "kg",
    ];

    public static ParityRunResult Run(string startDirectory, string module, string reportPath, string? goldenOverride = null)
    {
        var root = FindCorpusRoot(startDirectory);
        var goldenRoot = ResolveGoldenRoot(root, goldenOverride);
        var findings = new List<ParityFinding>();
        var gapLedger = FindGapLedgerPointer(root);

        try
        {
            switch (module)
            {
                case "config":
                    RunConfig(RequireCorpus(goldenRoot, "config"), findings);
                    break;
                case "ids":
                    RunIds(RequireCorpus(goldenRoot, "ids"), findings);
                    break;
                case "wal":
                    RunWal(RequireCorpus(goldenRoot, "wal"), findings);
                    break;
                case "search":
                    RunSearchCli(RequireCorpus(goldenRoot, "search", "cli"), findings);
                    RunSearchMcp(RequireCorpus(goldenRoot, "search", "mcp"), findings);
                    break;
                case "dedup":
                    RunDedup(RequireCorpus(goldenRoot, "dedup"), findings, root);
                    break;
                case "mcp":
                    RunMcp(RequireCorpus(goldenRoot, "mcp"), findings);
                    break;
                case "deferred_ids":
                    RunDeferredIds(RequireCorpus(goldenRoot, "deferred_ids"), findings, root);
                    break;
                case "chunks":
                    RunChunks(goldenRoot, findings);
                    break;
                case "dates":
                    RunDates(RequireCorpus(goldenRoot, "dates"), findings);
                    break;
                case "dynamics":
                    RunDynamics(RequireCorpus(goldenRoot, "dynamics"), findings);
                    break;
                case "locks":
                    RunLocks(RequireCorpus(goldenRoot, "locks"), findings);
                    break;
                case "spellcheck":
                    RunSpellcheck(RequireCorpus(goldenRoot, "spellcheck"), findings);
                    break;
                case "storage":
                    RunGraphFiles(RequireCorpus(goldenRoot, "graph_files"), findings);
                    RunSqliteExact(RequireCorpus(goldenRoot, "sqlite_exact"), findings);
                    break;
                case "model":
                    RunModel(RequireCorpus(goldenRoot, "model"), goldenRoot, findings);
                    break;
                case "embedding":
                    RunEmbedding(RequireCorpus(goldenRoot, "embedding"), goldenRoot, findings);
                    break;
                case "chroma":
                    RunChroma(RequireCorpus(goldenRoot, "chroma"), findings);
                    break;
                case "kg":
                    RunKg(RequireCorpus(goldenRoot, "kg"), goldenRoot, findings);
                    break;
                default:
                    throw new CorpusException($"CLI parity comparisons are not wired for module '{module}'.");
            }
        }
        catch (CorpusException)
        {
            throw;
        }
        catch (Exception ex)
        {
            findings.Add(new("parity_exception", "EXACT", false, ex.Message));
        }

        ParityReportWriter.Write(reportPath, SliceId(module), findings, gapLedger);
        return new(findings.Count > 0 && findings.All(x => x.Passed), findings.Count);
    }

    private static string ResolveGoldenRoot(string root, string? goldenOverride)
    {
        if (goldenOverride is null)
        {
            var defaultGolden = Path.Combine(root, "golden");
            if (!Directory.Exists(defaultGolden))
                throw new CorpusException($"No golden corpus directory found at '{defaultGolden}'.");
            return defaultGolden;
        }

        var overridePath = Path.GetFullPath(goldenOverride);
        if (!Directory.Exists(overridePath))
            throw new CorpusException($"--golden directory '{overridePath}' does not exist.");
        // Accept either the golden directory itself or a repo root containing golden/.
        var nested = Path.Combine(overridePath, "golden");
        return Directory.Exists(nested) ? nested : overridePath;
    }

    private static string RequireCorpus(string goldenRoot, params string[] parts)
    {
        var path = Path.Combine(new[] { goldenRoot }.Concat(parts).Append("vectors.jsonl").ToArray());
        if (!File.Exists(path))
            throw new CorpusException($"No frozen corpus at '{path}'.");
        return path;
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

    private static void RunWal(string corpusPath, List<ParityFinding> findings)
    {
        var frozen = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var writer = new WalWriter(clock: () => frozen);
        foreach (var row in CorpusReplay.Read(corpusPath))
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
            var ok = string.Equals(line, expected, StringComparison.Ordinal);
            findings.Add(new(row.Id, "EXACT", ok, ok ? null : $"actual={line} expected={expected}"));
        }
    }

    private static void RunSearchCli(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var input = row.Input;
            var query = input.GetProperty("query").GetString() ?? "";
            var vw = input.TryGetProperty("vector_weight", out var vwe) ? vwe.GetDouble() : 0.6;
            var bw = input.TryGetProperty("bm25_weight", out var bwe) ? bwe.GetDouble() : 0.4;
            var hits = new List<Searcher.SearchHit>();
            foreach (var c in input.GetProperty("candidates").EnumerateArray())
            {
                double? dist = c.TryGetProperty("distance", out var d) && d.ValueKind != JsonValueKind.Null
                    ? d.GetDouble() : null;
                string? authored = c.TryGetProperty("authored_at", out var a) && a.ValueKind == JsonValueKind.String
                    ? a.GetString() : null;
                hits.Add(new Searcher.SearchHit(
                    c.GetProperty("id").GetString()!,
                    c.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    dist,
                    authored));
            }

            var ranked = Searcher.HybridRank(hits, query, vectorWeight: vw, bm25Weight: bw);
            var order = ranked.Select(h => h.Id).ToList();
            var expectedOrder = row.Expected.GetProperty("order").EnumerateArray().Select(x => x.GetString()!).ToList();
            var mismatches = new List<string>();
            if (!order.SequenceEqual(expectedOrder))
                mismatches.Add($"order actual=[{string.Join(",", order)}] expected=[{string.Join(",", expectedOrder)}]");

            foreach (var es in row.Expected.GetProperty("scores").EnumerateArray())
            {
                var id = es.GetProperty("id").GetString()!;
                var hit = ranked.First(h => h.Id == id);
                var expBm25 = es.GetProperty("bm25").GetDouble();
                var expHybrid = es.GetProperty("hybrid").GetDouble();
                if (Math.Abs(hit.Bm25Score - expBm25) > 1e-9)
                    mismatches.Add($"{id} bm25 actual={hit.Bm25Score} expected={expBm25}");
                if (Math.Abs(hit.HybridScore - expHybrid) > 1e-9)
                    mismatches.Add($"{id} hybrid actual={hit.HybridScore} expected={expHybrid}");
            }

            findings.Add(new($"cli:{row.Id}", "EXACT", mismatches.Count == 0,
                mismatches.Count == 0 ? null : string.Join("; ", mismatches)));
        }
    }

    private static void RunSearchMcp(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var input = row.Input;
            var drawers = new List<Searcher.SearchHit>();
            foreach (var d in input.GetProperty("drawers").EnumerateArray())
            {
                drawers.Add(new Searcher.SearchHit(
                    d.GetProperty("id").GetString()!,
                    d.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    d.GetProperty("distance").GetDouble(),
                    d.TryGetProperty("authored_at", out var a) ? a.GetString() : null,
                    d.TryGetProperty("source", out var s) ? s.GetString() : null));
            }

            var closets = input.GetProperty("closets").EnumerateArray()
                .Select(c => (c.GetProperty("source").GetString()!, c.GetProperty("distance").GetDouble()))
                .OrderBy(x => x.Item2)
                .ToList();
            var selected = Searcher.RankWithClosetBoost(drawers, closets);
            var expected = row.Expected.GetProperty("selection").EnumerateArray().ToList();
            var mismatches = new List<string>();
            if (selected.Count != expected.Count)
                mismatches.Add($"count {selected.Count}!={expected.Count}");
            for (var i = 0; i < expected.Count && i < selected.Count; i++)
            {
                var e = expected[i];
                var a = selected[i];
                if (a.Id != e.GetProperty("id").GetString())
                    mismatches.Add($"[{i}] id {a.Id}!={e.GetProperty("id").GetString()}");
                if (Math.Abs(a.ClosetBoost - e.GetProperty("boost").GetDouble()) > 1e-12)
                    mismatches.Add($"{a.Id} boost mismatch");
                if (Math.Abs((a.Distance ?? -1) - e.GetProperty("effective_distance").GetDouble()) > 1e-12)
                    mismatches.Add($"{a.Id} effective_distance mismatch");
            }

            findings.Add(new($"mcp:{row.Id}", "EXACT", mismatches.Count == 0,
                mismatches.Count == 0 ? null : string.Join("; ", mismatches)));
        }
    }

    private static void RunDedup(string corpusPath, List<ParityFinding> findings, string root)
    {
        var fetch = 0;
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var drawers = new List<DedupGrouper.DrawerRecord>();
            foreach (var d in row.Input.GetProperty("drawers").EnumerateArray())
            {
                var emb = d.GetProperty("embedding").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                drawers.Add(new DedupGrouper.DrawerRecord(
                    d.GetProperty("id").GetString()!,
                    $"/fixture/{row.Id}",
                    d.GetProperty("document").GetString()!,
                    emb,
                    fetch++));
            }

            var threshold = row.Input.TryGetProperty("threshold", out var th) ? th.GetDouble() : 0.15;
            var (kept, deleted) = DedupGrouper.DedupSourceGroup(drawers, threshold);
            var expKept = row.Expected.GetProperty("kept").EnumerateArray().Select(x => x.GetString()!).ToList();
            var expDel = row.Expected.GetProperty("deleted").EnumerateArray().Select(x => x.GetString()!).ToList();
            var mismatches = new List<string>();
            if (!kept.SequenceEqual(expKept))
                mismatches.Add($"kept [{string.Join(",", kept)}] != [{string.Join(",", expKept)}]");
            if (!deleted.SequenceEqual(expDel))
                mismatches.Add($"deleted [{string.Join(",", deleted)}] != [{string.Join(",", expDel)}]");
            findings.Add(new(row.Id, "EXACT", mismatches.Count == 0,
                mismatches.Count == 0 ? null : string.Join("; ", mismatches),
                FindGapLedgerPointer(root)));
        }
    }

    private static void RunMcp(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var input = row.Input;
            var readOnly = input.TryGetProperty("read_only", out var ro) && ro.GetBoolean();
            var mcp = new McpServer { ReadOnly = readOnly };
            var requestNode = JsonNode.Parse(input.GetProperty("request").GetRawText());
            var actual = mcp.HandleRequest(requestNode);
            var expectedWrap = row.Expected.GetProperty("response");

            if (expectedWrap.ValueKind == JsonValueKind.Null)
            {
                findings.Add(new(row.Id, "EXACT", actual is null,
                    actual is null ? null : $"expected null, got {actual}"));
                continue;
            }

            if (actual is null)
            {
                findings.Add(new(row.Id, "EXACT", false, "expected response, got null"));
                continue;
            }

            using var expDoc = JsonDocument.Parse(expectedWrap.GetRawText());
            using var actDoc = JsonDocument.Parse(actual.ToJsonString());
            var ok = JsonEqual(actDoc.RootElement, expDoc.RootElement);
            findings.Add(new(row.Id, "EXACT", ok,
                ok ? null : $"actual={actual.ToJsonString()} expected={expectedWrap.GetRawText()}"));
        }
    }

    private static void RunDeferredIds(string corpusPath, List<ParityFinding> findings, string root)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var backend = row.Input.GetProperty("backend").GetString()!;
            var value = row.Input.GetProperty("value").GetString()!;
            var expected = row.Expected.GetProperty("id").GetString()!;
            var actual = DeferredIdOracle.ExpectedId(backend, value);
            var ok = string.Equals(actual, expected, StringComparison.Ordinal);
            findings.Add(new(row.Id, "PLACEHOLDER", ok,
                ok ? "oracle staged; comparison deferred" : $"actual={actual} expected={expected}"));
        }

        var outPath = Path.Combine(root, "testdata", "deferred_ids", "oracle.jsonl");
        DeferredIdOracle.EmitOracleJsonl(outPath);
        findings.Add(new("oracle_emit", "PLACEHOLDER", File.Exists(outPath),
            File.Exists(outPath) ? "staged under testdata/deferred_ids/oracle.jsonl" : "oracle emit failed"));
    }

    private static Dictionary<string, string?> ReadStringMap(JsonElement element)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
            map[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
        return map;
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

    private static bool JsonEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var ap = a.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                var bp = b.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                if (ap.Count != bp.Count) return false;
                for (var i = 0; i < ap.Count; i++)
                {
                    if (ap[i].Name != bp[i].Name) return false;
                    if (!JsonEqual(ap[i].Value, bp[i].Value)) return false;
                }
                return true;
            }
            case JsonValueKind.Array:
            {
                var aa = a.EnumerateArray().ToList();
                var bb = b.EnumerateArray().ToList();
                if (aa.Count != bb.Count) return false;
                for (var i = 0; i < aa.Count; i++)
                    if (!JsonEqual(aa[i], bb[i])) return false;
                return true;
            }
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Number:
                return a.GetRawText() == b.GetRawText() ||
                       Math.Abs(a.GetDouble() - b.GetDouble()) < 1e-12;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return a.GetRawText() == b.GetRawText();
        }
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
        "wal" => "S10",
        "search" => "S4",
        "dedup" => "S5a",
        "mcp" => "S9",
        "deferred_ids" => "S13",
        "chunks" => "S2",
        "dates" => "S2",
        "dynamics" => "S5b",
        "locks" => "S11",
        "spellcheck" => "S12",
        "storage" => "S6",
        "model" => "S3",
        "embedding" => "S3",
        "chroma" => "S7",
        "kg" => "S5c",
        _ => module
    };
}
