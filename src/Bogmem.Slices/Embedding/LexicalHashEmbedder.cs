using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Bogmem.Slices.Search;

namespace Bogmem.Slices.Embedding;

/// <summary>
/// Dependency-free, deterministic fallback. It hashes word and
/// character-trigram features into a normalized vector and is deliberately
/// advertised as lexical—not semantic.
/// </summary>
public sealed class LexicalHashEmbedder : IMemoryEmbedder
{
    public const int Dimensions = 384;
    public const string Identity = "bogmem-lexical-hash-v1";

    string IMemoryEmbedder.Identity => Identity;
    int IMemoryEmbedder.Dimensions => Dimensions;

    public float[] Embed(string text)
    {
        var counts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var token in Searcher.Tokenize(text))
        {
            counts[token] = counts.GetValueOrDefault(token) + 1.0;
            if (token.Length >= 3)
            {
                var padded = $"^{token}$";
                for (var i = 0; i <= padded.Length - 3; i++)
                {
                    var feature = "#" + padded.Substring(i, 3);
                    counts[feature] = counts.GetValueOrDefault(feature) + 0.25;
                }
            }
        }

        var vector = new float[Dimensions];
        foreach (var (feature, frequency) in counts)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(feature));
            var bucket = (int)(BinaryPrimitives.ReadUInt32LittleEndian(digest) % Dimensions);
            var sign = (digest[4] & 1) == 0 ? 1.0 : -1.0;
            vector[bucket] += (float)(sign * Math.Log(1.0 + frequency));
        }

        double squaredNorm = 0;
        foreach (var value in vector) squaredNorm += (double)value * value;
        if (squaredNorm == 0) return vector;
        var norm = Math.Sqrt(squaredNorm);
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        return vector;
    }

    public void Dispose()
    {
    }
}
