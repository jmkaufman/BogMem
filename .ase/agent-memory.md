# Current-state briefing — local-claude-code-claude-fable-5 (run 012, cycle 0)

## Lane map (my five work items, all COMPLETE)
- WI-003 S2 chunkers: `src/Bogmem.Slices/Chunkers/{WindowChunker,ConvoChunker,DiaryChunker}.cs` + `tests/.../Chunkers/ChunkerTests.cs` — EXACT vs `golden/chunks/{window,convo,diary}`.
- WI-006 S11 locking: `src/Bogmem.Slices/Locking/FileLock.cs` + `tests/.../Locking/FileLockTests.cs` — EXACT vs `golden/locks`.
- WI-009 S6 storage: `src/Bogmem.Slices/Storage/{GraphStore,SqliteExactStore}.cs` + `tests/.../Storage/GraphStoreTests.cs` — EXACT vs `golden/graph_files` + `golden/sqlite_exact`.
- WI-012 S5b dynamics: `src/Bogmem.Slices/Dynamics/DynamicsScorer.cs` + `tests/.../Dynamics/DynamicsScorerTests.cs` — strict-zero float32 ULP vs `golden/dynamics`.
- WI-015 S12 spellcheck: `src/Bogmem.Slices/Spellcheck/Speller.cs` (behind `ISpeller` in `src/Bogmem.Harness/Interfaces/`) + `tests/.../Spellcheck/SpellerTests.cs` — BOUNDED D2 ≥0.95 vs `golden/spellcheck`.

## Key state facts
- These implementations arrived in this worktree via ASE merges from prior runs (006/011) and were already committed at launch; this run's work was verification against the FRESHLY re-vendored corpus at pin `a4747d7ffc7818684f01ac96c886ff6a654dd301` (commit c1c2f64). All manifests cite that pin; the old `b060dda` corpus-pin worry from run-011 memory is resolved — PIN.txt notes "code == HEAD b060dda" (same code, canonical sha a4747d7).
- Suite: `dotnet run --project tests/Bogmem.Slices.Tests` (exit 0 = green; the custom console runner, not `dotnet test`). Results: chunkers 60, locking 30, storage 16, dynamics 22 (zero out-of-ULP), spellcheck agreement 1.0000 (386/386).
- Disposition ledgers regenerate deterministically on every suite run and are byte-identical to what's committed (slices S2/S11/S6/S5b/S12 in `tests/parity/disposition/{chunkers,locking,storage,dynamics,spellcheck}.json`).
- All five ACOP claims completed 2026-07-19 with evidence.

## Landmines
- Running the suite rewrites `tests/parity/disposition/dedup.json` with the CURRENT worktree's absolute path in a `reason` field (it points at gap_ledger.json). That's another lane's ledger (S5a, codex lane) — revert it with `git checkout` after test runs rather than committing path churn.
- `golden/` is read-only vendored oracle evidence. Never write there; run-authored evidence goes only under `tests/parity/disposition/`.
- POSIX locking uses raw libc `open(2)`/`flock(2)` P/Invoke because .NET FileStream's share emulation flocks on every open, which would block contenders from reading holder identity (legacy contenders can). `open(2)` is called without O_CREAT (variadic mode arg unmarshalable on arm64); file is pre-touched managed. Don't "simplify" this to FileStream.
- SqliteExactStore scoring: float32 dot with dual accumulators + float32 norm/sqrt, final division widened to float64 (numpy NEP-50 pipeline on the pinned Accelerate platform). Bit-exact against fixture doubles — don't reorder the arithmetic.
- Dynamics `combined_preclamp` in "full" mode is the ISOLATED raw potentiation sum × decay factor (not the capped record value); the record itself follows the clamped legacy pipeline. The fixture columns depend on this distinction.
- Window chunker fixtures include float-param error vectors (`w-err-float-size`) that are unrepresentable in the typed C# API — ledgered as `not_applicable(...)`, counted as pass. Deliberate.
- NU1903 warning (SQLitePCLRaw 2.1.11 vuln) on every restore — known, benign for parity work, do not "fix" by bumping without checking Microsoft.Data.Sqlite 10.0.0 compat.
- `rg` is not installed here; use `grep`/`find`.

## Honest state
- Done: all five work items verified green against the pin-correct corpus; claims completed; nothing new needed committing (worktree clean, work already on the branch).
- Not my lane / deferred: parity_report.json authorship (parity_validation seat), CLI corpus replay for chunkers/locking/storage/dynamics/spellcheck (ParityRunner only replays config + IDs).
