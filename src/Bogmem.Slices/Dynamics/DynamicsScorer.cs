using System.Globalization;
using Bogmem.Slices.Common;

namespace Bogmem.Slices.Dynamics;

/// <summary>
/// A hall/tunnel record as dynamics.py sees it: a loosely-typed dict. Field
/// values may be numbers or numeric strings (Python float() coercion applies).
/// </summary>
public sealed class DynamicsRecord
{
    public object? Strength;
    public object? Stability;
    public string? LastActivated;
    public string? CreatedAt;
    public object? AccessCount;
}

/// <summary>Per-stage capture for the S5b golden columns.</summary>
public sealed record DynamicsCapture(
    double Potentiation,
    double Stability,
    double Spaced,
    double CombinedPreclamp,
    double FinalClamped,
    DynamicsRecord RecordAfter);

/// <summary>
/// Port of dynamics.py at LEGACY_COMMIT. All arithmetic is double (Python
/// float); the golden comparison narrows each captured value with
/// (double)(float) and holds it to 1 float32 ULP (§1.5). The capture columns
/// isolate each stage: potentiation is the raw pre-cap sum, stability the
/// post-potentiation value, spaced the isolated stability increment, and
/// combined_preclamp the pre-floor decay product (or the pre-cap sum when only
/// potentiation runs).
/// </summary>
public static class DynamicsScorer
{
    public const double StrengthFloor = 0.05;
    public const double MaxStrength = 5.0;
    public const double DefaultStability = 1.0;
    public const double DefaultStrength = 1.0;
    public const double PotentiationIncrement = 0.05;
    public const double SpacedIntervalHours = 1.0;
    public const double StabilityIncrement = 0.1;

    public sealed record PotentiationResult(double RawSum, double NewStrength, bool Spaced, double NewStability);

    public sealed record DecayResult(double PreClamp, double NewStrength, bool EarlyReturn, double Factor = 1.0);

    public static void InitializeDynamicsFields(DynamicsRecord record, DateTimeOffset now)
    {
        var nowIso = IsoFormat(now);
        var createdAt = record.CreatedAt ?? nowIso;
        record.Strength ??= DefaultStrength;
        record.Stability ??= DefaultStability;
        record.LastActivated ??= createdAt;
        record.AccessCount ??= 0L;
    }

    public static PotentiationResult Potentiate(DynamicsRecord record, DateTimeOffset now, double increment = PotentiationIncrement)
    {
        InitializeDynamicsFields(record, now);

        var lastActivated = record.LastActivated ?? record.CreatedAt;
        double hoursSince = 0.0;
        if (TryParseIso(lastActivated, out var lastDt))
            hoursSince = TotalSeconds(now, lastDt) / 3600.0;

        double currentStrength = PyText.PyFloat(record.Strength, DefaultStrength);
        double rawSum = currentStrength + increment;
        double newStrength = Math.Min(MaxStrength, rawSum);
        record.Strength = newStrength;

        bool spaced = hoursSince >= SpacedIntervalHours;
        double stability = PyText.PyFloat(record.Stability, DefaultStability);
        if (spaced)
        {
            stability += StabilityIncrement;
            record.Stability = stability;
        }

        record.LastActivated = IsoFormat(now);
        record.AccessCount = ToPyInt(record.AccessCount) + 1;
        return new PotentiationResult(rawSum, newStrength, spaced, stability);
    }

    public static DecayResult ApplyDecay(DynamicsRecord record, DateTimeOffset now)
    {
        InitializeDynamicsFields(record, now);

        var lastActivated = record.LastActivated ?? record.CreatedAt;
        if (!TryParseIso(lastActivated, out var lastDt))
        {
            var unchanged = PyText.PyFloat(record.Strength, DefaultStrength);
            return new DecayResult(unchanged, unchanged, EarlyReturn: true);
        }

        double daysSince = TotalSeconds(now, lastDt) / 86400.0;
        double current = PyText.PyFloat(record.Strength, DefaultStrength);
        if (daysSince <= 0)
            return new DecayResult(current, current, EarlyReturn: true);

        double stability = PyText.PyFloat(record.Stability, DefaultStability);
        if (stability <= 0) stability = DefaultStability;

        double decayFactor = Math.Exp(-daysSince / stability);
        double preClamp = current * decayFactor;
        double newStrength = Math.Max(StrengthFloor, preClamp);
        record.Strength = newStrength;
        return new DecayResult(preClamp, newStrength, EarlyReturn: false, decayFactor);
    }

