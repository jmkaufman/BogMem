# BogMem

BogMem is a .NET 10, local-first memory store derived from MemPalace. It now
contains both the frozen compatibility corpus and two usable product paths:
hybrid-searchable drawers and weighted temporal graph memory, both persisted
in BogDB.

See the [changelog](CHANGELOG.md) for release history and work planned for the
next preview.

## Quick start

BogMem runs `all-MiniLM-L6-v2` locally by default. The first command that needs
an embedding downloads roughly 90 MB into the local model cache; later runs are
offline. Set `BOGMEM_MODEL_CACHE` to choose the cache directory.

```bash
# From a source checkout. Uses ~/.bogmem/palace unless --palace is supplied.
dotnet run --project src/Bogmem.Cli -- init --name my-project-memory

dotnet run --project src/Bogmem.Cli -- add \
  --wing myproject \
  --room decisions \
  --content "We chose BogDB for durable local memory."

dotnet run --project src/Bogmem.Cli -- mine . --wing myproject
dotnet run --project src/Bogmem.Cli -- search "why did we choose BogDB?"
dotnet run --project src/Bogmem.Cli -- sync . --wing myproject
dotnet run --project src/Bogmem.Cli -- status
```

The CLI is also a .NET tool package. Until `BogMem.Tool` is published, pack and
install it from the checkout:

```bash
dotnet pack src/Bogmem.Cli -c Release -o ./artifacts/packages
dotnet tool install --global BogMem.Tool \
  --version 0.1.0-preview.1 \
  --add-source ./artifacts/packages

bogmem --help
```

After the package is published, the `--add-source` option is unnecessary.

Run the persistent MCP server over newline-delimited JSON-RPC on stdio:

```bash
dotnet run --project src/Bogmem.Cli -- mcp --palace ~/.bogmem/palace
```

For FTT, services, and other independently managed processes, run the same MCP
surface over stateless Streamable HTTP:

```bash
export BOGMEM_MCP_TOKEN="$(openssl rand -hex 32)"
dotnet run --project src/Bogmem.Cli -- mcp \
  --palace ~/.bogmem/palace \
  --transport http \
  --listen http://127.0.0.1:7079
```

The MCP endpoint is `POST /mcp` and liveness is `GET /healthz`. The server
defaults to loopback, validates browser origins, and requires bearer
authentication for non-loopback listeners. See
[`docs/mcp-http.md`](docs/mcp-http.md) for the FTT request contract, security
settings, and a copyable client exchange.

Each live MCP process is bound to one persisted palace runtime. It exposes
status, taxonomy/listing, project mining, search, duplicate checks, drawer CRUD,
and temporal actor-graph observation and recall. Parity-only tool definitions
stay in the compatibility harness but are not advertised by the live server.

### Project mining

`bogmem mine` ingests the same ordinary text and code extensions as the
MemPalace project miner. In a Git worktree it asks Git for tracked and
non-ignored untracked files; elsewhere it skips common generated directories.
Files are stored verbatim using the parity-proven window chunker and legacy
source/chunk ID recipe. A changed source is replaced atomically, while an
unchanged rerun performs no writes.

Add `mempalace.yaml` to a project root to select its wing and rooms:

```yaml
wing: checkout_service
rooms:
  - name: architecture
    keywords: [docs, design, decision]
  - name: backend
    keywords: [api, database, service]
  - name: general
    keywords: []
```

The miner classifies each source by folder, filename, then keyword frequency in
the first 2,000 characters. Unmatched sources go to `general`. Explicit
`--wing` or `--room` values override configuration, and legacy `mempal.yaml`
and `.yml` names remain supported. Use `--dry-run` to inspect `filesByRoom`
without changing the palace. A copyable configured project lives under
[`samples/example-project/`](samples/example-project/).

Conversation and office-document extraction are not implemented yet.

`bogmem sync` previews project-owned drawers whose source was deleted or became
Git-ignored. Add `--apply` to prune the previewed set; destructive sync requires
an explicit project directory. Manual memories and drawers migrated from an
older BogMem schema are protected.

## Integration examples

See [`samples/`](samples/README.md) for copyable CLI automation, a generic MCP
host configuration, a runnable embedded .NET lifecycle, and a molecule
capability retrieval API. The actor-graph sample shows the separate graph
memory package:

```bash
dotnet run --project samples/Bogmem.Quickstart
dotnet run --project samples/Bogmem.MoleculeApi -- --demo
dotnet run --project samples/Bogmem.ActorGraph
```

The frozen parity layer is evidence about the port, not an endorsement of every
MemPalace behavior. Product APIs may correct inherited bugs when the divergence
is tested and documented; see
[`docs/parity-boundary.md`](docs/parity-boundary.md).

### Retrieval status

The current retrieval mode is `bogdb-hnsw-bm25-hybrid`: BogDB 1.4.0 maintains a
cosine HNSW index and a full-text BM25 index across commits, deletes, and
reopen. BogMem combines their scores with the MemPalace-compatible 0.6/0.4
weighting. Vector candidates come from the same 384-dimensional
`all-MiniLM-L6-v2` sentence-transformer family used by the pinned MemPalace
corpus, running locally through ONNX.

