using System.Text;
using System.Text.Json;
using Bogmem.Slices.Embedding;

namespace Bogmem.Slices.Runtime;

public sealed record PalaceRegistryEntry(
    string PalaceId,
    string Name,
    string DatabasePath,
    int PalaceSchemaVersion,
    string CreatedAt,
    string RegisteredAt,
    string ValidatedAt,
    IReadOnlyList<string> Capabilities);

public sealed record PalaceRegistrySnapshot(
    int SchemaVersion,
    string RegistryPath,
    IReadOnlyList<PalaceRegistryEntry> Palaces);

/// <summary>
/// Durable Coliseum routing catalog. The registry contains identity and
/// location metadata only; each palace remains an independently owned BogDB
/// database and runtime.
/// </summary>
public sealed class PalaceRegistry
{
    public const int CurrentSchemaVersion = 1;
    public const string EnvironmentVariable = "BOGMEM_REGISTRY_PATH";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly StringComparer _pathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public PalaceRegistry(string registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath))
            throw new ArgumentException("A registry path is required.", nameof(registryPath));
        RegistryPath = Path.GetFullPath(registryPath);
    }

    public string RegistryPath { get; }

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bogmem",
            "registry.json");

    public PalaceRegistrySnapshot Snapshot()
    {
        var document = ReadDocument();
        return new(
            document.SchemaVersion,
            RegistryPath,
            Sort(document.Palaces));
    }

    public PalaceRegistryEntry? Find(string palaceIdOrName)
    {
        var selector = RequiredSelector(palaceIdOrName);
        return FindEntry(ReadDocument().Palaces, selector);
    }

    public PalaceRegistryEntry Resolve(string palaceIdOrName) =>
        Find(palaceIdOrName)
        ?? throw new KeyNotFoundException(
            $"Palace '{palaceIdOrName.Trim()}' is not registered in '{RegistryPath}'.");

    public PalaceRegistryEntry Register(PalaceRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var manifest = runtime.Manifest;
        var databasePath = NormalizeDatabasePath(runtime.DatabasePath);
        using var registryLock = AcquireWriteLock();
        var document = ReadDocument();

        var nameConflict = document.Palaces.FirstOrDefault(entry =>
            !string.Equals(entry.PalaceId, manifest.PalaceId, StringComparison.Ordinal) &&
            string.Equals(entry.Name, manifest.Name, StringComparison.Ordinal));
        if (nameConflict is not null)
            throw new InvalidOperationException(
                $"Palace name '{manifest.Name}' is already registered to '{nameConflict.PalaceId}'. " +
                "Registry names must be unique for deterministic routing.");

        var pathConflict = document.Palaces.FirstOrDefault(entry =>
            !string.Equals(entry.PalaceId, manifest.PalaceId, StringComparison.Ordinal) &&
            _pathComparer.Equals(NormalizeDatabasePath(entry.DatabasePath), databasePath));
        if (pathConflict is not null)
            throw new InvalidOperationException(
                $"Path '{databasePath}' is already registered to palace '{pathConflict.PalaceId}'.");

        var existing = document.Palaces.FirstOrDefault(entry =>
            string.Equals(entry.PalaceId, manifest.PalaceId, StringComparison.Ordinal));
        var now = DateTimeOffset.UtcNow.ToString("O");
        var entry = new PalaceRegistryEntry(
            manifest.PalaceId,
            manifest.Name,
            databasePath,
            manifest.SchemaVersion,
            manifest.CreatedAt,
            existing?.RegisteredAt ?? now,
            now,
            manifest.Capabilities.ToArray());

        if (existing is null)
            document.Palaces.Add(entry);
        else
            document.Palaces[document.Palaces.IndexOf(existing)] = entry;
        WriteDocument(document);
        return entry;
    }

    public bool Unregister(string palaceIdOrName, out PalaceRegistryEntry? removed)
    {
        var selector = RequiredSelector(palaceIdOrName);
        using var registryLock = AcquireWriteLock();
        var document = ReadDocument();
        removed = FindEntry(document.Palaces, selector);
        if (removed is null) return false;
        document.Palaces.Remove(removed);
        WriteDocument(document);
        return true;
    }

    /// <summary>
    /// Opens a registered palace and verifies that the path still contains the
    /// expected stable identity before returning it to a router.
    /// </summary>
    public PalaceRuntime OpenPalace(
        string palaceIdOrName,
        IMemoryEmbedder? embedder = null)
    {
        var entry = Resolve(palaceIdOrName);
        if (!Directory.Exists(entry.DatabasePath))
            throw new DirectoryNotFoundException(
                $"Registered palace '{entry.PalaceId}' is missing at '{entry.DatabasePath}'.");

        var runtime = PalaceRuntime.Open(entry.DatabasePath, embedder: embedder);
        if (string.Equals(
                runtime.Manifest.PalaceId,
                entry.PalaceId,
                StringComparison.Ordinal))
            return runtime;

        var actualId = runtime.Manifest.PalaceId;
        runtime.Dispose();
        throw new InvalidOperationException(
            $"Palace registry identity mismatch at '{entry.DatabasePath}': " +
            $"expected '{entry.PalaceId}', found '{actualId}'.");
    }

    private RegistryDocument ReadDocument()
    {
        if (!File.Exists(RegistryPath))
            return new RegistryDocument();
        try
        {
            var json = File.ReadAllText(RegistryPath, Encoding.UTF8);
            var document = JsonSerializer.Deserialize<RegistryDocument>(json, JsonOptions)
                           ?? throw new InvalidOperationException(
                               $"Palace registry '{RegistryPath}' is empty.");
            if (document.SchemaVersion > CurrentSchemaVersion)
                throw new InvalidOperationException(
                    $"Palace registry schema {document.SchemaVersion} is newer than " +
                    $"supported schema {CurrentSchemaVersion}.");
            if (document.SchemaVersion < 1)
                throw new InvalidOperationException(
                    $"Palace registry '{RegistryPath}' has invalid schema {document.SchemaVersion}.");
            if (document.Palaces is null)
                throw new InvalidOperationException(
                    $"Palace registry '{RegistryPath}' has no palaces collection.");
            ValidateDocument(document);
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Palace registry '{RegistryPath}' is not valid JSON.", ex);
        }
    }

    private void ValidateDocument(RegistryDocument document)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(_pathComparer);
        foreach (var entry in document.Palaces)
        {
            if (string.IsNullOrWhiteSpace(entry.PalaceId) ||
                string.IsNullOrWhiteSpace(entry.Name) ||
                string.IsNullOrWhiteSpace(entry.DatabasePath) ||
                entry.PalaceSchemaVersion < 1 ||
                string.IsNullOrWhiteSpace(entry.CreatedAt) ||
                string.IsNullOrWhiteSpace(entry.RegisteredAt) ||
                string.IsNullOrWhiteSpace(entry.ValidatedAt) ||
                entry.Capabilities is null)
                throw new InvalidOperationException(
                    $"Palace registry '{RegistryPath}' contains an incomplete entry.");
            if (!ids.Add(entry.PalaceId))
                throw new InvalidOperationException(
                    $"Palace registry '{RegistryPath}' contains duplicate ID '{entry.PalaceId}'.");
            if (!names.Add(entry.Name))
                throw new InvalidOperationException(
                    $"Palace registry '{RegistryPath}' contains duplicate name '{entry.Name}'.");
            if (!paths.Add(NormalizeDatabasePath(entry.DatabasePath)))
                throw new InvalidOperationException(
                    $"Palace registry '{RegistryPath}' contains duplicate path '{entry.DatabasePath}'.");
        }
    }

    private void WriteDocument(RegistryDocument document)
    {
        document.Palaces = Sort(document.Palaces).ToList();
        var directory = Path.GetDirectoryName(RegistryPath)
                        ?? throw new InvalidOperationException(
                            $"Registry path '{RegistryPath}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(RegistryPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, RegistryPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
        }
    }

    private FileStream AcquireWriteLock()
    {
        var directory = Path.GetDirectoryName(RegistryPath)
                        ?? throw new InvalidOperationException(
                            $"Registry path '{RegistryPath}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var lockPath = RegistryPath + ".lock";
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Palace registry '{RegistryPath}' is busy; retry the operation.", ex);
        }
    }

    private static PalaceRegistryEntry? FindEntry(
        IEnumerable<PalaceRegistryEntry> entries,
        string selector) =>
        entries.FirstOrDefault(entry =>
            string.Equals(entry.PalaceId, selector, StringComparison.Ordinal))
        ?? entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, selector, StringComparison.Ordinal));

    private static string RequiredSelector(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A palace ID or name is required.", nameof(value));
        return value.Trim();
    }

    private static string NormalizeDatabasePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static IReadOnlyList<PalaceRegistryEntry> Sort(
        IEnumerable<PalaceRegistryEntry> entries) =>
        entries
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.PalaceId, StringComparer.Ordinal)
            .ToArray();

    private sealed class RegistryDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<PalaceRegistryEntry> Palaces { get; set; } = [];
    }
}
