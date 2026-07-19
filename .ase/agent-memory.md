# Current-state briefing

## Owned map (run 012 assignment)

- **S1** `src/Bogmem.Slices/Ids/IdRecipes.cs` — length-prefixed SHA recipes (rune counts, None→"None", drawer 24 / triple 12). Tests + CLI parity on `golden/ids/`.
- **S10** `src/Bogmem.Slices/Wal/WalWriter.cs` — Python-shaped JSONL audit lines with redaction and ensure_ascii escapes.
- **S4** `src/Bogmem.Slices/Search/Searcher.cs` — BM25 k1=1.5/b=0.75, hybrid 0.6/0.4, closet boosts, ordinal-stable OrderBy.
- **S5a** `src/Bogmem.Slices/Dedup/{DedupGrouper,GapLedger}.cs` — threshold 0.15, min group 5, `ase.dedup_gap_ledger.v1` under `tests/parity/disposition/`.
- **S9** `src/Bogmem.Slices/Mcp/McpServer.cs` + `tools.json` — 36 tools / 14 mutating; errors -32002/-32003/-32000; missing version→oldest, unrecognized→newest.
- **S13** `src/Bogmem.Slices/DeferredBackends/DeferredIdOracle.cs` — UUID5 oracle staged to `testdata/deferred_ids/oracle.jsonl` (not golden/).

CLI parity wiring for these modules lives in `src/Bogmem.Cli/ParityRunner.cs` (ids, wal, search, dedup, mcp, deferred_ids). Disposition evidence: `tests/parity/disposition/{ids,wal,search,dedup,mcp,deferred_ids,gap_ledger,smoke}.json`.

## Decisions and landmines

- `golden/` is **read-only**. Never write fixtures there. Disposition + gap ledger go under `tests/parity/disposition/`.
- Gap-ledger pointer must be **relative** (`tests/parity/disposition/gap_ledger.json`). Absolute worktree paths poison disposition when seats copy each other.
- Deferred oracle emits under `testdata/deferred_ids/` (stack-conformance allowlist), not `golden/deferred_ids/`.
- MCP version list is newest-first: `[0]` = newest (2025-11-25), `[^1]` = oldest (2024-11-05).
- MCP golden compares are semantic JSON equality (property order independent); wire uses `UnsafeRelaxedJsonEscaping`.
- Dedup: `n_results=min(kept,5)` over the full group — self-slot occupancy is intentional legacy behavior.
- `dotnet test` console suite is the reliable check; NU1903 on SQLitePCLRaw is known and non-blocking.
- Parity validation seat owns `parity_report.json`; we produce disposition ledgers and can emit reports via CLI for evidence.

## State

- **Done:** All six assigned slices implemented, golden replay tests green, CLI parity wired and green for S1/S4/S5a/S9/S10/S13.
  - `dotnet test Bogmem.sln -c Release` — all modules ok.
  - `bogmem parity {ids,wal,search,dedup,mcp,deferred_ids}` — all passed.
- **Known incomplete:** Hallway IDs unit-tested for symmetry but not present in `golden/ids` kinds (tunnel is). Full palace-I/O / mine-sweep still other lanes.
- **Deliberately deferred:** Live qdrant/milvus/pgvector comparison (S13 PLACEHOLDER only).
