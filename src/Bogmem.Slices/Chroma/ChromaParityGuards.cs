namespace Bogmem.Slices.Chroma;
public static class ChromaParityGuards
{
 public static void AssertSelfConsistent(IReadOnlyList<string>[] runs){if(runs.Length==0)return;var baseline=string.Join("\n",runs[0]);if(runs.Any(x=>string.Join("\n",x)!=baseline))throw new InvalidOperationException("Chroma ANN result is not deterministic across runs.");}
 public static void AssertJaccard(double value,double minimum=.8){if(value<minimum)throw new InvalidOperationException($"Chroma top-5 Jaccard {value:0.####} is below {minimum:0.####}.");}
 public static double MeanJaccard(IEnumerable<(IEnumerable<string> Actual,IEnumerable<string> Expected)> pairs)=>pairs.Select(x=>ChromaStore.Jaccard(x.Actual,x.Expected)).DefaultIfEmpty(1).Average();
}
