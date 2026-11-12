using System;
using System.IO;
using System.Linq;

namespace Xod.Services
{
    internal partial class IOService
    {
        // Every context holds "<database file>.lock" open while it has the database open: shared, or with
        // DatabaseOptions.LockDatabase exclusively. So a context knows whether others have the database open, and
        // a password change can require the database to itself (ISS-071): one open under the old password would
        // go on writing under it. The file is never deleted (see ReleaseHold).
        private bool exclusivePresence;
        private int lockRetryTimeoutMs;

        /// <summary>
        /// Why this context holds no lock on the database, or null while it holds one. A context without a hold
        /// doesn't know whether others have the database open, so it writes nothing (the engine refuses every
        /// write, XmlEngine.BeforeFileMutate): the lock file can't be created (a folder this user can't write,
        /// read-only media) or opened (its permissions); or it lost its hold (another context took the database in
        /// the instant a password change gave it back).
        /// </summary>
        internal string NoHoldReason { get; private set; }

        /// <summary>
        /// Takes this context's hold on the database and recovers what a crash left (a password change, an
        /// atomic write, a commit), but only with the database to itself: while another context has it open, a
        /// journal, a staged file or a <c>.tmp</c> may be that context's, live, and undoing it would undo its
        /// commit. What a crash left while others are open is recovered by the next open that finds the database
        /// to itself. A failed recovery stops the open and releases the hold.
        /// </summary>
        private void OpenAndRecover(bool lockDatabase, int retryTimeoutMs)
        {
            this.lockRetryTimeoutMs = retryTimeoutMs;

            if (lockDatabase)
            {
                this.lockStream = OpenLockFile(exclusive: true, retryTimeoutMs);
                this.exclusivePresence = true;
                try
                {
                    RecoverInterruptedWrites(root);
                }
                catch
                {
                    ReleaseHold();
                    throw;
                }
                return;
            }

            if (RecoveryPending(root))
            {
                FileStream alone = null;
                try
                {
                    alone = OpenLockFile(exclusive: true, retryTimeoutMs: 0);
                }
                catch (DatabaseLockedException)
                {
                    // Another context has the database open: what is pending may be its own, under way.
                }
                catch (UnauthorizedAccessException e) when (!WritesPending(root))
                {
                    // Only index markers: what they say can be honoured without writing (ISS-043).
                    _ = e;
                    IndexFilesUntrusted = true;
                }
                catch (UnauthorizedAccessException e)
                {
                    // No hold can be taken, so nothing tells whether what is pending is another context's, under
                    // way: it is never recovered without the database to itself (ISS-084: an open that took this
                    // for a folder nothing could write undid a live commit). A folder nothing can write can't be
                    // recovered either, so this open would have failed there anyway.
                    throw new XodException("'" + root + "' holds what a write left (cut short by a crash, or under way in another " +
                        "context). Recovering it needs the database to itself, but this context can't open or create the database's lock " +
                        "file (" + e.Message + "), so it can't tell which. Open the database as a user who can, where its folder can be written.", e);
                }

                if (alone != null)
                {
                    using (alone)
                        RecoverInterruptedWrites(root);
                }
            }

            try
            {
                this.lockStream = OpenLockFile(exclusive: false, retryTimeoutMs);
            }
            catch (UnauthorizedAccessException e)
            {
                // The lock file can't be created (a folder this user can't write: a database from 2.0.1, or copied
                // without the file, on read-only media) or can't be opened: open for reading only.
                NoHoldReason = "This context holds no lock on the database, since it can't open or create the database's lock file (" +
                    e.Message + "), so it can't tell whether others have the database open: it is open for reading only.";
            }
            this.exclusivePresence = false;
        }

        // What a crash may have left: a write cut short, or a context's indexes behind its pages (ISS-043).
        internal static bool RecoveryPending(string root) =>
            WritesPending(root) || Directory.EnumerateFiles(root, "*" + IndexesBehindSuffix).Any();

