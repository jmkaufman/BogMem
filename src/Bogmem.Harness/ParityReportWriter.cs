using System.Text.Json;
namespace Bogmem.Harness;
public sealed record ParityFinding(string Id,string Rule,bool Passed,string? Message=null,string? GapLedger=null);
public static class ParityReportWriter { public static void Write(string path,string slice,IEnumerable<ParityFinding> findings,string? gapLedger=null) { var rows=findings.ToArray(); var report=new {schema="ase.parity_report.v1",slice,passed=rows.All(x=>x.Passed),findings=rows,gap_ledger=gapLedger is null?null:new {pointer=gapLedger}}; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); File.WriteAllText(path,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase})); } }
