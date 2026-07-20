using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bogmem.Harness.Interfaces;
using Bogmem.Slices.Common;

namespace Bogmem.Slices.Spellcheck;

/// <summary>
/// Port of spellcheck.py at LEGACY_COMMIT behind ISpeller, including the
/// autocorrect library's Norvig-style corrector (word frequencies vendored
/// from autocorrect's en dictionary, MIT-licensed, as an embedded gzip).
///
/// Token pipeline: split on non-whitespace runs, strip trailing punctuation,
/// skip short/digit/camel/allcaps/technical/url/code tokens and known names,
/// skip capitalized words and words already in the system dictionary
/// (/usr/share/dict/words), autocorrect the rest, and reject corrections
/// farther than the Levenshtein guard (2 edits ≤7 chars, else 3).
///
/// This slice is BOUNDED (D2): scored as per-token agreement ≥ 0.95 against
/// the golden corpus, not exact equality.
/// </summary>
public sealed partial class Speller : ISpeller
{
    public const int MinLength = 4;
    private const string TrailingPunctuation = ".,!?;:'\")";
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz";

    [GeneratedRegex(@"\d")] private static partial Regex HasDigit();
    [GeneratedRegex("[A-Z][a-z]+[A-Z]")] private static partial Regex IsCamel();
    [GeneratedRegex(@"^[A-Z_@#$%^&*()+=\[\]{}|<>?.:/\\]+$")] private static partial Regex IsAllCaps();
    [GeneratedRegex("[-_]")] private static partial Regex IsTechnical();
    [GeneratedRegex(@"https?://|www\.|/Users/|~/|\.[a-z]{2,4}$", RegexOptions.IgnoreCase)] private static partial Regex IsUrl();
    [GeneratedRegex(@"[`*_#{}[\]\\]")] private static partial Regex IsCodeOrEmoji();
    [GeneratedRegex("[A-Za-z]+")] private static partial Regex WordRegex();

    private static Dictionary<string, long>? _sharedWordCounts;

    private readonly Dictionary<string, long> _wordCounts;
    private readonly HashSet<string> _systemWords;
    private readonly HashSet<string> _knownNames;

    public Speller(IEnumerable<string>? knownNames = null, string systemDictPath = "/usr/share/dict/words",
        Dictionary<string, long>? wordCounts = null)
    {
        _knownNames = new HashSet<string>(knownNames ?? [], StringComparer.Ordinal);
        _systemWords = LoadSystemWords(systemDictPath);
        _wordCounts = wordCounts ?? LoadSharedWordCounts();
    }

    public HashSet<string> SystemWords => _systemWords;

    /// <summary>ISpeller entry point — spellcheck_user_text with this instance's known names.</summary>
    public string Correct(string text) => SpellcheckUserText(text);

