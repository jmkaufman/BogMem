using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bogmem.Slices.Common;

/// <summary>
/// Byte-exact Python json.dump emitter over JsonNode trees. The S6 storage
/// files (tunnels.json, hallways.json, embedder sidecar, sqlite_exact
/// metadata_json) are compared byte-for-byte against the legacy corpus, so the
/// serializer must reproduce CPython's escaping, float repr, and indent rules
/// exactly — System.Text.Json's writer cannot be configured to match.
/// </summary>
public static class PyJson
{
    public static string Dumps(JsonNode? node, int? indent = null, bool ensureAscii = true, bool sortKeys = false, bool compactSeparators = false)
    {
        var sb = new StringBuilder();
        WriteNode(sb, node, indent, ensureAscii, sortKeys, compactSeparators, 0);
        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, JsonNode? node, int? indent, bool ensureAscii, bool sortKeys, bool compact, int depth)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
                WriteObject(sb, obj, indent, ensureAscii, sortKeys, compact, depth);
                return;
            case JsonArray arr:
                WriteArray(sb, arr, indent, ensureAscii, sortKeys, compact, depth);
                return;
            case JsonValue val:
                WriteValue(sb, val, ensureAscii);
                return;
            default:
                throw new NotSupportedException(node.GetType().Name);
        }
    }

    private static void WriteObject(StringBuilder sb, JsonObject obj, int? indent, bool ensureAscii, bool sortKeys, bool compact, int depth)
    {
        if (obj.Count == 0) { sb.Append("{}"); return; }
        IEnumerable<KeyValuePair<string, JsonNode?>> items = obj;
        if (sortKeys) items = obj.OrderBy(kv => kv.Key, StringComparer.Ordinal);
        sb.Append('{');
        bool first = true;
        foreach (var (key, value) in items)
        {
            if (!first) sb.Append(indent is null ? (compact ? "," : ", ") : ",");
            first = false;
            if (indent is int n) { sb.Append('\n'); sb.Append(' ', n * (depth + 1)); }
            WriteString(sb, key, ensureAscii);
            sb.Append(compact ? ":" : ": ");
            WriteNode(sb, value, indent, ensureAscii, sortKeys, compact, depth + 1);
        }
        if (indent is int m) { sb.Append('\n'); sb.Append(' ', m * depth); }
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, JsonArray arr, int? indent, bool ensureAscii, bool sortKeys, bool compact, int depth)
    {
        if (arr.Count == 0) { sb.Append("[]"); return; }
        sb.Append('[');
        bool first = true;
        foreach (var item in arr)
        {
            if (!first) sb.Append(indent is null ? (compact ? "," : ", ") : ",");
            first = false;
            if (indent is int n) { sb.Append('\n'); sb.Append(' ', n * (depth + 1)); }
            WriteNode(sb, item, indent, ensureAscii, sortKeys, compact, depth + 1);
        }
        if (indent is int m) { sb.Append('\n'); sb.Append(' ', m * depth); }
        sb.Append(']');
    }

    private static void WriteValue(StringBuilder sb, JsonValue val, bool ensureAscii)
    {
        // Values built in code wrap CLR primitives rather than JsonElements.
        if (!val.TryGetValue<JsonElement>(out var el))
        {
            switch (val.GetValue<object>())
            {
                case string str: WriteString(sb, str, ensureAscii); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(PyFloatRepr(d)); return;
                case float f: sb.Append(PyFloatRepr(f)); return;
                default: throw new NotSupportedException(val.GetValue<object>().GetType().Name);
            }
        }
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                WriteString(sb, el.GetString()!, ensureAscii);
                return;
            case JsonValueKind.True:
                sb.Append("true");
                return;
            case JsonValueKind.False:
                sb.Append("false");
                return;
            case JsonValueKind.Null:
                sb.Append("null");
                return;
            case JsonValueKind.Number:
                // Python json.load keeps int vs float from the source token; the
                // raw text is the only place that distinction survives in .NET.
                var raw = el.GetRawText();
                if (raw.IndexOfAny(['.', 'e', 'E']) >= 0) sb.Append(PyFloatRepr(el.GetDouble()));
                else sb.Append(raw);
                return;
            default:
                throw new NotSupportedException(el.ValueKind.ToString());
        }
    }

    public static void WriteString(StringBuilder sb, string s, bool ensureAscii)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); continue;
                case '\\': sb.Append("\\\\"); continue;
                case '\n': sb.Append("\\n"); continue;
                case '\r': sb.Append("\\r"); continue;
                case '\t': sb.Append("\\t"); continue;
                case '\b': sb.Append("\\b"); continue;
                case '\f': sb.Append("\\f"); continue;
            }
            if (c < 0x20 || (ensureAscii && c > 0x7E))
                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        sb.Append('"');
    }

    /// <summary>
    /// CPython repr(float): shortest round-trip digits, fixed notation for
    /// decimal exponent in [-4, 16), otherwise scientific with a signed
    /// two-digit-minimum exponent and lowercase 'e'.
    /// </summary>
    public static string PyFloatRepr(double v)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsPositiveInfinity(v)) return "inf";
        if (double.IsNegativeInfinity(v)) return "-inf";
        bool neg = double.IsNegative(v);
        if (v == 0.0) return neg ? "-0.0" : "0.0";

        string s = Math.Abs(v).ToString("R", CultureInfo.InvariantCulture);
        string digits;
        int pointExp; // value == 0.digits * 10^pointExp, i.e. decimal point sits before digits shifted by pointExp
        int e = s.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            var mant = s[..e];
            int exp = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
            int dot = mant.IndexOf('.');
            string mantDigits = dot < 0 ? mant : mant.Remove(dot, 1);
            int intDigits = dot < 0 ? mant.Length : dot;
            digits = mantDigits;
            pointExp = intDigits + exp;
        }
        else
        {
            int dot = s.IndexOf('.');
            if (dot < 0) { digits = s; pointExp = s.Length; }
            else { digits = s.Remove(dot, 1); pointExp = dot; }
        }
        // Normalize: strip leading zeros (adjusting exponent) and trailing zeros.
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; pointExp--; }
        digits = digits[lead..].TrimEnd('0');
        if (digits.Length == 0) { digits = "0"; pointExp = 1; }

        int k = pointExp - 1; // scientific exponent: value = d.ddd * 10^k
        string body;
        if (k >= -4 && k < 16)
        {
            if (pointExp >= digits.Length)
                body = digits + new string('0', pointExp - digits.Length) + ".0";
            else if (pointExp > 0)
                body = digits[..pointExp] + "." + digits[pointExp..];
            else
                body = "0." + new string('0', -pointExp) + digits;
        }
        else
        {
            var frac = digits.Length > 1 ? "." + digits[1..] : "";
            body = $"{digits[0]}{frac}e{(k < 0 ? "-" : "+")}{Math.Abs(k).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')}";
        }
        return neg ? "-" + body : body;
    }
}
