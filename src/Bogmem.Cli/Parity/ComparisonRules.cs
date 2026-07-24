namespace Bogmem.Cli.Parity;

public enum ComparisonRule { Exact, ExactSet, Ulp, Bounded }

public sealed record ComparisonResult(bool Passed, string Rule, string? Reason = null);

public static class ComparisonRules
{
    public static ComparisonResult Compare(
        ComparisonRule rule,
        object? actual,
        object? expected,
        double bound = 0,
        int maxUlps = 1) =>
        rule switch
        {
            ComparisonRule.Exact =>
                Equals(actual, expected) ? Pass(rule) : Fail(rule, actual, expected),
            ComparisonRule.ExactSet =>
                ToSet(actual).SetEquals(ToSet(expected))
                    ? Pass(rule)
                    : Fail(rule, actual, expected),
            ComparisonRule.Ulp =>
                UlpResult(Convert.ToSingle(actual), Convert.ToSingle(expected), maxUlps),
            ComparisonRule.Bounded =>
                Math.Abs(Convert.ToDouble(actual) - Convert.ToDouble(expected)) <= bound
                    ? Pass(rule)
                    : Fail(rule, actual, expected),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };

    public static ComparisonResult Exact(object? actual, object? expected) =>
        Compare(ComparisonRule.Exact, actual, expected);

    public static ComparisonResult ExactSet<T>(IEnumerable<T> actual, IEnumerable<T> expected) =>
        Compare(ComparisonRule.ExactSet, actual, expected);

    public static ComparisonResult Ulp(float actual, float expected, int maxUlps = 1) =>
        Compare(ComparisonRule.Ulp, actual, expected, maxUlps: maxUlps);

    public static ComparisonResult Bounded(double actual, double expected, double bound) =>
        Compare(ComparisonRule.Bounded, actual, expected, bound);

    private static ComparisonResult UlpResult(float actual, float expected, int maxUlps)
    {
        if (actual == expected || (float.IsNaN(actual) && float.IsNaN(expected)))
            return Pass(ComparisonRule.Ulp);
        if (float.IsNaN(actual) ||
            float.IsNaN(expected) ||
            float.IsInfinity(actual) ||
            float.IsInfinity(expected))
            return Fail(ComparisonRule.Ulp, actual, expected);

        var actualBits = OrderedBits(actual);
        var expectedBits = OrderedBits(expected);
        return Math.Abs(actualBits - expectedBits) <= maxUlps
            ? Pass(ComparisonRule.Ulp)
            : Fail(ComparisonRule.Ulp, actual, expected);
    }

    private static long OrderedBits(float value)
    {
        var bits = (long)BitConverter.SingleToInt32Bits(value);
        return bits < 0 ? 0x80000000L - bits : bits + 0x80000000L;
    }

    private static HashSet<object?> ToSet(object? value) =>
        value is System.Collections.IEnumerable enumerable and not string
            ? enumerable.Cast<object?>().ToHashSet()
            : [value];

    private static ComparisonResult Pass(ComparisonRule rule) =>
        new(true, rule.ToString().ToUpperInvariant());

    private static ComparisonResult Fail(
        ComparisonRule rule,
        object? actual,
        object? expected) =>
        new(
            false,
            rule.ToString().ToUpperInvariant(),
            $"actual={actual}; expected={expected}");
}
