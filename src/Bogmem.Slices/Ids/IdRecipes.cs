using System.Security.Cryptography;
using System.Text;

namespace Bogmem.Slices.Ids;

/// <summary>
/// Content-addressed ID recipes ported from mempalace.ids, palace_graph._canonical_tunnel_id,
/// and hallways._hallway_id at LEGACY_COMMIT a4747d7.
/// Preimage is length-prefixed with Unicode codepoint (rune) counts, not UTF-16 units.
/// </summary>
public static class IdRecipes
{
    public const string IdRecipe = "v3";
    public const int HashTruncDrawer = 24;
    public const int HashTruncTriple = 12;

    /// <summary>Length-prefixed SHA-256 hex, truncated. null → literal "None".</summary>
    public static string DelimitedSha256(IReadOnlyList<object?> parts, int truncate)
    {
        var preimage = BuildPreimage(parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage));
        return Convert.ToHexString(hash).ToLowerInvariant()[..truncate];
    }

    public static string BuildPreimage(IReadOnlyList<object?> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            var s = part is null ? "None" : part.ToString() ?? "";
            sb.Append(RuneCount(s));
            sb.Append(':');
            sb.Append(s);
        }
        return sb.ToString();
    }

    public static int RuneCount(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    public static string MakeDrawerIdFromChunk(string wing, string room, string sourceFile, int chunkIndex) =>
        $"drawer_{wing}_{room}_{DelimitedSha256([sourceFile, chunkIndex.ToString()], HashTruncDrawer)}";

    public static string MakeDrawerIdFromContent(string wing, string room, string content) =>
        $"drawer_{wing}_{room}_{DelimitedSha256([wing, room, content], HashTruncDrawer)}";

    public static string MakeConvoDrawerId(string wing, string room, string sourceFile, string extractMode, int chunkIndex) =>
        $"drawer_{wing}_{room}_{DelimitedSha256([sourceFile, extractMode, chunkIndex.ToString()], HashTruncDrawer)}";

    public static string MakeConvoSentinelId(string sourceFile, string extractMode) =>
        $"_reg_{DelimitedSha256([sourceFile, extractMode], HashTruncDrawer)}";

    public static string MakeTripleId(string subId, string predicate, string objId, string? validFrom, string recordedAt) =>
        $"t_{subId}_{predicate}_{objId}_{DelimitedSha256([validFrom, recordedAt], HashTruncTriple)}";

    /// <summary>Symmetric tunnel ID: sort endpoints ordinally, hash with ↔, truncate 16.</summary>
    public static string CanonicalTunnelId(string sourceWing, string sourceRoom, string targetWing, string targetRoom)
    {
        var src = $"{sourceWing}/{sourceRoom}";
        var tgt = $"{targetWing}/{targetRoom}";
        string a, b;
        if (string.CompareOrdinal(src, tgt) <= 0) { a = src; b = tgt; }
        else { a = tgt; b = src; }
        var key = Encoding.UTF8.GetBytes($"{a}↔{b}");
        return Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()[..16];
    }

    /// <summary>Symmetric hallway ID: sort entity pair ordinally.</summary>
    public static string HallwayId(string wing, string entityA, string entityB)
    {
        string a, b;
        if (string.CompareOrdinal(entityA, entityB) <= 0) { a = entityA; b = entityB; }
        else { a = entityB; b = entityA; }
        var key = Encoding.UTF8.GetBytes($"{wing}::{a}::{b}");
        var suffix = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()[..8];
        return $"hallway_{wing}_{a}_{b}_{suffix}";
    }
}
