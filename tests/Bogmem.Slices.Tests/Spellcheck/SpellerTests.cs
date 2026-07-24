using System.Globalization;
using System.Text.Json;
using Bogmem.Cli.Parity;
using Bogmem.Slices.Common;
using Bogmem.Slices.Spellcheck;

namespace Bogmem.Slices.Tests.Spellcheck;

/// <summary>
/// S12 — deterministic spellcheck, BOUNDED (D2): aggregate per-token
/// agreement against golden/spellcheck must be ≥ 0.95. Below the bar is a
/// regression; the module is excluded from the exact-parity floor denominator.
/// </summary>
public static class SpellerTests
{
    public const double AgreementBar = 0.95;

    public static void Run(TestDispositionLedgerWriter ledger)
    {
        var result = new SuiteResult("spellcheck");
        var embeddedWords = new Speller().ValidWords;
        result.Check("embedded web2 corpus", embeddedWords.Count == 234_487,
            $"embedded web2 normalized word count {embeddedWords.Count}, expected 234487");

        long agree = 0, total = 0;
        var perRow = new List<(string Id, long Agree, long Total)>();

        foreach (var (id, input, expected) in TestKit.Vectors("spellcheck"))
        {
            long rowAgree = 0, rowTotal = 0;
            switch (input.GetProperty("kind").GetString())
            {
                case "words":
                {
                    var speller = new Speller();
                    foreach (var pair in expected.GetProperty("pairs").EnumerateArray())
                    {
                        var token = pair.GetProperty("token").GetString()!;
                        var want = pair.GetProperty("corrected").GetString()!;
                        var got = speller.AutocorrectSentence(token);
                        rowTotal++;
                        if (got == want) rowAgree++;
                    }
                    break;
                }
                case "user_text":
                {
                    var known = input.GetProperty("known_names").EnumerateArray().Select(x => x.GetString()!);
                    var speller = new Speller(known);
                    var got = speller.SpellcheckUserText(input.GetProperty("text").GetString()!);
                    var want = expected.GetProperty("corrected").GetString()!;
                    (rowAgree, rowTotal) = TokenAgreement(got, want);
                    break;
                }
                case "transcript":
                {
                    var speller = new Speller();
                    var got = speller.SpellcheckTranscript(input.GetProperty("content").GetString()!);
                    var want = expected.GetProperty("corrected").GetString()!;
                    (rowAgree, rowTotal) = TokenAgreement(got, want);
                    break;
                }
                case "edit_distance":
                {
                    var expDists = expected.GetProperty("distances").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    int i = 0;
                    foreach (var pair in input.GetProperty("pairs").EnumerateArray())
                    {
                        int got = Speller.EditDistance(pair[0].GetString()!, pair[1].GetString()!);
                        rowTotal++;
                        if (got == expDists[i++]) rowAgree++;
                    }
                    break;
                }
                case "system_words":
                {
                    var speller = new Speller();
                    var members = expected.GetProperty("members").EnumerateArray()
                        .Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
                    foreach (var probe in input.GetProperty("probe").EnumerateArray().Select(x => x.GetString()!))
                    {
                        rowTotal++;
                        if (speller.SystemWords.Contains(probe) == members.Contains(probe)) rowAgree++;
                    }
                    break;
                }
                default:
                    result.Fail(id, "unknown fixture kind");
                    continue;
            }
            agree += rowAgree;
            total += rowTotal;
            perRow.Add((id, rowAgree, rowTotal));
        }

        double agreement = total == 0 ? 0 : (double)agree / total;
        bool pass = agreement >= AgreementBar;
        foreach (var (id, a, t) in perRow)
            ledger.Append(id, "spellcheck",
                pass ? "tolerated_divergence(D2 >=0.95 token agreement)" : "failed",
                "BOUNDED",
                reason: $"row agreement {a}/{t}; aggregate {agreement.ToString("F4", CultureInfo.InvariantCulture)}",
                slice: "S12");
        result.Check("aggregate", pass,
            $"token agreement {agreement.ToString("F4", CultureInfo.InvariantCulture)} below bar {AgreementBar}");
        if (pass) Console.WriteLine($"  spellcheck aggregate token agreement: {agreement.ToString("F4", CultureInfo.InvariantCulture)} ({agree}/{total})");
        result.ThrowIfFailed();
    }

    /// <summary>Position-wise agreement over Python str.split() tokens; length skew counts against.</summary>
    private static (long Agree, long Total) TokenAgreement(string actual, string expected)
    {
        var a = PySplit(actual);
        var e = PySplit(expected);
        long total = Math.Max(a.Count, e.Count);
        long agree = 0;
        for (int i = 0; i < Math.Min(a.Count, e.Count); i++)
            if (string.Equals(a[i], e[i], StringComparison.Ordinal)) agree++;
        return (agree, Math.Max(total, 1));
    }

    private static List<string> PySplit(string s)
    {
        var tokens = new List<string>();
        var runes = PyText.ToRunes(s);
        int i = 0;
        while (i < runes.Length)
        {
            while (i < runes.Length && PyText.IsPySpace(runes[i])) i++;
            int start = i;
            while (i < runes.Length && !PyText.IsPySpace(runes[i])) i++;
            if (i > start) tokens.Add(PyText.FromRunes(runes, start, i - start));
        }
        return tokens;
    }
}
