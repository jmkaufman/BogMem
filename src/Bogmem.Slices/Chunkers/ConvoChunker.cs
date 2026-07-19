using Bogmem.Slices.Common;

namespace Bogmem.Slices.Chunkers;

public sealed record ConvoChunk(string Content, int ChunkIndex);

/// <summary>
/// Port of convo_miner.chunk_exchanges at LEGACY_COMMIT: one "&gt;" user turn
/// plus the following AI response per unit when the transcript has at least
/// three quote lines, otherwise paragraph chunking with a 25-line-group
/// fallback for long unbroken content. The min_chunk_size floor gates the
/// whole unit (dropped when stripped length &lt;= floor); slices past the gate
/// are emitted verbatim in chunk_size code-point windows.
/// </summary>
public static class ConvoChunker
{
    public const int DefaultChunkSize = 800;
    public const int DefaultMinChunkSize = 30;
    public const int LineFallbackMinNewlines = 20;
    public const int LineGroupSize = 25;

    public static List<ConvoChunk> Chunk(string content, int? chunkSize = null, int? minChunkSize = null)
    {
        int size = chunkSize ?? DefaultChunkSize;
        int minSize = minChunkSize ?? DefaultMinChunkSize;
        if (size <= 0) throw new PyValueError($"chunk_size must be > 0, got {size}");
        if (minSize < 0) throw new PyValueError($"min_chunk_size must be >= 0, got {minSize}");

        var lines = content.Split('\n');
        int quoteLines = lines.Count(l => PyText.PyStrip(l).StartsWith('>'));
        return quoteLines >= 3
            ? ChunkByExchange(lines, size, minSize)
            : ChunkByParagraph(content, size, minSize);
    }

    private static List<ConvoChunk> ChunkByExchange(string[] lines, int chunkSize, int minChunkSize)
    {
        var chunks = new List<ConvoChunk>();
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (PyText.PyStrip(line).StartsWith('>'))
            {
                var userTurn = PyText.PyStrip(line);
                i++;
                var aiLines = new List<string>();
                while (i < lines.Length)
                {
                    var stripped = PyText.PyStrip(lines[i]);
                    if (stripped.StartsWith('>') || stripped.StartsWith("---", StringComparison.Ordinal)) break;
                    aiLines.Add(lines[i]);
                    i++;
                }
                var aiResponse = string.Join("\n", aiLines).TrimEnd('\n');
                var unit = aiResponse.Length > 0 ? $"{userTurn}\n{aiResponse}" : userTurn;
                EmitBounded(chunks, unit, chunkSize, minChunkSize);
            }
            else i++;
        }
        return chunks;
    }

    private static List<ConvoChunk> ChunkByParagraph(string content, int chunkSize, int minChunkSize)
    {
        var chunks = new List<ConvoChunk>();
        var paragraphs = content.Split("\n\n").Select(PyText.PyStrip).Where(p => p.Length > 0).ToList();

        if (paragraphs.Count <= 1 && content.Count(c => c == '\n') > LineFallbackMinNewlines)
        {
            var lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i += LineGroupSize)
            {
                var group = PyText.PyStrip(string.Join("\n", lines.Skip(i).Take(LineGroupSize)));
                EmitBounded(chunks, group, chunkSize, minChunkSize);
            }
            return chunks;
        }

        foreach (var para in paragraphs)
            EmitBounded(chunks, para, chunkSize, minChunkSize);
        return chunks;
    }

    private static void EmitBounded(List<ConvoChunk> chunks, string content, int chunkSize, int minChunkSize)
    {
        if (PyText.RuneLength(PyText.PyStrip(content)) <= minChunkSize) return;
        var runes = PyText.ToRunes(content);
        for (int i = 0; i < runes.Length; i += chunkSize)
            chunks.Add(new ConvoChunk(PyText.FromRunes(runes, i, Math.Min(chunkSize, runes.Length - i)), chunks.Count));
    }
}
