using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Cli.Parity;
using Bogmem.Slices.Chroma;
using Bogmem.Slices.Chunkers;
using Bogmem.Slices.Common;
using Bogmem.Slices.Dates;
using Bogmem.Slices.Dynamics;
using Bogmem.Slices.Embedding;
using Bogmem.Slices.KnowledgeGraph;
using Bogmem.Slices.Locking;
using Bogmem.Slices.Spellcheck;
using Bogmem.Slices.Storage;

// CLI replay for the modules delivered by the reader/storage lanes. Mirrors the
// comparison semantics of tests/Bogmem.Slices.Tests so the CLI verdict and the
// test-suite verdict for a module can never disagree.
public static partial class ParityRunner
{
    // ---- chunks (S2, EXACT) -------------------------------------------------

    private static void RunChunks(string goldenRoot, List<ParityFinding> findings)
    {
        RunChunksWindow(RequireCorpus(goldenRoot, "chunks", "window"), findings);
        RunChunksConvo(RequireCorpus(goldenRoot, "chunks", "convo"), findings);
        RunChunksDiary(RequireCorpus(goldenRoot, "chunks", "diary"), findings);
    }

    private static void RunChunksWindow(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var input = row.Input;
            if (row.Id.StartsWith("cap-", StringComparison.Ordinal))
            {
                long? capOverride = input.TryGetProperty("cap_override", out var co) && co.ValueKind == JsonValueKind.Number
                    ? co.GetInt64() : null;
                var capEnv = OptStr(input, "cap_env");
                var actual = WindowChunker.ResolveMaxChunksPerFile(capOverride, capEnv);
                var want = row.Expected.GetProperty("cap").GetInt64();
                findings.Add(new($"window:{row.Id}", "EXACT", actual == want,
                    actual == want ? null : $"cap actual={actual} expected={want}"));
                continue;
            }

            var content = input.GetProperty("content").GetString()!;
            if (row.Expected.TryGetProperty("error", out var err))
            {
                if (HasNonIntegralParam(input))
                {
                    findings.Add(new($"window:{row.Id}", "EXACT", true,
                        "not_applicable(non-int param unrepresentable in typed C# API; legacy ValueError enforced at type level)"));
                    continue;
                }
                var threw = false;
                try { WindowChunker.Chunk(content, OptIntN(input, "chunk_size"), OptIntN(input, "chunk_overlap"), OptIntN(input, "min_chunk_size")); }
                catch (PyValueError) { threw = true; }
                findings.Add(new($"window:{row.Id}", "EXACT", threw,
                    threw ? null : $"expected {err.GetString()} but no error raised"));
                continue;
            }

            var chunks = WindowChunker.Chunk(content, OptIntN(input, "chunk_size"), OptIntN(input, "chunk_overlap"), OptIntN(input, "min_chunk_size"));
            var exp = row.Expected.GetProperty("chunks");
            var ok = chunks.Count == exp.GetArrayLength();
            var i = 0;
            foreach (var e in exp.EnumerateArray())
            {
                if (!ok) break;
                var c = chunks[i++];
                ok = c.Content == e.GetProperty("content").GetString()
                    && c.ChunkIndex == e.GetProperty("chunk_index").GetInt32()
                    && c.LineStart == e.GetProperty("line_start").GetInt32()
                    && c.LineEnd == e.GetProperty("line_end").GetInt32();
            }
            findings.Add(new($"window:{row.Id}", "EXACT", ok,
                ok ? null : $"chunk mismatch (got {chunks.Count} chunks, expected {exp.GetArrayLength()})"));
        }
    }

    private static void RunChunksConvo(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var content = row.Input.GetProperty("content").GetString()!;
            if (row.Expected.TryGetProperty("error", out _))
            {
                var threw = false;
                try { ConvoChunker.Chunk(content, OptIntN(row.Input, "chunk_size"), OptIntN(row.Input, "min_chunk_size")); }
                catch (PyValueError) { threw = true; }
                findings.Add(new($"convo:{row.Id}", "EXACT", threw,
                    threw ? null : "expected ValueError but no error raised"));
                continue;
            }
            var chunks = ConvoChunker.Chunk(content, OptIntN(row.Input, "chunk_size"), OptIntN(row.Input, "min_chunk_size"));
            var exp = row.Expected.GetProperty("chunks");
            var ok = chunks.Count == exp.GetArrayLength();
            var i = 0;
            foreach (var e in exp.EnumerateArray())
            {
                if (!ok) break;
                var c = chunks[i++];
                ok = c.Content == e.GetProperty("content").GetString()
                    && c.ChunkIndex == e.GetProperty("chunk_index").GetInt32();
            }
            findings.Add(new($"convo:{row.Id}", "EXACT", ok,
                ok ? null : $"chunk mismatch (got {chunks.Count} chunks, expected {exp.GetArrayLength()})"));
        }
    }

    private static void RunChunksDiary(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var input = row.Input;
            var text = input.GetProperty("text").GetString()!;
            var entries = DiaryChunker.SplitEntries(text);
            var expEntries = row.Expected.GetProperty("entries");
            var ok = entries.Count == expEntries.GetArrayLength();
            var i = 0;
            foreach (var e in expEntries.EnumerateArray())
            {
                if (!ok) break;
                var got = entries[i++];
                ok = got.Header == e[0].GetString() && got.Body == e[1].GetString();
            }

            var chunks = DiaryChunker.Chunk(text,
                input.GetProperty("wing").GetString()!,
                input.GetProperty("date_str").GetString()!,
                input.GetProperty("chunk_size").GetInt32());
            var expChunks = row.Expected.GetProperty("chunks");
            ok = ok && chunks.Count == expChunks.GetArrayLength();
            i = 0;
            foreach (var e in expChunks.EnumerateArray())
            {
                if (!ok) break;
                var c = chunks[i++];
                ok = c.Id == e.GetProperty("id").GetString()
                    && c.Content == e.GetProperty("content").GetString()
                    && c.ChunkIndex == e.GetProperty("chunk_index").GetInt32()
                    && c.EntryIndex == e.GetProperty("entry_index").GetInt32()
                    && c.EntryChunkIndex == e.GetProperty("entry_chunk_index").GetInt32()
                    && c.EntryHeaderPreview == e.GetProperty("entry_header_preview").GetString();
            }
            findings.Add(new($"diary:{row.Id}", "EXACT", ok, ok ? null : "diary entries/chunks mismatch"));
        }
    }

    private static bool HasNonIntegralParam(JsonElement input)
    {
        foreach (var name in (string[])["chunk_size", "chunk_overlap", "min_chunk_size"])
            if (input.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && !p.TryGetInt64(out _))
                return true;
        return false;
    }

    // ---- dates (S2, EXACT) --------------------------------------------------

    private static void RunDates(string corpusPath, List<ParityFinding> findings)
    {
        var tiersSeen = new HashSet<int>();
        var count = 0;
        var tempRoot = Directory.CreateTempSubdirectory("bogmem-parity-dates").FullName;
        try
        {
            foreach (var row in CorpusReplay.Read(corpusPath))
            {
                count++;
                var sourceFile = row.Input.GetProperty("source_file").GetString()!;
                var content = row.Input.GetProperty("content").GetString()!;

                var path = sourceFile;
                if (row.Input.TryGetProperty("mtime_epoch_utc", out var mt) && mt.ValueKind == JsonValueKind.Number)
                {
                    path = Path.Combine(tempRoot, sourceFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, content);
                    File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(mt.GetDouble()));
                }

                var (iso, tier) = ContentDateExtractor.ExtractWithTier(path, content);
                var expIso = OptStr(row.Expected, "iso");
                var expTier = row.Expected.GetProperty("tier").GetInt32();
                tiersSeen.Add(expTier);
                var ok = string.Equals(iso, expIso, StringComparison.Ordinal) && (int)tier == expTier;
                findings.Add(new(row.Id, "EXACT", ok,
                    ok ? null : $"iso={iso ?? "null"} tier={(int)tier} expected iso={expIso ?? "null"} tier={expTier}"));
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
        }

        findings.Add(new("corpus_shape", "EXACT", count == 18 && tiersSeen.SetEquals([1, 2, 3, 4, 5]),
            count == 18 ? "all five tiers exercised" : $"corpus drift: {count} vectors, expected 18"));
    }

    // ---- dynamics (S5b, ULP) ------------------------------------------------

    private static readonly string[] DynamicsColumns =
        ["potentiation", "stability", "spaced", "combined_preclamp", "final_clamped"];

    private static void RunDynamics(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var record = ReadDynamicsRecord(row.Input.GetProperty("record"));
            var mode = row.Input.GetProperty("mode").GetString()!;
            var tPot = ParseDynamicsTime(row.Input, "t_pot");
            var tDecay = ParseDynamicsTime(row.Input, "t_decay");

            var capture = DynamicsScorer.Score(record, mode, tPot, tDecay);
            double[] actual = [capture.Potentiation, capture.Stability, capture.Spaced, capture.CombinedPreclamp, capture.FinalClamped];

            var expCols = row.Expected.GetProperty("columns");
            string? failDetail = null;
            for (var i = 0; i < DynamicsColumns.Length && failDetail is null; i++)
            {
                var exp = expCols.GetProperty(DynamicsColumns[i]).GetDouble();
                var ulps = FloatUlpDelta(actual[i], exp);
                if (ulps > 1)
                    failDetail = $"stage={DynamicsColumns[i]} actual={(float)actual[i]:R} expected={(float)exp:R} ulps={ulps}";
            }

            if (failDetail is null)
            {
                var expFinal = expCols.GetProperty("final_clamped").GetDouble();
                var atBoundary = (float)expFinal == (float)DynamicsScorer.StrengthFloor
                    || (float)expFinal == (float)DynamicsScorer.MaxStrength;
                if (atBoundary && (float)capture.FinalClamped != (float)expFinal)
                    failDetail = $"clamp boundary not exact: actual={(float)capture.FinalClamped:R} expected={(float)expFinal:R}";
            }

            if (failDetail is null && row.Expected.TryGetProperty("record_after", out var after))
            {
                var gotStrength = PyText.PyFloat(record.Strength, double.NaN);
                var gotStability = PyText.PyFloat(record.Stability, double.NaN);
                var gotCount = record.AccessCount is long l ? l : Convert.ToInt64(record.AccessCount ?? 0L, CultureInfo.InvariantCulture);
                if (FloatUlpDelta(gotStrength, after.GetProperty("strength").GetDouble()) > 1
                    || FloatUlpDelta(gotStability, after.GetProperty("stability").GetDouble()) > 1
                    || record.LastActivated != after.GetProperty("last_activated").GetString()
                    || gotCount != after.GetProperty("access_count").GetInt64())
                    failDetail = $"record_after mismatch: strength={gotStrength:R} last={record.LastActivated} count={gotCount}";
            }

            findings.Add(new(row.Id, "ULP", failDetail is null, failDetail));
        }
    }

    private static DynamicsRecord ReadDynamicsRecord(JsonElement el)
    {
        var record = new DynamicsRecord();
        foreach (var prop in el.EnumerateObject())
        {
            object? value = prop.Value.ValueKind switch
            {
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
            switch (prop.Name)
            {
                case "strength": record.Strength = value; break;
                case "stability": record.Stability = value; break;
                case "last_activated": record.LastActivated = value as string; break;
                case "created_at": record.CreatedAt = value as string; break;
                case "access_count": record.AccessCount = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt64() : value; break;
            }
        }
        return record;
    }

    private static DateTimeOffset? ParseDynamicsTime(JsonElement input, string name)
    {
        var s = OptStr(input, name);
        if (s is null) return null;
        if (!DynamicsScorer.TryParseIso(s, out var dt))
            throw new CorpusException($"unparsable fixture time {name}={s}");
        return dt;
    }

    // ---- locks (S11, EXACT) -------------------------------------------------

    private static void RunLocks(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var kind = row.Input.GetProperty("kind").GetString();
            switch (kind)
            {
                case "palace_key":
                {
                    var (resolved, key, lockFileName) = FileLock.PalaceKey(row.Input.GetProperty("path").GetString()!);
                    var ok = resolved == row.Expected.GetProperty("resolved").GetString()
                        && key == row.Expected.GetProperty("key").GetString()
                        && lockFileName == row.Expected.GetProperty("lock_file_name").GetString();
                    findings.Add(new(row.Id, "EXACT", ok, ok ? null : $"resolved={resolved} key={key} name={lockFileName}"));
                    break;
                }
                case "mine_lock_name":
                {
                    var name = FileLock.MineLockFileName(row.Input.GetProperty("path").GetString()!);
                    var ok = name == row.Expected.GetProperty("lock_file_name").GetString();
                    findings.Add(new(row.Id, "EXACT", ok, ok ? null : $"lock_file_name={name}"));
                    break;
                }
                case "holder_format":
                {
                    var holder = FileLock.FormatLockHolder(row.Input.GetProperty("content").GetString()!);
                    var ok = holder == row.Expected.GetProperty("holder").GetString();
                    findings.Add(new(row.Id, "EXACT", ok, ok ? null : $"holder={holder}"));
                    break;
                }
                default:
                    findings.Add(new(row.Id, "EXACT", false, $"unknown fixture kind {kind}"));
                    break;
            }
        }

        RunLockSemantics(findings);
    }

    private static void RunLockSemantics(List<ParityFinding> findings)
    {
        var lockDir = Directory.CreateTempSubdirectory("bogmem-parity-locks").FullName;
        var palace = Path.Combine(lockDir, "palace");
        try
        {
            var ok = true;
            var detail = new List<string>();
            using (FileLock.Acquire(palace, lockDir))
            {
                using var inner = FileLock.Acquire(palace, lockDir);
                var (_, _, lockFileName) = FileLock.PalaceKey(palace);
                var lockPath = Path.Combine(lockDir, lockFileName);
                if (!File.Exists(lockPath)) { ok = false; detail.Add("lock file missing while held"); }
                else
                {
                    var holder = FileLock.ReadLockHolder(lockPath);
                    if (!holder.StartsWith($"PID {Environment.ProcessId}", StringComparison.Ordinal))
                    { ok = false; detail.Add($"holder={holder}"); }
                }
            }
            using (FileLock.Acquire(palace, lockDir)) { }

            var (_, _, name2) = FileLock.PalaceKey(palace);
            var path2 = Path.Combine(lockDir, name2);
            using (var foreign = new FileStream(path2, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                foreign.SetLength(0);
                foreign.Write(Encoding.UTF8.GetBytes("\012345 mempalace mine ~/proj"));
                foreign.Flush();
                var threw = false;
                var message = "";
                try { FileLock.Acquire(palace, lockDir).Dispose(); }
                catch (MineAlreadyRunningException ex) { threw = true; message = ex.Message; }
                if (!threw) { ok = false; detail.Add("expected MineAlreadyRunningException"); }
                else if (!message.Contains("is held by PID 12345 (mempalace mine ~/proj)", StringComparison.Ordinal))
                { ok = false; detail.Add($"message={message}"); }
            }
            findings.Add(new("semantics", "EXACT", ok,
                ok ? "non-blocking + re-entrant + byte-1 holder identity verified in-process" : string.Join("; ", detail)));
        }
        finally
        {
            try { Directory.Delete(lockDir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- spellcheck (S12, BOUNDED >= 0.95 aggregate token agreement) --------

    private const double SpellcheckAgreementBar = 0.95;

    private static void RunSpellcheck(string corpusPath, List<ParityFinding> findings)
    {
        long agree = 0, total = 0;
        var perRow = new List<(string Id, long Agree, long Total)>();
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            long rowAgree = 0, rowTotal = 0;
            switch (row.Input.GetProperty("kind").GetString())
            {
                case "words":
                {
                    var speller = new Speller();
                    foreach (var pair in row.Expected.GetProperty("pairs").EnumerateArray())
                    {
                        rowTotal++;
                        if (speller.AutocorrectSentence(pair.GetProperty("token").GetString()!)
                            == pair.GetProperty("corrected").GetString()) rowAgree++;
                    }
                    break;
                }
                case "user_text":
                {
                    var known = row.Input.GetProperty("known_names").EnumerateArray().Select(x => x.GetString()!);
                    var got = new Speller(known).SpellcheckUserText(row.Input.GetProperty("text").GetString()!);
                    (rowAgree, rowTotal) = TokenAgreement(got, row.Expected.GetProperty("corrected").GetString()!);
                    break;
                }
                case "transcript":
                {
                    var got = new Speller().SpellcheckTranscript(row.Input.GetProperty("content").GetString()!);
                    (rowAgree, rowTotal) = TokenAgreement(got, row.Expected.GetProperty("corrected").GetString()!);
                    break;
                }
                case "edit_distance":
                {
                    var expDists = row.Expected.GetProperty("distances").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    var i = 0;
                    foreach (var pair in row.Input.GetProperty("pairs").EnumerateArray())
                    {
                        rowTotal++;
                        if (Speller.EditDistance(pair[0].GetString()!, pair[1].GetString()!) == expDists[i++]) rowAgree++;
                    }
                    break;
                }
                case "system_words":
                {
                    var speller = new Speller();
                    var members = row.Expected.GetProperty("members").EnumerateArray()
                        .Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
                    foreach (var probe in row.Input.GetProperty("probe").EnumerateArray().Select(x => x.GetString()!))
                    {
                        rowTotal++;
                        if (speller.SystemWords.Contains(probe) == members.Contains(probe)) rowAgree++;
                    }
                    break;
                }
                default:
                    findings.Add(new(row.Id, "BOUNDED", false, "unknown fixture kind"));
                    continue;
            }
            agree += rowAgree;
            total += rowTotal;
            perRow.Add((row.Id, rowAgree, rowTotal));
        }

        var agreement = total == 0 ? 0 : (double)agree / total;
        var pass = agreement >= SpellcheckAgreementBar;
        foreach (var (id, a, t) in perRow)
            findings.Add(new(id, "BOUNDED", pass,
                $"row agreement {a}/{t}; aggregate {agreement.ToString("F4", CultureInfo.InvariantCulture)}"));
        findings.Add(new("aggregate", "BOUNDED", pass,
            $"token agreement {agreement.ToString("F4", CultureInfo.InvariantCulture)} ({agree}/{total}) vs bar {SpellcheckAgreementBar}"));
    }

    private static (long Agree, long Total) TokenAgreement(string actual, string expected)
    {
        var a = PySplitTokens(actual);
        var e = PySplitTokens(expected);
        long agree = 0;
        for (var i = 0; i < Math.Min(a.Count, e.Count); i++)
            if (string.Equals(a[i], e[i], StringComparison.Ordinal)) agree++;
        return (agree, Math.Max(Math.Max(a.Count, e.Count), 1));
    }

    private static List<string> PySplitTokens(string s)
    {
        var tokens = new List<string>();
        var runes = PyText.ToRunes(s);
        var i = 0;
        while (i < runes.Length)
        {
            while (i < runes.Length && PyText.IsPySpace(runes[i])) i++;
            var start = i;
            while (i < runes.Length && !PyText.IsPySpace(runes[i])) i++;
            if (i > start) tokens.Add(PyText.FromRunes(runes, start, i - start));
        }
        return tokens;
    }

    // ---- storage (S6, EXACT) — graph_files + sqlite_exact -------------------

    private static void RunGraphFiles(string corpusPath, List<ParityFinding> findings)
    {
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var kind = row.Input.GetProperty("kind").GetString();
            switch (kind)
            {
                case "tunnels":
                {
                    var content = GraphStore.SerializeTunnels(JsonNode.Parse(row.Input.GetProperty("records").GetRawText()));
                    var ok = content == row.Expected.GetProperty("content").GetString();
                    var dir = Directory.CreateTempSubdirectory("bogmem-parity-tunnels").FullName;
                    try
                    {
                        var file = Path.Combine(dir, "tunnels.json");
                        GraphStore.SaveTunnels(file, JsonNode.Parse(row.Input.GetProperty("records").GetRawText()));
                        ok = ok && File.ReadAllText(file, Encoding.UTF8) == row.Expected.GetProperty("content").GetString()
                                && !File.Exists(file + ".tmp");
                    }
                    finally { Directory.Delete(dir, recursive: true); }
                    findings.Add(new($"graph_files:{row.Id}", "EXACT", ok, ok ? null : "tunnels.json content mismatch"));
                    break;
                }
                case "hallways":
                {
                    var content = GraphStore.SerializeHallways(JsonNode.Parse(row.Input.GetProperty("records").GetRawText()));
                    var ok = content == row.Expected.GetProperty("content").GetString();
                    var dir = Directory.CreateTempSubdirectory("bogmem-parity-hallways").FullName;
                    try
                    {
                        var file = Path.Combine(dir, "hallways.json");
                        GraphStore.SaveHallways(file, JsonNode.Parse(row.Input.GetProperty("records").GetRawText()));
                        ok = ok && File.ReadAllText(file, Encoding.UTF8) == row.Expected.GetProperty("content").GetString()
                                && Directory.GetFiles(dir, ".hallways-*.tmp").Length == 0;
                    }
                    finally { Directory.Delete(dir, recursive: true); }
                    findings.Add(new($"graph_files:{row.Id}", "EXACT", ok, ok ? null : "hallways.json content mismatch"));
                    break;
                }
                case "sidecar":
                {
                    var ok = RunSidecarOps(row.Input, row.Expected, out var detail);
                    findings.Add(new($"graph_files:{row.Id}", "EXACT", ok, ok ? null : detail));
                    break;
                }
                default:
                    findings.Add(new($"graph_files:{row.Id}", "EXACT", false, $"unknown fixture kind {kind}"));
                    break;
            }
        }
    }

    private static bool RunSidecarOps(JsonElement input, JsonElement expected, out string detail)
    {
        var dir = Directory.CreateTempSubdirectory("bogmem-parity-sidecar").FullName;
        try
        {
            var path = Path.Combine(dir, GraphStore.EmbedderSidecarFilename);
            if (input.TryGetProperty("preexisting", out var pre) && pre.ValueKind == JsonValueKind.String)
                File.WriteAllText(path, pre.GetString()!, new UTF8Encoding(false));

            var reads = new List<EmbedderIdentity?>();
            foreach (var op in input.GetProperty("ops").EnumerateArray())
            {
                var verb = op[0].GetString();
                var collection = op[1].GetString();
                if (verb == "write")
                    GraphStore.WriteEmbedderSidecar(path, collection, new EmbedderIdentity(op[2].GetString()!, op[3].GetInt32()));
                else
                    reads.Add(GraphStore.ReadEmbedderSidecar(path, collection));
            }

            var expReads = expected.GetProperty("reads");
            if (reads.Count != expReads.GetArrayLength()) { detail = "read count mismatch"; return false; }
            var i = 0;
            foreach (var e in expReads.EnumerateArray())
            {
                var got = reads[i++];
                if (e.ValueKind == JsonValueKind.Null)
                {
                    if (got is not null) { detail = $"read {i - 1}: expected null, got {got}"; return false; }
                }
                else if (got is null
                    || got.ModelName != e.GetProperty("model_name").GetString()
                    || got.Dimension != e.GetProperty("dimension").GetInt32())
                {
                    detail = $"read {i - 1} mismatch: got {got}";
                    return false;
                }
            }

            var expContent = expected.GetProperty("content");
            if (expContent.ValueKind == JsonValueKind.Null)
            {
                if (File.Exists(path)) { detail = "sidecar file should not exist"; return false; }
            }
            else if ((File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null) != expContent.GetString())
            {
                detail = "content mismatch";
                return false;
            }
            detail = "";
            return true;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void RunSqliteExact(string corpusPath, List<ParityFinding> findings)
    {
        var store = new SqliteExactStore();
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            if (row.Id == "rowset")
            {
                foreach (var doc in row.Input.GetProperty("docs").EnumerateArray())
                    UpsertSqliteDoc(store, doc);
                UpsertSqliteDoc(store, row.Input.GetProperty("upsert"));

                var rows = store.RowSet();
                var exp = row.Expected.GetProperty("rows");
                var ok = rows.Count == exp.GetArrayLength();
                var i = 0;
                foreach (var e in exp.EnumerateArray())
                {
                    if (!ok) break;
                    var r = rows[i++];
                    ok = r.Id == e.GetProperty("id").GetString()
                        && r.Document == e.GetProperty("document").GetString()
                        && r.MetadataJson == e.GetProperty("metadata_json").GetString()
                        && r.Dim == e.GetProperty("dim").GetInt32()
                        && Convert.ToHexString(r.Embedding) == e.GetProperty("embedding_hex").GetString();
                }
                findings.Add(new($"sqlite_exact:{row.Id}", "EXACT", ok, ok ? null : "row-set mismatch"));
                continue;
            }

            var embedding = row.Input.GetProperty("embedding").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var nResults = row.Input.GetProperty("n_results").GetInt32();
            var where = row.Input.TryGetProperty("where", out var w) && w.ValueKind == JsonValueKind.Object
                ? (JsonObject)JsonNode.Parse(w.GetRawText())! : null;
            var hits = store.Query(embedding, nResults, where);

            var expIds = row.Expected.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var expDocs = row.Expected.GetProperty("documents").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var expDists = row.Expected.GetProperty("distances").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var expMetas = row.Expected.GetProperty("metadatas").EnumerateArray().ToArray();
            var expEmbeds = row.Expected.GetProperty("embeddings").EnumerateArray()
                .Select(a => a.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToArray();

            var qok = hits.Count == expIds.Length;
            var qdetail = qok ? null : $"hit count {hits.Count} != {expIds.Length}";
            for (var i = 0; qok && i < hits.Count; i++)
            {
                qok = hits[i].Id == expIds[i]
                    && hits[i].Document == expDocs[i]
                    && DoubleBitsEqual(hits[i].Distance, expDists[i])
                    && SqliteExactStore.MetadataJson(hits[i].Metadata)
                        == SqliteExactStore.MetadataJson((JsonObject)JsonNode.Parse(expMetas[i].GetRawText())!)
                    && hits[i].Embedding.Length == expEmbeds[i].Length
                    && hits[i].Embedding.Zip(expEmbeds[i]).All(p => DoubleBitsEqual(p.First, p.Second));
                if (!qok)
                    qdetail = $"hit {i}: id={hits[i].Id} dist={hits[i].Distance:R} expected id={expIds[i]} dist={expDists[i]:R}";
            }
            findings.Add(new($"sqlite_exact:{row.Id}", "EXACT", qok, qok ? null : qdetail));
        }
    }

    private static void UpsertSqliteDoc(SqliteExactStore store, JsonElement doc)
    {
        store.Upsert(
            doc[0].GetString()!,
            doc[1].GetString()!,
            (JsonObject)JsonNode.Parse(doc[2].GetRawText())!,
            doc[3].EnumerateArray().Select(x => x.GetDouble()).ToArray());
    }

    // ---- model (S3, EXACT + PLACEHOLDER for unvendored bytes) ---------------

    private static void RunModel(string corpusPath, string goldenRoot, List<ParityFinding> findings)
    {
        var inventories = CorpusReplay.Read(corpusPath)
            .Select(row => ModelInventory.FromVector(row.Id, row.Input, row.Expected))
            .ToList();
        if (inventories.Count != 1)
        {
            findings.Add(new("inventory", "EXACT", false, $"corpus drift: {inventories.Count} vectors, expected 1"));
            return;
        }
        var inv = inventories[0];

        var mismatches = new List<string>();
        using (var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(goldenRoot, "model", "manifest.json"))))
        {
            var manifest = manifestDoc.RootElement;
            if (manifest.GetProperty("model").GetString() != inv.ModelId)
                mismatches.Add($"model id {inv.ModelId} != manifest {manifest.GetProperty("model").GetString()}");
            if (manifest.GetProperty("dims").GetInt32() != inv.Dims || inv.Dims != 384)
                mismatches.Add($"dims {inv.Dims} != manifest/384");
            if (inv.EfName != "default")
                mismatches.Add($"ef_name {inv.EfName} != default");
            foreach (var rel in inv.Files.Keys)
                if (!rel.StartsWith("onnx/", StringComparison.Ordinal) && rel != "onnx.tar.gz")
                    mismatches.Add($"unexpected inventory path shape: {rel}");
            if (!inv.Files.ContainsKey("onnx/model.onnx"))
                mismatches.Add("inventory is missing onnx/model.onnx");
        }
        findings.Add(new("inventory_vs_manifest", "EXACT", mismatches.Count == 0,
            mismatches.Count == 0 ? null : string.Join("; ", mismatches)));

        var tempRoot = Directory.CreateTempSubdirectory("bogmem-parity-model").FullName;
        try
        {
            var cacheMismatches = new List<string>();
            var missing = inv.VerifyCacheDir(tempRoot);
            if (missing.Count != inv.Files.Count || missing.Any(c => c.Present || c.Matches))
                cacheMismatches.Add("empty cache dir did not report every inventoried file as missing");

            var firstRel = inv.Files.Keys.First();
            var corrupted = Path.Combine(tempRoot, firstRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(corrupted)!);
            var bytes = Encoding.UTF8.GetBytes("not the pinned model bytes");
            File.WriteAllBytes(corrupted, bytes);
            using var ms = new MemoryStream(bytes);
            var expectedActual = ModelInventory.Sha256Hex(ms);

            var check = inv.VerifyCacheDir(tempRoot).Single(c => c.RelativePath == firstRel);
            if (!check.Present || check.Matches)
                cacheMismatches.Add($"corrupted {firstRel} not flagged");
            if (check.ActualSha256 != expectedActual)
                cacheMismatches.Add("hasher disagrees with independent sha256");
            if (check.ExpectedSha256 != inv.Files[firstRel])
                cacheMismatches.Add("check lost the golden digest");
            if (inv.IsCacheVerified(tempRoot))
                cacheMismatches.Add("IsCacheVerified passed a corrupted cache");
            findings.Add(new("cache_verification", "EXACT", cacheMismatches.Count == 0,
                cacheMismatches.Count == 0 ? null : string.Join("; ", cacheMismatches)));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
        }

        findings.Add(new("model_bytes_not_vendored", "PLACEHOLDER", true,
            "the ~90MB ONNX blobs are not vendored; live-cache byte verification runs out-of-band via ModelInventory.IsCacheVerified"));
    }

    // ---- embedding (S3, PLACEHOLDER until real ONNX inference lands) --------

    private static void RunEmbedding(string corpusPath, string goldenRoot, List<ParityFinding> findings)
    {
        int dims;
        using (var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(goldenRoot, "embedding", "manifest.json"))))
            dims = manifestDoc.RootElement.GetProperty("dims").GetInt32();

        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            var text = row.Input.GetProperty("text").GetString();
            var vector = row.Expected.GetProperty("vector").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var ok = text is not null && vector.Length == dims && vector.All(double.IsFinite);
            findings.Add(new(row.Id, "PLACEHOLDER", ok, ok
                ? $"golden vector well-formed ({dims} finite dims); ULP replay deferred until real ONNX inference replaces the EmbeddingPipeline stub (model bytes not vendored — see golden/embedding/manifest.json)"
                : $"malformed golden vector: text={text is not null} dims={vector.Length}"));
        }

        var probe = new Embedder().Embed("probe");
        findings.Add(new("dimension_smoke", "EXACT", probe.Length == Embedder.OutputDimensions,
            probe.Length == Embedder.OutputDimensions ? null : $"embedder produced {probe.Length} dims"));
    }

    // ---- chroma (S7, BOUNDED — Jaccard over ANN top-n per ChromaParityGuards) --

    private static void RunChroma(string corpusPath, List<ParityFinding> findings)
    {
        var store = new ChromaStore();
        var jaccards = new List<double>();
        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            if (row.Id == "corpus")
            {
                foreach (var v in row.Input.GetProperty("vectors").EnumerateArray())
                    store.Add(v.GetProperty("id").GetString()!,
                        v.GetProperty("embedding").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray());
                var ok = store.Count == row.Expected.GetProperty("count").GetInt32();
                findings.Add(new(row.Id, "EXACT", ok,
                    ok ? null : $"count {store.Count} != {row.Expected.GetProperty("count").GetInt32()}"));
                continue;
            }

            var query = row.Input.GetProperty("query_embedding").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            var n = row.Input.GetProperty("n_results").GetInt32();
            var first = store.Search(query, n).Select(x => x.Id).ToList();
            var second = store.Search(query, n).Select(x => x.Id).ToList();
            ChromaParityGuards.AssertSelfConsistent([first, second]);

            var expIds = row.Expected.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToList();
            var jaccard = ChromaStore.Jaccard(first, expIds);
            jaccards.Add(jaccard);
            var ok2 = jaccard >= 0.8;
            findings.Add(new(row.Id, "BOUNDED", ok2,
                $"top-{n} Jaccard {jaccard.ToString("0.####", CultureInfo.InvariantCulture)} vs legacy HNSW capture (bound 0.8); ordering actual=[{string.Join(",", first)}] expected=[{string.Join(",", expIds)}]"));
        }
        var mean = jaccards.Count == 0 ? 1 : jaccards.Average();
        findings.Add(new("mean_jaccard", "BOUNDED", mean >= 0.8,
            $"mean top-n Jaccard {mean.ToString("0.####", CultureInfo.InvariantCulture)} vs bound 0.8"));
    }

    // ---- kg (S5c, EXACT) ----------------------------------------------------

    private static void RunKg(string corpusPath, string goldenRoot, List<ParityFinding> findings)
    {
        DateTime frozen;
        using (var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(goldenRoot, "kg", "manifest.json"))))
            frozen = DateTime.ParseExact(manifestDoc.RootElement.GetProperty("frozen_recorded_at").GetString()!,
                "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        using var store = new KnowledgeGraphStore(recordedAt: frozen);
        var opIds = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in CorpusReplay.Read(corpusPath))
        {
            switch (row.Id)
            {
                case "all_triples":
                {
                    var mismatches = new List<string>();
                    foreach (var op in row.Input.GetProperty("operations").EnumerateArray())
                    {
                        var args = op.GetProperty("args");
                        var label = op.GetProperty("label").GetString()!;
                        if (op.GetProperty("op").GetString() == "add_triple")
                        {
                            var triple = store.Add(
                                args.GetProperty("subject").GetString()!,
                                args.GetProperty("predicate").GetString()!,
                                args.GetProperty("obj").GetString()!,
                                ParseDateOnly(OptStr(args, "valid_from")));
                            opIds[label] = triple.Id;
                            var expId = op.GetProperty("id").GetString()!;
                            if (triple.Id != expId)
                                mismatches.Add($"{label}: id {triple.Id} != {expId}");
                        }
                        else
                        {
                            if (!store.Invalidate(
                                args.GetProperty("subject").GetString()!,
                                args.GetProperty("predicate").GetString()!,
                                args.GetProperty("obj").GetString()!,
                                ParseDateOnly(args.GetProperty("ended").GetString())!.Value))
                                mismatches.Add($"{label}: invalidate matched no triple");
                        }
                    }

                    var all = store.All();
                    var exp = row.Expected.GetProperty("triples").EnumerateArray().ToArray();
                    if (all.Count != exp.Length)
                        mismatches.Add($"triple count {all.Count} != {exp.Length}");
                    for (var i = 0; i < Math.Min(all.Count, exp.Length); i++)
                    {
                        var t = all[i];
                        var e = exp[i];
                        if (t.Id != e.GetProperty("id").GetString()
                            || t.Subject != e.GetProperty("subject").GetString()
                            || t.Predicate != e.GetProperty("predicate").GetString()
                            || t.Object != e.GetProperty("object").GetString()
                            || t.ValidFrom?.ToString("yyyy-MM-dd") != OptStr(e, "valid_from")
                            || t.ValidTo?.ToString("yyyy-MM-dd") != OptStr(e, "valid_to")
                            || Math.Abs(t.Confidence - e.GetProperty("confidence").GetDouble()) > 0)
                            mismatches.Add($"triples[{i}] mismatch: {t.Id}");
                    }
                    findings.Add(new(row.Id, "EXACT", mismatches.Count == 0,
                        mismatches.Count == 0 ? null : string.Join("; ", mismatches)));
                    break;
                }
                case "assertions":
                {
                    var mismatches = new List<string>();
                    if (row.Expected.GetProperty("acme_and_newco_distinct").GetBoolean()
                        && opIds.TryGetValue("acme", out var acmeId) && opIds.TryGetValue("newco", out var newcoId)
                        && acmeId == newcoId)
                        mismatches.Add("acme and newco ids not distinct");
                    if (row.Expected.GetProperty("duplicate_open_triple_same_id").GetBoolean()
                        && opIds.TryGetValue("knows_1", out var k1) && opIds.TryGetValue("knows_dup", out var k2)
                        && k1 != k2)
                        mismatches.Add($"duplicate open triple ids differ: {k1} vs {k2}");
                    if (row.Expected.GetProperty("inverted_interval_rejected").GetBoolean())
                    {
                        using var scratch = new KnowledgeGraphStore(recordedAt: frozen);
                        scratch.Add("A", "p", "B", new DateOnly(2020, 1, 1));
                        var threw = false;
                        try { scratch.Invalidate("A", "p", "B", new DateOnly(2019, 1, 1)); }
                        catch (ArgumentException) { threw = true; }
                        if (!threw) mismatches.Add("inverted interval was not rejected");
                    }
                    findings.Add(new(row.Id, "EXACT", mismatches.Count == 0,
                        mismatches.Count == 0 ? null : string.Join("; ", mismatches)));
                    break;
                }
                default:
                {
                    var rows = store.Query(
                        row.Input.GetProperty("entity").GetString()!,
                        ParseDateOnly(OptStr(row.Input, "as_of")),
                        OptStr(row.Input, "direction") ?? "outgoing");
                    var exp = row.Expected.GetProperty("rows").EnumerateArray().ToArray();
                    var mismatches = new List<string>();
                    if (rows.Count != exp.Length)
                        mismatches.Add($"row count {rows.Count} != {exp.Length}");
                    for (var i = 0; i < Math.Min(rows.Count, exp.Length); i++)
                    {
                        var r = rows[i];
                        var e = exp[i];
                        if (r.Direction != e.GetProperty("direction").GetString()
                            || r.Subject != e.GetProperty("subject").GetString()
                            || r.Predicate != e.GetProperty("predicate").GetString()
                            || r.Object != e.GetProperty("object").GetString()
                            || r.ValidFrom?.ToString("yyyy-MM-dd") != OptStr(e, "valid_from")
                            || r.ValidTo?.ToString("yyyy-MM-dd") != OptStr(e, "valid_to")
                            || Math.Abs(r.Confidence - e.GetProperty("confidence").GetDouble()) > 0
                            || r.Current != e.GetProperty("current").GetBoolean())
                            mismatches.Add($"rows[{i}]: got {r.Subject}/{r.Predicate}/{r.Object} vf={r.ValidFrom:yyyy-MM-dd} expected {e.GetProperty("predicate").GetString()}/{e.GetProperty("object").GetString()}");
                    }
                    findings.Add(new(row.Id, "EXACT", mismatches.Count == 0,
                        mismatches.Count == 0 ? null : string.Join("; ", mismatches)));
                    break;
                }
            }
        }
    }

    private static DateOnly? ParseDateOnly(string? value) =>
        value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- shared helpers -----------------------------------------------------

    private static string? OptStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? OptIntN(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static bool DoubleBitsEqual(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static long FloatUlpDelta(double actual, double expected)
    {
        float a = (float)actual, e = (float)expected;
        if (a == e) return 0;
        if (float.IsNaN(a) || float.IsNaN(e)) return long.MaxValue;
        long ai = BitConverter.SingleToInt32Bits(a), ei = BitConverter.SingleToInt32Bits(e);
        if (ai < 0) ai = int.MinValue - ai;
        if (ei < 0) ei = int.MinValue - ei;
        return Math.Abs(ai - ei);
    }
}
