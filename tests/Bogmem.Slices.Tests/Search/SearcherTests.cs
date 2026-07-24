using System.Text.Json;
using Bogmem.Cli.Parity;
using Bogmem.Slices.Search;

namespace Bogmem.Slices.Tests.Search;

public static class SearcherTests
{
    public static void Run()
    {
        RunCli();
        RunMcp();
        TestSupport.WriteModuleLedger("search", "S4",
            ("search_cli_hybrid", "pass", "EXACT", null),
            ("search_mcp_closet", "pass", "EXACT", null),
            ("search_tie_breaks", "pass", "EXACT", "covered_by_golden"));
    }

    private static void RunCli()
    {
        var path = TestSupport.PathUnderRepo("golden", "search", "cli", "vectors.jsonl");
        var failures = new List<string>();
        foreach (var row in CorpusReplay.Read(path))
        {
            var input = row.Input;
            var query = input.GetProperty("query").GetString() ?? "";
            var vw = input.TryGetProperty("vector_weight", out var vwe) ? vwe.GetDouble() : 0.6;
            var bw = input.TryGetProperty("bm25_weight", out var bwe) ? bwe.GetDouble() : 0.4;
            var hits = new List<Searcher.SearchHit>();
            foreach (var c in input.GetProperty("candidates").EnumerateArray())
            {
                double? dist = c.TryGetProperty("distance", out var d) && d.ValueKind != JsonValueKind.Null
                    ? d.GetDouble() : null;
                string? authored = c.TryGetProperty("authored_at", out var a) && a.ValueKind == JsonValueKind.String
                    ? a.GetString() : null;
                hits.Add(new Searcher.SearchHit(
                    c.GetProperty("id").GetString()!,
                    c.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    dist,
                    authored));
            }

            var ranked = Searcher.HybridRank(hits, query, vectorWeight: vw, bm25Weight: bw);
            var order = ranked.Select(h => h.Id).ToList();
            var expectedOrder = row.Expected.GetProperty("order").EnumerateArray().Select(x => x.GetString()!).ToList();
            if (!order.SequenceEqual(expectedOrder))
                failures.Add($"{row.Id} order: actual=[{string.Join(",", order)}] expected=[{string.Join(",", expectedOrder)}]");

            var expectedScores = row.Expected.GetProperty("scores").EnumerateArray().ToList();
            for (var i = 0; i < expectedScores.Count && i < ranked.Count; i++)
            {
                var es = expectedScores[i];
                var id = es.GetProperty("id").GetString()!;
                var hit = ranked.First(h => h.Id == id);
                var expBm25 = es.GetProperty("bm25").GetDouble();
                var expHybrid = es.GetProperty("hybrid").GetDouble();
                if (Math.Abs(hit.Bm25Score - expBm25) > 1e-9)
                    failures.Add($"{row.Id}/{id} bm25: actual={hit.Bm25Score} expected={expBm25}");
                if (Math.Abs(hit.HybridScore - expHybrid) > 1e-9)
                    failures.Add($"{row.Id}/{id} hybrid: actual={hit.HybridScore} expected={expHybrid}");
            }
        }
        TestSupport.AssertTrue(failures.Count == 0, "Search CLI failures:\n" + string.Join("\n", failures));
    }

    private static void RunMcp()
    {
        var path = TestSupport.PathUnderRepo("golden", "search", "mcp", "vectors.jsonl");
        var failures = new List<string>();
        foreach (var row in CorpusReplay.Read(path))
        {
            var input = row.Input;
            var drawers = new List<Searcher.SearchHit>();
            foreach (var d in input.GetProperty("drawers").EnumerateArray())
            {
                drawers.Add(new Searcher.SearchHit(
                    d.GetProperty("id").GetString()!,
                    d.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    d.GetProperty("distance").GetDouble(),
                    d.TryGetProperty("authored_at", out var a) ? a.GetString() : null,
                    d.TryGetProperty("source", out var s) ? s.GetString() : null));
            }
            var closets = new List<(string, double)>();
            foreach (var c in input.GetProperty("closets").EnumerateArray())
                closets.Add((c.GetProperty("source").GetString()!, c.GetProperty("distance").GetDouble()));

            // Closets should be ranked by distance asc (Stage-1 rank order).
            closets = closets.OrderBy(x => x.Item2).ToList();
            var selected = Searcher.RankWithClosetBoost(drawers, closets);

            var expected = row.Expected.GetProperty("selection").EnumerateArray().ToList();
            if (selected.Count != expected.Count)
                failures.Add($"{row.Id} count {selected.Count}!={expected.Count}");
            for (var i = 0; i < expected.Count && i < selected.Count; i++)
            {
                var e = expected[i];
                var a = selected[i];
                if (a.Id != e.GetProperty("id").GetString())
                    failures.Add($"{row.Id}[{i}] id {a.Id}!={e.GetProperty("id").GetString()}");
                var expBoost = e.GetProperty("boost").GetDouble();
                var expDist = e.GetProperty("effective_distance").GetDouble();
                if (Math.Abs(a.ClosetBoost - expBoost) > 1e-12)
                    failures.Add($"{row.Id}/{a.Id} boost {a.ClosetBoost}!={expBoost}");
                if (Math.Abs((a.Distance ?? -1) - expDist) > 1e-12)
                    failures.Add($"{row.Id}/{a.Id} effdist {a.Distance}!={expDist}");
            }
        }
        TestSupport.AssertTrue(failures.Count == 0, "Search MCP failures:\n" + string.Join("\n", failures));
    }
}
