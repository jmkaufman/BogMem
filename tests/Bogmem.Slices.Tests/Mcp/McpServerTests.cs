using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Cli;
using Bogmem.Graph;
using Bogmem.Harness;
using Bogmem.Slices.Mcp;
using Bogmem.Slices.Embedding;
using Bogmem.Slices.Runtime;
using Bogmem.Slices.Storage;

namespace Bogmem.Slices.Tests.Mcp;

public static class McpServerTests
{
    public static void Run()
    {
        var path = TestSupport.PathUnderRepo("golden", "mcp", "vectors.jsonl");
        var failures = new List<string>();
        var sawErrorCodes = new HashSet<int>();
        var sawMissingVersion = false;
        var sawUnknownVersion = false;

        foreach (var row in CorpusReplay.Read(path))
        {
            var input = row.Input;
            var readOnly = input.TryGetProperty("read_only", out var ro) && ro.GetBoolean();
            var mcp = new McpServer { ReadOnly = readOnly };
            var requestEl = input.GetProperty("request");
            JsonNode? requestNode = JsonNode.Parse(requestEl.GetRawText());
            var actual = mcp.HandleRequest(requestNode);

            var expectedWrap = row.Expected.GetProperty("response");
            if (expectedWrap.ValueKind == JsonValueKind.Null)
            {
                if (actual is not null)
                    failures.Add($"{row.Id}: expected null response, got {actual}");
                continue;
            }

            if (actual is null)
            {
                failures.Add($"{row.Id}: expected response, got null");
                continue;
            }

            // Semantic JSON equality (property order / whitespace independent).
            using var expDoc = JsonDocument.Parse(expectedWrap.GetRawText());
            using var actDoc = JsonDocument.Parse(actual.ToJsonString());
            if (!JsonEqual(actDoc.RootElement, expDoc.RootElement))
            {
                failures.Add($"{row.Id}:\n  actual  ={actual.ToJsonString()}\n  expected={expectedWrap.GetRawText()}");
            }

            if (expDoc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("code", out var code))
                sawErrorCodes.Add(code.GetInt32());

            if (row.Id == "initialize_missing_version") sawMissingVersion = true;
            if (row.Id == "initialize_unknown_version") sawUnknownVersion = true;
        }

        // Exercise -32002 / -32000 beyond the golden fixture set.
        var s2 = new McpServer { SqliteIntegrityOk = false };
        var r2 = s2.HandleRequest(JsonNode.Parse("""{"jsonrpc":"2.0","id":99,"method":"tools/call","params":{"name":"mempalace_search","arguments":{"query":"x"}}}""")!);
        TestSupport.AssertTrue(r2!["error"]!["code"]!.GetValue<int>() == McpServer.ErrorSqliteIntegrity, "-32002");
        sawErrorCodes.Add(McpServer.ErrorSqliteIntegrity);

        var s3 = new McpServer { ForceInternalError = true };
        var r3 = s3.HandleRequest(JsonNode.Parse("""{"jsonrpc":"2.0","id":100,"method":"tools/call","params":{"name":"mempalace_status","arguments":{}}}""")!);
        TestSupport.AssertTrue(r3!["error"]!["code"]!.GetValue<int>() == McpServer.ErrorInternal, "-32000");
        sawErrorCodes.Add(McpServer.ErrorInternal);

        TestSupport.AssertTrue(failures.Count == 0, "MCP failures:\n" + string.Join("\n", failures));
        TestSupport.AssertTrue(sawMissingVersion && sawUnknownVersion, "version fallback branches");
        TestSupport.AssertTrue(sawErrorCodes.Contains(-32003), "read-only -32003");
        TestSupport.AssertTrue(sawErrorCodes.Contains(-32002) && sawErrorCodes.Contains(-32000), "error codes");
        var server = new McpServer();
        TestSupport.AssertTrue(server.ToolList.Count == 36, "36 tools");
        TestSupport.AssertTrue(server.ToolList.Count(t => t.Mutating) == 14, "14 mutating");

        ExercisePersistentTools();
        ExerciseRuntimeGraphTools();
        ExerciseColiseumTools();
        ExerciseHttpTransport().GetAwaiter().GetResult();

        TestSupport.WriteModuleLedger("mcp", "S9",
            ("mcp_wire_golden", "pass", "EXACT", null),
            ("mcp_error_codes", "pass", "EXACT", "covered_by_golden+synthetic"),
            ("mcp_version_fallback", "pass", "EXACT", "covered_by_golden"),
            ("mcp_streamable_http", "pass", "PRODUCT", "stateless JSON response mode"));
    }

