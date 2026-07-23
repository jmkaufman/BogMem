using Bogmem.Harness;

namespace Bogmem.Slices.Tests;

internal static class TestSupport
{
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Bogmem.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Bogmem.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return Directory.GetCurrentDirectory();
        }
    }

    public static string PathUnderRepo(params string[] parts) =>
        Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray());

    public static void AssertTrue(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'");
    }

    public static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"{message}: expected {typeof(TException).Name}");
    }

    public static void WriteModuleLedger(string module, string slice, params (string TestId, string Status, string Rule, string? Reason)[] rows)
    {
        var ledger = new TestDispositionLedgerWriter();
        foreach (var (testId, status, rule, reason) in rows)
            ledger.Append(testId, module, status, rule, reason, slice);
        var path = PathUnderRepo("tests", "parity", "disposition", $"{module}.json");
        ledger.Write(path);
    }
}
