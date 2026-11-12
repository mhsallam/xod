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
        /// write, XmlEngine.BeforeFileMutate): its folder can't be written, so the lock file couldn't be created;
        /// or it lost its hold (another context took the database in the instant a password change gave it back).
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
                bool folderWritable = true;
                try
                {
                    alone = OpenLockFile(exclusive: true, retryTimeoutMs: 0);
                }
                catch (DatabaseLockedException)
                {
                    // Another context has the database open: what is pending may be its own, under way.
                }
                catch (UnauthorizedAccessException)
                {
                    folderWritable = false;
                }

                if (alone != null)
                {
                    using (alone)
                        RecoverInterruptedWrites(root);
                }
                else if (!folderWritable)
                {
                    // Nothing can be written here, by this context or another: the recovery either finds nothing it
                    // must change, or stops the open (a commit or a password change half done can't be opened).
                    RecoverInterruptedWrites(root);
                }
            }

            try
            {
                this.lockStream = OpenLockFile(exclusive: false, retryTimeoutMs);
            }
            catch (UnauthorizedAccessException e)
            {
                // The lock file isn't there and the folder can't be written (a database from 2.0.1, or copied
                // without the file, on read-only media): opened for reading only.
                NoHoldReason = "This context holds no lock on the database, since the lock file can't be created in its folder (" +
                    e.Message + "), so it can't tell whether others have the database open: it is open for reading only.";
            }
            this.exclusivePresence = false;
        }

        // What a crash may have left: a transaction journal (or one cut short), a password change, an atomic write
        // of a database file.
        private static bool RecoveryPending(string root) =>
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
        /// <see cref="XodMigrator"/>. Shared: read access, others may read and write (on Unix .NET takes a shared
        /// advisory lock); exclusive: no sharing (an exclusive one). Either fails while the other kind is held, on a
        /// file system with locking (not when .NET's file locking is switched off: System.IO.DisableFileLocking),
        /// with <see cref="DatabaseLockedException"/> once <paramref name="retryTimeoutMs"/> has passed. Creating the
        /// file needs the folder to be writable; an UnauthorizedAccessException says it isn't.
        /// </summary>
        internal static FileStream OpenLockFile(string databasePath, bool exclusive, int retryTimeoutMs)
        {
            string lockFilePath = databasePath + ".lock";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int delayMs = 5;

            while (true)
            {
                try
                {
                    return exclusive
                        ? new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                        : new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
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
                }
            }
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
