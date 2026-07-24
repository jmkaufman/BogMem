using System.Text.Json.Nodes;
using Bogmem.Slices.Runtime;

namespace Bogmem.Slices.Mcp;

public sealed partial class McpServer
{
    private static IReadOnlyList<ToolSpec> ColiseumToolSpecs() =>
    [
        ColiseumTool(
            "bogmem_recall",
            "Read-only recall across registered palaces. Results retain palace provenance and are interleaved by palace-local rank.",
            """
            {
              "type":"object",
              "properties":{
                "query":{"type":"string"},
                "palaces":{"type":"array","items":{"type":"string"},"description":"Optional exact palace IDs or names; omitted means all registered palaces."},
                "wing":{"type":"string"},
                "room":{"type":"string"},
                "source_file":{"type":"string"},
                "limit":{"type":"integer","minimum":1,"maximum":1000,"default":10},
                "per_palace_limit":{"type":"integer","minimum":1,"maximum":100,"default":5},
                "max_distance":{"type":"number","minimum":0,"default":1.5}
              },
              "required":["query"]
            }
            """),
        ColiseumTool(
            "bogmem_graph_recall_neighbors",
            "Read-only neighborhood recall across registered palace graph windows. No cross-palace edges are created.",
            """
            {
              "type":"object",
              "properties":{
                "actor_id":{"type":"string"},
                "window_start":{"type":"string","description":"Inclusive ISO-8601 window start."},
                "window_end":{"type":"string","description":"Exclusive ISO-8601 window end."},
                "palaces":{"type":"array","items":{"type":"string"},"description":"Optional exact palace IDs or names; omitted means all registered palaces."},
                "minimum_edge_weight":{"type":"number","minimum":0,"default":0},
                "limit":{"type":"integer","minimum":1,"maximum":1000,"default":100},
                "per_palace_limit":{"type":"integer","minimum":1,"maximum":100,"default":100}
              },
              "required":["actor_id","window_start","window_end"]
            }
            """),
    ];

    private static ToolSpec ColiseumTool(
        string name,
        string description,
        string schemaJson)
    {
        var schema = JsonNode.Parse(schemaJson)?.AsObject()
                     ?? throw new InvalidOperationException(
                         $"Invalid schema for {name}.");
        return new(name, description, schema, Mutating: false);
    }

    private JsonObject DispatchColiseumTool(string name, JsonObject args) =>
        name switch
        {
            "bogmem_recall" => ColiseumSearchResult(args),
            "bogmem_graph_recall_neighbors" => ColiseumNeighborsResult(args),
            _ => throw new InvalidOperationException(
                $"Unhandled Coliseum tool '{name}'."),
        };

    private JsonObject ColiseumSearchResult(JsonObject args)
    {
        var result = _coliseum!.Search(
            RequiredArg(args, "query"),
            PalaceSelectorArgs(args),
            ArgInt(args, "limit", 10, 1, 1000),
            ArgInt(args, "per_palace_limit", 5, 1, 100),
            ArgString(args, "wing"),
            ArgString(args, "room"),
            ArgString(args, "source_file"),
            ArgDouble(args, "max_distance", 1.5));
        var hits = new JsonArray();
        foreach (var hit in result.Hits)
        {
            var row = DrawerJson(hit.Drawer);
            row["federated_rank"] = hit.FederatedRank;
            row["local_rank"] = hit.LocalRank;
            row["palace_id"] = hit.PalaceId;
            row["palace_name"] = hit.PalaceName;
            row["distance"] = hit.Distance;
            row["local_score"] = hit.LocalScore;
            row["bm25_score"] = hit.Bm25Score;
            hits.Add(row);
        }
        return new JsonObject
        {
            ["query"] = result.Query,
            ["ranking"] = result.Ranking,
            ["palaces_requested"] = result.PalacesRequested,
            ["palaces_succeeded"] = result.PalacesSucceeded,
            ["sources"] = RecallSources(result.Sources),
            ["results"] = hits,
            ["failures"] = Failures(result.Failures),
        };
    }

    private JsonObject ColiseumNeighborsResult(JsonObject args)
    {
        var (start, end) = Window(args);
        var result = _coliseum!.Neighbors(
            RequiredArg(args, "actor_id"),
            start,
            end,
            PalaceSelectorArgs(args),
            ArgInt(args, "limit", 100, 1, 1000),
            ArgInt(args, "per_palace_limit", 100, 1, 100),
            ArgDouble(args, "minimum_edge_weight", 0));
        var hits = new JsonArray();
        foreach (var hit in result.Hits)
            hits.Add(new JsonObject
            {
                ["federated_rank"] = hit.FederatedRank,
                ["local_rank"] = hit.LocalRank,
                ["palace_id"] = hit.PalaceId,
                ["palace_name"] = hit.PalaceName,
                ["actor_id"] = hit.Actor.Id,
                ["kind"] = hit.Actor.Kind,
                ["display_name"] = hit.Actor.DisplayName,
                ["weight"] = hit.Weight,
                ["observation_count"] = hit.ObservationCount,
                ["first_seen"] = hit.FirstSeen.ToString("O"),
                ["last_seen"] = hit.LastSeen.ToString("O"),
            });
        var sources = new JsonArray();
        foreach (var source in result.Sources)
            sources.Add(new JsonObject
            {
                ["palace_id"] = source.PalaceId,
                ["palace_name"] = source.PalaceName,
                ["hits"] = source.Hits,
            });
        return new JsonObject
        {
            ["actor_id"] = result.ActorId,
            ["window_start"] = result.WindowStart.ToString("O"),
            ["window_end"] = result.WindowEnd.ToString("O"),
            ["ranking"] = result.Ranking,
            ["palaces_requested"] = result.PalacesRequested,
            ["palaces_succeeded"] = result.PalacesSucceeded,
            ["sources"] = sources,
            ["neighbors"] = hits,
            ["failures"] = Failures(result.Failures),
        };
    }

    private static IReadOnlyList<string>? PalaceSelectorArgs(JsonObject args)
    {
        if (args["palaces"] is null) return null;
        if (args["palaces"] is not JsonArray array)
            throw new ArgumentException("'palaces' must be an array");
        return array
            .Select((node, index) =>
                node is null ||
                node.GetValueKind() != System.Text.Json.JsonValueKind.String
                    ? throw new ArgumentException(
                        $"'palaces[{index}]' must be a string")
                    : node.GetValue<string>())
            .ToArray();
    }

    private static JsonArray RecallSources(
        IEnumerable<PalaceRecallSource> values)
    {
        var sources = new JsonArray();
        foreach (var source in values)
            sources.Add(new JsonObject
            {
                ["palace_id"] = source.PalaceId,
                ["palace_name"] = source.PalaceName,
                ["retrieval_mode"] = source.RetrievalMode,
                ["embedding_model"] = source.EmbeddingModel,
                ["hits"] = source.Hits,
            });
        return sources;
    }

    private static JsonArray Failures(
        IEnumerable<PalaceRecallFailure> values)
    {
        var failures = new JsonArray();
        foreach (var failure in values)
            failures.Add(new JsonObject
            {
                ["palace_id"] = failure.PalaceId,
                ["palace_name"] = failure.PalaceName,
                ["error_class"] = failure.ErrorClass,
                ["message"] = failure.Message,
            });
        return failures;
    }
}
