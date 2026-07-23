using System.Text.Json;
namespace Bogmem.Harness;
public sealed record CorpusVector(string Id,JsonElement Input,JsonElement Expected);
public static class CorpusReplay { public static IEnumerable<CorpusVector> Read(string path) { foreach(var line in File.ReadLines(path).Where(x=>!string.IsNullOrWhiteSpace(x))) { using var d=JsonDocument.Parse(line); var r=d.RootElement; yield return new(r.GetProperty("id").GetString()!,r.GetProperty("input").Clone(),r.GetProperty("expected").Clone()); } } public static string FindModule(string root,string module)=>Path.Combine(root,"golden",module,"vectors.jsonl"); }
