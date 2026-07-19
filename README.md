# BogMem

BogMem is the dotnet-10 port of MemPalace's parity surfaces. The solution contains the runnable `bogmem` CLI, a reusable golden-corpus harness, and compatibility slices.

## Clean checkout

```bash
dotnet restore Bogmem.sln
dotnet build Bogmem.sln
dotnet run --project src/Bogmem.Cli -- --help
dotnet run --project src/Bogmem.Cli -- parity config --report parity_report.json
```

The frozen oracle under `golden/` is read-only.
