# Current-state briefing — grok-cli-grok-build, remediation cycle 1 (run 012)

## Owned map (after remediation cycle 1)

- **S1** `src/Bogmem.Slices/Ids/IdRecipes.cs`, **S10** `Wal/WalWriter.cs`, **S4** `Search/Searcher.cs`, **S5a** `Dedup/{DedupGrouper,GapLedger}.cs`, **S9** `Mcp/McpServer.cs`, **S13** `DeferredBackends/DeferredIdOracle.cs` — all green since cycle 0.
- **CLI** `src/Bogmem.Cli/` — now the full parity surface:
  - `Program.cs` is a one-liner delegating to `CliMain.Run(args, stdout, stderr, cwd)` (public, in-process testable).
  - `ParityRunner.cs` (original 7 modules) + `ParityRunner.Modules.cs` (10 more: chunks, dates, dynamics, locks, spellcheck, storage, model, embedding, chroma, kg) — public partial class; all 17 golden modules replay via `bogmem parity <module>`.
  - `tests/Bogmem.Slices.Tests/Cli/CliExitCodeTests.cs` — exit-code contract checks + a loop replaying ALL `ParityRunner.KnownModules` in-process, so `dotnet test` and the CLI can never disagree again (verifier claim C5).

## Remediation cycle 1 — what was fixed and why

- **C6 (exit codes)**: unknown module/option/command → exit 2; `--golden <path>` is now parsed (accepts the golden dir itself or a root containing golden/); nonexistent or corpus-less `--golden` → `CorpusException` → exit 2 (previously the flag was silently ignored and the run "passed" against the default corpus). Missing corpus files are exit 2, not exit-1 findings. Parity mismatch (incl. replay exceptions) stays exit 1.
- **C5 (coverage)**: wired the 10 remaining modules by porting the test suite's replay logic verbatim (comparison semantics copied from tests/Bogmem.Slices.Tests — keep them in sync if a test changes). "storage" = graph_files + sqlite_exact corpora. embedding = structural PLACEHOLDER (ONNX bytes not vendored; vector replay impossible — same stance as the test suite). chroma = BOUNDED Jaccard ≥ 0.8 vs the legacy HNSW capture (exact distances are NOT reproducible: legacy is float32 hnswlib, ours is a double-accumulated exact scan).
- **KG parity defects found by wiring kg replay** (fixed in `src/Bogmem.Slices/KnowledgeGraph/KnowledgeGraphStore.cs`; another lane's file, but the golden oracle proves both):
  1. `Query` ordered by valid_from; legacy returns insertion (rowid) order — golden/kg query vectors encode it. Now `ORDER BY rowid`.
  2. `current` was an as-of window test; legacy means "still open" (`valid_to is null`) — golden marks the closed Acme triple current=false at as_of 2023. Now `t.ValidTo is null`.
  Nothing replayed golden/kg before (smoke only), which is why both survived integration.

## Landmines

- `golden/` is vendored oracle evidence — READ-ONLY. Run-authored evidence goes under `tests/parity/disposition/`; deferred oracle emits to `testdata/deferred_ids/` (stack-conformance allowlist).
- `ParityRunner`/helpers are in the GLOBAL namespace; `CliMain` is in `Bogmem.Cli`. The test project references the Bogmem.Cli exe project — fine, each assembly keeps its own top-level Program.
- `dotnet test` now runs all 17 CLI replays; deferred_ids re-emits `testdata/deferred_ids/oracle.jsonl` each run (idempotent, no git churn).
- Console suite may rewrite `tests/parity/disposition/dedup.json` with an absolute worktree path in a reason string; revert that churn rather than committing it.
- Running `bogmem parity` outside a tree containing `golden/` is exit 2 by design.
- NU1903 on SQLitePCLRaw is known/non-blocking. Chunker WARNING lines on stderr during tests are expected fixture behavior. `rg` unavailable here; use grep/find.

## Checks

- `dotnet run --project tests/Bogmem.Slices.Tests -c Release` — full console suite (includes cli_exit_codes + 17-module replay).
- Verifier repros: `parity nosuchmodule` → 2; `parity ids --golden /nonexistent` → 2 with stderr naming the missing dir; loop over all 17 modules → all "passed", exit 0.

## State

- **Done:** C6 + C5 fixed and reproduced with the verifier's exact commands; full suite green.
- **Known incomplete:** embedding stays PLACEHOLDER until real ONNX inference + model bytes exist; chroma is BOUNDED (Jaccard), not distance-exact — honest ceilings, not bugs.
- **Deliberately deferred:** live qdrant/milvus/pgvector comparisons (S13 PLACEHOLDER).
