using System.Globalization;
using System.Text.Json.Nodes;
using Bogmem.Graph;

namespace Bogmem.Slices.Mcp;

public sealed partial class McpServer
{
    private static IReadOnlyList<ToolSpec> RuntimeToolSpecs() =>
    [
        RuntimeTool(
            "bogmem_palace_status",
            "Health, identity, schema, capabilities, and memory counts for this palace.",
            """
            {"type":"object","properties":{}}
            """),
        RuntimeTool(
            "bogmem_graph_observe",
            "Record one normalized, replay-safe co-activity observation in this palace. The caller owns entity normalization and signal weighting.",
            """
            {
              "type":"object",
              "properties":{
                "palace_id":{"type":"string","description":"Optional routing guard; must match this MCP service's palace."},
                "observation_id":{"type":"string"},
                "occurred_at":{"type":"string","description":"ISO-8601 event time."},
                "actor_ids":{"type":"array","items":{"type":"string"},"minItems":1},
                "weight":{"type":"number","exclusiveMinimum":0,"default":1},
                "context":{"type":"string"},
                "source":{"type":"string"},
                "workflow_id":{"type":"string"},
                "run_id":{"type":"string"},
                "artifact_id":{"type":"string"},
                "signal_type":{"type":"string"}
              },
              "required":["observation_id","occurred_at","actor_ids"]
            }
            """),
        RuntimeTool(
            "bogmem_graph_neighbors",
            "Recall the strongest directly connected actors in a temporal window.",
            WindowSchema(
                """
                "actor_id":{"type":"string"},
                "minimum_edge_weight":{"type":"number","minimum":0,"default":0},
                "limit":{"type":"integer","minimum":1,"maximum":1000,"default":100}
                """,
                "actor_id")),
        RuntimeTool(
            "bogmem_graph_traverse",
            "Traverse the weighted actor graph within a temporal window. Path strength is the best bottleneck weight among shortest paths.",
            WindowSchema(
                """
                "actor_id":{"type":"string"},
                "max_hops":{"type":"integer","minimum":1,"maximum":12,"default":2},
                "minimum_edge_weight":{"type":"number","minimum":0,"default":0},
                "limit":{"type":"integer","minimum":1,"maximum":5000,"default":100}
                """,
                "actor_id")),
        RuntimeTool(
            "bogmem_graph_communities",
            "Detect connected weighted communities in a temporal actor-graph window using BogMem's versioned deterministic Leiden-style algorithm.",
            WindowSchema(
                """
                "minimum_edge_weight":{"type":"number","minimum":0,"default":0},
                "resolution":{"type":"number","exclusiveMinimum":0,"default":1}
                """)),
    ];

    private static ToolSpec RuntimeTool(
        string name,
        string description,
        string schemaJson)
    {
        var schema = JsonNode.Parse(schemaJson)?.AsObject()
                     ?? throw new InvalidOperationException($"Invalid schema for {name}.");
        return new(name, description, schema, MutatingTools.Contains(name));
    }

    private static string WindowSchema(string extraProperties, params string[] extraRequired)
    {
        var required = new[] { "window_start", "window_end" }.Concat(extraRequired);
        return $$"""
        {
          "type":"object",
          "properties":{
            "palace_id":{"type":"string","description":"Optional routing guard; must match this MCP service's palace."},
            "window_start":{"type":"string","description":"Inclusive ISO-8601 window start."},
            "window_end":{"type":"string","description":"Exclusive ISO-8601 window end."},
            {{extraProperties}}
          },
          "required":[{{string.Join(",", required.Select(value => $"\"{value}\""))}}]
        }
        """;
    }

    private JsonObject GraphObserveResult(JsonObject args)
    {
        var runtime = RequiredRuntime(args);
        var actorIdsNode = args["actor_ids"] as JsonArray
                           ?? throw new ArgumentException("'actor_ids' must be an array");
        var actorIds = actorIdsNode
            .Select((node, index) =>
                node is null || node.GetValueKind() != System.Text.Json.JsonValueKind.String
                    ? throw new ArgumentException($"'actor_ids[{index}]' must be a string")
                    : node.GetValue<string>())
            .ToArray();
        var observationId = RequiredArg(args, "observation_id");
        var provenance = new ObservationProvenance(
            ArgString(args, "source"),
            ArgString(args, "workflow_id"),
            ArgString(args, "run_id"),
            ArgString(args, "artifact_id"),
            ArgString(args, "signal_type"));
        var created = runtime.Graph.Observe(new(
            observationId,
            ArgTimestamp(args, "occurred_at"),
            actorIds,
            ArgDouble(args, "weight", 1),
            ArgString(args, "context"),
            provenance));
        return new JsonObject
        {
            ["success"] = true,
            ["reason"] = created ? "created" : "already_exists",
            ["palace_id"] = runtime.Manifest.PalaceId,
            ["observation_id"] = observationId,
            ["actor_count"] = actorIds.Distinct(StringComparer.Ordinal).Count(),
        };
    }

