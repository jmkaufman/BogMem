using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Bogmem.Slices.Common;

namespace Bogmem.Slices.Locking;

/// <summary>Raised when another mine already holds the per-palace lock (legacy MineAlreadyRunning).</summary>
public sealed class MineAlreadyRunningException(string message) : InvalidOperationException(message);

/// <summary>
/// Port of palace.mine_palace_lock / _mine_lock_path at LEGACY_COMMIT.
///
/// Key derivation: palace key = sha256(normcase(realpath(expanduser(path))))[:16]
/// (lock file "mine_palace_&lt;key&gt;.lock"); per-source mine lock =
/// sha256(raw source path)[:16] + ".lock" (no normalization — legacy hashes the
/// string as given). normcase folds case only on Windows, matching Python.
///
/// Acquisition is non-blocking and process-wide re-entrant. Byte 0 of the lock
/// file is the OS lock sentinel; holder identity ("pid argv") is written from
/// byte 1 so contenders can read it without touching the locked byte. On
/// Windows the sentinel is a byte-0 range lock (msvcrt parity, FileStream.Lock)
/// over a shared-open handle. On POSIX the lock is an explicit
/// flock(LOCK_EX|LOCK_NB) via libc on a raw open(2) descriptor — the same
/// primitive legacy fcntl.flock uses. FileStream's own share emulation cannot
/// be used here: it flocks on every open, which would block contenders from
/// reading the holder identity (legacy contenders can always read it).
/// </summary>
public sealed class FileLock : IDisposable
{
    private static readonly object Guard = new();
    private static readonly HashSet<string> HeldKeys = [];

    private readonly string _palaceKey;
    private readonly FileStream? _stream; // null when re-entrant pass-through
    private readonly bool _windowsRangeLock;
    private bool _disposed;

    public string Resolved { get; }
    public string LockPath { get; }

    private FileLock(string resolved, string palaceKey, string lockPath, FileStream? stream, bool windowsRangeLock)
    {
        Resolved = resolved;
        _palaceKey = palaceKey;
        LockPath = lockPath;
        _stream = stream;
        _windowsRangeLock = windowsRangeLock;
    }

    public static string DefaultLockDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mempalace", "locks");

    /// <summary>sha256(normcase(realpath(expanduser(path))))[:16] plus the resolved path.</summary>
    public static (string Resolved, string Key, string LockFileName) PalaceKey(string palacePath)
    {
        var resolved = RealPath(ExpandUser(palacePath));
        var normalized = NormCase(resolved);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return (resolved, key, $"mine_palace_{key}.lock");
    }

