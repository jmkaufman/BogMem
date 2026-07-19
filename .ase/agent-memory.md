# Current-state briefing — integration merge landscape (run 012)

## Merge state
- Integration branch: `agent/openai-gpt-5-5-integration/run-mempalace-to-bogmem-v3-constellation-012`.
- Merged cleanly: `codex-cli-5-6-sol-coder` then `grok-cli-grok-build`.
- `local-claude-code-claude-fable-5` had an additive conflict only in this memory file; source/test files did not require semantic conflict resolution.
- No queued branch was observed modifying `golden/` during merge output; keep `golden/` read-only.

## Lane areas now present
- Harness/CLI foundation: comparison dispatch, corpus JSONL replay, parity reports, disposition ledgers, D1/D2/D3 interfaces, CLI help/parity entrypoints.
- S8 config: file/env precedence and legacy boolean coercion.
- S3 embedding: prefix/tokenizer seam, 768-to-384 float32 normalization, batch padding seam.
- S7 Chroma/ANN: deterministic cosine ANN and D3 guard helpers.
- S5c knowledge graph: SQLite temporal graph with add/query/invalidate/timeline; explicit recordedAt used for golden replay.
- S1 IDs: length-prefixed SHA recipes (rune counts, None→"None", drawer 24 / triple 12), CLI parity on `golden/ids/`.
- S10 WAL: Python-shaped JSONL audit lines with redaction and ensure_ascii escapes.
- S4 search: BM25 k1=1.5/b=0.75, hybrid 0.6/0.4, closet boosts, ordinal-stable ordering.
- S5a dedup: threshold 0.15, min group 5, `tests/parity/disposition/gap_ledger.json`; self-slot occupancy is intentional.
- S9 MCP: `McpServer.cs` plus `tools.json`; 36 tools / 14 mutating; error codes -32002/-32003/-32000; missing version→oldest, unrecognized→newest.
- S13 deferred backends: UUID5 oracle staged under `testdata/deferred_ids/oracle.jsonl`, not `golden/`.
- S2 chunkers: window/convo/diary exact vs `golden/chunks/{window,convo,diary}`.
- S11 locking: POSIX `open(2)`/`flock(2)` implementation exact vs `golden/locks`.
- S6 storage: graph files and sqlite_exact exact vs `golden/graph_files` and `golden/sqlite_exact`.
- S5b dynamics: strict-zero float32 ULP vs `golden/dynamics`.
- S12 spellcheck: `ISpeller`/`Speller` bounded D2 agreement vs `golden/spellcheck`.

## Conflict/merge landmines
- `golden/` is vendored oracle evidence and must remain read-only. Run-authored evidence belongs under `tests/parity/disposition/` or approved `testdata/` paths.
- Running the console suite may rewrite `tests/parity/disposition/dedup.json` with an absolute worktree path in a reason string; revert that path churn rather than committing it.
- Gap-ledger pointers should stay relative (`tests/parity/disposition/gap_ledger.json`).
- POSIX locking intentionally avoids FileStream share emulation; do not simplify it.
- SqliteExactStore scoring and dynamics arithmetic are fixture-sensitive; do not reorder float32 operations.
- MCP version list is newest-first (`[0]` newest, `[^1]` oldest); semantic JSON equality is used for golden compares.
- NU1903 for SQLitePCLRaw is known/non-blocking for parity checks.
- `rg` is unavailable in this environment; use `grep`/`find`.

## Checks to adjudicate this integration
- Primary command: `dotnet run --project tests/Bogmem.Slices.Tests` (custom console runner).
- Additional solution check used by lanes: `dotnet test Bogmem.sln -c Release`.
- CLI parity evidence lanes reported: `bogmem parity {ids,wal,search,dedup,mcp,deferred_ids}` passing; other slice CLI replay may still be incomplete.
- Parity validation owns any final `parity_report.json`; integration should not author it unless explicitly assigned.