    public string SpellcheckUserText(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && IsSpaceUnit(text, i)) i++;
            sb.Append(text, start, i - start);
            start = i;
            while (i < text.Length && !IsSpaceUnit(text, i)) i++;
            if (i > start) sb.Append(FixToken(text[start..i]));
        }
        return sb.ToString();
    }

    private static bool IsSpaceUnit(string s, int idx)
    {
        // Python \S+ tokenization: whitespace is Py-whitespace; surrogate pairs
        // are never whitespace, so UTF-16 unit checks suffice here.
        char c = s[idx];
        if (char.IsHighSurrogate(c)) return false;
        return PyText.IsPySpace(c);
    }

    private string FixToken(string token)
    {
        var stripped = token.TrimEnd(TrailingPunctuation.ToCharArray());
        var punct = token[stripped.Length..];

        if (stripped.Length == 0 || ShouldSkip(stripped)) return token;
        if (char.IsUpper(stripped[0])) return token; // capitalized → likely proper noun
        if (_systemWords.Contains(stripped.ToLowerInvariant())) return token;

        var corrected = AutocorrectSentence(stripped);
        if (!string.Equals(corrected, stripped, StringComparison.Ordinal))
        {
            int dist = EditDistance(stripped, corrected);
            int maxEdits = PyText.RuneLength(stripped) <= 7 ? 2 : 3;
            if (dist > maxEdits) return token;
        }
        return corrected + punct;
    }

    public bool ShouldSkip(string token)
    {
        if (PyText.RuneLength(token) < MinLength) return true;
        if (HasDigit().IsMatch(token)) return true;
        if (IsCamel().IsMatch(token)) return true;
        if (IsAllCaps().IsMatch(token)) return true;
        if (IsTechnical().IsMatch(token)) return true;
        if (IsUrl().IsMatch(token)) return true;
        if (IsCodeOrEmoji().IsMatch(token)) return true;
        if (_knownNames.Contains(token.ToLowerInvariant())) return true;
        return false;
    }

    public string SpellcheckTranscriptLine(string line)
    {
        var stripped = PyText.PyLStrip(line);
        if (!stripped.StartsWith('>')) return line;
        int prefixLen = line.Length - stripped.Length + 2; // '> '
        if (prefixLen > line.Length) return line;          // bare '>' — nothing after
        var message = line[prefixLen..];
        if (PyText.PyStrip(message).Length == 0) return line;
        return line[..prefixLen] + SpellcheckUserText(message);
    }

    public string SpellcheckTranscript(string content) =>
        string.Join("\n", content.Split('\n').Select(SpellcheckTranscriptLine));

    // ── autocorrect (Norvig-style, frequency-ranked) ────────────────────────

    public string AutocorrectSentence(string sentence) =>
        WordRegex().Replace(sentence, m => AutocorrectWord(m.Value));

    public string AutocorrectWord(string word)
    {
        if (word.Length == 0) return "";
        var candidates = GetCandidates(word);
        if (char.IsUpper(word[0]))
        {
            var decap = char.ToLowerInvariant(word[0]) + word[1..];
            candidates.AddRange(GetCandidates(decap));
        }
        // max((count, word)) — count first, ordinal word breaks ties.
        var best = candidates[0];
        foreach (var c in candidates.Skip(1))
            if (c.Count > best.Count || (c.Count == best.Count && string.CompareOrdinal(c.Word, best.Word) > 0))
                best = c;
        var bestWord = best.Word;
        if (char.IsUpper(word[0]) && bestWord.Length > 0)
            bestWord = char.ToUpperInvariant(bestWord[0]) + bestWord[1..];
        return bestWord;
    }

    private List<(long Count, string Word)> GetCandidates(string word)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        if (_wordCounts.ContainsKey(word)) existing.Add(word);
        if (existing.Count == 0)
            foreach (var t in Typos(word))
                if (_wordCounts.ContainsKey(t)) existing.Add(t);
        if (existing.Count == 0)
            foreach (var t1 in Typos(word))
                foreach (var t2 in Typos(t1))
                    if (_wordCounts.ContainsKey(t2)) existing.Add(t2);
        if (existing.Count == 0) existing.Add(word);
        return existing.Select(c => (_wordCounts.GetValueOrDefault(c, 0L), c)).ToList();
    }

    private static IEnumerable<string> Typos(string word)
    {
        // deletes
        for (int i = 0; i < word.Length; i++)
            yield return word[..i] + word[(i + 1)..];
        // transposes
        for (int i = 0; i < word.Length - 1; i++)
            yield return word[..i] + word[i + 1] + word[i] + word[(i + 2)..];
        // replaces
        for (int i = 0; i < word.Length; i++)
            foreach (var c in Alphabet)
                yield return word[..i] + c + word[(i + 1)..];
        // inserts
        for (int i = 0; i <= word.Length; i++)
            foreach (var c in Alphabet)
                yield return word[..i] + c + word[i..];
    }

    /// <summary>Levenshtein distance — legacy _edit_distance.</summary>
    public static int EditDistance(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 0;
        var ra = PyText.ToRunes(a);
        var rb = PyText.ToRunes(b);
        if (ra.Length == 0) return rb.Length;
        if (rb.Length == 0) return ra.Length;
        var prev = Enumerable.Range(0, rb.Length + 1).ToArray();
        for (int i = 1; i <= ra.Length; i++)
        {
            var curr = new int[rb.Length + 1];
            curr[0] = i;
            for (int j = 1; j <= rb.Length; j++)
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + (ra[i - 1] != rb[j - 1] ? 1 : 0));
            prev = curr;
        }
        return prev[rb.Length];
    }

    private static HashSet<string> LoadSystemWords(string path)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return words;
        foreach (var line in File.ReadLines(path))
        {
            var w = PyText.PyStrip(line);
            if (w.Length > 0) words.Add(w.ToLowerInvariant());
        }
        return words;
    }

    private static Dictionary<string, long> LoadSharedWordCounts()
    {
        if (_sharedWordCounts is not null) return _sharedWordCounts;
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Bogmem.Slices.Spellcheck.data.word_count.en.json.gz")
            ?? throw new InvalidOperationException("embedded word_count.en.json.gz missing");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        var counts = JsonSerializer.Deserialize<Dictionary<string, long>>(gz)
            ?? throw new InvalidOperationException("word_count.en.json.gz is empty");
        _sharedWordCounts = counts;
        return counts;
    }
}
