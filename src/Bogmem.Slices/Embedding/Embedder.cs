namespace Bogmem.Slices.Embedding;
public interface IEmbeddingModel { float[] Embed(ReadOnlySpan<int> tokenIds); }
public sealed class Embedder
{
 public const string Prefix="task: sentence similarity | query: "; public const int OutputDimensions=384, ModelDimensions=768; private readonly IEmbeddingModel? model;
 public Embedder(IEmbeddingModel? model=null){this.model=model;}
 public float[] Embed(string text){var ids=Tokenize(Prefix+text); var full=model?.Embed(ids)??new float[ModelDimensions]; return Normalize(full[..OutputDimensions]);}
 public float[][] EmbedBatch(IEnumerable<string> texts)=>texts.Select(Embed).ToArray();
 public static int[] Tokenize(string text)=>text.EnumerateRunes().Select(r=>r.Value).Take(512).ToArray();
 private static float[] Normalize(ReadOnlySpan<float> values){var output=values.ToArray();double sum=0;foreach(var x in output)sum+=(double)x*x;var norm=Math.Sqrt(sum)+1e-12;for(var i=0;i<output.Length;i++)output[i]=(float)(output[i]/norm);return output;}
}
