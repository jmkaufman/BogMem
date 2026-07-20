using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Slices.Common;

namespace Bogmem.Slices.Storage;

public sealed record SqliteExactRow(string Id, string Document, string MetadataJson, int Dim, byte[] Embedding);

public sealed record SqliteQueryHit(string Id, string Document, JsonObject Metadata, double Distance, double[] Embedding);

public sealed class UnsupportedFilterException(string message) : NotSupportedException(message);

/// <summary>
/// Port of the backends/sqlite_exact row-set and scoring semantics at
/// LEGACY_COMMIT. Rows keep sqlite rowid order: upsert is INSERT ... ON
/// CONFLICT DO UPDATE, so replacing a document keeps its original position.
/// Embeddings narrow to float32 on write (little-endian blob) and widen back
/// to float64 on read; query scores in float32 (dot and norms) with the final
/// division and clamp in float64 — exactly the numpy pipeline legacy runs.
/// metadata_json is json.dumps(sort_keys=True, separators=(",",":"),
/// ensure_ascii=False).
///
/// The relational substrate itself (real SQLite file, FTS mirror) belongs to
/// the backend-contract integration lane; this slice proves the observable
/// row-set + scoring contract the corpus captures, dependency-free.
/// </summary>
public sealed class SqliteExactStore
{
    private static readonly string[] SupportedOperators =
        ["$eq", "$ne", "$in", "$nin", "$contains", "$gt", "$gte", "$lt", "$lte", "$and", "$or"];

    private sealed record Row(string Id, string Document, JsonObject Metadata, float[] Embedding);

    private readonly List<Row> _rows = []; // list order == sqlite rowid order
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public static string MetadataJson(JsonObject? metadata) =>
        PyJson.Dumps(metadata ?? new JsonObject(), ensureAscii: false, sortKeys: true, compactSeparators: true);

    public static float[] EncodeVector(IReadOnlyList<double> vector)
    {
        if (vector.Count == 0) throw new ArgumentException("embedding must be a non-empty 1D vector");
        var arr = new float[vector.Count];
        for (int i = 0; i < vector.Count; i++) arr[i] = (float)vector[i];
        return arr;
    }

    public void Upsert(string id, string document, JsonObject? metadata, IReadOnlyList<double> embedding)
    {
        var row = new Row(id, document, (JsonObject?)metadata?.DeepClone() ?? [], EncodeVector(embedding));
        if (_index.TryGetValue(id, out int at)) _rows[at] = row; // ON CONFLICT UPDATE keeps rowid
        else { _index[id] = _rows.Count; _rows.Add(row); }
    }

    /// <summary>Row-set snapshot in rowid order, with the on-disk column encodings.</summary>
    public List<SqliteExactRow> RowSet() =>
        _rows.Select(r => new SqliteExactRow(
            r.Id, r.Document, MetadataJson(r.Metadata), r.Embedding.Length, VectorBlob(r.Embedding))).ToList();

