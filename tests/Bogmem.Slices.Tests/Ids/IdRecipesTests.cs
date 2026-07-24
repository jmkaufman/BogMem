using System.Text.Json;
using Bogmem.Cli.Parity;
using Bogmem.Slices.Ids;

namespace Bogmem.Slices.Tests.Ids;

public static class IdRecipesTests
{
    public static void Run()
    {
        var path = TestSupport.PathUnderRepo("golden", "ids", "vectors.jsonl");
        var failures = new List<string>();
        foreach (var row in CorpusReplay.Read(path))
        {
            var input = row.Input;
            var kind = input.GetProperty("kind").GetString()!;
            var expectedId = row.Expected.GetProperty("id").GetString()!;
            string actual;
            string? preimage = null;

            switch (kind)
            {
                case "drawer_chunk":
                    actual = IdRecipes.MakeDrawerIdFromChunk(
                        input.GetProperty("wing").GetString()!,
                        input.GetProperty("room").GetString()!,
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("chunk_index").GetInt32());
                    preimage = IdRecipes.BuildPreimage([
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("chunk_index").GetInt32().ToString()]);
                    break;
                case "drawer_content":
                    actual = IdRecipes.MakeDrawerIdFromContent(
                        input.GetProperty("wing").GetString()!,
                        input.GetProperty("room").GetString()!,
                        input.GetProperty("content").GetString()!);
                    preimage = IdRecipes.BuildPreimage([
                        input.GetProperty("wing").GetString()!,
                        input.GetProperty("room").GetString()!,
                        input.GetProperty("content").GetString()!]);
                    break;
                case "convo_drawer":
                    actual = IdRecipes.MakeConvoDrawerId(
                        input.GetProperty("wing").GetString()!,
                        input.GetProperty("room").GetString()!,
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("extract_mode").GetString()!,
                        input.GetProperty("chunk_index").GetInt32());
                    preimage = IdRecipes.BuildPreimage([
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("extract_mode").GetString()!,
                        input.GetProperty("chunk_index").GetInt32().ToString()]);
                    break;
                case "convo_sentinel":
                    actual = IdRecipes.MakeConvoSentinelId(
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("extract_mode").GetString()!);
                    preimage = IdRecipes.BuildPreimage([
                        input.GetProperty("source_file").GetString()!,
                        input.GetProperty("extract_mode").GetString()!]);
                    break;
                case "triple":
                {
                    var vfEl = input.GetProperty("valid_from");
                    string? vf = vfEl.ValueKind == JsonValueKind.Null ? null : vfEl.GetString();
                    // Python str("None") is the string None; fixture may pass literal "None".
                    if (vf == "None") vf = null;
                    actual = IdRecipes.MakeTripleId(
                        input.GetProperty("sub_id").GetString()!,
                        input.GetProperty("predicate").GetString()!,
                        input.GetProperty("obj_id").GetString()!,
                        vf,
                        input.GetProperty("recorded_at").GetString()!);
                    preimage = IdRecipes.BuildPreimage([vf, input.GetProperty("recorded_at").GetString()!]);
                    break;
                }
                case "delimited":
                {
                    var parts = new List<object?>();
                    foreach (var p in input.GetProperty("parts").EnumerateArray())
                    {
                        if (p.ValueKind == JsonValueKind.Null) parts.Add(null);
                        else parts.Add(p.GetString());
                    }
                    actual = IdRecipes.DelimitedSha256(parts, expectedId.Length);
                    preimage = IdRecipes.BuildPreimage(parts);
                    break;
                }
                case "tunnel":
                    actual = IdRecipes.CanonicalTunnelId(
                        input.GetProperty("source_wing").GetString()!,
                        input.GetProperty("source_room").GetString()!,
                        input.GetProperty("target_wing").GetString()!,
                        input.GetProperty("target_room").GetString()!);
                    break;
                default:
                    throw new InvalidOperationException($"unknown kind {kind}");
            }

            var idCmp = ComparisonRules.Exact(actual, expectedId);
            if (!idCmp.Passed) failures.Add($"{row.Id}: expected={expectedId} actual={actual}");

            if (row.Expected.TryGetProperty("preimage", out var pe) &&
                pe.ValueKind == JsonValueKind.String && preimage is not null)
            {
                var preCmp = ComparisonRules.Exact(preimage, pe.GetString()!);
                if (!preCmp.Passed) failures.Add($"{row.Id} preimage: expected={pe.GetString()} actual={preimage}");
            }
        }

        TestSupport.AssertTrue(failures.Count == 0, "IdRecipes failures:\n" + string.Join("\n", failures));
        TestSupport.AssertTrue(IdRecipes.RuneCount("/x/\U0001F600") == 4, "emoji rune count");
        TestSupport.AssertTrue(IdRecipes.BuildPreimage([null, "x"]) == "4:None1:x", "None preimage");
        TestSupport.AssertTrue(
            IdRecipes.HallwayId("w", "Aya", "Lumi") == IdRecipes.HallwayId("w", "Lumi", "Aya"),
            "hallway symmetric");
        TestSupport.AssertTrue(
            IdRecipes.CanonicalTunnelId("a", "r1", "b", "r2") == IdRecipes.CanonicalTunnelId("b", "r2", "a", "r1"),
            "tunnel symmetric");

        TestSupport.WriteModuleLedger("ids", "S1",
            ("ids_recipes_golden", "pass", "EXACT", null),
            ("ids_codepoints", "pass", "EXACT", "covered_by_golden"),
            ("ids_surrogatepass", "not_applicable", "EXACT", "deferred to S13"));
    }
}