        // A transaction journal (or one cut short), a password change, an atomic write of a database file.
        private static bool WritesPending(string root) =>
            Directory.EnumerateFiles(root).Any(f =>
                f.EndsWith(".xwal", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".xwal.tmp", StringComparison.OrdinalIgnoreCase) ||
                (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && Helpers.DatabaseFiles.IsOfDatabase(f.Substring(0, f.Length - 4))) ||
                f.EndsWith(PasswordSwitch.JournalSuffix, StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(PasswordSwitch.StagedSuffix, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Lets go of the database. The lock file stays, whoever held it: on Unix a context that opened it just
        /// before a deletion would hold a file no longer in the folder while the next open created another, and
        /// the two wouldn't see each other; on Windows an open racing the deletion fails.
        /// </summary>
        private void ReleaseHold()
        {
            if (lockStream == null)
                return;
            lockStream.Dispose();
            lockStream = null;
        }

        private FileStream OpenLockFile(bool exclusive, int retryTimeoutMs) => OpenLockFile(this.path, exclusive, retryTimeoutMs);

        /// <summary>
        /// The one place a database's lock file (<c>&lt;database file&gt;.lock</c>) is opened, by a context and by
        /// <see cref="XodMigrator"/>. Shared, it is opened with read access, so a read-only lock file holds (on Unix
        /// .NET takes flock LOCK_SH, which needs only read access, NFS included). Exclusive, it shares nothing (.NET
        /// takes LOCK_EX for FileShare.None: SafeFileHandle.Unix.cs, dotnet/runtime release/8.0 and 9.0), and is
        /// opened with write access where the file allows it, since an NFS client grants LOCK_EX only to a file open
        /// for writing (flock(2)); a read-only lock file is opened for reading (ISS-084). .NET ignores a lock it
        /// couldn't take for any reason but another's lock, so an exclusive hold is claimed only once a second open of
        /// the file is refused (<see cref="ProveExclusive"/>; review of TASK-063: on NFS the read-only exclusive open
        /// took no lock at all). Either kind fails while the other is held, with <see cref="DatabaseLockedException"/>
        /// once <paramref name="retryTimeoutMs"/> has passed. An UnauthorizedAccessException says the file can't be
        /// created (its folder can't be written, its medium is read-only) or opened (its permissions): no hold, and no
        /// telling whether others have the database open.
        /// </summary>
        internal static FileStream OpenLockFile(string databasePath, bool exclusive, int retryTimeoutMs)
        {
            string lockFilePath = databasePath + ".lock";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int delayMs = 5;
            bool writable = exclusive;

            while (true)
            {
                FileStream stream;
                try
                {
                    stream = new FileStream(lockFilePath, FileMode.OpenOrCreate, writable ? FileAccess.ReadWrite : FileAccess.Read,
                        exclusive ? FileShare.None : FileShare.ReadWrite);
                }
                catch (UnauthorizedAccessException) when (writable)
                {
                    writable = false; // a lock file this user can only read
                    continue;
                }
                catch (IOException ex) when (!File.Exists(lockFilePath))
                {
                    // No file to be locked: it couldn't be created (EROFS is an IOException, not an
                    // UnauthorizedAccessException: Interop.IOErrors.cs, release/9.0). Not another's lock.
                    throw new UnauthorizedAccessException("The database's lock file '" + lockFilePath + "' can't be created: " + ex.Message, ex);
                }
                catch (IOException ex)
                {
                    int remaining = retryTimeoutMs - (int)sw.ElapsedMilliseconds;
                    if (retryTimeoutMs <= 0 || remaining <= 0)
                    {
                        throw new DatabaseLockedException(databasePath, ex);
                    }

                    System.Threading.Thread.Sleep(Math.Min(delayMs, remaining));
                    delayMs = Math.Min(delayMs * 2, 50);
                    continue;
                }

                if (exclusive)
                    ProveExclusive(stream, lockFilePath, databasePath);
                return stream;
            }
        }

        /// <summary>
        /// An exclusive hold is real only if no other open of the file can succeed: a shared open is tried, and must
        /// be refused (Windows refuses it for the sharing; Unix for the lock). Where it succeeds, the file system gave
        /// no lock (an NFS client and a lock file this user can only read), so nothing keeps others out: the hold is let
        /// go and refused as if another context held the database, which is the safe reading. Not where the caller
        /// switched .NET's file locking off (System.IO.DisableFileLocking, read as the runtime reads it): that says
        /// nothing else opens the database, and is taken at its word, as before.
        /// </summary>
        private static void ProveExclusive(FileStream exclusive, string lockFilePath, string databasePath)
        {
            if (FileLockingSwitchedOff)
                return;
            FileStream probe;
            try
            {
                probe = new FileStream(lockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                return; // refused: the hold is exclusive
            }
            catch (UnauthorizedAccessException)
            {
                return; // refused too (Windows reports a denied share this way for some files)
            }
            probe.Dispose();
            exclusive.Dispose();
            throw new DatabaseLockedException(databasePath, new IOException("The file system gave no exclusive lock on '" + lockFilePath +
                "' (an NFS client and a lock file this user can only read, for example), so nothing keeps other contexts out."));
        }

        // The runtime's own reading (SafeFileHandle.DisableFileLocking: AppContextConfigHelper.GetBooleanConfig(
        // "System.IO.DisableFileLocking", "DOTNET_SYSTEM_IO_DISABLEFILELOCKING"), dotnet/runtime release/9.0): the
        // environment variable when it says "1"/"true" or "0"/"false" (the words in any case), else the AppContext
        // switch, else off.
        private static readonly bool FileLockingSwitchedOff = ReadFileLockingSwitchedOff();

        private static bool ReadFileLockingSwitchedOff()
        {
            string v = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
            if (v == "1" || bool.TrueString.Equals(v, StringComparison.OrdinalIgnoreCase))
                return true;
            if (v == "0" || bool.FalseString.Equals(v, StringComparison.OrdinalIgnoreCase))
                return false;
            return AppContext.TryGetSwitch("System.IO.DisableFileLocking", out bool switchedOff) && switchedOff;
        }

        /// <summary>
        /// Runs <paramref name="action"/> with the database to this context alone: refused (changing nothing)
        /// while another context or process has it open, and no other can open it meanwhile. A context opened
        /// with LockDatabase has it so already; one that holds no lock is refused.
        /// </summary>
        internal void WithDatabaseToItself(Action action)
        {
            if (NoHoldReason != null)
                throw new XodException(NoHoldReason);
            if (exclusivePresence || lockStream == null)
            {
                // Exclusive already, or a database whose folder isn't there yet (nothing to hold).
                action();
                return;
            }

            ReleaseHold();
            FileStream exclusive;
            try
            {
                exclusive = OpenLockFile(exclusive: true, retryTimeoutMs: 0);
            }
            catch (Exception e) when (e is DatabaseLockedException || e is UnauthorizedAccessException)
            {
                TakeBackSharedHold();
                throw new XodException("Another context or process has the database open; this needs the database to itself. Close the others and try again.", e);
            }

            Exception failure = null;
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure = e;
                throw;
            }
            finally
            {
                exclusive.Dispose();
                try
                {
                    TakeBackSharedHold();
                }
                catch (XodException lost) when (failure != null)
                {
                    throw new AggregateException(failure, lost);
                }
            }
        }

        // Back to the shared hold after holding the database alone, with nothing held in between (a lock can't be
        // changed from one kind to the other in place). If another context took the database in that instant,
        // this one no longer knows whether others have it open: from now on it refuses writes (NoHoldReason).
        private void TakeBackSharedHold()
        {
            try
            {
                FaultInjection.Step("retaking-hold");
                lockStream = OpenLockFile(exclusive: false, lockRetryTimeoutMs);
            }
            catch (Exception e) when (e is DatabaseLockedException || e is IOException || e is UnauthorizedAccessException)
            {
                NoHoldReason = "This context no longer holds the database: another context or process took it while this one had it to itself (" +
                    e.Message + "). It refuses writes; dispose it and open the database again.";
                throw new XodException(NoHoldReason, e);
            }
        }
    }
}
