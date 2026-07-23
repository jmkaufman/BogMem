using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Slices.Mcp;
using Bogmem.Slices.Mining;
using Bogmem.Slices.Storage;
using Bogmem.Slices.Sync;

namespace Bogmem.Cli;

/// <summary>
/// Process entry logic, separated from Program.cs so product commands and the
/// parity exit-code contract are testable in-process.
/// </summary>
public static class CliMain
{
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string workingDirectory, TextReader? stdin = null)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            stdout.WriteLine(
                $"""
                bogmem - local-first persistent memory

                Commands:
                  init [--palace path]
                  add --wing name --room name --content text [--source-file path] [--palace path]
                  mine directory [--wing name] [--room name] [--agent name] [--limit n]
                                 [--max-chunks-per-file n] [--dry-run] [--palace path]
                  sync [directory] [--wing name] [--apply] [--palace path]
                  search "query" [--wing name] [--room name] [--limit n] [--palace path]
                  status [--palace path]
                  list [--wing name] [--room name] [--limit n] [--offset n] [--palace path]
                  get drawer-id [--palace path]
                  delete drawer-id [--palace path]
                  mcp [--palace path] [--read-only]
                  parity [module|all] [--golden path] [--report path]
                  --help

                Palace path: --palace, BOGMEM_PALACE_PATH, MEMPALACE_PALACE_PATH,
                             or ~/.bogmem/palace

                Mining config: mempalace.yaml (or .yml; legacy mempal.yaml/.yml also works).
                               --wing and --room override project configuration.

                Exit codes: 0 = success, 1 = operation/parity failure, 2 = usage/corpus error

                Modules: all, {string.Join(", ", ParityRunner.KnownModules)}
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
            if (module != "all" && !ParityRunner.KnownModules.Contains(module))
            {
                stderr.WriteLine($"Unknown module '{module}'. Modules: all, {string.Join(", ", ParityRunner.KnownModules)}.");
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

        try
        {
            return RunProductCommand(args, stdout, stderr, workingDirectory, stdin ?? Console.In);
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine($"Usage error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"BogMem error: {ex.Message}");
            return 1;
        }
    }

    private static int RunProductCommand(
        string[] args, TextWriter stdout, TextWriter stderr, string workingDirectory, TextReader stdin)
    {
        var parsed = ParseArguments(args.Skip(1).ToArray());
        var palace = ResolvePalacePath(parsed.Options.GetValueOrDefault("palace"), workingDirectory);
        switch (args[0])
        {
            case "init":
                ValidateOptions(parsed, ["palace"]);
                RequireNoPositionals(parsed);
                using (var store = new BogDbMemoryStore(palace)) WriteJson(stdout, store.Status());
                return 0;

            case "status":
                ValidateOptions(parsed, ["palace"]);
                RequireNoPositionals(parsed);
                using (var store = new BogDbMemoryStore(palace)) WriteJson(stdout, store.Status());
                return 0;

            case "add":
                ValidateOptions(parsed, ["palace", "wing", "room", "content", "source-file", "added-by"]);
                RequireNoPositionals(parsed);
                using (var store = new BogDbMemoryStore(palace))
                    WriteJson(stdout, store.Add(
                        RequiredOption(parsed, "wing"),
                        RequiredOption(parsed, "room"),
                        RequiredOption(parsed, "content", trim: false),
                        parsed.Options.GetValueOrDefault("source-file"),
                        parsed.Options.GetValueOrDefault("added-by") ?? "cli"));
                return 0;

            case "mine":
            {
                ValidateOptions(
                    parsed,
                    ["palace", "wing", "room", "agent", "limit", "max-chunks-per-file"],
                    ["dry-run"]);
                var source = Path.GetFullPath(SinglePositional(parsed, "mine requires a source directory"), workingDirectory);
                using var store = new BogDbMemoryStore(palace);
                var miner = new ProjectMiner(store);
                var maxChunks = parsed.Options.ContainsKey("max-chunks-per-file")
                    ? LongOption(parsed, "max-chunks-per-file", 0, 0, long.MaxValue)
                    : (long?)null;
                WriteJson(stdout, miner.Mine(new ProjectMineRequest(
                    source,
                    parsed.Options.GetValueOrDefault("wing"),
                    parsed.Options.GetValueOrDefault("room"),
                    parsed.Options.GetValueOrDefault("agent") ?? "mempalace",
                    IntOption(parsed, "limit", 0, 0, int.MaxValue),
                    parsed.Flags.Contains("dry-run"),
                    maxChunks)));
                return 0;
            }

            case "sync":
            {
                ValidateOptions(parsed, ["palace", "wing"], ["apply"]);
                if (parsed.Positionals.Count > 1)
                    throw new ArgumentException("sync accepts at most one project directory");
                var projectDirectory = parsed.Positionals.Count == 0
                    ? null
                    : Path.GetFullPath(parsed.Positionals[0], workingDirectory);
                using var store = new BogDbMemoryStore(palace);
                WriteJson(stdout, new ProjectSync(store).Run(new ProjectSyncRequest(
                    projectDirectory,
                    parsed.Options.GetValueOrDefault("wing"),
                    parsed.Flags.Contains("apply"))));
                return 0;
            }

            case "search":
            {
                ValidateOptions(parsed, ["palace", "query", "wing", "room", "source-file", "limit", "max-distance"]);
                var query = parsed.Positionals.Count == 1
                    ? parsed.Positionals[0]
                    : parsed.Positionals.Count == 0
                        ? RequiredOption(parsed, "query")
                        : throw new ArgumentException("search accepts exactly one query");
                using var store = new BogDbMemoryStore(palace);
                WriteJson(stdout, new
                {
                    query,
                    retrievalMode = store.Status().RetrievalMode,
                    results = store.Search(
                        query,
                        IntOption(parsed, "limit", 5, 1, 100),
                        parsed.Options.GetValueOrDefault("wing"),
                        parsed.Options.GetValueOrDefault("room"),
                        parsed.Options.GetValueOrDefault("source-file"),
                        DoubleOption(parsed, "max-distance", 1.5)),
                });
                return 0;
            }

            case "list":
                ValidateOptions(parsed, ["palace", "wing", "room", "limit", "offset"]);
                RequireNoPositionals(parsed);
                using (var store = new BogDbMemoryStore(palace))
                    WriteJson(stdout, store.List(
                        parsed.Options.GetValueOrDefault("wing"),
                        parsed.Options.GetValueOrDefault("room"),
                        IntOption(parsed, "limit", 20, 1, 100),
                        IntOption(parsed, "offset", 0, 0, int.MaxValue)));
                return 0;

            case "get":
            {
                ValidateOptions(parsed, ["palace"]);
                var id = SinglePositional(parsed, "get requires a drawer ID");
                using var store = new BogDbMemoryStore(palace);
                var drawer = store.Get(id);
                if (drawer is null) { stderr.WriteLine($"Drawer not found: {id}"); return 1; }
                WriteJson(stdout, drawer);
                return 0;
            }

            case "delete":
            {
                ValidateOptions(parsed, ["palace"]);
                var id = SinglePositional(parsed, "delete requires a drawer ID");
                using var store = new BogDbMemoryStore(palace);
                var deleted = store.Delete(id);
                WriteJson(stdout, new { drawerId = id, deleted });
                return deleted ? 0 : 1;
            }

            case "mcp":
                ValidateOptions(parsed, ["palace"], ["read-only"]);
                RequireNoPositionals(parsed);
                return RunMcp(palace, parsed.Flags.Contains("read-only"), stdin, stdout, stderr);

            default:
                stderr.WriteLine($"Unknown command '{args[0]}'. Use --help.");
                return 2;
        }
    }

    private static int RunMcp(string palace, bool readOnly, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        using var store = new BogDbMemoryStore(palace);
        var server = new McpServer(store) { ReadOnly = readOnly };
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var response = server.HandleRequestJson(line);
                if (response is not null) stdout.WriteLine(response);
            }
            catch (JsonException ex)
            {
                var error = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = null,
                    ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error", ["data"] = ex.Message },
                };
                stdout.WriteLine(error.ToJsonString());
            }
            stdout.Flush();
        }
        return 0;
    }

    private sealed record ParsedArguments(
        Dictionary<string, string> Options,
        HashSet<string> Flags,
        List<string> Positionals);

    private static ParsedArguments ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }
            var name = arg[2..];
            if (name is "read-only" or "dry-run" or "apply") { flags.Add(name); continue; }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option '{arg}' requires a value.");
            options[name] = args[++i];
        }
        return new(options, flags, positionals);
    }

    private static string ResolvePalacePath(string? option, string workingDirectory)
    {
        var configured = option
            ?? Environment.GetEnvironmentVariable("BOGMEM_PALACE_PATH")
            ?? Environment.GetEnvironmentVariable("MEMPALACE_PALACE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured, workingDirectory);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".bogmem", "palace");
    }

    private static string RequiredOption(ParsedArguments parsed, string name, bool trim = true)
    {
        if (!parsed.Options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"--{name} is required");
        return trim ? value.Trim() : value;
    }

    private static int IntOption(ParsedArguments parsed, string name, int fallback, int min, int max)
    {
        if (!parsed.Options.TryGetValue(name, out var raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value < min || value > max)
            throw new ArgumentException($"--{name} must be an integer between {min} and {max}");
        return value;
    }

    private static double DoubleOption(ParsedArguments parsed, string name, double fallback)
    {
        if (!parsed.Options.TryGetValue(name, out var raw)) return fallback;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"--{name} must be a number");
        return value;
    }

    private static long LongOption(ParsedArguments parsed, string name, long fallback, long min, long max)
    {
        if (!parsed.Options.TryGetValue(name, out var raw)) return fallback;
        if (!long.TryParse(raw, out var value) || value < min || value > max)
            throw new ArgumentException($"--{name} must be an integer between {min} and {max}");
        return value;
    }

    private static string SinglePositional(ParsedArguments parsed, string error)
    {
        if (parsed.Positionals.Count != 1) throw new ArgumentException(error);
        return parsed.Positionals[0];
    }

    private static void RequireNoPositionals(ParsedArguments parsed)
    {
        if (parsed.Positionals.Count > 0) throw new ArgumentException($"Unexpected argument '{parsed.Positionals[0]}'.");
    }

    private static void ValidateOptions(
        ParsedArguments parsed,
        IReadOnlyCollection<string> allowedOptions,
        IReadOnlyCollection<string>? allowedFlags = null)
    {
        var unknownOption = parsed.Options.Keys.FirstOrDefault(key => !allowedOptions.Contains(key));
        if (unknownOption is not null) throw new ArgumentException($"Unknown option '--{unknownOption}'.");
        allowedFlags ??= Array.Empty<string>();
        var unknownFlag = parsed.Flags.FirstOrDefault(flag => !allowedFlags.Contains(flag));
        if (unknownFlag is not null) throw new ArgumentException($"Unknown flag '--{unknownFlag}'.");
    }

    private static void WriteJson(TextWriter writer, object value) => writer.WriteLine(JsonSerializer.Serialize(value, OutputJson));
}
