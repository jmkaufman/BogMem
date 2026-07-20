using Bogmem.Slices.Embedding;
namespace Bogmem.Slices.Tests.Embedding;
public static class EmbedderTests
{
 public static void Run(){var e=new Embedder(new Model());var v=e.Embed("probe");if(v.Length!=384)throw new InvalidOperationException("embedding dimension");if(!Embedder.Prefix.StartsWith("task: sentence similarity | query: ",StringComparison.Ordinal))throw new InvalidOperationException("embedding prefix");}
 private sealed class Model:IEmbeddingModel { public float[] Embed(ReadOnlySpan<int> ids){var x=new float[768];for(var i=0;i<ids.Length;i++)x[i%768]=ids[i];return x;} }
}