Every drawer records the exact embedding producer. Opening a palace with a
different configured model re-embeds its drawers before querying, so BogMem
never compares vectors from incompatible spaces. For deliberately
dependency-free lexical retrieval, set `BOGMEM_EMBEDDING_MODEL=lexical`; set it
back to `minilm` to migrate the palace to semantic vectors.

Ordinary searches hydrate only a bounded union of HNSW and FTS candidates,
rather than loading the palace into application memory. BogDB secondary indexes
also serve scoped wing/room/source searches and source-replacement deletes.

No Chroma process or Chroma package is used by the product path.

### Graph memory

`BogMem.Graph` is a reusable library for actor/co-activity memory. An upstream
workflow supplies explicit, stable `CoActivityObservation` values; BogMem does
not guess entities or relationships from arbitrary files. The same observation
contract supports an in-memory fixed window and durable BogDB evidence:

```csharp
using Bogmem.Graph;

var start = DateTimeOffset.UtcNow;
var window = new ActorGraphWindow(start, start.AddMinutes(15));
window.Observe(new(
    "transfer-burst:42",
    start.AddMinutes(1),
    ["account-a", "account-b", "account-c"],
    Weight: 2,
    Context: "shared-endpoint"));

var graph = window.Snapshot(minimumEdgeWeight: 0.5);
var communities = new LeidenCommunityDetector().Detect(graph);
var neighbors = graph.Neighbors("account-a");
```

Each observation is replay-safe. A multi-actor event projects to weighted,
undirected actor pairs, while `BogDbActorGraphStore` retains the event and its
participation edges rather than persisting only a lossy aggregate. Window
updates are thread-safe and fan-out is bounded by default because pair
projection grows quadratically.

Community detection is reported as `leiden-deterministic-v1`: modularity local
moving, connected-community refinement, and multilevel aggregation with stable
ordering. It fixes the disconnected-community failure mode of naive Louvain,
but its deterministic assignments are not promised to match a stochastic
Leiden implementation bit for bit. See
[`docs/graph-memory.md`](docs/graph-memory.md) for the model and integration
boundary.

### Palace runtime and MCP

Every product command now opens a `PalaceRuntime`: one owner for one BogDB
database, one stable palace manifest, and the drawer and graph capabilities
inside it. `bogmem init --name NAME` persists an immutable palace ID and name;
subsequent status and MCP results include that ID so a router can detect a
misdirected request.

The runtime MCP surface adds:

- `bogmem_palace_status`
- `bogmem_graph_observe`
- `bogmem_graph_neighbors`
- `bogmem_graph_traverse`
- `bogmem_graph_communities`

`bogmem_graph_observe` accepts optional FTT lineage (`source`, `workflow_id`,
`run_id`, `artifact_id`, and `signal_type`) as part of its idempotency
fingerprint. Supplying `palace_id` on graph calls acts as a routing guard. Each
MCP process still serves exactly one palace. Stdio and Streamable HTTP share
the same dispatcher and tool catalog.

### Palace registry

The durable registry is the first Coliseum primitive. A Coliseum is BogMem's
multi-palace layer: it maps a stable palace ID and unique name to an independent
palace path without merging the underlying stores:

```bash
bogmem registry register --palace /data/palaces/social-signals
bogmem registry list
bogmem registry resolve social-signals
```

Re-registering a moved palace repairs its path without changing its identity.
Programmatic `OpenPalace` routing verifies the manifest ID before returning a
runtime, preventing a stale path from serving the wrong memory.

Read-only cross-palace recall is available through the CLI or a registry-bound
MCP service:

```bash
bogmem recall "where was token rotation decided?" --limit 20
bogmem mcp --registry ~/.bogmem/registry.json
```

Coliseum recall preserves palace provenance and local ranking metadata; missing
palaces are reported as partial failures. Graph-neighbor recall is available
through `recall-neighbors` and `bogmem_graph_recall_neighbors`. No cross-palace
edges are created. Process supervision remains a later Coliseum layer. See
[`docs/palace-registry.md`](docs/palace-registry.md) and
[`docs/cross-palace-recall.md`](docs/cross-palace-recall.md).

## Compatibility suite

The solution also contains the reusable golden-corpus harness and compatibility
slices from the ASE porting effort.

Spellchecking is deterministic across Windows, macOS, and Linux. BogMem embeds
the FreeBSD-maintained `web2` corpus, derived from *Webster's Second
International Dictionary*, instead of consulting a host-specific
`/usr/share/dict/words`. Corpus provenance and checksums are recorded in
[`web2.NOTICE.md`](src/Bogmem.Slices/Spellcheck/data/web2.NOTICE.md).

## Clean checkout

```bash
dotnet restore Bogmem.sln
dotnet build Bogmem.sln -c Release
dotnet test Bogmem.sln -c Release
dotnet pack src/Bogmem.Cli -c Release
dotnet run --project src/Bogmem.Cli -- --help
dotnet run --project src/Bogmem.Cli -- parity config --report parity_report.json
```

The frozen oracle under `golden/` is read-only.

Maintainers can find the package and tag workflow in
[`docs/releasing.md`](docs/releasing.md).

## License

BogMem is licensed under the [Apache License 2.0](LICENSE). Portions derived
from MemPalace retain their original MIT notice in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
