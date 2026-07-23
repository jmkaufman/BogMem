using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bogmem.Slices.Wal;

/// <summary>
/// WAL JSONL audit writer ported from mempalace.wal._wal_log.
/// Produces one JSON object per line; redacts sensitive keys.
/// Serialization mirrors Python json.dumps (ensure_ascii=True, separators=(', ', ': ')).
/// </summary>
public sealed class WalWriter
{
    private static readonly HashSet<string> RedactKeys = new(StringComparer.Ordinal)
    {
        "content", "content_preview", "document", "entry", "entry_preview", "query", "text"
    };

    private readonly object _gate = new();
    private readonly List<string> _lines = [];
    private readonly string? _path;
    private readonly Func<DateTime>? _clock;

    public WalWriter(string? path = null, Func<DateTime>? clock = null)
    {
        _path = path;
        _clock = clock;
    }

    public IReadOnlyList<string> Lines => _lines;

    public string FormatEntry(string operation, IReadOnlyDictionary<string, object?> parameters, object? result = null, DateTime? timestamp = null)
    {
        var safe = new Dictionary<string, object?>();
        foreach (var (k, v) in parameters)
        {
            if (RedactKeys.Contains(k))
            {
                if (v is string s)
                {
                    // Python len() counts code points, not UTF-16 units.
                    var n = 0;
                    foreach (var _ in s.EnumerateRunes()) n++;
                    safe[k] = $"[REDACTED {n} chars]";
                }
                else
                {
                    safe[k] = "[REDACTED]";
                }
            }
            else
            {
                safe[k] = v;
            }
        }

        var ts = timestamp ?? _clock?.Invoke() ?? DateTime.Now;
        // Python datetime.isoformat(): omit fractional seconds when zero.
        var tsStr = ts.Ticks % TimeSpan.TicksPerSecond == 0
            ? ts.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : ts.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append(PyKv("timestamp", tsStr));
        sb.Append(", ");
        sb.Append(PyKv("operation", operation));
        sb.Append(", ");
        sb.Append("\"params\": ");
        sb.Append(SerializePy(safe));
        sb.Append(", ");
        sb.Append("\"result\": ");
        sb.Append(result is null ? "null" : SerializePy(result));
        sb.Append('}');
        return sb.ToString();
    }

    private static string PyKv(string key, string value) =>
        $"\"{key}\": {PyString(value)}";

    /// <summary>Python json.dumps string: ensure_ascii with lowercase \uXXXX hex.</summary>
    private static string PyString(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        sb.Append('"');
        foreach (var r in s.EnumerateRunes())
        {
            var v = r.Value;
            switch (v)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (v < 0x20 || v > 0x7E)
                    {
                        // BMP → \uxxxx; non-BMP → UTF-16 surrogate pair escapes (Python ensure_ascii).
                        if (v <= 0xFFFF)
                            sb.Append("\\u").Append(v.ToString("x4", CultureInfo.InvariantCulture));
                        else
                        {
                            var enc = r.ToString();
                            foreach (var ch in enc)
                                sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                    }
                    else sb.Append((char)v);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string SerializePy(object value)
    {
        if (value is IReadOnlyDictionary<string, object?> dict)
        {
            var parts = new List<string>();
            foreach (var (k, v) in dict)
                parts.Add($"{PyString(k)}: {SerializePyScalar(v)}");
            return "{" + string.Join(", ", parts) + "}";
        }
        if (value is System.Collections.IDictionary idict)
        {
            var parts = new List<string>();
            foreach (System.Collections.DictionaryEntry e in idict)
            {
                var k = e.Key?.ToString() ?? "";
                parts.Add($"{PyString(k)}: {SerializePyScalar(e.Value)}");
            }
            return "{" + string.Join(", ", parts) + "}";
        }
        if (value is System.Collections.IEnumerable seq and not string)
        {
            var parts = new List<string>();
            foreach (var item in seq)
                parts.Add(SerializePyScalar(item));
            return "[" + string.Join(", ", parts) + "]";
        }
        return SerializePyScalar(value);
    }

    private static string SerializePyScalar(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => PyString(s),
        int or long or float or double or decimal => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        JsonElement je => SerializeJsonElement(je),
        _ when v is System.Collections.IDictionary or System.Collections.IEnumerable => SerializePy(v),
        _ => PyString(v.ToString() ?? ""),
    };

    private static string SerializeJsonElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => je.GetRawText(),
        JsonValueKind.String => PyString(je.GetString() ?? ""),
        JsonValueKind.Array =>
            "[" + string.Join(", ", je.EnumerateArray().Select(SerializeJsonElement)) + "]",
        JsonValueKind.Object =>
            "{" + string.Join(", ", je.EnumerateObject().Select(p =>
                $"{PyString(p.Name)}: {SerializeJsonElement(p.Value)}")) + "}",
        _ => je.GetRawText(),
    };

    public void Append(string operation, IReadOnlyDictionary<string, object?> parameters, object? result = null, DateTime? timestamp = null)
    {
        var line = FormatEntry(operation, parameters, result, timestamp);
        lock (_gate)
        {
            _lines.Add(line);
            if (_path is not null)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_path, line + "\n");
            }
        }
    }

    public string ToJsonl() => string.Join("\n", _lines) + (_lines.Count > 0 ? "\n" : "");
}
