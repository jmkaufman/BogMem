namespace Bogmem.Slices.Embedding;

/// <summary>
/// Embedding boundary used by the product store. Identity must change whenever
/// two implementations produce vectors in different spaces, even when their
/// dimensions are equal.
/// </summary>
public interface IMemoryEmbedder : IDisposable
{
    string Identity { get; }
    int Dimensions { get; }
    float[] Embed(string text);

    IReadOnlyList<float[]> EmbedMany(IReadOnlyList<string> texts) =>
        texts.Select(Embed).ToArray();
}
