namespace Bogmem.Slices.Embedding;
public interface IEmbeddingModel { float[] Embed(ReadOnlySpan<int> tokenIds); }
public interface IEmbeddingBatchModel : IEmbeddingModel { float[][] EmbedBatch(IReadOnlyList<int[]> paddedTokenIds); }
public interface ITokenEncoder { int[] Encode(string text); }
public sealed class Embedder
{
 public const string Prefix="task: sentence similarity | query: "; public const int OutputDimensions=384, ModelDimensions=768; private readonly IEmbeddingModel? model;
 private readonly ITokenEncoder? tokenizer;
 public Embedder(IEmbeddingModel? model=null, ITokenEncoder? tokenizer=null){this.model=model;this.tokenizer=tokenizer;}
 public float[] Embed(string text){var ids=Encode(text); var full=model?.Embed(ids)??new float[ModelDimensions]; return Normalize(CheckedVector(full));}
 public float[][] EmbedBatch(IEnumerable<string> texts){
  // Padding is observable at the model seam. Encode first, pad only for the
  // batch call, and trim the result to the model's declared 768 dimensions.
  var encoded=texts.Select(Encode).ToArray(); var width=encoded.Length==0?0:encoded.Max(x=>x.Length);
  if (width==0) return [];
  var padded=encoded.Select(ids=>ids.Concat(Enumerable.Repeat(0,width-ids.Length)).ToArray()).ToArray();
  if(model is IEmbeddingBatchModel batch)return batch.EmbedBatch(padded).Select(x=>Normalize(CheckedVector(x))).ToArray();
  return encoded.Select(ids=>EmbedEncoded(ids)).ToArray();
 }
 public static int[] Tokenize(string text)=>text.EnumerateRunes().Select(r=>r.Value).Take(512).ToArray();
 private int[] Encode(string text)=>(tokenizer?.Encode(Prefix+text)??Tokenize(Prefix+text)).Take(512).ToArray();
 private float[] EmbedEncoded(int[] ids){var full=model?.Embed(ids)??new float[ModelDimensions];return Normalize(CheckedVector(full));}
 private static ReadOnlySpan<float> CheckedVector(float[] vector){if(vector.Length<ModelDimensions)throw new ArgumentException($"Embedding model returned {vector.Length} dimensions; expected at least {ModelDimensions}.",nameof(vector));return vector.AsSpan(0,ModelDimensions);}
 private static float[] Normalize(ReadOnlySpan<float> values){var output=values[..OutputDimensions].ToArray();double sum=0;foreach(var x in output)sum+=(double)x*x;var norm=Math.Sqrt(sum)+1e-12;for(var i=0;i<output.Length;i++)output[i]=(float)(output[i]/norm);return output;}
}
