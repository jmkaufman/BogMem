using System.Text.Json;

namespace Bogmem.Cli.Parity;

public sealed record CorpusVector(
    string Id,
    JsonElement Input,
    JsonElement Expected);

public static class CorpusReplay
{
    public static IEnumerable<CorpusVector> Read(string path)
    {
        foreach (var line in File.ReadLines(path).Where(value =>
                     !string.IsNullOrWhiteSpace(value)))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            yield return new(
                root.GetProperty("id").GetString()!,
                root.GetProperty("input").Clone(),
                root.GetProperty("expected").Clone());
        }
    }

    public static string FindModule(string root, string module) =>
        Path.Combine(root, "golden", module, "vectors.jsonl");
}
