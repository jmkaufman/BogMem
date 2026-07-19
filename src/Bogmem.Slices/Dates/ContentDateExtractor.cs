using System.Globalization;
using System.Text.RegularExpressions;
using Bogmem.Slices.Common;

namespace Bogmem.Slices.Dates;

/// <summary>
/// Port of the Tier-6a content-date extraction in mempalace/miner.py at the
/// oracle pin a4747d7ffc7818684f01ac96c886ff6a654dd301:
/// _extract_content_date + _try_filename_date / _try_frontmatter_date /
/// _try_content_body_date / _try_mtime_date.
///
/// Hierarchy (first match wins): filename → YAML frontmatter → content body
/// (first ~10 lines) → filesystem mtime → none. Tier numbers follow
/// golden/dates/manifest.json tier_enum (filename=1 … none=5). Per that
/// manifest the capture ran under TZ=UTC, so mtime is interpreted in UTC.
///
/// dateutil is replaced by a deterministic parser that accepts exactly the
/// shapes the legacy _VALID_DATE_RE gate lets through (complete
/// year+month+day: numeric, month-name-first, or day-first) — the gate is
/// what made legacy dateutil deterministic, so the reachable inputs agree.
/// </summary>
public static class ContentDateExtractor
{
    private static readonly Regex OrdinalSuffix = new(@"\b(\d+)(st|nd|rd|th)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IsoDate = new(@"\b(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex SlashDate = new(@"\b(\d{1,2})[/-](\d{1,2})[/-](\d{2,4})\b", RegexOptions.Compiled);

    private const string MonthName =
        "(?:january|february|march|april|may|june|july|august|" +
        "september|october|november|december|" +
        "jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec)";

    private static readonly Regex ValidDate = new(
        "(?:" +
        @"\b\d{4}[-/.\s]+\d{1,2}[-/.\s]+\d{1,2}\b" +
        "|" +
        @"\b" + MonthName + @"\.?[-\s]+\d{1,2}(?:st|nd|rd|th)?[,\s-]+\d{4}\b" +
        "|" +
        @"\b\d{1,2}(?:st|nd|rd|th)?[-\s]+" + MonthName + @"\.?[,\s-]+\d{4}\b" +
        ")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"] = 1, ["feb"] = 2, ["february"] = 2,
        ["mar"] = 3, ["march"] = 3, ["apr"] = 4, ["april"] = 4,
        ["may"] = 5, ["jun"] = 6, ["june"] = 6, ["jul"] = 7, ["july"] = 7,
        ["aug"] = 8, ["august"] = 8, ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
        ["oct"] = 10, ["october"] = 10, ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12,
    };

    /// <summary>Tier enum from golden/dates/manifest.json.</summary>
    public enum Tier { Filename = 1, Frontmatter = 2, ContentBody = 3, Mtime = 4, None = 5 }

    public static string? Extract(string sourceFile, string content) =>
        ExtractWithTier(sourceFile, content).Iso;

    public static (string? Iso, Tier Tier) ExtractWithTier(string sourceFile, string content)
    {
        var result = TryFilenameDate(sourceFile);
        if (result is not null) return (result, Tier.Filename);

        result = TryFrontmatterDate(content);
        if (result is not null) return (result, Tier.Frontmatter);

        result = TryContentBodyDate(content);
        if (result is not null) return (result, Tier.ContentBody);

        result = TryMtimeDate(sourceFile);
        if (result is not null) return (result, Tier.Mtime);

        return (null, Tier.None);
    }

    private static string? IsoFromYmd(int year, int month, int day)
    {
        // date(y, m, d).isoformat() — ValueError → None.
        if (year < 1 || year > 9999) return null;
        if (month < 1 || month > 12) return null;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return $"{year:D4}-{month:D2}-{day:D2}";
    }

    private static string? TryIsoMatch(string text)
    {
        var m = IsoDate.Match(text);
        if (!m.Success) return null;
        return IsoFromYmd(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Deterministic stand-in for dateutil_parser.parse on a _VALID_DATE_RE
    /// match: tokenises into month names and numbers, drops ordinal suffixes,
    /// then reads year/month/day from the gated shape.
    /// </summary>
    private static string? ParseGatedDate(string matched)
    {
        var tokens = Regex.Matches(matched, "[A-Za-z]+|[0-9]+")
            .Select(m => m.Value)
            .Where(t => t is not ("st" or "nd" or "rd" or "th" or "ST" or "ND" or "RD" or "TH"))
            .ToList();

        int? year = null, month = null, day = null;
        foreach (var t in tokens)
        {
            if (Months.TryGetValue(t, out var mo))
            {
                if (month is null) month = mo;
            }
            else if (t.All(char.IsAsciiDigit))
            {
                int v = int.Parse(t, CultureInfo.InvariantCulture);
                if (t.Length == 4 && year is null) year = v;
                else if (month is null && year is not null && day is null && tokens[0].Length == 4) month = v; // numeric shape: Y M D order
                else if (month is null && !tokens.Any(x => Months.ContainsKey(x))) month = v;
                else if (day is null) day = v;
            }
        }
        if (year is null || month is null || day is null) return null;
        return IsoFromYmd(year.Value, month.Value, day.Value);
    }

    private static string? TryFilenameDate(string? sourceFile)
    {
        if (sourceFile is null) return null;
        string stem;
        try { stem = Path.GetFileNameWithoutExtension(sourceFile); }
        catch (ArgumentException) { return null; }
        if (string.IsNullOrEmpty(stem)) return null;

        var iso = TryIsoMatch(stem);
        if (iso is not null) return iso;

        var normalized = OrdinalSuffix.Replace(stem, "$1").Replace('-', ' ').Replace('_', ' ');
        var m = ValidDate.Match(normalized);
        if (!m.Success) return null;
        return ParseGatedDate(m.Value);
    }

    private static string? TryFrontmatterDate(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var stripped = PyText.PyLStrip(content);
        if (!stripped.StartsWith("---", StringComparison.Ordinal)) return null;

        int endPos = stripped.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endPos == -1) return null;

        var frontmatter = PyText.PyStrip(stripped[3..endPos]);
        if (frontmatter.Length == 0) return null;

        var map = ParseFlatYamlMapping(frontmatter);
        if (map is null) return null;

        foreach (var field in new[] { "date", "created", "published" })
        {
            if (!map.TryGetValue(field, out var value) || string.IsNullOrEmpty(value)) continue;
            // yaml date scalars and dateutil-parseable strings both funnel
            // through the gated parser; failure falls through to the next field.
            var direct = TryIsoMatch(value);
            if (direct is not null && ValidDate.IsMatch(value)) return direct;
            var m = ValidDate.Match(value);
            if (m.Success)
            {
                var iso = ParseGatedDate(m.Value);
                if (iso is not null) return iso;
            }
        }
        return null;
    }

    private static Dictionary<string, string>? ParseFlatYamlMapping(string frontmatter)
    {
        // Legacy uses yaml.safe_load; the reachable frontmatter in the palace
        // corpus is a flat scalar mapping, parsed here without a YAML engine.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = PyText.PyStrip(line[..colon]);
            var value = PyText.PyStrip(line[(colon + 1)..]);
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];
            if (key.Length > 0 && !map.ContainsKey(key)) map[key] = value;
        }
        return map.Count > 0 ? map : null;
    }