    public static byte[] VectorBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * 4];
        for (int i = 0; i < vector.Length; i++)
        {
            var b = BitConverter.GetBytes(vector[i]);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            b.CopyTo(bytes, i * 4);
        }
        return bytes;
    }

    public List<SqliteQueryHit> Query(IReadOnlyList<double> queryEmbedding, int nResults, JsonObject? where = null)
    {
        ValidateWhere(where);
        var q = EncodeVector(queryEmbedding);
        double qNorm = Norm(q);
        var scored = new List<(double Distance, Row Row)>();
        foreach (var row in _rows)
        {
            if (!MatchesWhere(row.Metadata, where)) continue;
            if (row.Embedding.Length != q.Length) continue;
            double denom = qNorm * Norm(row.Embedding);
            // numpy 2.x NEP-50: float32 scalar / python float stays float32 —
            // the division itself narrows, then widens for the clamp.
            double cos = denom <= 0 ? 0.0 : (double)(Dot(q, row.Embedding) / (float)denom);
            double distance = 1.0 - Math.Max(-1.0, Math.Min(1.0, cos));
            scored.Add((distance, row));
        }
        return scored
            .OrderBy(s => s.Distance) // LINQ OrderBy is stable → ties keep rowid order
            .Take(nResults)
            .Select(s => new SqliteQueryHit(
                s.Row.Id, s.Row.Document, s.Row.Metadata, s.Distance,
                s.Row.Embedding.Select(f => (double)f).ToArray()))
            .ToList();
    }

    /// <summary>
    /// float32 dot product exactly as the pinned platform's np.dot (Accelerate
    /// sdot) computes it for the corpus vectors: per-element float32 products
    /// into two strided lane accumulators (even/odd), then one final add.
    /// Calibrated against golden/sqlite_exact — a plain sequential loop is one
    /// ULP off on some fixture pairs.
    /// </summary>
    internal static float Dot(float[] a, float[] b)
    {
        float acc0 = 0f, acc1 = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            float p = a[i] * b[i];
            if ((i & 1) == 0) acc0 += p;
            else acc1 += p;
        }
        return acc0 + acc1;
    }

    /// <summary>np.linalg.norm on a float32 vector: float32 sqrt(float32 dot(v,v)), widened.</summary>
    internal static double Norm(float[] v) => MathF.Sqrt(Dot(v, v));

    // ── where-filter semantics (legacy _validate_where / _matches_where) ────

    private static void ValidateWhere(JsonObject? where)
    {
        if (where is null) return;
        var stack = new Stack<JsonObject>();
        stack.Push(where);
        while (stack.Count > 0)
        {
            foreach (var (key, value) in stack.Pop())
            {
                if (key.StartsWith('$') && !SupportedOperators.Contains(key))
                    throw new UnsupportedFilterException($"operator '{key}' not supported by sqlite_exact");
                if (value is JsonObject o) stack.Push(o);
                else if (value is JsonArray arr)
                    foreach (var item in arr)
                        if (item is JsonObject io) stack.Push(io);
            }
        }
    }

    private static bool MatchesWhere(JsonObject meta, JsonObject? where)
    {
        if (where is null || where.Count == 0) return true;
        foreach (var (key, expected) in where)
        {
            if (key == "$and")
            {
                foreach (var clause in (expected as JsonArray) ?? [])
                    if (!MatchesWhere(meta, clause as JsonObject)) return false;
                continue;
            }
            if (key == "$or")
            {
                bool any = false;
                foreach (var clause in (expected as JsonArray) ?? [])
                    if (MatchesWhere(meta, clause as JsonObject)) { any = true; break; }
                if (!any) return false;
                continue;
            }
            if (key.StartsWith('$'))
                throw new UnsupportedFilterException($"operator '{key}' not supported by sqlite_exact");
            var actual = meta[key];
            if (expected is JsonObject ops)
            {
                foreach (var (op, operand) in ops)
                    if (!Compare(actual, op, operand)) return false;
            }
            else if (!ValuesEqual(actual, expected)) return false;
        }
        return true;
    }

    private static bool Compare(JsonNode? actual, string op, JsonNode? expected)
    {
        switch (op)
        {
            case "$eq": return ValuesEqual(actual, expected);
            case "$ne": return !ValuesEqual(actual, expected);
            case "$in":
                return expected is JsonArray inArr && inArr.Any(item => ValuesEqual(actual, item));
            case "$nin":
                return !(expected is JsonArray ninArr && ninArr.Any(item => ValuesEqual(actual, item)));
            case "$contains":
                return AsPyStr(actual).Contains(AsPyStr(expected), StringComparison.Ordinal);
            case "$gt" or "$gte" or "$lt" or "$lte":
            {
                if (!TryNumeric(actual, out double a) || !TryNumeric(expected, out double e))
                {
                    // Python compares strings ordinally; mixed types raise → False.
                    if (actual?.GetValueKind() == JsonValueKind.String && expected?.GetValueKind() == JsonValueKind.String)
                    {
                        int c = string.CompareOrdinal(actual!.GetValue<string>(), expected!.GetValue<string>());
                        return op switch { "$gt" => c > 0, "$gte" => c >= 0, "$lt" => c < 0, _ => c <= 0 };
                    }
                    return false;
                }
                return op switch { "$gt" => a > e, "$gte" => a >= e, "$lt" => a < e, _ => a <= e };
            }
            default:
                throw new UnsupportedFilterException($"operator '{op}' not supported by sqlite_exact");
        }
    }

    /// <summary>Python == with bool→int coercion (legacy _coerce_comparable).</summary>
    private static bool ValuesEqual(JsonNode? a, JsonNode? b)
    {
        bool aNum = TryNumeric(a, out double an), bNum = TryNumeric(b, out double bn);
        if (aNum && bNum) return an == bn;
        var ak = a?.GetValueKind() ?? JsonValueKind.Null;
        var bk = b?.GetValueKind() ?? JsonValueKind.Null;
        if (ak != bk) return false;
        return ak switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.String => string.Equals(a!.GetValue<string>(), b!.GetValue<string>(), StringComparison.Ordinal),
            _ => JsonNode.DeepEquals(a, b),
        };
    }

    private static bool TryNumeric(JsonNode? node, out double value)
    {
        value = 0;
        switch (node?.GetValueKind())
        {
            case JsonValueKind.Number: value = node.GetValue<double>(); return true;
            case JsonValueKind.True: value = 1; return true;
            case JsonValueKind.False: value = 0; return true;
            default: return false;
        }
    }

    private static string AsPyStr(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node!.GetValue<string>(),
        null or JsonValueKind.Null => "",
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => node!.ToJsonString(),
    };
}
