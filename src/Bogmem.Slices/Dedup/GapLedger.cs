using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bogmem.Slices.Dedup;

/// <summary>
/// Concrete schema ase.dedup_gap_ledger.v1 authored for S5a.
/// Records terminal boundary-coverage gaps after full-palace exhaustion.
/// Evidence lives under tests/parity/disposition/ (golden/ is read-only).
/// </summary>
public sealed class GapLedger
{
    public const string Schema = "ase.dedup_gap_ledger.v1";

    [JsonPropertyName("schema")]
    public string SchemaName { get; set; } = Schema;

    /// <summary>Record/range that reached full-palace exhaustion (e.g. "0..N-1").</summary>
    [JsonPropertyName("final_slice_extent")]
    public string FinalSliceExtent { get; set; } = "";

    /// <summary>"0.15_distance_line" or "4v5_member_cutoff" (or empty when both covered).</summary>
    [JsonPropertyName("missed_boundary")]
    public string MissedBoundary { get; set; } = "";

    [JsonPropertyName("exhaustion_confirmed")]
    public bool ExhaustionConfirmed { get; set; }

    [JsonPropertyName("covered_compares_exact")]
    public bool CoveredComparesExact { get; set; }

    [JsonPropertyName("pin_sha")]
    public string? PinSha { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    public static GapLedger Create(
        string finalSliceExtent,
        string missedBoundary,
        bool exhaustionConfirmed,
        bool coveredComparesExact,
        string? pinSha = null,
        string? notes = null) => new()
    {
        FinalSliceExtent = finalSliceExtent,
        MissedBoundary = missedBoundary,
        ExhaustionConfirmed = exhaustionConfirmed,
        CoveredComparesExact = coveredComparesExact,
        PinSha = pinSha,
        Notes = notes,
    };

    public void Write(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static GapLedger? Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<GapLedger>(File.ReadAllText(path))
            : null;
}
