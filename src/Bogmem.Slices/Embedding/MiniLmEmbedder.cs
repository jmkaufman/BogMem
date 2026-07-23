using ElBruno.LocalEmbeddings;
using ElBruno.LocalEmbeddings.Options;

namespace Bogmem.Slices.Embedding;

/// <summary>
/// Local sentence-transformer embeddings using all-MiniLM-L6-v2 through ONNX.
/// Model files are downloaded once and then reused from the local cache.
/// </summary>
public sealed class MiniLmEmbedder : IMemoryEmbedder
{
    public const string ModelName = "sentence-transformers/all-MiniLM-L6-v2";
    public const string ModelSha256 = "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452";
    public const string ModelIdentity =
        "sentence-transformers/all-MiniLM-L6-v2@onnx-sha256:6fd5d72fe4589f18";
    public const int ModelDimensions = 384;

    private readonly Lazy<LocalEmbeddingGenerator> _generator;
    private bool _disposed;

    public MiniLmEmbedder(string? cacheDirectory = null)
    {
        _generator = new Lazy<LocalEmbeddingGenerator>(
            () => LocalEmbeddingGenerator.CreateAsync(new LocalEmbeddingsOptions
            {
                ModelName = ModelName,
                CacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory) ? null : Path.GetFullPath(cacheDirectory),
                EnsureModelDownloaded = true,
                ExpectedHash = ModelSha256,
                NormalizeEmbeddings = true,
                MaxSequenceLength = 256,
            }).GetAwaiter().GetResult(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Identity => ModelIdentity;
    public int Dimensions => ModelDimensions;

    public float[] Embed(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        return EmbedMany([text])[0];
    }

    public IReadOnlyList<float[]> EmbedMany(IReadOnlyList<string> texts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) return [];
        if (texts.Any(text => text is null))
            throw new ArgumentException("Embedding input cannot contain null text.", nameof(texts));

        var generated = _generator.Value.GenerateAsync(texts).GetAwaiter().GetResult();
        var vectors = generated.Select(embedding => embedding.Vector.ToArray()).ToArray();
        if (vectors.Any(vector => vector.Length != Dimensions))
            throw new InvalidOperationException(
                $"{ModelName} returned an embedding with an unexpected dimension; expected {Dimensions}.");
        return vectors;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_generator.IsValueCreated)
            _generator.Value.Dispose();
        _disposed = true;
    }
}
