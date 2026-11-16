using System;
using System.IO;
using System.Linq;

namespace Xod.Services
{
    internal partial class IOService
    {
        // Every context holds the folder's lock file and "<database file>.lock" open, exclusively, while it has the
        // database open (OpenAndRecover, PLAN-064): a database is open in one context at a time, so no other context
        // writes beside it, changes its password or recovers under it. The files are never deleted (see ReleaseHold).

        /// <summary>
        /// Why this context holds no lock on the database, or null while it holds one. A context without a hold
        /// doesn't know whether others have the database open, so it writes nothing (the engine refuses every
        /// write, XmlEngine.BeforeFileMutate): the lock file can't be created (a folder this user can't write,
        /// read-only media) or opened (its permissions), or the database's folder isn't there. It can't be seen either: a
        /// writer that can take the hold opens beside it (ISS-173).
        /// </summary>
        internal string NoHoldReason { get; private set; }

        /// <summary>
        /// Takes this context's hold on the database, alone, and recovers what a crash left (a password change, an
        /// atomic write, a commit). A database is open in one context at a time (PLAN-064), as the README's "Where it
        /// fits" has always said: any other open, in this process or another, is refused with
        /// <see cref="DatabaseLockedException"/> once <paramref name="retryTimeoutMs"/> has passed, an open of XOD 2.0.2
        /// (which holds the lock files shared) included. <see cref="DatabaseOptions.LockDatabase"/> changes nothing. Only where
        /// the file system takes the lock: with .NET's file locking off on Unix (<see cref="FileLockingSwitchedOff"/>), or
        /// locks local to one host, two opens can both hold "alone" (ISS-104).
        /// The folder is checked (<paramref name="checkFolder"/>, DatabaseFiles.Recheck) under the hold, before anything
        /// is recovered, so two opens naming different new database files in one folder can't both pass it (ISS-096),
        /// and an open that would be refused never recovers. A failed check or recovery stops the open and releases the
        /// hold.
        /// <para>
        /// Where no lock file can be created or opened (a folder this user can't write, read-only media, the file's
        /// permissions), no hold is taken, and the context is open for reading only (<see cref="NoHoldReason"/>; the
        /// engine refuses every write). Nothing can be recovered there, so an open that finds a write a crash left is
        /// refused; a crash's index markers are only honoured, by each index load (IndexesLeftBehind, ISS-043).
        /// </para>
        /// </summary>
        private void OpenAndRecover(int retryTimeoutMs, Action checkFolder)
        {
            try
            {
                this.lockStream = OpenLockFile(this.path, retryTimeoutMs);
            }
            catch (UnauthorizedAccessException e)
            {
                if (WritesPending(root))
                    throw new XodException("'" + root + "' holds what a write left, cut short by a crash. Recovering it needs a hold on " +
                        "the database, but this context can't open or create the database's lock file (" + e.Message + "). Open the " +
                        "database as a user who can, where its folder can be written.", e);
                NoHoldReason = "This context holds no lock on the database, since it can't open or create the database's lock file (" +
                    e.Message + "): it is open for reading only.";
                return;
            }

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
        }

        private static bool WritesPending(string root) => WriteLeft(root) != null;