    /// <summary>Legacy _mine_lock_path file name: sha256 of the raw source path, no normalization.</summary>
    public static string MineLockFileName(string sourceFile) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceFile)))[..16] + ".lock";

    /// <summary>Render a lock-file body as "PID N (cmdline)" — legacy _format_lock_holder.</summary>
    public static string FormatLockHolder(string content)
    {
        var parts = PySplitMax1(content);
        if (parts.Count == 0 || parts[0].Length == 0 || !parts[0].All(char.IsDigit))
            return "another writer (identity not recorded)";
        var pid = parts[0];
        if (parts.Count > 1 && PyText.PyStrip(parts[1]).Length > 0)
            return $"PID {pid} ({PyText.PyStrip(parts[1])})";
        return $"PID {pid}";
    }

    /// <summary>Non-blocking, process-wide re-entrant acquire of the per-palace lock.</summary>
    public static FileLock Acquire(string palacePath, string? lockDir = null)
    {
        lockDir ??= DefaultLockDir;
        Directory.CreateDirectory(lockDir);
        var (resolved, key, lockFileName) = PalaceKey(palacePath);
        var lockPath = Path.Combine(lockDir, lockFileName);

        lock (Guard)
        {
            if (HeldKeys.Contains(key))
                return new FileLock(resolved, key, lockPath, null, false); // re-entrant pass-through
        }

        FileStream stream;
        bool windowsRangeLock = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        if (windowsRangeLock)
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            try
            {
#pragma warning disable CA1416
                stream.Lock(0, 1);
#pragma warning restore CA1416
            }
            catch (IOException)
            {
                var holder = ReadHolderFrom(stream);
                stream.Dispose();
                throw AlreadyRunning(resolved, holder);
            }
        }
        else
        {
            stream = Posix.OpenReadWrite(lockPath);
            if (!Posix.TryFlockExclusiveNonBlocking(stream.SafeFileHandle))
            {
                var holder = ReadHolderFrom(stream);
                stream.Dispose();
                throw AlreadyRunning(resolved, holder);
            }
        }

        WriteHolder(stream);
        lock (Guard) HeldKeys.Add(key);
        return new FileLock(resolved, key, lockPath, stream, windowsRangeLock);
    }

    /// <summary>Best-effort read of the current holder identity from bytes 1.. of the lock file.</summary>
    public static string ReadLockHolder(string lockPath)
    {
        try
        {
            using var fs = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                : Posix.OpenReadOnly(lockPath); // raw open(2): no share-emulation flock
            return ReadHolderFrom(fs);
        }
        catch (IOException) { return "another writer (identity not recorded)"; }
        catch (UnauthorizedAccessException) { return "another writer (identity not recorded)"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_stream is null) return; // re-entrant pass-through never owned the OS lock
        lock (Guard) HeldKeys.Remove(_palaceKey);
        try
        {
            if (_windowsRangeLock)
            {
#pragma warning disable CA1416
                _stream.Unlock(0, 1);
#pragma warning restore CA1416
            }
            else
            {
                Posix.FlockUnlock(_stream.SafeFileHandle);
            }
        }
        catch (IOException) { }
        _stream.Dispose();
    }

    private static MineAlreadyRunningException AlreadyRunning(string resolved, string holder) =>
        new($"palace {resolved} is held by {holder}; wait for it to finish or stop the holder before retrying");

    /// <summary>
    /// Raw POSIX file plumbing: open(2) descriptors wrapped in FileStreams (the
    /// handle-based constructor skips .NET's share-emulation flock) and libc
    /// flock(2) — the exact primitive legacy fcntl.flock wraps.
    /// </summary>
    private static class Posix
    {
        private const int LockSh = 1, LockEx = 2, LockNb = 4, LockUn = 8;
        private const int ORdonly = 0, ORdwr = 2;

        // open(2) is variadic; the mode argument cannot be marshalled reliably
        // on arm64. The file is therefore pre-created with a managed touch
        // (matching legacy's os.open(O_CREAT|O_WRONLY) rendezvous touch) and
        // open(2) is always called WITHOUT O_CREAT, so the callee never reads
        // the variadic slot.
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int OpenNative(string path, int flags);

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        private static extern int FlockNative(nint fd, int operation);

        public static FileStream OpenReadWrite(string path)
        {
            if (!File.Exists(path))
            {
                // Touch without holding: an absent file means no current holder,
                // so the transient managed open cannot hit a foreign flock.
                new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite).Dispose();
            }
            int fd = OpenNative(path, ORdwr);
            if (fd < 0) throw new IOException($"open(2) failed for {path} (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
            return new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true), FileAccess.ReadWrite);
        }

        public static FileStream OpenReadOnly(string path)
        {
            int fd = OpenNative(path, ORdonly);
            if (fd < 0) throw new IOException($"open(2) failed for {path} (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
            return new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true), FileAccess.Read);
        }

        public static bool TryFlockExclusiveNonBlocking(Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
            FlockNative(handle.DangerousGetHandle(), LockEx | LockNb) == 0;

        public static void FlockUnlock(Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
            FlockNative(handle.DangerousGetHandle(), LockUn);
    }

    private static string ReadHolderFrom(FileStream fs)
    {
        try
        {
            if (fs.Length <= 1) return "another writer (identity not recorded)";
            fs.Seek(1, SeekOrigin.Begin);
            var buf = new byte[fs.Length - 1];
            fs.ReadExactly(buf);
            var content = PyText.PyStrip(Encoding.UTF8.GetString(buf));
            return content.Length == 0 ? "another writer (identity not recorded)" : FormatLockHolder(content);
        }
        catch (IOException) { return "another writer (identity not recorded)"; }
    }

    private static void WriteHolder(FileStream fs)
    {
        try
        {
            var ident = PyText.PyStrip($"{Environment.ProcessId} {string.Join(' ', Environment.GetCommandLineArgs().Take(3))}");
            var bytes = Encoding.UTF8.GetBytes(ident);
            fs.SetLength(1 + bytes.Length);
            fs.Seek(1, SeekOrigin.Begin);
            fs.Write(bytes);
            fs.Flush();
        }
        catch (IOException) { }
    }

    /// <summary>Python str.split(maxsplit=1) with Unicode whitespace.</summary>
    private static List<string> PySplitMax1(string s)
    {
        var runes = PyText.ToRunes(s);
        int i = 0;
        while (i < runes.Length && PyText.IsPySpace(runes[i])) i++;
        if (i >= runes.Length) return [];
        int start = i;
        while (i < runes.Length && !PyText.IsPySpace(runes[i])) i++;
        var first = PyText.FromRunes(runes, start, i - start);
        while (i < runes.Length && PyText.IsPySpace(runes[i])) i++;
        if (i >= runes.Length) return [first];
        return [first, PyText.FromRunes(runes, i, runes.Length - i)];
    }

    private static string ExpandUser(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path == "~" ? home : Path.Combine(home, path[2..]);
        }
        return path;
    }

    private static string NormCase(string path) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? path.Replace('/', '\\').ToLowerInvariant()
            : path;

    /// <summary>
    /// posixpath.realpath(strict=False): resolve components left to right,
    /// following symlinks where they exist, collapsing "." / ".." / empty
    /// segments lexically for the (common in tests) nonexistent tail.
    /// </summary>
    public static string RealPath(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.GetFullPath(path);
        if (!path.StartsWith('/'))
            path = Environment.CurrentDirectory.TrimEnd('/') + "/" + path;
        var parts = new List<string>();
        ResolveInto(parts, path, 0);
        return parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
    }

    private static void ResolveInto(List<string> parts, string path, int depth)
    {
        if (depth > 40) { AppendLexical(parts, path); return; }
        foreach (var comp in path.Split('/'))
        {
            if (comp.Length == 0 || comp == ".") continue;
            if (comp == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(comp);
            var current = "/" + string.Join('/', parts);
            string? target = TryReadLink(current);
            if (target is not null)
            {
                parts.RemoveAt(parts.Count - 1);
                if (target.StartsWith('/')) parts.Clear();
                ResolveInto(parts, target, depth + 1);
            }
        }
    }

    private static void AppendLexical(List<string> parts, string path)
    {
        foreach (var comp in path.Split('/'))
        {
            if (comp.Length == 0 || comp == ".") continue;
            if (comp == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(comp);
        }
    }

    private static string? TryReadLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is string t) return t;
            var dir = new DirectoryInfo(path);
            return dir.LinkTarget;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
