using Bogmem.Slices.KnowledgeGraph;
namespace Bogmem.Slices.Tests.KnowledgeGraph;
public static class KnowledgeGraphStoreTests
{
 public static void Run(){var g=new KnowledgeGraphStore();g.Add("Alice","works_at","Acme",new DateOnly(2020,1,1));g.Invalidate("Alice","works_at","Acme",new DateOnly(2024,1,1));if(g.Query("Alice",new DateOnly(2023,1,1)).Count!=1||g.Query("Alice",new DateOnly(2025,1,1)).Count!=0)throw new InvalidOperationException("validity window");}
}
