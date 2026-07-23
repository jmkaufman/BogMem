using Bogmem.Slices.Chroma;
namespace Bogmem.Slices.Tests.Chroma;
public static class ChromaStoreTests
{
 public static void Run(){var s=new ChromaStore();s.Add("a",[1,0]);s.Add("b",[0,1]);var r=s.Search([1,0],2);if(r[0].Id!="a"||r.Count!=2)throw new InvalidOperationException("cosine ordering");ChromaParityGuards.AssertJaccard(1);}
}
