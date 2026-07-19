namespace Bogmem.Harness;
public enum ComparisonRule { Exact, ExactSet, Ulp, Bounded }
public sealed record ComparisonResult(bool Passed, string Rule, string? Reason = null);
public static class ComparisonRules
{
 public static ComparisonResult Compare(ComparisonRule rule, object? actual, object? expected, double bound=0, int maxUlps=1) => rule switch {
  ComparisonRule.Exact => Equals(actual, expected) ? Pass(rule) : Fail(rule, actual, expected),
  ComparisonRule.ExactSet => ToSet(actual).SetEquals(ToSet(expected)) ? Pass(rule) : Fail(rule, actual, expected),
  ComparisonRule.Ulp => UlpResult(Convert.ToSingle(actual), Convert.ToSingle(expected), maxUlps),
  ComparisonRule.Bounded => Math.Abs(Convert.ToDouble(actual)-Convert.ToDouble(expected)) <= bound ? Pass(rule) : Fail(rule, actual, expected),
  _ => throw new ArgumentOutOfRangeException(nameof(rule)) };
 public static ComparisonResult Exact(object? a, object? e) => Compare(ComparisonRule.Exact,a,e);
 public static ComparisonResult ExactSet<T>(IEnumerable<T> a,IEnumerable<T> e) => Compare(ComparisonRule.ExactSet,a,e);
 public static ComparisonResult Ulp(float a,float e,int maxUlps=1) => Compare(ComparisonRule.Ulp,a,e,maxUlps:maxUlps);
 public static ComparisonResult Bounded(double a,double e,double bound) => Compare(ComparisonRule.Bounded,a,e,bound);
 private static ComparisonResult UlpResult(float a,float e,int max) { if (a==e || (float.IsNaN(a)&&float.IsNaN(e))) return Pass(ComparisonRule.Ulp); if(float.IsNaN(a)||float.IsNaN(e)||float.IsInfinity(a)||float.IsInfinity(e))return Fail(ComparisonRule.Ulp,a,e); long ai=OrderedBits(a), ei=OrderedBits(e); return Math.Abs(ai-ei)<=max?Pass(ComparisonRule.Ulp):Fail(ComparisonRule.Ulp,a,e); }
 private static long OrderedBits(float value){var bits=(long)BitConverter.SingleToInt32Bits(value);return bits<0?0x80000000L-bits:bits+0x80000000L;}
 private static HashSet<object?> ToSet(object? v) => v is System.Collections.IEnumerable x and not string ? x.Cast<object?>().ToHashSet() : [v];
 private static ComparisonResult Pass(ComparisonRule r)=>new(true,r.ToString().ToUpperInvariant());
 private static ComparisonResult Fail(ComparisonRule r,object? a,object? e)=>new(false,r.ToString().ToUpperInvariant(),$"actual={a}; expected={e}");
}
