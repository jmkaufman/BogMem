using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bogmem.Slices.Common;

namespace Bogmem.Slices.Chunkers;

public sealed record DiaryEntry(string Header, string Body);

public sealed record DiaryChunk(
    string Id,
    string Content,
    int ChunkIndex,
    int EntryIndex,
    int EntryChunkIndex,
    string EntryHeaderPreview);

/// <summary>
/// Port of diary_ingest at LEGACY_COMMIT: split on "## " headers (multiline,
/// dot-matches-not-newline so a CRLF header keeps its \r until strip), one
/// drawer per entry with per-entry code-point chunking when an entry exceeds
/// chunk_size. Drawer ids are the v2 scheme:
/// drawer_diary_v2_ + sha256("wing|date|entry_idx|entry_chunk_idx")[:24].
/// </summary>
public static partial class DiaryChunker
{
    [GeneratedRegex("^## .+", RegexOptions.Multiline)]
    private static partial Regex DiaryEntryRegex();

    public const int HeaderPreviewLength = 120;

    public static List<DiaryEntry> SplitEntries(string text)
    {
        var matches = DiaryEntryRegex().Matches(text);
        var entries = new List<DiaryEntry>(matches.Count);
        for (int i = 0; i < matches.Count; i++)
        {
            var header = matches[i].Value;
            int bodyStart = matches[i].Index + matches[i].Length;
            int bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var body = text[bodyStart..bodyEnd];
            entries.Add(new DiaryEntry(PyText.PyStrip(header), PyText.PyStrip(body)));
        }
        return entries;
    }

    public static string DrawerIdEntry(string wing, string dateStr, int entryIdx, int entryChunkIdx)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{wing}|{dateStr}|{entryIdx}|{entryChunkIdx}"));
        return "drawer_diary_v2_" + Convert.ToHexStringLower(hash)[..24];
    }

    public static List<DiaryChunk> Chunk(string text, string wing, string dateStr, int chunkSize)
    {
        var entries = SplitEntries(text);
        var chunks = new List<DiaryChunk>();
        int globalChunkIndex = 0;
        for (int entryIdx = 0; entryIdx < entries.Count; entryIdx++)
        {
            var (header, body) = entries[entryIdx];
            var entryText = body.Length > 0 ? $"{header}\n{body}" : header;
            var preview = PyText.RuneSubstringStart(header, HeaderPreviewLength);
            var runes = PyText.ToRunes(entryText);
            if (runes.Length <= chunkSize)
            {
                chunks.Add(new DiaryChunk(
                    DrawerIdEntry(wing, dateStr, entryIdx, 0), entryText,
                    globalChunkIndex, entryIdx, 0, preview));
                globalChunkIndex++;
            }
            else
            {
                for (int entryChunkIdx = 0, start = 0; start < runes.Length; entryChunkIdx++, start += chunkSize)
                {
                    chunks.Add(new DiaryChunk(
                        DrawerIdEntry(wing, dateStr, entryIdx, entryChunkIdx),
                        PyText.FromRunes(runes, start, Math.Min(chunkSize, runes.Length - start)),
                        globalChunkIndex, entryIdx, entryChunkIdx, preview));
                    globalChunkIndex++;
                }
            }
        }
        return chunks;
    }
}