    private static string? TryContentBodyDate(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var stripped = PyText.PyLStrip(content);

        // Skip frontmatter if present — find-based, mirroring the legacy slices.
        if (stripped.StartsWith("---", StringComparison.Ordinal))
        {
            int endFm = stripped.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endFm != -1)
            {
                int eol = stripped.IndexOf('\n', endFm + 1);
                if (eol != -1) stripped = stripped[(eol + 1)..];
            }
        }

        // head = "\n".join(stripped.split("\n", 10)[:10])
        var parts = stripped.Split('\n', 11);
        var head = string.Join("\n", parts.Take(10));
        if (head.Length == 0) return null;

        // 1. ISO regex.
        var iso = TryIsoMatch(head);
        if (iso is not null) return iso;

        // 2. Slash dates with locale auto-disambiguation.
        var slashMatches = SlashDate.Matches(head);
        if (slashMatches.Count > 0)
        {
            bool isDdMm = slashMatches.Any(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 12);
            var first = slashMatches[0];
            int a = int.Parse(first.Groups[1].Value, CultureInfo.InvariantCulture);
            int b = int.Parse(first.Groups[2].Value, CultureInfo.InvariantCulture);
            int y = int.Parse(first.Groups[3].Value, CultureInfo.InvariantCulture);
            if (y < 100) y = y >= 70 ? 1900 + y : 2000 + y;
            var slashIso = isDdMm ? IsoFromYmd(y, b, a) : IsoFromYmd(y, a, b);
            if (slashIso is not null) return slashIso;
            // ValueError → fall through to the gated parser.
        }

        // 3. Gated natural-language fallback.
        var gate = ValidDate.Match(head);
        if (!gate.Success) return null;
        return ParseGatedDate(gate.Value);
    }

    private static string? TryMtimeDate(string sourceFile)
    {
        try
        {
            if (!File.Exists(sourceFile)) return null;   // OSError → None
            var mtimeUtc = File.GetLastWriteTimeUtc(sourceFile);
            return mtimeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
