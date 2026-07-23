# BogMem

BogMem is a .NET 10, local-first memory store derived from MemPalace. It now
contains both the frozen compatibility corpus and a first usable product path:
verbatim drawers persisted in BogDB, searchable from the CLI or over MCP.

## Quick start

```bash
# Uses ~/.bogmem/palace unless --palace is supplied.
dotnet run --project src/Bogmem.Cli -- init

dotnet run --project src/Bogmem.Cli -- add \
  --wing myproject \
  --room decisions \
  --content "We chose BogDB for durable local memory."

dotnet run --project src/Bogmem.Cli -- search "why did we choose BogDB?"
dotnet run --project src/Bogmem.Cli -- status
```

Run the persistent MCP server over newline-delimited JSON-RPC on stdio:

```bash
dotnet run --project src/Bogmem.Cli -- mcp --palace ~/.bogmem/palace
```

The functional MCP path currently backs status, taxonomy/listing, search,
duplicate checks, and drawer CRUD with BogDB. Parity-only tool definitions stay
in the compatibility harness but are not advertised by the live server.

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
dotnet run --project src/Bogmem.Cli -- --help
dotnet run --project src/Bogmem.Cli -- parity config --report parity_report.json
```

The frozen oracle under `golden/` is read-only.
