using System.Globalization;
using System.Text.Json;
using Bogmem.Cli.Parity;
using Bogmem.Slices.Dynamics;

namespace Bogmem.Slices.Tests.Dynamics;

/// <summary>
/// S5b — dynamics scoring, strict-zero float32 ULP over the five named
/// columns (potentiation / stability / spaced / combined_preclamp /
/// final_clamped). Zero out-of-ULP tolerated; clamp boundaries exact; a
/// failure names the diverging stage.
/// </summary>
public static class DynamicsScorerTests
{
    private static readonly string[] Columns =
        ["potentiation", "stability", "spaced", "combined_preclamp", "final_clamped"];

    public static void Run(TestDispositionLedgerWriter ledger)
    {
        var result = new SuiteResult("dynamics");
        foreach (var (id, input, expected) in TestKit.Vectors("dynamics"))
        {
            var record = ReadRecord(input.GetProperty("record"));
            var mode = input.GetProperty("mode").GetString()!;
            var tPot = ParseTime(input, "t_pot");
            var tDecay = ParseTime(input, "t_decay");

            var capture = DynamicsScorer.Score(record, mode, tPot, tDecay);
            double[] actual = [capture.Potentiation, capture.Stability, capture.Spaced, capture.CombinedPreclamp, capture.FinalClamped];

            var expCols = expected.GetProperty("columns");
            string? failedStage = null;
            string failDetail = "";
            for (int i = 0; i < Columns.Length; i++)
            {
                double exp = expCols.GetProperty(Columns[i]).GetDouble();
                long ulps = TestKit.FloatUlpDelta(actual[i], exp);
                if (ulps > 1)
                {
                    failedStage = Columns[i];
                    failDetail = $"stage={Columns[i]} actual={(float)actual[i]:R} expected={(float)exp:R} ulps={ulps}";
                    break;
                }
            }

            // Clamp boundaries must be exact, not merely within a ULP.
            if (failedStage is null)
            {
                double expFinal = expCols.GetProperty("final_clamped").GetDouble();
                bool atFloor = (float)expFinal == (float)DynamicsScorer.StrengthFloor;
                bool atCap = (float)expFinal == (float)DynamicsScorer.MaxStrength;
                if ((atFloor || atCap) && (float)capture.FinalClamped != (float)expFinal)
                {
                    failedStage = "final_clamp";
                    failDetail = $"clamp boundary not exact: actual={(float)capture.FinalClamped:R} expected={(float)expFinal:R}";
                }
            }

            // record_after: strength/stability within a float32 ULP, timestamps and counter exact.
            if (failedStage is null && expected.TryGetProperty("record_after", out var after))
            {
                double expStrength = after.GetProperty("strength").GetDouble();
                double expStability = after.GetProperty("stability").GetDouble();
                var expLast = after.GetProperty("last_activated").GetString();
                long expCount = after.GetProperty("access_count").GetInt64();
                double gotStrength = Bogmem.Slices.Common.PyText.PyFloat(record.Strength, double.NaN);
                double gotStability = Bogmem.Slices.Common.PyText.PyFloat(record.Stability, double.NaN);
                long gotCount = record.AccessCount is long l ? l : Convert.ToInt64(record.AccessCount ?? 0L, CultureInfo.InvariantCulture);
                if (TestKit.FloatUlpDelta(gotStrength, expStrength) > 1
                    || TestKit.FloatUlpDelta(gotStability, expStability) > 1
                    || record.LastActivated != expLast
                    || gotCount != expCount)
                {
                    failedStage = "record_after";
                    failDetail = $"record_after mismatch: strength={gotStrength:R} last={record.LastActivated} count={gotCount}";
                }
            }

            bool ok = failedStage is null;
            result.Check(id, ok, failDetail);
            ledger.Append(id, "dynamics", ok ? "converted" : "failed", "ULP",
                reason: ok ? null : $"diverging stage: {failedStage}; {failDetail}", slice: "S5b");
        }
        result.ThrowIfFailed();
    }

    private static DynamicsRecord ReadRecord(JsonElement el)
    {
        var record = new DynamicsRecord();
        foreach (var prop in el.EnumerateObject())
        {
            object? value = prop.Value.ValueKind switch
            {
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
            switch (prop.Name)
            {
                case "strength": record.Strength = value; break;
                case "stability": record.Stability = value; break;
                case "last_activated": record.LastActivated = value as string; break;
                case "created_at": record.CreatedAt = value as string; break;
                case "access_count": record.AccessCount = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt64() : value; break;
            }
        }
        return record;
    }

    private static DateTimeOffset? ParseTime(JsonElement input, string name)
    {
        var s = TestKit.OptString(input, name);
        if (s is null) return null;
        if (!DynamicsScorer.TryParseIso(s, out var dt))
            throw new InvalidOperationException($"unparsable fixture time {name}={s}");
        return dt;
    }
}
