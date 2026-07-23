# Integrating BogMem

BogMem has three integration surfaces. Pick the smallest one that fits your
application; all three use the same persistent BogDB palace.

## 1. CLI automation

Use the CLI from scripts, CI jobs, editor tasks, or a terminal:

```bash
PALACE="$HOME/.bogmem/palace"

dotnet run --project src/Bogmem.Cli -- mine ./my-project \
  --wing my_project \
  --palace "$PALACE"

dotnet run --project src/Bogmem.Cli -- search "where is authentication configured?" \
  --wing my_project \
  --palace "$PALACE"

# Preview first. This never deletes.
dotnet run --project src/Bogmem.Cli -- sync ./my-project \
  --wing my_project \
  --palace "$PALACE"

# Apply requires the explicit project root.
dotnet run --project src/Bogmem.Cli -- sync ./my-project \
  --wing my_project \
  --apply \
  --palace "$PALACE"
```

Commands emit JSON on stdout. A nonzero exit code means the operation or its
arguments failed, so callers do not need to parse human-oriented log text.

## 2. MCP clients and coding agents

During development, point an MCP host at the checkout using absolute paths:

```json
{
  "mcpServers": {
    "bogmem": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/BogMem/src/Bogmem.Cli",
        "--",
        "mcp",
        "--palace",
        "/absolute/path/to/palace"
      ]
    }
  }
}
```

The server uses newline-delimited JSON-RPC over stdio. Its advertised tools are
only the product operations that are actually implemented. A practical agent
flow is:

1. Call `mempalace_mine` once for a project.
2. Call `mempalace_search` before guessing about prior decisions or code.
3. Re-run `mempalace_mine` after meaningful file changes; unchanged sources do
   not write.
4. Call `mempalace_sync` without `apply` to inspect stale sources.
5. Apply sync only with the intended `project_dir`.

The current retrieval mode is exact hybrid lexical retrieval, not semantic
search. Check `mempalace_status.retrieval_mode` instead of assuming a backend.

## 3. Embedded .NET API

Run the self-contained lifecycle example:

```bash
dotnet run --project samples/Bogmem.Quickstart
```

The sample creates disposable project and palace directories, then demonstrates
project mining, direct application-owned memories, search, sync preview, and
scoped sync apply. Its source is
[`Bogmem.Quickstart/Program.cs`](Bogmem.Quickstart/Program.cs).

The essential embedded setup is:

```csharp
using Bogmem.Slices.Mining;
using Bogmem.Slices.Storage;

using var store = new BogDbMemoryStore("/path/to/palace");
new ProjectMiner(store).Mine(new ProjectMineRequest(
    "/path/to/project",
    Wing: "my_project"));

var hits = store.Search("authentication decision", wing: "my_project");
```

Keep one long-lived store per process. Dispose it during shutdown. Coordinate
different processes through the CLI/MCP workflow so the palace write lock can
prevent mine/sync overlap.
