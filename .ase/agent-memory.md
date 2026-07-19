# Current-state briefing

## Owned map

- `src/Bogmem.Harness`: comparison dispatch, corpus JSONL replay, parity report, disposition ledger, and D1/D2/D3 interfaces.
- `src/Bogmem.Cli`: executable help/parity entrypoint; `ParityRunner` currently replays config and IDs.
- `src/Bogmem.Slices/Config`: file/env precedence and legacy boolean coercion.
- `src/Bogmem.Slices/Embedding`: prefix, tokenizer seam, 768-to-384 float32 normalization, and batch padding seam.
- `src/Bogmem.Slices/Chroma`: deterministic cosine ANN and D3 guard helpers.
- `src/Bogmem.Slices/KnowledgeGraph`: SQLite temporal graph with add/query/invalidate/timeline.

## Decisions and landmines

- `golden/` is vendored oracle evidence and must remain read-only. Run-authored disposition evidence belongs under `tests/parity/disposition/`.
- The corpus module is `golden/embedding` (singular), despite the work-item prose saying embeddings.
- KG IDs use the shared `IdRecipes` length-prefixed SHA recipe and require a frozen `recordedAt` to replay golden IDs. The store defaults to UTC for production and accepts an explicit timestamp for replay.
- `Microsoft.Data.Sqlite` 10.0.0 is required for the SQLite implementation; restore emits NU1903 for its native SQLite transitive package, but build remains successful.
- The console suite is wired through the custom `RunConsoleSuite` target; `dotnet run --project tests/Bogmem.Slices.Tests` is the direct reliable check.

## State

- Done: solution/harness/CLI, S8 config, S3 embedding seam, S7 ANN/guards, S5c SQLite graph. Restore/build and integrated tests pass.
- Done: CLAiR `/health` returned HTTP 200 healthy.
- Known incomplete: CLI parity replay is wired for config and IDs only; embedding/chroma/KG corpus replay should be added by the parity-validation lane.
- Deliberately deferred: cross-wing/BFS KG navigation remains not applicable for S5c, as specified.
