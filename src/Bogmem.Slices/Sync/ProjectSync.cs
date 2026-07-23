using System.Diagnostics;
using Bogmem.Slices.Locking;
using Bogmem.Slices.Storage;

namespace Bogmem.Slices.Sync;

public sealed record ProjectSyncRequest(
    string? ProjectDirectory = null,
    string? Wing = null,
    bool Apply = false);

public sealed record ProjectSyncCandidate(
    string SourceFile,
    string Reason,
    int Drawers);

public sealed record ProjectSyncReport(
    int Scanned,
    int Kept,
    int GitIgnored,
    int Missing,
    int NoSource,
    int OutOfScope,
    int Protected,
    int Unverifiable,
    int RemovedDrawers,
    bool DryRun,
    IReadOnlyDictionary<string, int> BySource,
    IReadOnlyList<ProjectSyncCandidate> Candidates,
    IReadOnlyList<string> ProjectRoots,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Plans and applies cleanup for project-owned drawers whose source is gone
/// or is now ignored by Git. Manual and legacy drawers are always protected.
/// Apply requires an explicit project root; root auto-detection is preview-only.
/// </summary>
public sealed class ProjectSync(IMemoryStore store)
{
    public ProjectSyncReport Run(ProjectSyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var explicitRoot = string.IsNullOrWhiteSpace(request.ProjectDirectory)
            ? null
            : FileLock.RealPath(request.ProjectDirectory.Trim());
        if (request.Apply && explicitRoot is null)
            throw new ArgumentException("sync --apply requires an explicit project directory");
        if (explicitRoot is not null && !Directory.Exists(explicitRoot))
            throw new DirectoryNotFoundException($"Project directory does not exist: {explicitRoot}");

        using var palaceLock = FileLock.Acquire(store.Status().DatabasePath);
        var drawers = ReadAll()
            .Where(drawer => request.Wing is null || string.Equals(drawer.Wing, request.Wing, StringComparison.Ordinal))
            .ToArray();
        var warnings = new List<string>();
        var roots = explicitRoot is null
            ? AutoDetectRoots(drawers)
            : [explicitRoot];

        var kept = 0;
        var gitIgnored = 0;
        var missing = 0;
        var noSource = 0;
        var outOfScope = 0;
        var protectedCount = 0;
        var unverifiable = 0;
        var removableIds = new List<string>();
        var candidates = new Dictionary<(string Source, string Reason), int>();
        var classificationCache = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var drawer in drawers)
        {
            if (!string.Equals(drawer.Origin, "project", StringComparison.Ordinal))
            {
                protectedCount++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(drawer.SourceFile) || !Path.IsPathFullyQualified(drawer.SourceFile))
            {
                noSource++;
                continue;
            }

            var source = FileLock.RealPath(drawer.SourceFile);
            if (!classificationCache.TryGetValue(source, out var classification))
            {
                classification = Classify(source, roots, warnings);
                classificationCache[source] = classification;
            }

            switch (classification)
            {
                case "kept":
                    kept++;
                    break;
                case "gitignored":
                    gitIgnored++;
                    removableIds.Add(drawer.Id);
                    AddCandidate(candidates, source, classification);
                    break;
                case "missing":
                    missing++;
                    removableIds.Add(drawer.Id);
                    AddCandidate(candidates, source, classification);
                    break;
                case "unverifiable":
                    unverifiable++;
                    break;
                default:
                    outOfScope++;
                    break;
            }
        }

        var bySource = candidates
            .GroupBy(entry => entry.Key.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Value), StringComparer.Ordinal);
        var candidateList = candidates
            .OrderBy(entry => entry.Key.Source, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Reason, StringComparer.Ordinal)
            .Select(entry => new ProjectSyncCandidate(entry.Key.Source, entry.Key.Reason, entry.Value))
            .ToArray();
        var removed = request.Apply ? store.DeleteMany(removableIds) : 0;

        return new(
            drawers.Length,
            kept,
            gitIgnored,
            missing,
            noSource,
            outOfScope,
            protectedCount,
            unverifiable,
            removed,
            DryRun: !request.Apply,
            bySource,
            candidateList,
            roots,
            warnings.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private string Classify(string source, IReadOnlyList<string> roots, List<string> warnings)
    {
        var root = roots
            .Where(candidate => IsWithinRoot(source, candidate))
            .OrderByDescending(candidate => candidate.Length)
            .ThenBy(candidate => candidate, StringComparer.Ordinal)
            .FirstOrDefault();
        if (root is null) return "out_of_scope";
        if (!File.Exists(source)) return "missing";
        if (!TryIsGitIgnored(root, source, out var ignored, out var error))
        {
            warnings.Add($"{source}: Git ignore status could not be verified ({error}); protected from deletion");
            return "unverifiable";
        }
        return ignored ? "gitignored" : "kept";
    }

    private IReadOnlyList<MemoryDrawer> ReadAll()
    {
        var all = new List<MemoryDrawer>();
        for (var offset = 0; ; offset += 100)
        {
            var page = store.List(limit: 100, offset: offset);
            all.AddRange(page);
            if (page.Count < 100) return all;
        }
    }

    private static IReadOnlyList<string> AutoDetectRoots(IEnumerable<MemoryDrawer> drawers) =>
        drawers
            .Where(drawer => string.Equals(drawer.Origin, "project", StringComparison.Ordinal))
            .Select(drawer => drawer.SourceFile)
            .Where(Path.IsPathFullyQualified)
            .Select(FindGitRoot)
            .Where(root => root is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(root => root.Length)
            .ThenBy(root => root, StringComparer.Ordinal)
            .ToArray();

    private static string? FindGitRoot(string source)
    {
        var cursor = new DirectoryInfo(Path.GetDirectoryName(FileLock.RealPath(source)) ?? "");
        while (cursor is not null)
        {
            var marker = Path.Combine(cursor.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return FileLock.RealPath(cursor.FullName);
            cursor = cursor.Parent;
        }
        return null;
    }

    private static bool TryIsGitIgnored(
        string root,
        string source,
        out bool ignored,
        out string error)
    {
        ignored = false;
        error = "";
        try
        {
            var relative = Path.GetRelativePath(root, source);
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("check-ignore");
            start.ArgumentList.Add("--quiet");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(relative);
            using var process = Process.Start(start);
            if (process is null)
            {
                error = "git process did not start";
                return false;
            }
            process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                ignored = true;
                return true;
            }
            if (process.ExitCode == 1) return true;
            error = stderr.Length == 0 ? $"git check-ignore exited {process.ExitCode}" : stderr;
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static void AddCandidate(
        IDictionary<(string Source, string Reason), int> candidates,
        string source,
        string reason)
    {
        var key = (source, reason);
        candidates.TryGetValue(key, out var count);
        candidates[key] = count + 1;
    }
}