        /// <summary>
        /// What a write cut short leaves for the next open alone to recover (<see cref="RecoverInterruptedWrites"/>): a
        /// transaction journal, a password change's record or staged file, an atomic write of a database file or a
        /// journal (<see cref="IsAtomicWriteLeft"/>), a file a transaction set aside (<see cref="IsSetAsideLeft"/>,
        /// ISS-107). The first such file, or null. Read by the open, to know whether to recover, and by a password
        /// change, which is refused over any of them (ISS-149). The recovery picks the atomic writes and set-asides by
        /// the same predicates, so the two agree on those; the journal and the password change's files are matched by
        /// their suffix in both. (The indexes' markers are no write left: each index load honours them, ISS-043.)
        /// </summary>
        internal static string WriteLeft(string root) =>
            Directory.EnumerateFiles(root).FirstOrDefault(f =>
                f.EndsWith(".xwal", StringComparison.OrdinalIgnoreCase) ||
                IsAtomicWriteLeft(f) ||
                IsSetAsideLeft(f) ||
                f.EndsWith(PasswordSwitch.JournalSuffix, StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(PasswordSwitch.StagedSuffix, StringComparison.OrdinalIgnoreCase));

        // An atomic write cut short: "<file>.tmp" of a database file or of the transaction journal. Another program's
        // ".tmp" in the folder is none of the database's, and the recovery leaves it alone.
        private static bool IsAtomicWriteLeft(string file)
        {
            if (!file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                return false;
            string target = file.Substring(0, file.Length - 4);
            return target.EndsWith(".xwal", StringComparison.OrdinalIgnoreCase) || Helpers.DatabaseFiles.IsOfDatabase(target);
        }

        // A database file a transaction set aside ("<file>.xdel").
        private static bool IsSetAsideLeft(string file) =>
            file.EndsWith(SetAsideSuffix, StringComparison.OrdinalIgnoreCase) &&
            Helpers.DatabaseFiles.IsOfDatabase(file.Substring(0, file.Length - SetAsideSuffix.Length));

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

        /// <summary>The folder's lock file (PLAN-046 section 9), beside the database file's own.</summary>
        internal const string FolderLockName = "xod.lock";

        /// <summary>
        /// The one place a hold on a database is taken, by a context and by <see cref="XodMigrator"/>, always alone
        /// (PLAN-064): the folder's lock file (<see cref="FolderLockName"/>), then the database file's
        /// (<c>&lt;database file&gt;.lock</c>). The folder's because every database file in a folder shares the folder's
        /// tables, pages, journals and markers, and what a hold proves is about those: an open naming a second, new
        /// database file beside one another context had open held its own file's lock alone, so it ran the folder's
        /// recovery as if it had the database to itself, undid that context's commit under way and dropped its indexes,
        /// and only then found the other database file and was refused (review of TASK-071; ISS-096). The database
        /// file's, because XOD 2.0.0 and 2.0.1 hold only that one (with LockDatabase), and XOD 2.0.2 holds both, shared.
        /// Each try takes both without waiting, and lets the first go when the second is held, so no holder waits for one
        /// while holding the other; tries go on until <paramref name="retryTimeoutMs"/> has passed.
        /// </summary>
        internal static DatabaseHold OpenLockFile(string databasePath, int retryTimeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int delayMs = 5;
            while (true)
            {
                try
                {
                    return OpenHold(databasePath);
                }
                catch (DatabaseLockedException) when (retryTimeoutMs > 0 && sw.ElapsedMilliseconds < retryTimeoutMs)
                {
                    System.Threading.Thread.Sleep((int)Math.Min(delayMs, Math.Max(1, retryTimeoutMs - sw.ElapsedMilliseconds)));
                    delayMs = Math.Min(delayMs * 2, 50);
                }
            }
        }

        // The two lock files of a hold, in order (OpenLockFile), taken without waiting.
        private static DatabaseHold OpenHold(string databasePath)
        {
            string folderLock = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)), FolderLockName);
            FileStream folder = OpenOneLockFile(folderLock, databasePath);
            try
            {
                return new DatabaseHold(folder, OpenOneLockFile(databasePath + ".lock", databasePath));
            }
            catch
            {
                folder.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens one lock file of a hold (<see cref="OpenLockFile(string, int)"/>), sharing nothing (.NET takes flock
        /// LOCK_EX for FileShare.None on Unix: SafeFileHandle.Unix.cs, dotnet/runtime release/8.0 and 9.0), with write
        /// access where the file allows it, since an NFS client grants LOCK_EX only to a file open for writing
        /// (flock(2)); a lock file this user can only read, or one a read-only medium won't open for writing, is opened
        /// for reading (ISS-084). .NET ignores a lock it couldn't take for any reason but another's lock, so the hold is
        /// claimed only once a second open of the file is refused (<see cref="ProveExclusive"/>; review of TASK-063: on
        /// NFS the read-only open took no lock at all). Held by another, it fails with
        /// <see cref="DatabaseLockedException"/>. An UnauthorizedAccessException says the file can't be created (its
        /// folder can't be written, its medium is read-only) or opened (its permissions): no hold.
        /// </summary>
        private static FileStream OpenOneLockFile(string lockFilePath, string databasePath)
        {
            bool writable = true;

            while (true)
            {
                FileStream stream;
                try
                {
                    stream = new FileStream(lockFilePath, FileMode.OpenOrCreate, writable ? FileAccess.ReadWrite : FileAccess.Read,
                        FileShare.None);
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
                // A lock file that is there but can't be opened for writing for a reason other than another's lock: a
                // read-only medium (EROFS is an IOException), as for a file this user can only read. Met at every open
                // there since every open holds the database alone (PLAN-064), where the shared open asked for reading.
                catch (IOException ex) when (writable && !HeldByAnother(ex))
                {
                    writable = false;
                    continue;
                }
                // Held by another context (or program): reported as locked, and the hold asked for again
                // (OpenLockFile). Any other failure is reported as itself (HeldByAnother; it used to read as another's
                // lock, review of TASK-068).
                catch (IOException ex) when (HeldByAnother(ex))
                {
                    throw new DatabaseLockedException(databasePath, ex);
                }

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
        /// nothing else opens the database, and is taken at its word (ISS-104). Only a refusal for the sharing proves
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

        // The runtime's own reading (SafeFileHandle.DisableFileLocking = OperatingSystem.IsBrowser() ||
        // AppContextConfigHelper.GetBooleanConfig("System.IO.DisableFileLocking", "DOTNET_SYSTEM_IO_DISABLEFILELOCKING"),
        // SafeFileHandle.Unix.cs, dotnet/runtime release/8.0 and release/9.0 alike): always in a browser (Emscripten has
        // no file locking; this reading missed it, so with every open alone, PLAN-064, every open there was refused),
        // else the environment variable when it says "1"/"true" or "0"/"false" (the words in any case), else the
        // AppContext switch, else off. Only the Unix file handle reads it (CanLockTheFile, so no flock, LOCK_SH or
        // LOCK_EX); the Windows one passes FileShare to CreateFile whatever it says (SafeFileHandle.Windows.cs,
        // release/9.0), so on Windows sharing still keeps other opens out and the probes still prove a hold.
        internal static readonly bool FileLockingSwitchedOff =
            !OperatingSystem.IsWindows() && (OperatingSystem.IsBrowser() || ReadFileLockingSwitchedOff());

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
        /// Runs <paramref name="action"/>, which needs the database to this context alone (a password change, the AES256
        /// key made): every context with a hold has it so (PLAN-064); one without is refused.
        /// </summary>
        internal void WithDatabaseToItself(Action action)
        {
            if (!HasDatabaseToItself)
                throw new XodException(NoHoldReason ?? "This context holds no lock on the database.");
            action();
        }

        /// <summary>
        /// Whether this context has the database to itself: it holds it (every hold is alone, PLAN-064). Not a context
        /// that holds no lock (<see cref="NoHoldReason"/>), nor one closed.
        /// </summary>
        internal bool HasDatabaseToItself => lockStream != null;
    }

    /// <summary>A hold on a database: the folder's lock file and the database file's, both alone
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
