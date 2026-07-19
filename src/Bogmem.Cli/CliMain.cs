namespace Bogmem.Cli;

/// <summary>
/// Process entry logic, separated from Program.cs so the exit-code contract
/// (0 = parity holds, 1 = parity mismatch, 2 = usage/corpus error) is testable in-process.
/// </summary>
public static class CliMain
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string workingDirectory)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            stdout.WriteLine(
                $"""
                bogmem - parity harness

                Commands:
                  parity [module] [--golden path] [--report path]
                  --help

                Exit codes: 0 = parity holds, 1 = parity mismatch, 2 = usage/corpus error

                Modules: {string.Join(", ", ParityRunner.KnownModules)}
                """);
            return 0;
        }
        if (args[0] == "parity")
        {
            string? module = null;
            string? report = null;
            string? golden = null;
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--report":
                    case "--golden":
                        if (i + 1 >= args.Length)
                        {
                            stderr.WriteLine($"Option '{args[i]}' requires a value.");
                            return 2;
                        }
                        if (args[i] == "--report") report = args[++i];
                        else golden = args[++i];
                        break;
                    default:
                        if (args[i].StartsWith('-'))
                        {
                            stderr.WriteLine($"Unknown option '{args[i]}'. Use --help.");
                            return 2;
                        }
                        if (module is not null)
                        {
                            stderr.WriteLine($"Unexpected argument '{args[i]}'.");
                            return 2;
                        }
                        module = args[i];
                        break;
                }
            }
            module ??= "config";
            if (!ParityRunner.KnownModules.Contains(module))
            {
                stderr.WriteLine($"Unknown module '{module}'. Modules: {string.Join(", ", ParityRunner.KnownModules)}.");
                return 2;
            }
            try
            {
                var result = ParityRunner.Run(workingDirectory, module, report ?? "parity_report.json", golden);
                stdout.WriteLine($"parity {module}: {(result.Passed ? "passed" : "failed")} ({result.CheckedCount} checks); report={report ?? "parity_report.json"}");
                return result.Passed ? 0 : 1;
            }
            catch (CorpusException ex)
            {
                stderr.WriteLine($"Corpus error: {ex.Message}");
                return 2;
            }
        }
        stderr.WriteLine($"Unknown command '{args[0]}'. Use --help.");
        return 2;
    }
}
