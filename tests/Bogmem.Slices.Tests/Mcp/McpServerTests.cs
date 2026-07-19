using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Harness;
using Bogmem.Slices.Mcp;

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

        TestSupport.WriteModuleLedger("mcp", "S9",
            ("mcp_wire_golden", "pass", "EXACT", null),
            ("mcp_error_codes", "pass", "EXACT", "covered_by_golden+synthetic"),
            ("mcp_version_fallback", "pass", "EXACT", "covered_by_golden"));
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
