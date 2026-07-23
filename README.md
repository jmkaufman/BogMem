# BogMem

BogMem is a .NET 10, local-first memory store derived from MemPalace. It now
contains both the frozen compatibility corpus and a first usable product path:
verbatim drawers persisted in BogDB, searchable from the CLI or over MCP.

## Quick start

```bash
# From a source checkout. Uses ~/.bogmem/palace unless --palace is supplied.
dotnet run --project src/Bogmem.Cli -- init

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

The functional MCP path currently backs status, taxonomy/listing, project
mining, search, duplicate checks, and drawer CRUD with BogDB. Parity-only tool
definitions stay in the compatibility harness but are not advertised by the
live server.

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
host configuration, and a runnable embedded .NET lifecycle:

```bash
dotnet run --project samples/Bogmem.Quickstart
```

The frozen parity layer is evidence about the port, not an endorsement of every
MemPalace behavior. Product APIs may correct inherited bugs when the divergence
is tested and documented; see
[`docs/parity-boundary.md`](docs/parity-boundary.md).

### Retrieval status

The current retrieval mode is `exact-hybrid-lexical`: deterministic hashed
word/trigram vectors plus MemPalace-compatible BM25 reranking. It is useful for
local retrieval and accurately labels itself, but it is not yet the original
ONNX semantic embedder. Stored embeddings and the storage boundary are ready
for that replacement. ANN is also intentionally disabled until BogDB's HNSW
index remains current after mutation and is rebuilt or restored on reopen.

No Chroma process or Chroma package is used by the product path.

## Compatibility suite

The solution also contains the reusable golden-corpus harness and compatibility
slices from the ASE porting effort.

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
