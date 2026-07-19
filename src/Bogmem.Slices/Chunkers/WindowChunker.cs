using Bogmem.Slices.Common;

namespace Bogmem.Slices.Chunkers;

public sealed record TextChunk(string Content, int ChunkIndex, int LineStart, int LineEnd);

/// <summary>Raised where legacy chunk_text raises ValueError — same guard set, same reasons.</summary>
public sealed class PyValueError(string message) : ArgumentException(message);

/// <summary>
/// Port of miner.chunk_text at LEGACY_COMMIT: sliding window of
/// DEFAULT_CHUNK_SIZE code points with DEFAULT_CHUNK_OVERLAP, preferring a
/// "\n\n" then "\n" break past the window midpoint, dropping stripped chunks
/// shorter than DEFAULT_MIN_CHUNK_SIZE. All indices are code points (Python
/// str), so surrogate pairs count as one unit and are never split.
/// </summary>
public static class WindowChunker
{
    public const int DefaultChunkSize = 800;
    public const int DefaultChunkOverlap = 100;
    public const int DefaultMinChunkSize = 50;
    public const int DefaultMaxChunksPerFile = 50_000;
    public const string MaxChunksEnvVar = "MEMPALACE_MAX_CHUNKS_PER_FILE";

    public static List<TextChunk> Chunk(string content, int? chunkSize = null, int? chunkOverlap = null, int? minChunkSize = null)
    {
        int size = chunkSize ?? DefaultChunkSize;
        int overlap = chunkOverlap ?? DefaultChunkOverlap;
        int minSize = minChunkSize ?? DefaultMinChunkSize;

        if (size <= 0)
            throw new PyValueError($"chunk_size must be a positive int, got {size}");
        if (overlap < 0)
            throw new PyValueError($"chunk_overlap must be a non-negative int, got {overlap}");
        if (overlap >= size)
            throw new PyValueError(
                $"chunk_overlap ({overlap}) must be less than chunk_size ({size}); equality or greater would loop forever");
        if (minSize < 0)
            throw new PyValueError($"min_chunk_size must be a non-negative int, got {minSize}");

        var all = PyText.ToRunes(content);
        var (lo, hi) = PyText.StripBounds(all, 0, all.Length);
        int len = hi - lo;
        if (len == 0) return [];
        var runes = new int[len];
        Array.Copy(all, lo, runes, 0, len);

        var chunks = new List<TextChunk>();
        int start = 0, chunkIndex = 0;
        while (start < len)
        {
            int end = Math.Min(start + size, len);
            if (end < len)
            {
                int pos = RFind2(runes, '\n', '\n', start, end);
                if (pos > start + size / 2) end = pos;
                else
                {
                    pos = RFind1(runes, '\n', start, end);
                    if (pos > start + size / 2) end = pos;
                }
            }
            var (clo, chi) = PyText.StripBounds(runes, start, end);
            if (chi - clo >= minSize)
            {
                int lineStart = CountNewlines(runes, start) + 1;
                int lineEnd = CountNewlines(runes, end) + 1;
                chunks.Add(new TextChunk(PyText.FromRunes(runes, clo, chi - clo), chunkIndex, lineStart, lineEnd));
                chunkIndex++;
            }
            start = end < len ? end - overlap : end;
        }
        return chunks;
    }

    /// <summary>
    /// Port of miner._resolve_max_chunks_per_file: override (CLI) > env var >
    /// default; 0 disables the cap; negative or unparsable values warn on
    /// stderr and fall back to the default. Env parsing is Python int() —
    /// whitespace, sign prefix, and digit-group underscores accepted.
    /// </summary>
    public static long ResolveMaxChunksPerFile(long? overrideValue = null, string? envValue = null)
    {
        if (overrideValue is long ov)
        {
            if (ov < 0)
            {
                Console.Error.WriteLine($"  ! WARNING: --max-chunks-per-file={ov} is negative; using default {DefaultMaxChunksPerFile}");
                return DefaultMaxChunksPerFile;
            }
            return ov;
        }
        if (envValue is null) return DefaultMaxChunksPerFile;
        if (!PyText.TryPyInt(envValue, out long val))
        {
            Console.Error.WriteLine($"  ! WARNING: {MaxChunksEnvVar}='{envValue}' is not an integer; using default {DefaultMaxChunksPerFile}");
            return DefaultMaxChunksPerFile;
        }
        if (val < 0)
        {
            Console.Error.WriteLine($"  ! WARNING: {MaxChunksEnvVar}={val} is negative; using default {DefaultMaxChunksPerFile}");
            return DefaultMaxChunksPerFile;
        }
        return val;
    }

    /// <summary>Python str.rfind over [start, end) — highest index where the pattern begins.</summary>
    private static int RFind2(int[] runes, int a, int b, int start, int end)
    {
        for (int i = end - 2; i >= start; i--)
            if (runes[i] == a && runes[i + 1] == b) return i;
        return -1;
    }

    private static int RFind1(int[] runes, int a, int start, int end)
    {
        for (int i = end - 1; i >= start; i--)
            if (runes[i] == a) return i;
        return -1;
    }

    private static int CountNewlines(int[] runes, int end)
    {
        int n = 0;
        for (int i = 0; i < end; i++) if (runes[i] == '\n') n++;
        return n;
    }
}
