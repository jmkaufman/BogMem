# Current-state briefing — grok-cli-grok-build, remediation cycle 2 (run 012)

## Lane map

- Product parity slices remain green: S1 IDs, S10 WAL, S4 search, S5a dedup,
  S9 MCP, and S13 deferred-backend oracle under `src/Bogmem.Slices/`.
- CLI parity surface is `src/Bogmem.Cli/`: `CliMain` owns argument/exit-code
  behavior; `ParityRunner` plus `ParityRunner.Modules.cs` replays 17 leaf
  corpus modules.
- Cycle-2 fix touched `CliMain.cs`, `ParityRunner.cs`, and
  `tests/Bogmem.Slices.Tests/Cli/CliExitCodeTests.cs`.

## Cycle-2 fix

- Fixed N1: `parity all` is now accepted, documented, and dispatches every
  `ParityRunner.KnownModules` leaf into one findings collection/report.
- Aggregate reports use slice `ALL`; `--golden` and `--report` are resolved
  once and apply to the full replay. Leaf dispatch is centralized in
  `RunModule` so aggregate and single-module behavior cannot diverge.
- Regression coverage checks exit 0, report existence, `slice == ALL`, and
  non-empty findings.

## Landmines

- `golden/` is vendored oracle evidence and READ-ONLY. Run-authored evidence
  belongs under `tests/parity/disposition/`; deferred IDs use `testdata/`.
- The console test suite may rewrite dedup disposition paths; inspect status
  after running it and do not commit unrelated generated churn.
- `rg` is unavailable; use `grep`/`find`.
- Restore/build emits known NU1903 for SQLitePCLRaw.lib.e_sqlite3; this was
  not changed in strict-parity work.

## Verification and honest state

- `dotnet restore Bogmem.sln` then `dotnet build Bogmem.sln -c Release
  --no-restore`: passed (known NU1903 warnings).
- Exact verifier command `dotnet run --project src/Bogmem.Cli -c Release
  --no-build -- parity all` passed twice, exit 0, 261 checks each.
- Full console suite passed, including aggregate regression and all 17 leaf
  replays. Two aggregate reports were byte-identical (SHA-256
  `417460f668d281158c831a3c8fc887af7f3d76037965e5f188ac079539255387`).
- Known incomplete/deferred upstream state: embedding remains structural
  PLACEHOLDER, Chroma is bounded Jaccard rather than distance-exact, and live
  qdrant/milvus/pgvector comparisons remain deferred.