    private static async Task ExerciseHttpTransport()
    {
        TestSupport.AssertThrows<ArgumentException>(
            () => new McpHttpOptions("http://0.0.0.0:7079"),
            "non-loopback MCP HTTP requires authentication");

        const string token = "bogmem-http-test-token";
        const string allowedOrigin = "https://ftt.example";
        var options = new McpHttpOptions(
            "http://127.0.0.1:0",
            bearerToken: token,
            allowedOrigins: [allowedOrigin]);
        await using var host = McpHttpTransport.Create(new McpServer(), options);
        await host.StartAsync();

        using var client = new HttpClient
        {
            BaseAddress = new Uri(host.Addresses.Single()),
        };

        using (var unauthorized = await PostMcpAsync(
                   client,
                   """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""))
            TestSupport.AssertEqual(
                HttpStatusCode.Unauthorized,
                unauthorized.StatusCode,
                "MCP HTTP bearer authentication");

        using (var forbiddenOrigin = await PostMcpAsync(
                   client,
                   """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
                   token,
                   "https://attacker.example"))
            TestSupport.AssertEqual(
                HttpStatusCode.Forbidden,
                forbiddenOrigin.StatusCode,
                "MCP HTTP origin validation");

        using (var initialize = await PostMcpAsync(
                   client,
                   """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""",
                   token,
                   allowedOrigin,
                   "2025-11-25"))
        {
            TestSupport.AssertEqual(HttpStatusCode.OK, initialize.StatusCode, "MCP HTTP initialize status");
            TestSupport.AssertEqual(
                "application/json",
                initialize.Content.Headers.ContentType?.MediaType,
                "MCP HTTP JSON response");
            TestSupport.AssertEqual(
                allowedOrigin,
                initialize.Headers.GetValues("Access-Control-Allow-Origin").Single(),
                "MCP HTTP CORS origin");
            var body = JsonNode.Parse(await initialize.Content.ReadAsStringAsync())!;
            TestSupport.AssertEqual(
                "mempalace",
                body["result"]!["serverInfo"]!["name"]!.GetValue<string>(),
                "MCP HTTP dispatcher reuse");
        }

        using (var notification = await PostMcpAsync(
                   client,
                   """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                   token,
                   protocolVersion: "2025-11-25"))
        {
            TestSupport.AssertEqual(
                HttpStatusCode.Accepted,
                notification.StatusCode,
                "MCP HTTP notification status");
            TestSupport.AssertEqual(
                "",
                await notification.Content.ReadAsStringAsync(),
                "MCP HTTP notification body");
        }

        using (var clientResponse = await PostMcpAsync(
                   client,
                   """{"jsonrpc":"2.0","id":7,"result":{}}""",
                   token,
                   protocolVersion: "2025-11-25"))
            TestSupport.AssertEqual(
                HttpStatusCode.Accepted,
                clientResponse.StatusCode,
                "MCP HTTP client response status");

        using (var badVersion = await PostMcpAsync(
                   client,
                   """{"jsonrpc":"2.0","id":2,"method":"ping"}""",
                   token,
                   protocolVersion: "2099-01-01"))
            TestSupport.AssertEqual(
                HttpStatusCode.BadRequest,
                badVersion.StatusCode,
                "MCP HTTP protocol version validation");

        using (var badAccept = await PostMcpAsync(
                   client,
                   """{"jsonrpc":"2.0","id":2,"method":"ping"}""",
                   token,
                   includeEventStreamAccept: false))
            TestSupport.AssertEqual(
                HttpStatusCode.NotAcceptable,
                badAccept.StatusCode,
                "MCP HTTP Accept validation");

        using (var get = new HttpRequestMessage(HttpMethod.Get, "mcp"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var getResponse = await client.SendAsync(get);
            TestSupport.AssertEqual(
                HttpStatusCode.MethodNotAllowed,
                getResponse.StatusCode,
                "MCP HTTP stateless GET");
        }

        using (var health = new HttpRequestMessage(HttpMethod.Get, "healthz"))
        {
            health.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var healthResponse = await client.SendAsync(health);
            TestSupport.AssertEqual(HttpStatusCode.OK, healthResponse.StatusCode, "MCP HTTP health");
            var body = JsonNode.Parse(await healthResponse.Content.ReadAsStringAsync())!;
            TestSupport.AssertEqual(
                "streamable-http",
                body["transport"]!.GetValue<string>(),
                "MCP HTTP health transport");
        }

        await host.StopAsync();
    }

    private static async Task<HttpResponseMessage> PostMcpAsync(
        HttpClient client,
        string json,
        string? token = null,
        string? origin = null,
        string? protocolVersion = null,
        bool includeEventStreamAccept = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (includeEventStreamAccept)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);
        if (protocolVersion is not null)
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
        return await client.SendAsync(request);
    }

    private static void ExerciseRuntimeGraphTools()
    {
        var path = Directory.CreateTempSubdirectory("bogmem-mcp-runtime-").FullName;
        try
        {
            using var runtime = PalaceRuntime.Open(path, "social-signals", new LexicalHashEmbedder());
            var server = new McpServer(runtime);
            var toolsResponse = server.HandleRequest(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""")!)!;
            var tools = toolsResponse["result"]!["tools"]!.AsArray();
            TestSupport.AssertEqual(19, tools.Count, "runtime MCP product tool count");
            TestSupport.AssertTrue(
                tools.Any(tool => tool!["name"]!.GetValue<string>() == "bogmem_graph_observe"),
                "runtime MCP advertises graph observation");

            var initialize = server.HandleRequest(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!)!;
            TestSupport.AssertEqual(
                "bogmem",
                initialize["result"]!["serverInfo"]!["name"]!.GetValue<string>(),
                "runtime MCP server identity");
            TestSupport.AssertTrue(
                initialize["result"]!["serverInfo"]!["version"]!.GetValue<string>() != "3.5.0",
                "runtime MCP reports BogMem rather than MemPalace version");

            var occurredAt = "2026-07-23T12:05:00Z";
            var observeArgs = new JsonObject
            {
                ["palace_id"] = runtime.Manifest.PalaceId,
                ["observation_id"] = "mcp-signal-1",
                ["occurred_at"] = occurredAt,
                ["actor_ids"] = new JsonArray("account-a", "account-b", "account-c"),
                ["weight"] = 2.0,
                ["context"] = "shared-endpoint",
                ["source"] = "ftt",
                ["workflow_id"] = "social-signal-ingest",
                ["run_id"] = "run-42",
                ["artifact_id"] = "artifact-7",
                ["signal_type"] = "co-activity",
            };
            var observed = ToolResult(server, "bogmem_graph_observe", observeArgs);
            TestSupport.AssertEqual("created", observed["reason"]!.GetValue<string>(), "MCP graph observation");
            var replayed = ToolResult(
                server,
                "bogmem_graph_observe",
                observeArgs.DeepClone().AsObject());
            TestSupport.AssertEqual("already_exists", replayed["reason"]!.GetValue<string>(), "MCP graph replay");

            var neighbors = ToolResult(server, "bogmem_graph_neighbors", new JsonObject
            {
                ["palace_id"] = runtime.Manifest.PalaceId,
                ["actor_id"] = "account-a",
                ["window_start"] = "2026-07-23T12:00:00Z",
                ["window_end"] = "2026-07-23T13:00:00Z",
            });
            TestSupport.AssertEqual(2, neighbors["neighbors"]!.AsArray().Count, "MCP graph neighbors");
            TestSupport.AssertEqual(
                runtime.Manifest.PalaceId,
                neighbors["palace_id"]!.GetValue<string>(),
                "MCP result palace provenance");

            var communities = ToolResult(server, "bogmem_graph_communities", new JsonObject
            {
                ["window_start"] = "2026-07-23T12:00:00Z",
                ["window_end"] = "2026-07-23T13:00:00Z",
            });
            TestSupport.AssertEqual(
                LeidenCommunityDetector.AlgorithmId,
                communities["algorithm"]!.GetValue<string>(),
                "MCP community algorithm identity");
            TestSupport.AssertEqual(1, communities["communities"]!.AsArray().Count, "MCP community count");

            var mismatch = server.HandleRequest(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 9,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "bogmem_graph_neighbors",
                    ["arguments"] = new JsonObject
                    {
                        ["palace_id"] = "another-palace",
                        ["actor_id"] = "account-a",
                        ["window_start"] = "2026-07-23T12:00:00Z",
                        ["window_end"] = "2026-07-23T13:00:00Z",
                    },
                },
            })!;
            TestSupport.AssertEqual(
                -32602,
                mismatch["error"]!["code"]!.GetValue<int>(),
                "palace routing mismatch rejected");

            var readOnly = new McpServer(runtime) { ReadOnly = true };
            var readOnlyTools = readOnly.HandleRequest(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""")!)!;
            TestSupport.AssertTrue(
                readOnlyTools["result"]!["tools"]!.AsArray()
                    .All(tool => tool!["name"]!.GetValue<string>() != "bogmem_graph_observe"),
                "read-only runtime hides graph mutation");
            var denied = readOnly.HandleRequest(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 10,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "bogmem_graph_observe",
                    ["arguments"] = new JsonObject(),
                },
            })!;
            TestSupport.AssertEqual(
                McpServer.ErrorReadOnly,
                denied["error"]!["code"]!.GetValue<int>(),
                "read-only runtime rejects graph mutation");
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static void ExercisePersistentTools()
    {
        var path = Directory.CreateTempSubdirectory("bogmem-mcp-product-").FullName;
        try
        {
            using var store = new BogDbMemoryStore(path);
            var server = new McpServer(store);
            var toolsResponse = server.HandleRequest(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""")!)!;
            var productTools = toolsResponse["result"]!["tools"]!.AsArray();
            TestSupport.AssertTrue(productTools.Count == 14, "functional MCP should advertise only implemented product tools");
            TestSupport.AssertTrue(productTools.All(t => !t!["name"]!.GetValue<string>().StartsWith("mempalace_kg_", StringComparison.Ordinal)),
                "functional MCP must not advertise parity-only KG stubs");
            var add = ToolResult(server, "mempalace_add_drawer",
                new JsonObject { ["wing"] = "project", ["room"] = "backend", ["content"] = "BogDB stores the durable memory records." });
            TestSupport.AssertTrue(add["success"]!.GetValue<bool>(), "functional MCP add");
            var status = ToolResult(server, "mempalace_status", new JsonObject());
            TestSupport.AssertTrue(status["backend"]!.GetValue<string>() == "bogdb" && status["drawers"]!.GetValue<int>() == 1,
                "functional MCP status");
            var search = ToolResult(server, "mempalace_search", new JsonObject { ["query"] = "durable BogDB memory" });
            TestSupport.AssertTrue(search["results"] is JsonArray { Count: 1 }, "functional MCP search");

            var project = Directory.CreateTempSubdirectory("bogmem-mcp-mine-").FullName;
            try
            {
                var notes = Path.Combine(project, "notes.md");
                File.WriteAllText(notes,
                    string.Join("\n\n", Enumerable.Repeat("MCP project mining stores verbatim durable notes.", 40)));
                var mine = ToolResult(server, "mempalace_mine",
                    new JsonObject { ["source"] = project, ["wing"] = "mcp_project" });
                TestSupport.AssertTrue(mine["success"]!.GetValue<bool>() && mine["drawers_written"]!.GetValue<int>() > 0,
                    "functional MCP mine should write project drawers");
                File.Delete(notes);
                var preview = ToolResult(server, "mempalace_sync",
                    new JsonObject { ["project_dir"] = project, ["wing"] = "mcp_project" });
                TestSupport.AssertTrue(preview["success"]!.GetValue<bool>() && preview["dry_run"]!.GetValue<bool>() &&
                                       preview["missing"]!.GetValue<int>() > 0 && preview["removed_drawers"]!.GetValue<int>() == 0,
                    "functional MCP sync should preview by default");
                var unsafeApply = ToolResult(server, "mempalace_sync",
                    new JsonObject { ["wing"] = "mcp_project", ["apply"] = true });
                TestSupport.AssertTrue(!unsafeApply["success"]!.GetValue<bool>(), "functional MCP sync must reject unscoped apply");
                var apply = ToolResult(server, "mempalace_sync",
                    new JsonObject { ["project_dir"] = project, ["wing"] = "mcp_project", ["apply"] = true });
                TestSupport.AssertTrue(apply["success"]!.GetValue<bool>() && apply["removed_drawers"]!.GetValue<int>() > 0,
                    "functional MCP sync apply should remove missing project drawers");
            }
            finally
            {
                if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static void ExerciseColiseumTools()
    {
        var root = Directory.CreateTempSubdirectory("bogmem-mcp-coliseum-").FullName;
        var registry = new PalaceRegistry(Path.Combine(root, "registry.json"));
        try
        {
            foreach (var palaceName in new[] { "alpha", "beta" })
            {
                using var runtime = PalaceRuntime.Open(
                    Path.Combine(root, palaceName),
                    palaceName,
                    new LexicalHashEmbedder());
                runtime.Memory.Add(
                    "shared",
                    "decisions",
                    $"{palaceName} uses signed authentication envelopes.");
                runtime.Graph.Observe(new(
                    $"{palaceName}:signal:1",
                    DateTimeOffset.Parse("2026-07-23T12:15:00Z"),
                    ["account-root", $"account-{palaceName}"],
                    Weight: palaceName == "alpha" ? 3 : 2));
                registry.Register(runtime);
            }

            var server = new McpServer(registry);
            var initialize = server.HandleRequest(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!)!;
            TestSupport.AssertEqual(
                "bogmem-coliseum",
                initialize["result"]!["serverInfo"]!["name"]!.GetValue<string>(),
                "Coliseum MCP identity");

            var toolsResponse = server.HandleRequest(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""")!)!;
            var tools = toolsResponse["result"]!["tools"]!.AsArray();
            TestSupport.AssertEqual(2, tools.Count, "Coliseum MCP tool count");
            TestSupport.AssertTrue(
                tools.All(tool =>
                    !server.Tools[tool!["name"]!.GetValue<string>()].Mutating),
                "Coliseum MCP is read-only");

            var recall = ToolResult(server, "bogmem_recall", new JsonObject
            {
                ["query"] = "signed authentication envelope",
                ["limit"] = 2,
                ["max_distance"] = 2,
            });
            TestSupport.AssertEqual(2, recall["palaces_succeeded"]!.GetValue<int>(), "MCP federated recall");
            TestSupport.AssertEqual(2, recall["results"]!.AsArray().Count, "MCP federated results");
            TestSupport.AssertEqual(
                2,
                recall["results"]!.AsArray()
                    .Select(hit => hit!["palace_id"]!.GetValue<string>())
                    .Distinct()
                    .Count(),
                "MCP results preserve palace provenance");

            var neighbors = ToolResult(
                server,
                "bogmem_graph_recall_neighbors",
                new JsonObject
                {
                    ["actor_id"] = "account-root",
                    ["window_start"] = "2026-07-23T12:00:00Z",
                    ["window_end"] = "2026-07-23T13:00:00Z",
                });
            TestSupport.AssertEqual(
                2,
                neighbors["neighbors"]!.AsArray().Count,
                "MCP federated graph recall");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static JsonObject ToolResult(McpServer server, string name, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
        };
        var response = server.HandleRequest(request)!.AsObject();
        if (response["error"] is not null) throw new InvalidOperationException(response["error"]!.ToJsonString());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        return JsonNode.Parse(text)!.AsObject();
    }

    private static bool JsonEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var ap = a.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                    var bp = b.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                    if (ap.Count != bp.Count) return false;
                    for (var i = 0; i < ap.Count; i++)
                    {
                        if (ap[i].Name != bp[i].Name) return false;
                        if (!JsonEqual(ap[i].Value, bp[i].Value)) return false;
                    }
                    return true;
                }
            case JsonValueKind.Array:
                {
                    var aa = a.EnumerateArray().ToList();
                    var bb = b.EnumerateArray().ToList();
                    if (aa.Count != bb.Count) return false;
                    for (var i = 0; i < aa.Count; i++)
                        if (!JsonEqual(aa[i], bb[i])) return false;
                    return true;
                }
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Number:
                return a.GetRawText() == b.GetRawText() ||
                       Math.Abs(a.GetDouble() - b.GetDouble()) < 1e-12;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return a.GetRawText() == b.GetRawText();
        }
    }
}
