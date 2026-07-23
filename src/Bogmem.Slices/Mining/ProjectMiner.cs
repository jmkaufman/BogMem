using System.Diagnostics;
using System.Text;
using Bogmem.Slices.Chunkers;
using Bogmem.Slices.Locking;
using Bogmem.Slices.Storage;

namespace Bogmem.Slices.Mining;

public sealed record ProjectMineRequest(
    string SourceDirectory,
    string? Wing = null,
    string Room = "general",
    string Agent = "mempalace",
    int Limit = 0,
    bool DryRun = false,
    long? MaxChunksPerFile = null);

public sealed record ProjectMineResult(
    string Source,
    string Wing,
    string Room,
    bool DryRun,
    int FilesDiscovered,
    int FilesProcessed,
    int FilesChanged,
    int FilesUnchanged,
    int FilesSkipped,
    int DrawersPlanned,
    int DrawersWritten,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Mines ordinary project text/code into an <see cref="IMemoryStore"/>.
/// Candidate discovery uses Git's own exclude engine when possible and a
/// conservative generated-directory fallback outside Git repositories.
/// Each source file is replaced atomically, so reruns are idempotent and
/// edits cannot leave old tail chunks behind.
/// </summary>
public sealed class ProjectMiner(IMemoryStore store)
{
    public const long MaxFileSize = 500L * 1024 * 1024;

    private static readonly HashSet<string> ReadableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".py", ".js", ".ts", ".jsx", ".tsx", ".json", ".jsonl",
        ".yaml", ".yml", ".html", ".css", ".scss", ".java", ".go", ".rs", ".swift",
        ".kt", ".kts", ".rb", ".sh", ".ps1", ".csv", ".sql", ".toml", ".tex", ".bib",
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".razor", ".cshtml",
        ".fs", ".fsproj", ".vb", ".vbproj", ".xml", ".xaml", ".c", ".h", ".cpp", ".hpp",
        ".php", ".php3", ".php4", ".php5", ".php7", ".php8", ".phtml", ".phps", ".phpt",
        ".inc", ".aw", ".fcgi", ".ctp", ".module", ".install", ".profile", ".theme",
        ".engine", ".twig", ".blade", ".tpl", ".latte", ".volt",
    };

    private static readonly HashSet<string> SkipFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "entities.json", "mempalace.yaml", "mempalace.yml", "mempal.yaml", "mempal.yml",
        ".gitignore", "package-lock.json", "pnpm-lock.yaml", "yarn.lock",
    };

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "__pycache__", ".venv", "venv", "env", "dist", "build",
        "bin", "obj", ".next", "coverage", ".mempalace", ".bogmem", ".ruff_cache",
        ".mypy_cache", ".pytest_cache", ".cache", ".tox", ".nox", ".idea", ".vscode",
        ".ipynb_checkpoints", ".eggs", "htmlcov", "target",
    };

    public ProjectMineResult Mine(ProjectMineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit < 0) throw new ArgumentOutOfRangeException(nameof(request.Limit), "limit must be non-negative");

        var source = Path.GetFullPath(Required(request.SourceDirectory, nameof(request.SourceDirectory)));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Source directory does not exist: {source}");
        var wing = string.IsNullOrWhiteSpace(request.Wing)
            ? NormalizeWing(new DirectoryInfo(source).Name)
            : Required(request.Wing, nameof(request.Wing));
        var room = Required(request.Room, nameof(request.Room));
        var agent = string.IsNullOrWhiteSpace(request.Agent) ? "mempalace" : request.Agent.Trim();
        var warnings = new List<string>();
        var candidates = DiscoverFiles(source, warnings);
        var selected = request.Limit == 0 ? candidates : candidates.Take(request.Limit).ToArray();
        var maxChunks = WindowChunker.ResolveMaxChunksPerFile(
            request.MaxChunksPerFile,
            Environment.GetEnvironmentVariable(WindowChunker.MaxChunksEnvVar));

        var processed = 0;
        var changed = 0;
        var unchanged = 0;
        var skipped = candidates.Count - selected.Count;
        var planned = 0;
        var written = 0;

        using var palaceLock = FileLock.Acquire(store.Status().DatabasePath);
        foreach (var file in selected)
        {
            string content;
            try
            {
                content = ReadText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                warnings.Add($"{Relative(source, file)}: {ex.Message}");
                continue;
            }

            if (content.IndexOf('\0') >= 0)
            {
                skipped++;
                warnings.Add($"{Relative(source, file)}: binary content skipped");
                continue;
            }

            var chunks = WindowChunker.Chunk(content);
            if (maxChunks > 0 && chunks.Count > maxChunks)
            {
                skipped++;
                warnings.Add($"{Relative(source, file)}: {chunks.Count} chunks exceeds cap {maxChunks}");
                continue;
            }

            var replacement = store.ReplaceSource(
                wing,
                room,
                file,
                chunks.Select(chunk => chunk.Content).ToArray(),
                agent,
                request.DryRun);
            processed++;
            if (replacement.Changed)
            {
                changed++;
                planned += replacement.CurrentDrawers;
                if (replacement.Applied) written += replacement.CurrentDrawers;
            }
            else
            {
                unchanged++;
            }
        }

        return new(
            source, wing, room, request.DryRun, candidates.Count, processed, changed, unchanged,
            skipped, planned, written, warnings);
    }

    private static IReadOnlyList<string> DiscoverFiles(string source, List<string> warnings)
    {
        var gitFiles = TryDiscoverGitFiles(source);
        IEnumerable<string> paths = gitFiles ?? EnumerateFallback(source);
        if (gitFiles is null)
            warnings.Add("Git file discovery unavailable; used generated-directory fallback without .gitignore parsing.");

        return paths
            .Select(path => Path.GetFullPath(path, source))
            .Where(path => IsWithinRoot(path, source))
            .Where(File.Exists)
            .Where(path => !HasSkippedDirectory(path, source))
            .Where(path => !SkipFileNames.Contains(Path.GetFileName(path)))
            .Where(path => ReadableExtensions.Contains(Path.GetExtension(path)))
            .Where(path =>
            {
                var info = new FileInfo(path);
                return !info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.Length <= MaxFileSize;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => Relative(source, path), StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string>? TryDiscoverGitFiles(string source)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = source,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("ls-files");
            start.ArgumentList.Add("--cached");
            start.ArgumentList.Add("--others");
            start.ArgumentList.Add("--exclude-standard");
            start.ArgumentList.Add("-z");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(".");
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return null;
            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => Path.GetFullPath(path, source))
                .ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateFallback(string source)
    {
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> childDirectories;
            IEnumerable<string> files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory).ToArray();
                files = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files) yield return file;
            foreach (var child in childDirectories.OrderByDescending(path => path, StringComparer.Ordinal))
            {
                var info = new DirectoryInfo(child);
                if (!SkipDirectories.Contains(info.Name) &&
                    !info.Name.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase) &&
                    !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push(child);
            }
        }
    }

    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool HasSkippedDirectory(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return parts.Take(Math.Max(0, parts.Length - 1))
            .Any(part => SkipDirectories.Contains(part) || part.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static string NormalizeWing(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_').Trim('_');
        return normalized.Length == 0 ? "project" : normalized;
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{parameterName} is required", parameterName);
        return value.Trim();
    }
}
