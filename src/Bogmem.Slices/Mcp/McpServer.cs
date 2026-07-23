using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Slices.Storage;

namespace Bogmem.Slices.Mcp;

/// <summary>
/// JSON-RPC 2.0 MCP server wire: 36 tools (14 mutating), error codes
/// -32002/-32003/-32000, asymmetric protocol-version fallback
/// (missing→oldest, unrecognized→newest).
/// </summary>
public sealed class McpServer
{
    private readonly IMemoryStore? _memoryStore;
    public static readonly string[] SupportedProtocolVersions =
    [
        "2025-11-25",
        "2025-06-18",
        "2025-03-26",
        "2024-11-05",
    ];

    public const int ErrorSqliteIntegrity = -32002;
    public const int ErrorReadOnly = -32003;
    public const int ErrorInternal = -32000;

    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal)
    {
        "mempalace_kg_add", "mempalace_kg_invalidate", "mempalace_kg_supersede",
        "mempalace_create_tunnel", "mempalace_delete_tunnel", "mempalace_delete_hallway",
        "mempalace_add_drawer", "mempalace_checkpoint", "mempalace_delete_drawer",
        "mempalace_mine", "mempalace_delete_by_source", "mempalace_sync",
        "mempalace_update_drawer", "mempalace_diary_write",
    };

    private static readonly HashSet<string> SqliteIntegrityAllowedTools = new(StringComparer.Ordinal)
    {
        "mempalace_status",
        "mempalace_reconnect",
    };

    private static readonly HashSet<string> ProductTools = new(StringComparer.Ordinal)
    {
        "mempalace_status", "mempalace_list_wings", "mempalace_list_rooms", "mempalace_get_taxonomy",
        "mempalace_search", "mempalace_check_duplicate", "mempalace_add_drawer",
        "mempalace_delete_drawer", "mempalace_delete_by_source", "mempalace_get_drawer",
        "mempalace_list_drawers", "mempalace_update_drawer",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        PropertyNamingPolicy = null,
    };

    public bool ReadOnly { get; set; }
    public bool SqliteIntegrityOk { get; set; } = true;
    public bool ForceInternalError { get; set; }
    public string ServerVersion { get; set; } = "3.5.0";

    public IReadOnlyList<ToolSpec> ToolList { get; }
    public IReadOnlyDictionary<string, ToolSpec> Tools { get; }

    public McpServer(IMemoryStore? memoryStore = null)
    {
        _memoryStore = memoryStore;
        ToolList = LoadTools();
        Tools = ToolList.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public sealed record ToolSpec(string Name, string Description, JsonObject InputSchema, bool Mutating);

    public string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    public JsonNode? HandleRequest(JsonNode? request)
    {
        if (request is not JsonObject obj)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = null,
                ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "Invalid Request" },
            };
        }

        var method = obj["method"]?.GetValue<string>() ?? "";
        var paramsNode = obj["params"] as JsonObject ?? new JsonObject();
        var reqId = obj.ContainsKey("id") ? obj["id"]?.DeepClone() : null;
        var hasId = obj.ContainsKey("id");

        if (method == "initialize")
        {
            // params.get("protocolVersion", OLDEST) — missing key → oldest.
            string negotiated;
            if (paramsNode.TryGetPropertyValue("protocolVersion", out var pv) && pv is not null &&
                pv.GetValueKind() != JsonValueKind.Null)
            {
                var clientVersion = pv.GetValue<string>();
                negotiated = SupportedProtocolVersions.Contains(clientVersion)
                    ? clientVersion
                    : SupportedProtocolVersions[0]; // unrecognized → newest
            }
            else
            {
                negotiated = SupportedProtocolVersions[^1]; // missing → oldest
            }

            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = reqId,
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = negotiated,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "mempalace",
                        ["version"] = ServerVersion,
                    },
                },
            };
        }

        if (method == "ping")
        {
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = reqId, ["result"] = new JsonObject() };
        }

        if (method.StartsWith("notifications/", StringComparison.Ordinal))
            return null;

        if (method == "tools/list")
        {
            var arr = new JsonArray();
            foreach (var t in ToolList)
            {
                if (_memoryStore is not null && !ProductTools.Contains(t.Name)) continue;
                if (ReadOnly && t.Mutating) continue;
                arr.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = _memoryStore is not null && t.Name == "mempalace_search"
                        ? "Exact local hybrid lexical search over persistent BogDB drawers. Returns verbatim content with distance and ranking scores."
                        : t.Description,
                    ["inputSchema"] = t.InputSchema.DeepClone(),
                });
            }
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = reqId,
                ["result"] = new JsonObject { ["tools"] = arr },
            };
        }

        if (method == "tools/call")
        {
            if (obj["params"] is not JsonObject || !paramsNode.ContainsKey("name"))
            {
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = reqId,
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32602,
                        ["message"] = "Invalid params: 'name' is required for tools/call",
                    },
                };
            }

            var toolName = paramsNode["name"]?.GetValue<string>() ?? "";
            if (!Tools.TryGetValue(toolName, out var tool))
            {
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = reqId,
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32601,
                        ["message"] = $"Unknown tool: {toolName}",
                    },
                };
            }

            var toolArgs = paramsNode["arguments"] as JsonObject ?? new JsonObject();
            var schemaProps = tool.InputSchema["properties"] as JsonObject ?? new JsonObject();
            var unknown = toolArgs
                .Where(kv => !schemaProps.ContainsKey(kv.Key) && kv.Key != "wait_for_previous")
                .Select(kv => kv.Key)
                .ToList();
            if (unknown.Count > 0)
            {
                var quoted = string.Join(", ", unknown.Select(k => $"'{k}'"));
                var word = unknown.Count == 1 ? "parameter" : "parameters";
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = reqId,
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32602,
                        ["message"] = $"Unknown {word} {quoted} for tool {toolName}",
                    },
                };
            }

            if (ReadOnly && MutatingTools.Contains(toolName))
            {
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = reqId,
                    ["error"] = new JsonObject
                    {
                        ["code"] = ErrorReadOnly,
                        ["message"] = "Server is in read-only mode; this tool is disabled",
                        ["data"] = new JsonObject { ["tool"] = toolName },
                    },
                };
            }

            if (!SqliteIntegrityOk && !SqliteIntegrityAllowedTools.Contains(toolName))
            {
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = reqId,
                    ["error"] = new JsonObject
                    {
                        ["code"] = ErrorSqliteIntegrity,
                        ["message"] = "SQLite integrity check failed",
                        ["data"] = new JsonObject { ["tool"] = toolName },
                    },
                };
            }

            try
            {
                if (ForceInternalError)
                    throw new InvalidOperationException("forced internal error for parity fixture");
                var result = DispatchTool(toolName, toolArgs);
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = reqId,
                    ["result"] = new JsonObject
                    {
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = result.ToJsonString(JsonOptions),
                            },
                        },
                    },
                };
            }
            catch (Exception ex)
            {
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = reqId,
                    ["error"] = new JsonObject
                    {
                        ["code"] = ErrorInternal,
                        ["message"] = "Internal tool error",
                        ["data"] = new JsonObject
                        {
                            ["error_class"] = ex.GetType().Name,
                            ["message"] = ex.Message,
                        },
                    },
                };
            }
        }

        // Notifications (missing id) must never get a response.
        if (!hasId) return null;
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = reqId,
            ["error"] = new JsonObject
            {
                ["code"] = -32601,
                ["message"] = $"Unknown method: {method}",
            },
        };
    }

    public string? HandleRequestJson(string requestJson)
    {
        var node = JsonNode.Parse(requestJson);
        var response = HandleRequest(node);
        return response?.ToJsonString(JsonOptions);
    }

    private JsonObject DispatchTool(string name, JsonObject args)
    {
        if (_memoryStore is null) return DispatchStub(name, args);
        return name switch
        {
            "mempalace_status" => StatusResult(),
            "mempalace_list_wings" => WingsResult(),
            "mempalace_list_rooms" => RoomsResult(ArgString(args, "wing")),
            "mempalace_get_taxonomy" => TaxonomyResult(),
            "mempalace_search" => SearchResult(args),
            "mempalace_check_duplicate" => DuplicateResult(args),
            "mempalace_add_drawer" => AddDrawerResult(args),
            "mempalace_delete_drawer" => new JsonObject
            {
                ["success"] = _memoryStore.Delete(RequiredArg(args, "drawer_id")),
                ["drawer_id"] = RequiredArg(args, "drawer_id"),
            },
            "mempalace_delete_by_source" => DeleteBySourceResult(args),
            "mempalace_get_drawer" => GetDrawerResult(RequiredArg(args, "drawer_id")),
            "mempalace_list_drawers" => ListDrawersResult(args),
            "mempalace_update_drawer" => UpdateDrawerResult(args),
            _ => new JsonObject
            {
                ["success"] = false,
                ["error"] = $"Tool '{name}' has parity coverage but no product implementation yet.",
            },
        };
    }

    private JsonObject StatusResult()
    {
        var status = _memoryStore!.Status();
        return new JsonObject
        {
            ["status"] = "ok",
            ["backend"] = status.Backend,
            ["drawers"] = status.Drawers,
            ["wings"] = status.Wings,
            ["rooms"] = status.Rooms,
            ["database_path"] = status.DatabasePath,
            ["retrieval_mode"] = status.RetrievalMode,
            ["embedding_model"] = status.EmbeddingModel,
        };
    }

    private JsonObject WingsResult()
    {
        var array = new JsonArray();
        foreach (var group in AllDrawers().GroupBy(d => d.Wing, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            array.Add(new JsonObject { ["wing"] = group.Key, ["drawers"] = group.Count() });
        return new JsonObject { ["wings"] = array };
    }

    private JsonObject RoomsResult(string? wing)
    {
        var array = new JsonArray();
        foreach (var group in AllDrawers()
                     .Where(d => wing is null || string.Equals(d.Wing, wing, StringComparison.Ordinal))
                     .GroupBy(d => (d.Wing, d.Room))
                     .OrderBy(g => g.Key.Wing, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Room, StringComparer.Ordinal))
            array.Add(new JsonObject { ["wing"] = group.Key.Wing, ["room"] = group.Key.Room, ["drawers"] = group.Count() });
        return new JsonObject { ["rooms"] = array };
    }

    private JsonObject TaxonomyResult()
    {
        var wings = new JsonObject();
        foreach (var wing in AllDrawers().GroupBy(d => d.Wing, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rooms = new JsonObject();
            foreach (var room in wing.GroupBy(d => d.Room, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
                rooms[room.Key] = room.Count();
            wings[wing.Key] = rooms;
        }
        return new JsonObject { ["wings"] = wings };
    }

    private JsonObject SearchResult(JsonObject args)
    {
        var query = RequiredArg(args, "query");
        var hits = _memoryStore!.Search(
            query,
            ArgInt(args, "limit", 5, 1, 100),
            ArgString(args, "wing"),
            ArgString(args, "room"),
            ArgString(args, "source_file"),
            ArgDouble(args, "max_distance", 1.5));
        var array = new JsonArray();
        foreach (var hit in hits)
        {
            var row = DrawerJson(hit.Drawer);
            row["distance"] = hit.Distance;
            row["score"] = hit.Score;
            row["bm25_score"] = hit.Bm25Score;
            array.Add(row);
        }
        return new JsonObject
        {
            ["query"] = query,
            ["results"] = array,
            ["retrieval_mode"] = _memoryStore.Status().RetrievalMode,
        };
    }

    private JsonObject DuplicateResult(JsonObject args)
    {
        var content = RequiredArg(args, "content");
        var threshold = ArgDouble(args, "threshold", 0.9);
        var hit = _memoryStore!.Search(content, limit: 1).FirstOrDefault();
        var similarity = hit is null ? 0 : Math.Max(0, 1 - hit.Distance);
        return new JsonObject
        {
            ["is_duplicate"] = similarity >= threshold,
            ["similarity"] = similarity,
            ["drawer_id"] = hit?.Drawer.Id,
        };
    }

    private JsonObject AddDrawerResult(JsonObject args)
    {
        var result = _memoryStore!.Add(
            RequiredArg(args, "wing"),
            RequiredArg(args, "room"),
            RequiredArg(args, "content", trim: false),
            ArgString(args, "source_file"),
            ArgString(args, "added_by") ?? "mcp");
        return new JsonObject
        {
            ["success"] = true,
            ["reason"] = result.Created ? "created" : "already_exists",
            ["drawer_id"] = result.Drawer.Id,
            ["wing"] = result.Drawer.Wing,
            ["room"] = result.Drawer.Room,
            ["chunks"] = 1,
        };
    }

    private JsonObject GetDrawerResult(string id)
    {
        var drawer = _memoryStore!.Get(id);
        return drawer is null
            ? new JsonObject { ["success"] = false, ["error"] = $"Drawer not found: {id}" }
            : new JsonObject { ["success"] = true, ["drawer"] = DrawerJson(drawer) };
    }

    private JsonObject ListDrawersResult(JsonObject args)
    {
        var wing = ArgString(args, "wing");
        var room = ArgString(args, "room");
        var all = AllDrawers()
            .Where(d => wing is null || string.Equals(d.Wing, wing, StringComparison.Ordinal))
            .Where(d => room is null || string.Equals(d.Room, room, StringComparison.Ordinal))
            .ToArray();
        var limit = ArgInt(args, "limit", 20, 1, 100);
        var offset = ArgInt(args, "offset", 0, 0, int.MaxValue);
        var array = new JsonArray();
        foreach (var drawer in all.Skip(offset).Take(limit)) array.Add(DrawerJson(drawer, preview: true));
        return new JsonObject { ["drawers"] = array, ["total"] = all.Length, ["limit"] = limit, ["offset"] = offset };
    }

    private JsonObject UpdateDrawerResult(JsonObject args)
    {
        var id = RequiredArg(args, "drawer_id");
        var drawer = _memoryStore!.Update(id, ArgString(args, "content", trim: false), ArgString(args, "wing"), ArgString(args, "room"));
        return drawer is null
            ? new JsonObject { ["success"] = false, ["error"] = $"Drawer not found: {id}" }
            : new JsonObject { ["success"] = true, ["drawer"] = DrawerJson(drawer) };
    }

    private JsonObject DeleteBySourceResult(JsonObject args)
    {
        var source = RequiredArg(args, "source_file");
        var dryRun = args["dry_run"]?.GetValue<bool>() ?? true;
        var count = _memoryStore!.DeleteBySource(source, dryRun);
        return new JsonObject { ["success"] = true, ["dry_run"] = dryRun, [dryRun ? "matched" : "deleted"] = count };
    }

    private IReadOnlyList<MemoryDrawer> AllDrawers()
    {
        var all = new List<MemoryDrawer>();
        for (var offset = 0; ; offset += 100)
        {
            var page = _memoryStore!.List(limit: 100, offset: offset);
            all.AddRange(page);
            if (page.Count < 100) break;
        }
        return all;
    }

    private static JsonObject DrawerJson(MemoryDrawer drawer, bool preview = false) => new()
    {
        ["id"] = drawer.Id,
        ["wing"] = drawer.Wing,
        ["room"] = drawer.Room,
        [preview ? "preview" : "content"] = preview && drawer.Content.Length > 200 ? drawer.Content[..200] : drawer.Content,
        ["source_file"] = Path.GetFileName(drawer.SourceFile),
        ["source_path"] = drawer.SourceFile,
        ["added_by"] = drawer.AddedBy,
        ["filed_at"] = drawer.FiledAt,
        ["id_recipe"] = drawer.IdRecipe,
    };

    private static string RequiredArg(JsonObject args, string name, bool trim = true) =>
        ArgString(args, name, trim) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"'{name}' is required");

    private static string? ArgString(JsonObject args, string name, bool trim = true)
    {
        var value = args[name]?.GetValue<string>();
        return value is null || !trim ? value : value.Trim();
    }

    private static int ArgInt(JsonObject args, string name, int fallback, int min, int max)
    {
        var value = args[name]?.GetValue<int>() ?? fallback;
        if (value < min || value > max) throw new ArgumentOutOfRangeException(name, $"{name} must be between {min} and {max}");
        return value;
    }

    private static double ArgDouble(JsonObject args, string name, double fallback) => args[name]?.GetValue<double>() ?? fallback;

    private static JsonObject DispatchStub(string name, JsonObject args) => name switch
    {
        "mempalace_status" => new JsonObject { ["status"] = "ok", ["drawers"] = 0, ["backend"] = "chroma" },
        "mempalace_list_wings" => new JsonObject { ["wings"] = new JsonArray() },
        "mempalace_list_rooms" => new JsonObject { ["rooms"] = new JsonArray() },
        "mempalace_get_taxonomy" => new JsonObject { ["wings"] = new JsonObject() },
        "mempalace_get_aaak_spec" => new JsonObject { ["spec"] = "aaak" },
        "mempalace_kg_query" => new JsonObject { ["triples"] = new JsonArray() },
        "mempalace_kg_add" => new JsonObject { ["added"] = true },
        "mempalace_kg_invalidate" => new JsonObject { ["invalidated"] = true },
        "mempalace_kg_supersede" => new JsonObject { ["superseded"] = true },
        "mempalace_kg_timeline" => new JsonObject { ["events"] = new JsonArray() },
        "mempalace_kg_stats" => new JsonObject { ["triples"] = 0 },
        "mempalace_traverse" => new JsonObject { ["path"] = new JsonArray() },
        "mempalace_find_tunnels" => new JsonObject { ["tunnels"] = new JsonArray() },
        "mempalace_graph_stats" => new JsonObject { ["tunnels"] = 0, ["hallways"] = 0 },
        "mempalace_create_tunnel" => new JsonObject { ["created"] = true },
        "mempalace_list_tunnels" => new JsonObject { ["tunnels"] = new JsonArray() },
        "mempalace_delete_tunnel" => new JsonObject { ["deleted"] = true },
        "mempalace_list_hallways" => new JsonObject { ["hallways"] = new JsonArray() },
        "mempalace_delete_hallway" => new JsonObject { ["deleted"] = true },
        "mempalace_follow_tunnels" => new JsonObject { ["rooms"] = new JsonArray() },
        "mempalace_search" => new JsonObject { ["results"] = new JsonArray(), ["query"] = args["query"]?.GetValue<string>() ?? "" },
        "mempalace_check_duplicate" => new JsonObject { ["is_duplicate"] = false },
        "mempalace_add_drawer" => new JsonObject { ["id"] = "drawer_stub" },
        "mempalace_checkpoint" => new JsonObject { ["filed"] = 0 },
        "mempalace_delete_drawer" => new JsonObject { ["deleted"] = true },
        "mempalace_mine" => new JsonObject { ["drawers_filed"] = 0 },
        "mempalace_delete_by_source" => new JsonObject { ["deleted"] = 0 },
        "mempalace_sync" => new JsonObject { ["synced"] = true },
        "mempalace_get_drawer" => new JsonObject { ["id"] = args["drawer_id"]?.GetValue<string>() ?? "" },
        "mempalace_list_drawers" => new JsonObject { ["drawers"] = new JsonArray() },
        "mempalace_update_drawer" => new JsonObject { ["updated"] = true },
        "mempalace_diary_write" => new JsonObject { ["written"] = true },
        "mempalace_diary_read" => new JsonObject { ["entries"] = new JsonArray() },
        "mempalace_hook_settings" => new JsonObject { ["enabled"] = true },
        "mempalace_memories_filed_away" => new JsonObject { ["count"] = 0 },
        "mempalace_reconnect" => new JsonObject { ["reconnected"] = true },
        _ => throw new InvalidOperationException($"Unhandled tool {name}"),
    };

    private static List<ToolSpec> LoadTools()
    {
        // Prefer adjacent tools.json (source / output), then embedded resource.
        var candidates = new List<string>();
        var asm = Assembly.GetExecutingAssembly();
        var asmDir = Path.GetDirectoryName(asm.Location);
        if (!string.IsNullOrEmpty(asmDir))
            candidates.Add(Path.Combine(asmDir, "Mcp", "tools.json"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Mcp", "tools.json"));
        // Walk up for repo checkout during `dotnet run`.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            candidates.Add(Path.Combine(dir.FullName, "src", "Bogmem.Slices", "Mcp", "tools.json"));
            dir = dir.Parent;
        }

        string? json = null;
        foreach (var path in candidates.Distinct())
        {
            if (File.Exists(path))
            {
                json = File.ReadAllText(path);
                break;
            }
        }
        if (json is null)
        {
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("tools.json", StringComparison.Ordinal));
            if (resName is not null)
            {
                using var s = asm.GetManifestResourceStream(resName)!;
                using var r = new StreamReader(s);
                json = r.ReadToEnd();
            }
        }
        if (json is null)
            throw new InvalidOperationException("MCP tools.json not found");

        using var doc = JsonDocument.Parse(json);
        var list = new List<ToolSpec>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = el.GetProperty("name").GetString()!;
            var desc = el.GetProperty("description").GetString()!;
            var schema = JsonNode.Parse(el.GetProperty("inputSchema").GetRawText()) as JsonObject
                         ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            list.Add(new ToolSpec(name, desc, schema, MutatingTools.Contains(name)));
        }
        if (list.Count != 36)
            throw new InvalidOperationException($"Expected 36 tools, got {list.Count}");
        return list;
    }
}