    private JsonObject GraphNeighborsResult(JsonObject args)
    {
        var runtime = RequiredRuntime(args);
        var (start, end) = Window(args);
        var minimumWeight = ArgDouble(args, "minimum_edge_weight", 0);
        var graph = runtime.Graph.Snapshot(start, end, minimumWeight);
        var neighbors = graph.Neighbors(
            RequiredArg(args, "actor_id"),
            minimumWeight,
            ArgInt(args, "limit", 100, 1, 1000));
        var rows = new JsonArray();
        foreach (var neighbor in neighbors)
            rows.Add(new JsonObject
            {
                ["actor_id"] = neighbor.Actor.Id,
                ["kind"] = neighbor.Actor.Kind,
                ["display_name"] = neighbor.Actor.DisplayName,
                ["weight"] = neighbor.Weight,
                ["observation_count"] = neighbor.ObservationCount,
                ["first_seen"] = neighbor.FirstSeen.ToString("O"),
                ["last_seen"] = neighbor.LastSeen.ToString("O"),
            });
        return WindowResult(runtime.Manifest.PalaceId, start, end, new JsonObject
        {
            ["actor_id"] = RequiredArg(args, "actor_id"),
            ["neighbors"] = rows,
        });
    }

    private JsonObject GraphTraverseResult(JsonObject args)
    {
        var runtime = RequiredRuntime(args);
        var (start, end) = Window(args);
        var minimumWeight = ArgDouble(args, "minimum_edge_weight", 0);
        var graph = runtime.Graph.Snapshot(start, end, minimumWeight);
        var reached = graph.Traverse(
            RequiredArg(args, "actor_id"),
            ArgInt(args, "max_hops", 2, 1, 12),
            minimumWeight,
            ArgInt(args, "limit", 100, 1, 5000));
        var rows = new JsonArray();
        foreach (var reach in reached)
            rows.Add(new JsonObject
            {
                ["actor_id"] = reach.Actor.Id,
                ["kind"] = reach.Actor.Kind,
                ["display_name"] = reach.Actor.DisplayName,
                ["hops"] = reach.Hops,
                ["path_strength"] = reach.PathStrength,
            });
        return WindowResult(runtime.Manifest.PalaceId, start, end, new JsonObject
        {
            ["actor_id"] = RequiredArg(args, "actor_id"),
            ["reached"] = rows,
        });
    }

    private JsonObject GraphCommunitiesResult(JsonObject args)
    {
        var runtime = RequiredRuntime(args);
        var (start, end) = Window(args);
        var minimumWeight = ArgDouble(args, "minimum_edge_weight", 0);
        var graph = runtime.Graph.Snapshot(start, end, minimumWeight);
        var partition = new LeidenCommunityDetector().Detect(
            graph,
            new CommunityDetectionOptions(
                Resolution: ArgDouble(args, "resolution", 1),
                MinimumEdgeWeight: minimumWeight));
        var communities = new JsonArray();
        foreach (var community in partition.Communities)
            communities.Add(new JsonObject
            {
                ["community_id"] = community.Id,
                ["actor_ids"] = new JsonArray(
                    community.ActorIds.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["internal_weight"] = community.InternalWeight,
            });
        return WindowResult(runtime.Manifest.PalaceId, start, end, new JsonObject
        {
            ["algorithm"] = partition.Algorithm,
            ["modularity"] = partition.Modularity,
            ["levels"] = partition.Levels,
            ["local_moving_passes"] = partition.LocalMovingPasses,
            ["communities"] = communities,
        });
    }

    private Bogmem.Slices.Runtime.PalaceRuntime RequiredRuntime(JsonObject args)
    {
        var runtime = _runtime
                      ?? throw new InvalidOperationException("This MCP server is not bound to a palace runtime.");
        var routedPalaceId = ArgString(args, "palace_id");
        if (routedPalaceId is not null &&
            !string.Equals(routedPalaceId, runtime.Manifest.PalaceId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Palace routing mismatch: this service owns '{runtime.Manifest.PalaceId}', not '{routedPalaceId}'.");
        return runtime;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Window(JsonObject args)
    {
        var start = ArgTimestamp(args, "window_start");
        var end = ArgTimestamp(args, "window_end");
        if (end <= start)
            throw new ArgumentException("'window_end' must be after 'window_start'");
        return (start, end);
    }

    private static DateTimeOffset ArgTimestamp(JsonObject args, string name)
    {
        var value = RequiredArg(args, name);
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
            throw new ArgumentException($"'{name}' must be an ISO-8601 timestamp");
        return timestamp;
    }

    private static JsonObject WindowResult(
        string palaceId,
        DateTimeOffset start,
        DateTimeOffset end,
        JsonObject content)
    {
        content["palace_id"] = palaceId;
        content["window_start"] = start.ToString("O");
        content["window_end"] = end.ToString("O");
        return content;
    }
}
