using System.Globalization;

namespace Bogmem.Slices.Common;

/// <summary>
/// Python string/number semantics shared by the parity slices. Legacy mempalace
/// operates on Python str (code points, Unicode whitespace) and Python int()
/// parsing; C# strings are UTF-16, so every length/slice/strip that feeds a
/// parity surface goes through these helpers instead of raw string APIs.
/// </summary>
public static class PyText
{
    /// <summary>Python str.strip()/split() whitespace: .NET whitespace plus U+001C..U+001F.</summary>
    public static bool IsPySpace(int rune) =>
        (rune is >= 0x1C and <= 0x1F) || (rune <= 0xFFFF ? char.IsWhiteSpace((char)rune) : false);

    /// <summary>Decode to code points — Python's unit of indexing and length.</summary>
    public static int[] ToRunes(string s)
    {
        var runes = new List<int>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                runes.Add(char.ConvertToUtf32(s[i], s[i + 1]));
                i++;
            }
            else runes.Add(s[i]);
        }
        return runes.ToArray();
    }

    public static string FromRunes(int[] runes, int start, int count)
    {
        var sb = new System.Text.StringBuilder(count + 8);
        for (int i = start; i < start + count; i++) sb.Append(char.ConvertFromUtf32(runes[i]));
        return sb.ToString();
    }

    public static string FromRunes(int[] runes) => FromRunes(runes, 0, runes.Length);

    /// <summary>Python len(str) — code-point count.</summary>
    public static int RuneLength(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }

    /// <summary>Python str[:n] — first n code points.</summary>
    public static string RuneSubstringStart(string s, int n)
    {
        int taken = 0, i = 0;
        for (; i < s.Length && taken < n; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            taken++;
        }
        return s[..i];
    }

    /// <summary>Python str.strip() with no args.</summary>
    public static string PyStrip(string s)
    {
        var runes = ToRunes(s);
        var (lo, hi) = StripBounds(runes, 0, runes.Length);
        return FromRunes(runes, lo, hi - lo);
    }

    public static string PyLStrip(string s)
    {
        var runes = ToRunes(s);
        int lo = 0;
        while (lo < runes.Length && IsPySpace(runes[lo])) lo++;
        return FromRunes(runes, lo, runes.Length - lo);
    }

    /// <summary>Strip bounds [lo, hi) over a rune window — Python str.strip() on a slice.</summary>
    public static (int Lo, int Hi) StripBounds(int[] runes, int start, int end)
    {
        int lo = start, hi = end;
        while (lo < hi && IsPySpace(runes[lo])) lo++;
        while (hi > lo && IsPySpace(runes[hi - 1])) hi--;
        return (lo, hi);
    }

    /// <summary>Python int(str) for base-10: whitespace-trimmed, optional sign, underscores between digits.</summary>
    public static bool TryPyInt(string raw, out long value)
    {
        value = 0;
        var s = PyStrip(raw);
        if (s.Length == 0) return false;
        int i = 0;
        bool neg = false;
        if (s[0] is '+' or '-') { neg = s[0] == '-'; i = 1; }
        if (i >= s.Length) return false;
        bool lastWasDigit = false;
        long acc = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '_')
            {
                if (!lastWasDigit) return false;
                lastWasDigit = false;
                continue;
            }
            if (c is < '0' or > '9') return false;
            acc = acc * 10 + (c - '0');
            lastWasDigit = true;
        }
        if (!lastWasDigit) return false;
        value = neg ? -acc : acc;
        return true;
    }

    /// <summary>Python float(value) over the loosely-typed record fields dynamics.py sees.</summary>
    public static double PyFloat(object? value, double fallback)
    {
        return value switch
        {
            null => fallback,
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            bool b => b ? 1.0 : 0.0,
            string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => fallback,
        };
    }
}
