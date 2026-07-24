using System.Text.Json;

namespace Bogmem.Cli.Parity;

public sealed record DispositionRow(
    string TestId,
    string Module,
    string Status,
    string Rule,
    string? Reason = null,
    string? Slice = null);

public sealed class TestDispositionLedgerWriter
{
    private readonly List<DispositionRow> _rows = [];

    public IReadOnlyList<DispositionRow> Rows => _rows;

    public void Append(
        string testId,
        string module,
        string status,
        string rule,
        string? reason = null,
        string? slice = null) =>
        _rows.Add(new(testId, module, status, rule, reason, slice));

    public void AppendSlice(string slice, IEnumerable<DispositionRow> entries) =>
        _rows.AddRange(entries.Select(entry => entry with { Slice = slice }));

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    schema = "ase.test_disposition_ledger.v1",
                    rows = _rows.ToArray(),
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                }));
    }
}
