using System.Text;
using System.Text.RegularExpressions;
using Bogmem.Slices.Common;

namespace Bogmem.Slices.Search;

/// <summary>Result shape of mempalace.query_sanitizer.sanitize_query — the dict keys, verbatim.</summary>
public sealed record SanitizedQuery(
    string CleanQuery,
    bool WasSanitized,
    int OriginalLength,
    int CleanLength,
    string Method);

/// <summary>
/// Port of mempalace/query_sanitizer.py at the oracle pin
/// a4747d7ffc7818684f01ac96c886ff6a654dd301 (S4, issue #333 mitigation).
/// All lengths, slices, and strips are Python code-point semantics via PyText;
/// the dead sentence-split "?" fallback in step 2 is preserved as-is.
/// </summary>
public static class QuerySanitizer
{
    public const int MaxQueryLength = 250;
    public const int SafeQueryLength = 200;
    public const int MinQueryLength = 10;

    // _SENTENCE_SPLIT = [.!?。！？\n]+ ; _QUESTION_MARK = [?？]\s*["']?\s*$
    // Python \s additionally matches U+001C..U+001F, so the class is widened here.
    private static readonly Regex SentenceSplit = new(@"[.!?。！？\n]+", RegexOptions.Compiled);
    private static readonly Regex QuestionMark = new("[?？][\\s\u001C-\u001F]*[\"']?[\\s\u001C-\u001F]*$", RegexOptions.Compiled);

    private static bool IsQuote(int rune) => rune is '\'' or '"';

    /// <summary>mempalace.config.strip_lone_surrogates (#1235): lone UTF-16 surrogates → U+FFFD.</summary>
    public static string StripLoneSurrogates(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c)) sb.Append('�');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string StripWrappingQuotes(string candidate)
    {
        candidate = PyText.PyStrip(candidate);
        while (true)
        {
            var runes = PyText.ToRunes(candidate);
            if (runes.Length >= 2 && IsQuote(runes[0]) && runes[0] == runes[^1])
            {
                candidate = PyText.PyStrip(PyText.FromRunes(runes, 1, runes.Length - 2));
                if (candidate.Length == 0) return "";
                continue;
            }
            break;
        }
        var r = PyText.ToRunes(candidate);
        if (r.Length > 0 && IsQuote(r[0]))
        {
            candidate = PyText.PyStrip(PyText.FromRunes(r, 1, r.Length - 1));
            r = PyText.ToRunes(candidate);
        }
        if (r.Length > 0 && IsQuote(r[^1]))
            candidate = PyText.PyStrip(PyText.FromRunes(r, 0, r.Length - 1));
        return candidate;
    }

    private static string TailRunes(string s, int n)
    {
        var runes = PyText.ToRunes(s);
        int start = Math.Max(0, runes.Length - n);
        return PyText.FromRunes(runes, start, runes.Length - start);
    }

    private static string TrimCandidate(string candidate)
    {
        candidate = StripWrappingQuotes(candidate);
        if (PyText.RuneLength(candidate) <= MaxQueryLength) return candidate;

        var nested = SentenceSplit.Split(candidate)
            .Where(frag => PyText.PyStrip(frag).Length != 0)
            .Select(StripWrappingQuotes)
            .ToList();
        for (int i = nested.Count - 1; i >= 0; i--)
        {
            int len = PyText.RuneLength(nested[i]);
            if (len is >= MinQueryLength and <= MaxQueryLength) return nested[i];
        }

        return PyText.PyStrip(TailRunes(candidate, MaxQueryLength));
    }

    public static SanitizedQuery Sanitize(string? rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery) || PyText.PyStrip(rawQuery).Length == 0)
        {
            var q = rawQuery ?? "";
            int len = rawQuery is null ? 0 : PyText.RuneLength(rawQuery);
            return new(q, false, len, len, "passthrough");
        }

        var raw = StripLoneSurrogates(PyText.PyStrip(rawQuery));
        int originalLength = PyText.RuneLength(raw);

        // --- Step 1: short query passthrough ---
        if (originalLength <= SafeQueryLength)
            return new(raw, false, originalLength, originalLength, "passthrough");

        // --- Step 2: question extraction ---
        var sentences = SentenceSplit.Split(raw)
            .Select(PyText.PyStrip).Where(s => s.Length != 0).ToList();

        var allSegments = raw.Split('\n')
            .Select(PyText.PyStrip).Where(s => s.Length != 0).ToList();

        var questionSentences = new List<string>();
        for (int i = allSegments.Count - 1; i >= 0; i--)
            if (QuestionMark.IsMatch(allSegments[i]))
                questionSentences.Add(allSegments[i]);

        if (questionSentences.Count == 0)
        {
            // Dead in practice — the sentence split consumed every ?/？ as a
            // delimiter — but the legacy branch is ported for parity.
            for (int i = sentences.Count - 1; i >= 0; i--)
                if (sentences[i].Contains('?') || sentences[i].Contains('？'))
                    questionSentences.Add(sentences[i]);
        }

        if (questionSentences.Count > 0)
        {
            var candidate = PyText.PyStrip(questionSentences[0]);
            if (PyText.RuneLength(candidate) >= MinQueryLength)
            {
                if (PyText.RuneLength(candidate) > MaxQueryLength)
                    candidate = TrimCandidate(candidate);
                return new(candidate, true, originalLength, PyText.RuneLength(candidate), "question_extraction");
            }
        }

        // --- Step 3: tail sentence extraction ---
        for (int i = allSegments.Count - 1; i >= 0; i--)
        {
            var seg = PyText.PyStrip(allSegments[i]);
            if (PyText.RuneLength(seg) < MinQueryLength) continue;
            var candidate = TrimCandidate(seg);
            if (PyText.RuneLength(candidate) < MinQueryLength) continue;
            return new(candidate, true, originalLength, PyText.RuneLength(candidate), "tail_sentence");
        }

        // --- Step 4: tail truncation (fallback) ---
        var tail = PyText.PyStrip(TailRunes(raw, MaxQueryLength));
        return new(tail, true, originalLength, PyText.RuneLength(tail), "tail_truncation");
    }
}
