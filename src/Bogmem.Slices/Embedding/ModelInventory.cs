using System.Security.Cryptography;
using System.Text.Json;

namespace Bogmem.Slices.Embedding;

/// <summary>Verification result for one file in a model cache directory.</summary>
public sealed record ModelFileCheck(
    string RelativePath,
    string ExpectedSha256,
    string? ActualSha256,
    bool Present,
    bool Matches);

/// <summary>
/// Consumer for the golden/model corpus (bogmem.golden.model_meta.v1): the
/// sha256 inventory of the chroma ONNX embedding-model cache captured at the
/// oracle pin. Per the manifest, the model bytes ARE part of the oracle —
/// embedding parity is undefined without exactly these files — so this type
/// lets the embedding subsystem prove a local cache is byte-identical to the
/// capture before any parity claim that flows through the default embedder.
/// </summary>
public sealed class ModelInventory
{
    public string ModelId { get; }
    public string EfName { get; }
    public int Dims { get; }
    public IReadOnlyDictionary<string, string> Files { get; }

    private ModelInventory(string modelId, string efName, int dims, Dictionary<string, string> files)
    {
        ModelId = modelId;
        EfName = efName;
        Dims = dims;
        Files = files;
    }

    /// <summary>Parse one vectors.jsonl row of the model_meta corpus.</summary>
    public static ModelInventory FromVector(string id, JsonElement input, JsonElement expected)
    {
        var efName = input.GetProperty("ef_name").GetString()
            ?? throw new InvalidDataException("model_meta vector missing ef_name");
        int dims = expected.GetProperty("dims").GetInt32();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in expected.GetProperty("files").EnumerateObject())
        {
            var digest = p.Value.GetString()
                ?? throw new InvalidDataException($"model_meta digest for {p.Name} is not a string");
            if (digest.Length != 64 || !digest.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
                throw new InvalidDataException($"model_meta digest for {p.Name} is not lowercase sha256 hex: {digest}");
            files[p.Name] = digest;
        }
        if (files.Count == 0) throw new InvalidDataException("model_meta vector declares no files");
        return new ModelInventory(id, efName, dims, files);
    }

    public static string Sha256Hex(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    /// <summary>
    /// Hash every inventoried file under <paramref name="cacheDir"/> and compare
    /// against the golden digests. Ordering follows the inventory's declared
    /// file order; absent files report Present=false.
    /// </summary>
    public IReadOnlyList<ModelFileCheck> VerifyCacheDir(string cacheDir)
    {
        var checks = new List<ModelFileCheck>(Files.Count);
        foreach (var (relPath, expectedSha) in Files)
        {
            var full = Path.Combine(cacheDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                checks.Add(new ModelFileCheck(relPath, expectedSha, null, false, false));
                continue;
            }
            using var fs = File.OpenRead(full);
            var actual = Sha256Hex(fs);
            checks.Add(new ModelFileCheck(relPath, expectedSha, actual,
                Present: true, Matches: string.Equals(actual, expectedSha, StringComparison.Ordinal)));
        }
        return checks;
    }

    public bool IsCacheVerified(string cacheDir) => VerifyCacheDir(cacheDir).All(c => c.Matches);
}
