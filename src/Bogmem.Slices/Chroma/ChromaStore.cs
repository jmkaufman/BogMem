using Bogmem.Harness.Interfaces;
namespace Bogmem.Slices.Chroma;
public sealed class ChromaStore : IAnnIndex
{
 private readonly List<(string Id,float[] Vector)> items=[];
 public void Add(string id,ReadOnlySpan<float> vector){items.RemoveAll(x=>x.Id==id);items.Add((id,vector.ToArray()));}
 public int Count=>items.Count;
 public IReadOnlyList<(string Id,float Distance)> Search(ReadOnlySpan<float> query,int limit){var q=query.ToArray();return items.Select((x,i)=>(x.Id,Distance:CosineDistance(q,x.Vector),Index:i)).OrderBy(x=>x.Distance).ThenBy(x=>x.Index).Take(Math.Max(0,limit)).Select(x=>(x.Id,x.Distance)).ToArray();}
 public static double Jaccard(IEnumerable<string> a,IEnumerable<string> b){var x=a.ToHashSet();var y=b.ToHashSet();return x.Count==0&&y.Count==0?1.0:x.Intersect(y).Count()/(double)x.Union(y).Count();}
 private static float CosineDistance(float[] a,float[] b){double dot=0,an=0,bn=0;for(var i=0;i<Math.Min(a.Length,b.Length);i++){dot+=a[i]*b[i];an+=a[i]*a[i];bn+=b[i]*b[i];}return (float)(1-dot/(Math.Sqrt(an)*Math.Sqrt(bn)));}
}
