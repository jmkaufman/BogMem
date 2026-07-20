using Bogmem.Harness;
using Bogmem.Slices.Locking;

namespace Bogmem.Slices.Tests.Locking;

/// <summary>S11 — lock key derivation, holder formatting, and acquire semantics.</summary>
public static class FileLockTests
{
    public static void Run(TestDispositionLedgerWriter ledger)
    {
        var result = new SuiteResult("locking");
        foreach (var (id, input, expected) in TestKit.Vectors("locks"))
        {
            var kind = input.GetProperty("kind").GetString();
            switch (kind)
            {
                case "palace_key":
                {
                    var path = input.GetProperty("path").GetString()!;
                    var (resolved, key, lockFileName) = FileLock.PalaceKey(path);
                    bool ok = resolved == expected.GetProperty("resolved").GetString()
                        && key == expected.GetProperty("key").GetString()
                        && lockFileName == expected.GetProperty("lock_file_name").GetString();
                    result.Check(id, ok, $"resolved={resolved} key={key} name={lockFileName}");
                    ledger.Append(id, "locks", ok ? "converted" : "failed", "EXACT", slice: "S11");
                    break;
                }
                case "mine_lock_name":
                {
                    var path = input.GetProperty("path").GetString()!;
                    var name = FileLock.MineLockFileName(path);
                    bool ok = name == expected.GetProperty("lock_file_name").GetString();
                    result.Check(id, ok, $"lock_file_name={name}");
                    ledger.Append(id, "locks", ok ? "converted" : "failed", "EXACT", slice: "S11");
                    break;
                }
                case "holder_format":
                {
                    var content = input.GetProperty("content").GetString()!;
                    var holder = FileLock.FormatLockHolder(content);
                    bool ok = holder == expected.GetProperty("holder").GetString();
                    result.Check(id, ok, $"holder={holder}");
                    ledger.Append(id, "locks", ok ? "converted" : "failed", "EXACT", slice: "S11");
                    break;
                }
                default:
                    result.Fail(id, $"unknown fixture kind {kind}");
                    break;
            }
        }

        RunSemantics(result, ledger);
        result.ThrowIfFailed();
    }

    /// <summary>Non-blocking / re-entrant / holder-identity behavior (not fixture-driven).</summary>
    private static void RunSemantics(SuiteResult result, TestDispositionLedgerWriter ledger)
    {
        var lockDir = TestKit.TempDir("locks");
        var palace = Path.Combine(lockDir, "palace");
        try
        {
            // First acquire holds; a second acquire in the same process passes through.
            using (var outer = FileLock.Acquire(palace, lockDir))
            {
                using var inner = FileLock.Acquire(palace, lockDir);
                var (_, key, lockFileName) = FileLock.PalaceKey(palace);
                var lockPath = Path.Combine(lockDir, lockFileName);
                result.Check("semantics:reentrant", File.Exists(lockPath), "lock file missing while held");

                // Holder identity is readable from byte 1 while the lock is held.
                var holder = FileLock.ReadLockHolder(lockPath);
                result.Check("semantics:holder-identity",
                    holder.StartsWith($"PID {Environment.ProcessId}", StringComparison.Ordinal),
                    $"holder={holder}");
            }
            // Released → acquirable again.
            using (FileLock.Acquire(palace, lockDir))
                result.Pass();

            // Foreign holder (simulated with a raw exclusive handle) → MineAlreadyRunning, non-blocking.
            var (_, _, name2) = FileLock.PalaceKey(palace);
            var path2 = Path.Combine(lockDir, name2);
            using (var foreign = new FileStream(path2, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                foreign.SetLength(0);
                var body = System.Text.Encoding.UTF8.GetBytes("\012345 mempalace mine ~/proj");
                foreign.Write(body); // byte 0 sentinel + identity from byte 1
                foreign.Flush();
                bool threw = false;
                string message = "";
                try { FileLock.Acquire(palace, lockDir).Dispose(); }
                catch (MineAlreadyRunningException ex) { threw = true; message = ex.Message; }
                result.Check("semantics:nonblocking-conflict", threw, "expected MineAlreadyRunningException");
                result.Check("semantics:conflict-message",
                    threw && message.Contains("is held by PID 12345 (mempalace mine ~/proj)", StringComparison.Ordinal),
                    $"message={message}");
            }
            ledger.Append("semantics", "locks", "converted", "EXACT",
                reason: "non-blocking + re-entrant + byte-1 holder identity verified in-process", slice: "S11");
        }
        finally
        {
            try { Directory.Delete(lockDir, recursive: true); } catch (IOException) { }
        }
    }
}
