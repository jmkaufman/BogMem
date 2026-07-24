# Integrating BogMem

BogMem has four integration surfaces. Pick the smallest one that fits your
application; the persistent surfaces use BogDB and the fixed-window graph can
run entirely in memory.

## Real-world service example

[`Bogmem.MoleculeApi`](Bogmem.MoleculeApi/README.md) is a runnable ASP.NET
service for biological molecule memory. It demonstrates stable entity upserts,
sourced structured records, free-text candidate retrieval, and exact
all-capabilities filtering:

```bash
dotnet run --project samples/Bogmem.MoleculeApi -- --demo
```

Use this sample when the application needs to answer questions such as “which
molecules provide DNA repair and homologous recombination?” without treating a
similarity score as proof that both constraints matched.

## 1. CLI automation

Use the CLI from scripts, CI jobs, editor tasks, or a terminal:

```bash
PALACE="$HOME/.bogmem/palace"

bogmem mine ./my-project \
  --palace "$PALACE"

bogmem search "where is authentication configured?" \
  --wing my_project \
  --palace "$PALACE"

# Preview first. This never deletes.
bogmem sync ./my-project \
  --wing my_project \
  --palace "$PALACE"

# Apply requires the explicit project root.
bogmem sync ./my-project \
  --wing my_project \
  --apply \
  --palace "$PALACE"
```

Commands emit JSON on stdout. A nonzero exit code means the operation or its
arguments failed, so callers do not need to parse human-oriented log text.
From a source checkout, replace `bogmem` with
`dotnet run --project src/Bogmem.Cli --`.

### Route a project into rooms

Copy [`example-project/mempalace.yaml`](example-project/mempalace.yaml) into a
project root and edit the wing, room descriptions, and keywords. Then preview
the routing:

```bash
bogmem mine samples/example-project --dry-run
```

The JSON result reports `filesByRoom`, so a script or human can validate the
taxonomy before writing anything. Folder matches win over filename matches,
which win over content frequency. `--room one_room` deliberately bypasses
routing, and `--wing another_wing` overrides only the configured wing.

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
only the product operations that are actually implemented. The process owns one
palace runtime; start another process with a different `--palace` path for an
isolated MCP service. A practical agent flow is:

1. Call `mempalace_mine` once for a project.
2. Call `mempalace_search` before guessing about prior decisions or code.
3. Re-run `mempalace_mine` after meaningful file changes; unchanged sources do
   not write.
4. Call `mempalace_sync` without `apply` to inspect stale sources.
5. Apply sync only with the intended `project_dir`.

Event workflows can call `bogmem_graph_observe`, then query a temporal window
with `bogmem_graph_neighbors`, `bogmem_graph_traverse`, or
`bogmem_graph_communities`. Include the `palace_id` returned by
`bogmem_palace_status` when a router or Coliseum is selecting the destination.

The current retrieval mode combines local MiniLM semantic vectors in BogDB's
maintained HNSW index with native BM25. Check both
`mempalace_status.retrieval_mode` and `embedding_model` instead of assuming a
backend or model. `mempalace_mine` uses the same project configuration and
returns its `files_by_room` distribution.

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
using Bogmem.Slices.Runtime;

using var palace = PalaceRuntime.Open("/path/to/palace", "my-project");
new ProjectMiner(palace.Memory).Mine(new ProjectMineRequest(
    "/path/to/project",
    Wing: "my_project"));

var hits = palace.Memory.Search("authentication decision", wing: "my_project");
```

Keep one long-lived runtime per palace service and dispose it during shutdown.
Coordinate different processes through the CLI/MCP workflow so the palace
database lock can prevent competing owners.

The default constructor resolves local MiniLM and downloads its model on first
embedding. Tests or constrained offline deployments can inject an
`IMemoryEmbedder` directly or set `BOGMEM_EMBEDDING_MODEL=lexical`.

## 4. Weighted actor-graph memory

Run the fixed-window co-activity example:

```bash
dotnet run --project samples/Bogmem.ActorGraph
```

[`Bogmem.ActorGraph`](Bogmem.ActorGraph/README.md) demonstrates the intended
seam for workflow and stream-processing systems. The caller emits normalized,
replay-safe observations; `BogMem.Graph` aggregates weighted actor pairs,
serves neighborhoods, and detects communities. Use `ActorGraphWindow` for an
in-memory analysis window or `BogDbActorGraphStore` when observations must be
replayed across windows.
