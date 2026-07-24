using System.Text.Json;
using Bogmem.Cli.Parity;
using Bogmem.Slices.Chunkers;

namespace Bogmem.Slices.Tests.Chunkers;

/// <summary>S2 — window/convo/diary chunkers, EXACT against golden/chunks/*.</summary>
public static class ChunkerTests
{
    public static void Run(TestDispositionLedgerWriter ledger)
    {
        var result = new SuiteResult("chunkers");
        RunWindow(result, ledger);
        RunConvo(result, ledger);
        RunDiary(result, ledger);
        result.ThrowIfFailed();
    }

    private static void RunWindow(SuiteResult result, TestDispositionLedgerWriter ledger)
    {
        foreach (var (id, input, expected) in TestKit.Vectors("chunks/window"))
        {
            if (id.StartsWith("cap-", StringComparison.Ordinal))
            {
                long? capOverride = input.TryGetProperty("cap_override", out var co) && co.ValueKind == JsonValueKind.Number
                    ? co.GetInt64() : null;
                string? capEnv = TestKit.OptString(input, "cap_env");
                long actual = WindowChunker.ResolveMaxChunksPerFile(capOverride, capEnv);
                long want = expected.GetProperty("cap").GetInt64();
                result.Check(id, actual == want, $"cap: actual={actual} expected={want}");
                ledger.Append(id, "chunks/window", actual == want ? "converted" : "failed", "EXACT", slice: "S2");
                continue;
            }

            var content = input.GetProperty("content").GetString()!;
            bool hasNonIntParam = HasNonIntegralParam(input);
            if (expected.TryGetProperty("error", out var err))
            {
                if (hasNonIntParam)
                {
                    // Python raises ValueError on a float chunk param; the typed C#
                    // API makes that call shape unrepresentable.
                    result.Pass();
                    ledger.Append(id, "chunks/window", "not_applicable(non-int param unrepresentable in typed C# API; legacy ValueError enforced at type level)", "EXACT", slice: "S2");
                    continue;
                }
                bool threw = false;
                try { WindowChunker.Chunk(content, TestKit.OptInt(input, "chunk_size"), TestKit.OptInt(input, "chunk_overlap"), TestKit.OptInt(input, "min_chunk_size")); }
                catch (PyValueError) { threw = true; }
                result.Check(id, threw, $"expected {err.GetString()} but no error raised");
                ledger.Append(id, "chunks/window", threw ? "converted" : "failed", "EXACT", slice: "S2");
                continue;
            }

            var chunks = WindowChunker.Chunk(content, TestKit.OptInt(input, "chunk_size"), TestKit.OptInt(input, "chunk_overlap"), TestKit.OptInt(input, "min_chunk_size"));
            var exp = expected.GetProperty("chunks");
            bool ok = chunks.Count == exp.GetArrayLength();
            int i = 0;
            foreach (var e in exp.EnumerateArray())
            {
                if (!ok) break;
                var c = chunks[i++];
                ok = c.Content == e.GetProperty("content").GetString()
                    && c.ChunkIndex == e.GetProperty("chunk_index").GetInt32()
                    && c.LineStart == e.GetProperty("line_start").GetInt32()
                    && c.LineEnd == e.GetProperty("line_end").GetInt32();
            }
            result.Check(id, ok, $"chunk mismatch (got {chunks.Count} chunks, expected {exp.GetArrayLength()})");
            ledger.Append(id, "chunks/window", ok ? "converted" : "failed", "EXACT", slice: "S2");
        }
    }

    private static void RunConvo(SuiteResult result, TestDispositionLedgerWriter ledger)
    {
        foreach (var (id, input, expected) in TestKit.Vectors("chunks/convo"))
        {
            var content = input.GetProperty("content").GetString()!;
            if (expected.TryGetProperty("error", out _))
            {
                bool threw = false;
                try { ConvoChunker.Chunk(content, TestKit.OptInt(input, "chunk_size"), TestKit.OptInt(input, "min_chunk_size")); }
                catch (PyValueError) { threw = true; }
                result.Check(id, threw, "expected ValueError but no error raised");
                ledger.Append(id, "chunks/convo", threw ? "converted" : "failed", "EXACT", slice: "S2");
                continue;
            }
            var chunks = ConvoChunker.Chunk(content, TestKit.OptInt(input, "chunk_size"), TestKit.OptInt(input, "min_chunk_size"));
            var exp = expected.GetProperty("chunks");
            bool ok = chunks.Count == exp.GetArrayLength();
            int i = 0;
            foreach (var e in exp.EnumerateArray())
            {
                if (!ok) break;
                var c = chunks[i++];
                ok = c.Content == e.GetProperty("content").GetString()
                    && c.ChunkIndex == e.GetProperty("chunk_index").GetInt32();
            }
            result.Check(id, ok, $"chunk mismatch (got {chunks.Count} chunks, expected {exp.GetArrayLength()})");
            ledger.Append(id, "chunks/convo", ok ? "converted" : "failed", "EXACT", slice: "S2");
        }
    }

    private static void RunDiary(SuiteResult result, TestDispositionLedgerWriter ledger)
    {
        foreach (var (id, input, expected) in TestKit.Vectors("chunks/diary"))
        {
            var text = input.GetProperty("text").GetString()!;
            var wing = input.GetProperty("wing").GetString()!;
            var dateStr = input.GetProperty("date_str").GetString()!;
            int chunkSize = input.GetProperty("chunk_size").GetInt32();

            var entries = DiaryChunker.SplitEntries(text);
            var expEntries = expected.GetProperty("entries");
            bool ok = entries.Count == expEntries.GetArrayLength();
            int i = 0;
            foreach (var e in expEntries.EnumerateArray())
            {
                if (!ok) break;
                var got = entries[i++];
                ok = got.Header == e[0].GetString() && got.Body == e[1].GetString();
            }

            var chunks = DiaryChunker.Chunk(text, wing, dateStr, chunkSize);
            var expChunks = expected.GetProperty("chunks");
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
            result.Check(id, ok, "diary entries/chunks mismatch");
            ledger.Append(id, "chunks/diary", ok ? "converted" : "failed", "EXACT", slice: "S2");
        }
    }

    private static bool HasNonIntegralParam(JsonElement input)
    {
        foreach (var name in (string[])["chunk_size", "chunk_overlap", "min_chunk_size"])
            if (input.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && !p.TryGetInt64(out _))
                return true;
        return false;
    }
}
