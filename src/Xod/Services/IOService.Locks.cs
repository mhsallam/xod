using System;
using System.IO;
using System.Linq;

namespace Xod.Services
{
    internal partial class IOService
    {
        // Every context holds the folder's lock file and "<database file>.lock" open while it has the database
        // open: shared, or with DatabaseOptions.LockDatabase exclusively (OpenLockFile). So a context knows whether
        // others have the database open, and a password change can require the database to itself (ISS-071): one
        // open under the old password would go on writing under it. The files are never deleted (see ReleaseHold).
        private bool exclusivePresence;
        private int lockRetryTimeoutMs;
        // Held alone to create the database file (OpenAndRecover), until the engine has written it (ShareHoldOnceCreated).
        private bool holdingToCreate;

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
        /// <para>
        /// A new database's file (<paramref name="creating"/>) is created only with the database to this context
        /// too, which the engine ends once it has written the file (<see cref="ShareHoldOnceCreated"/>): a folder holds
        /// one database, and the check (<paramref name="checkFolder"/>, DatabaseFiles.Recheck) stands only while no
        /// other context can create one, so two opens naming different new files in one folder can't both pass it and
        /// both create theirs (ISS-096). One that finds the folder held by another context checks under a shared
        /// hold: it opens the database another created meanwhile, or is refused, with no file created. The check runs
        /// under every hold, before anything is recovered: an open that would be refused never recovers.
        /// </para>
        /// </summary>
        private void OpenAndRecover(bool lockDatabase, int retryTimeoutMs, bool creating, Action checkFolder)
        {
            this.lockRetryTimeoutMs = retryTimeoutMs;

            if (lockDatabase)
            {
                this.lockStream = OpenLockFile(exclusive: true, retryTimeoutMs);
                this.exclusivePresence = true;
                try
                {
                    checkFolder?.Invoke();
                    RecoverInterruptedWrites(root);
                }
                catch
                {
                    ReleaseHold();
                    throw;
                }
                return;
            }

            if (creating || RecoveryPending(root))
            {
                DatabaseHold alone = null;
                try
                {
                    alone = OpenLockFile(exclusive: true, retryTimeoutMs: 0);
                }
                catch (DatabaseLockedException)
                {
                    // Another context has the database open: what is pending may be its own, under way; and a new
                    // database file is created only under the shared hold's check below.
                }
                catch (UnauthorizedAccessException) when (!WritesPending(root))
                {
                    // Only index markers: each index load honours them without writing (IndexesLeftBehind, ISS-043).
                    // Or nothing pending, and a database to create where no lock file can be: the shared hold fails
                    // the same way below, and the engine refuses the write that would create it (NoHoldReason).
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
                    try
                    {
                        checkFolder?.Invoke();
                        RecoverInterruptedWrites(root);
                    }
                    catch
                    {
                        alone.Dispose();
                        throw;
                    }
                    if (creating)
                    {
                        this.lockStream = alone;
                        this.exclusivePresence = true;
                        this.holdingToCreate = true;
                        return;
                    }
                    alone.Dispose();
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
            if (lockStream == null)
                return;
            try
            {
                checkFolder?.Invoke();
                // Another context holds the folder (it held it when this one asked for it alone), and no database
                // file has been created meanwhile: one can't be created while another context could create one too.
                if (creating && !File.Exists(path))
                    throw new DatabaseLockedException(path, new IOException("Another context or process has the folder '" + root +
                        "' open, so a new database file can't be created in it."));
            }
            catch
            {
                ReleaseHold();
                throw;
            }
        }

        /// <summary>
        /// Back to the shared hold once the engine has written the database file this context held the database
        /// alone to create (<see cref="OpenAndRecover"/>), with everything it writes at creation. Nothing to do for
        /// a context that holds it alone by choice (LockDatabase), holds nothing, or opened a database that was there.
        /// </summary>
        internal void ShareHoldOnceCreated()
        {
            if (!holdingToCreate)
                return;
            holdingToCreate = false;
            ReleaseHold();
            exclusivePresence = false;
            TakeBackSharedHold();
        }

        // What a crash may have left: a write cut short, or a context's indexes behind its pages (ISS-043).
        internal static bool RecoveryPending(string root) =>
            WritesPending(root) || Directory.EnumerateFiles(root, "*" + IndexesBehindSuffix).Any();

        // A transaction journal (or one cut short), a password change, an atomic write of a database file, a file a
        // transaction set aside (ISS-107).
        private static bool WritesPending(string root) =>
            Directory.EnumerateFiles(root).Any(f =>
                f.EndsWith(".xwal", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".xwal.tmp", StringComparison.OrdinalIgnoreCase) ||
                (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && Helpers.DatabaseFiles.IsOfDatabase(f.Substring(0, f.Length - 4))) ||
                (f.EndsWith(SetAsideSuffix, StringComparison.OrdinalIgnoreCase) && Helpers.DatabaseFiles.IsOfDatabase(f.Substring(0, f.Length - SetAsideSuffix.Length))) ||
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

        private DatabaseHold OpenLockFile(bool exclusive, int retryTimeoutMs) => OpenLockFile(this.path, exclusive, retryTimeoutMs);

        /// <summary>The folder's lock file (PLAN-046 section 9), beside the database file's own.</summary>
        internal const string FolderLockName = "xod.lock";

        /// <summary>
        /// The one place a hold on a database is taken, by a context and by <see cref="XodMigrator"/>: the folder's
        /// lock file (<see cref="FolderLockName"/>), then the database file's (<c>&lt;database file&gt;.lock</c>), both of
        /// one kind. The folder's because every database file in a folder shares the folder's tables, pages, journals
        /// and markers, and what a hold proves is about those: an open naming a second, new database file beside one
        /// another context had open held its own file's lock alone, so it ran the folder's recovery as if it had the
        /// database to itself, undid that context's commit under way and dropped its indexes, and only then found the
        /// other database file and was refused (review of TASK-071; ISS-096). The database file's, because XOD 2.0.0
        /// and 2.0.1 hold only that one (with LockDatabase), and the contexts of one database file still have to see
        /// them. Always in this order, so two holders never wait on each other. <paramref name="retryTimeoutMs"/> covers
        /// both.
        /// </summary>
        internal static DatabaseHold OpenLockFile(string databasePath, bool exclusive, int retryTimeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string folderLock = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)), FolderLockName);
            FileStream folder = OpenOneLockFile(folderLock, databasePath, exclusive, retryTimeoutMs);
            try
            {
                if (exclusive)
                    FaultInjection.Step("hold-alone");
                int remaining = retryTimeoutMs <= 0 ? retryTimeoutMs : Math.Max(1, retryTimeoutMs - (int)sw.ElapsedMilliseconds);
                return new DatabaseHold(folder, OpenOneLockFile(databasePath + ".lock", databasePath, exclusive, remaining));
            }
            catch
            {
                folder.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens one lock file of a hold (<see cref="OpenLockFile(string, bool, int)"/>). Shared, it is opened with read
        /// access, so a read-only lock file holds (on Unix .NET takes flock LOCK_SH, which needs only read access, NFS
        /// included). Exclusive, it shares nothing (.NET
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
        private static FileStream OpenOneLockFile(string lockFilePath, string databasePath, bool exclusive, int retryTimeoutMs)
        {
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
                    throw new UnauthorizedAccessException("The lock file '" + lockFilePath + "' can't be created: " + ex.Message, ex);
                }
                // Held by another context (or program): waited for, then reported as locked. Any other failure is
                // reported as itself (HeldByAnother; it used to read as another's lock, review of TASK-068).
                catch (IOException ex) when (HeldByAnother(ex))
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
        /// switched .NET's file locking off on Unix (System.IO.DisableFileLocking, read as the runtime reads it): that says
        /// nothing else opens the database, and is taken at its word, as before. Only a refusal for the sharing proves
        /// the hold (HeldByAnother); any other failure of the probe (too many open files) proves nothing, and is
        /// refused like an open that succeeded (review of TASK-068: every IOException counted as proof).
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
            catch (IOException e) when (HeldByAnother(e))
            {
                return; // refused for the sharing: the hold is exclusive
            }
            catch (UnauthorizedAccessException)
            {
                return; // refused too (Windows reports a denied share this way for some files)
            }
            catch (IOException e)
            {
                exclusive.Dispose();
                throw new DatabaseLockedException(databasePath, new IOException("The exclusive hold on '" + lockFilePath +
                    "' can't be proven: " + e.Message, e));
            }
            probe.Dispose();
            exclusive.Dispose();
            throw new DatabaseLockedException(databasePath, new IOException("The file system gave no exclusive lock on '" + lockFilePath +
                "' (an NFS client and a lock file this user can only read, for example), so nothing keeps other contexts out."));
        }

        // The runtime's own reading (SafeFileHandle.DisableFileLocking: AppContextConfigHelper.GetBooleanConfig(
        // "System.IO.DisableFileLocking", "DOTNET_SYSTEM_IO_DISABLEFILELOCKING"), dotnet/runtime release/9.0): the
        // environment variable when it says "1"/"true" or "0"/"false" (the words in any case), else the AppContext
        // switch, else off. Only the Unix file handle reads it (SafeFileHandle.Unix.cs: CanLockTheFile, so no flock,
        // LOCK_SH or LOCK_EX); the Windows one passes FileShare to CreateFile whatever it says (SafeFileHandle.Windows.cs,
        // release/9.0), so on Windows sharing still keeps other opens out and the probes still prove a hold.
        internal static readonly bool FileLockingSwitchedOff = !OperatingSystem.IsWindows() && ReadFileLockingSwitchedOff();

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
            DatabaseHold exclusive;
            try
            {
                exclusive = OpenLockFile(exclusive: true, retryTimeoutMs: 0);
            }
            catch (Exception e)
            {
                // Whatever failed, the shared hold is taken back: a failure that isn't another's lock (an IOException
                // such as too many open files, or a network error) used to leave this context holding nothing, with
                // no NoHoldReason, so it went on writing, and its next password change ran as if alone (review of
                // TASK-071).
                TakeBackSharedHold();
                if (e is DatabaseLockedException || e is UnauthorizedAccessException)
                    throw new XodException("Another context or process has the database open; this needs the database to itself. Close the others and try again.", e);
                throw;
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

    /// <summary>A hold on a database: the folder's lock file and the database file's, of one kind
    /// (IOService.OpenLockFile). Let go of the database file's first.</summary>
    internal sealed class DatabaseHold : IDisposable
    {
        private FileStream folder;
        private FileStream database;

        internal DatabaseHold(FileStream folder, FileStream database)
        {
            this.folder = folder;
            this.database = database;
        }

        public void Dispose()
        {
            database?.Dispose();
            database = null;
            folder?.Dispose();
            folder = null;
        }
    }
}