    /// <summary>Run the capture pipeline for a golden vector: full / pot_only / decay_only.</summary>
    public static DynamicsCapture Score(DynamicsRecord record, string mode, DateTimeOffset? tPot, DateTimeOffset? tDecay)
    {
        double potentiationCol, stabilityCol, spacedCol, combined, final;
        switch (mode)
        {
            case "pot_only":
            {
                var pot = Potentiate(record, tPot!.Value);
                potentiationCol = pot.RawSum;
                stabilityCol = pot.NewStability;
                spacedCol = pot.Spaced ? StabilityIncrement : 0.0;
                combined = pot.RawSum;
                final = pot.NewStrength;
                break;
            }
            case "decay_only":
            {
                potentiationCol = PyText.PyFloat(record.Strength, DefaultStrength);
                stabilityCol = PyText.PyFloat(record.Stability, DefaultStability);
                spacedCol = 0.0;
                var dec = ApplyDecay(record, tDecay!.Value);
                combined = dec.PreClamp;
                final = dec.NewStrength;
                break;
            }
            case "full":
            {
                var pot = Potentiate(record, tPot!.Value);
                potentiationCol = pot.RawSum;
                stabilityCol = pot.NewStability;
                spacedCol = pot.Spaced ? StabilityIncrement : 0.0;
                var dec = ApplyDecay(record, tDecay!.Value);
                // combined_preclamp combines the ISOLATED pre-clamp stages: the
                // raw potentiation sum (not the capped record value) times the
                // decay factor. The record itself still follows the clamped
                // legacy pipeline (see record_after).
                combined = dec.EarlyReturn ? pot.RawSum : pot.RawSum * dec.Factor;
                final = dec.EarlyReturn ? pot.NewStrength : Math.Max(StrengthFloor, combined);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
        return new DynamicsCapture(potentiationCol, stabilityCol, spacedCol, combined, final, record);
    }

    /// <summary>timedelta.total_seconds(): single division, exact tick arithmetic.</summary>
    private static double TotalSeconds(DateTimeOffset a, DateTimeOffset b) =>
        (a.UtcTicks - b.UtcTicks) / 1e7;

    private static long ToPyInt(object? value) => value switch
    {
        null => 0L,
        long l => l,
        int i => i,
        double d => (long)d,
        string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => 0L,
    };

    /// <summary>Legacy _parse_iso: fromisoformat with Z→+00:00, naive→UTC, None on failure.</summary>
    public static bool TryParseIso(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrEmpty(value)) return false;
        var v = PyText.PyStrip(value);
        if (v.Length == 0) return false;
        if (v.EndsWith('Z')) v = v[..^1] + "+00:00";
        string[] formats =
        [
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
            "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm:ss.ffffff",
            "yyyy-MM-dd'T'HH:mm:ss.fff",
            "yyyy-MM-dd'T'HH:mm",
            "yyyy-MM-dd",
        ];
        foreach (var fmt in formats)
        {
            if (fmt.EndsWith("zzz", StringComparison.Ordinal))
            {
                if (DateTimeOffset.TryParseExact(v, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                    return true;
            }
            else if (DateTime.TryParseExact(v, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
            {
                // Python replaces missing tzinfo with UTC.
                result = new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Utc));
                return true;
            }
        }
        return false;
    }

    /// <summary>datetime.isoformat(): seconds always, microseconds only when nonzero, offset as +HH:MM.</summary>
    public static string IsoFormat(DateTimeOffset dt)
    {
        long subsecondTicks = dt.Ticks % TimeSpan.TicksPerSecond;
        var body = subsecondTicks == 0
            ? dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        var off = dt.Offset;
        var sign = off < TimeSpan.Zero ? "-" : "+";
        return body + sign + off.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }
}
