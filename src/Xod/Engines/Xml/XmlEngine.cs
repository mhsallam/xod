using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Xod.Engines.Serializers;
using Xod.Helpers;
using Xod.Infra;
using Xod.Services;

namespace Xod.Engines.Xml
{
    public partial class XmlEngine : IXodEngine
    {
        private readonly ReaderWriterLockSlim rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        // How deep the thread holding rwLock's write lock is in EnterTableWriteLock's engine-wide write (a trigger's
        // write nests one deeper). Only that thread changes or reads it while it is above 0: every other thread that
        // reads it holds rwLock in some mode, which the write lock excludes. The statement undo asks it (ISS-062),
        // since ReaderWriterLockSlim.IsWriteLockHeld is for asserts and debugging only, per its documentation.
        private int globalWriteDepth;

        // How many times the thread holding rwLock's write lock has taken it, through EnterExclusive and the close:
        // the one pair that takes and lets go of it. Only that thread changes it, and every reader of it holds rwLock
        // in some mode, which the write lock excludes, so on any other thread it reads 0 (ISS-101: FlushToDisk read
        // ReaderWriterLockSlim.IsWriteLockHeld, which its documentation reserves for asserts and debugging).
        private int exclusiveDepth;
        // One lock per table, by its name in any case: SQL names a table by its file, which a case-insensitive file
        // system may spell differently from its class's full name, by which its writers name it.
        private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> tableLocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> standaloneTableCache = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim transactionGate = new SemaphoreSlim(1, 1);
        private readonly object hydrationLock = new object();
        private readonly int pageSize;

        private string password;
        private readonly string root;
        private readonly string path;
        private readonly DatabaseOptions options;

        // Services
        private readonly IOService ioService;
        private readonly PasswordSwitch passwordSwitch;
        private readonly PropertyService propertyService;
        private readonly AutonumberService autonumberService;
        private readonly PropertyKeyService propertyKeys;
        private readonly ItemsCacheService itemsCacheService;
        private readonly IndexService indexService;
        private readonly LogService logService;
        private readonly ExceptionService exceptionService;
        private XodTransaction activeTransaction;
        // Set when a commit failed and couldn't be undone (ISS-070): every write is refused until the database
        // is reopened, which undoes the commit from its journal.
        private volatile string transactionFault;
        // Set when a failed statement's undo failed too (ISS-091): what this context holds may not be what the disk
        // holds, so it writes nothing more until it is reopened.
        private volatile string statementFault;
        // The transaction's batch is open: its commit hasn't ended it yet (a rollback discards it).
        private bool transactionBatchOpen;
        private int activeTransactionThreadId;

        public event EventHandler<TriggerEventArgs> BeforeAction;
        public event EventHandler<TriggerEventArgs> AfterAction;

        public string Path => path;
        public bool IsNew { get; private set; }
        public bool LazyLoad { get; set; }
        public bool LazyLoadParent { get; set; }
        public StorageFormat StorageFormat { get; private set; }

        public XmlEngine(string file, string password = null, DatabaseOptions options = null)
        {
            if (string.IsNullOrEmpty(file))
                throw new ArgumentNullException(nameof(file));

            if (!string.IsNullOrEmpty(password) && (password.Length < 1 || password.Length > 256))
                throw new SecurityException("Password length should be between 1 and 256.");

            // The encoding of the files this context writes: the one given; else, for an existing database, the one
            // its database file records (taken once that file is open, below); else the one its database file's
            // extension picks.
            bool formatGiven = options != null && options.StorageFormatSet;
            this.options = options ?? new DatabaseOptions
            {
                InitialCreate = true,
                LazyLoadParent = true,
            };

            this.pageSize = this.options.PageSize > 0 ? this.options.PageSize : 524288;

            var (resolvedPath, resolvedRoot) = DatabaseFiles.Resolve(file);
            // The database file's extension, not the path's: a folder named "shop.bin" picked Binary for its data.xod,
            // and a database opened through its folder picked XML whatever its file (review of TASK-071).
            this.StorageFormat = formatGiven ? this.options.StorageFormat : InferStorageFormat(resolvedPath);
            if (!File.Exists(resolvedPath) && this.options.InitialCreate)
            {
                Directory.CreateDirectory(resolvedRoot);
                IsNew = true;
            }
            FaultInjection.Step("open-checked");

            this.path = resolvedPath;
            this.root = resolvedRoot;

            this.propertyService = new PropertyService();
            this.propertyService.LoadType(typeof(Database));
            this.propertyService.LoadType(typeof(Table));

            if (!string.IsNullOrEmpty(password))
                this.password = CryptoHelper.GetSHA256HashData(password);

            this.exceptionService = new ExceptionService(path);
            this.passwordSwitch = new PasswordSwitch(path, this.password);
            this.ioService = new IOService(
                path,
                this.password,
                this.propertyService,
                this.StorageFormat,
                this.options.LockDatabase,
                this.options.LockRetryTimeoutMs,
                this.options.Durability == DurabilityMode.Buffered,
                creating: IsNew,
                checkFolder: () => DatabaseFiles.Recheck(file, resolvedPath));
            // Under the hold: another context may have created the file since it was looked for.
            IsNew = IsNew && !File.Exists(resolvedPath);
            // Every file mutation passes here. A password switch this context committed but couldn't finish is
            // finished first (or the write refused): the next open would put the staged copy, from before this
            // write, over it.
            this.ioService.BeforeFileMutate = f =>
            {
                ThrowIfWritesRefused(f);
                passwordSwitch.FinishPendingSwitch();
                activeTransaction?.CaptureBeforeMutate(f);
                ioService.NoteFileMutation(f);
            };
            this.ioService.HasActiveTransaction = () => activeTransaction != null && activeTransaction.IsActive;
            this.ioService.ItemWriterDelegate = NewFileDocument;

            try
            {
                string databaseFile = System.IO.Path.GetFileName(path);
                // A new database's file is written with the database held alone, its folder checked under that hold
                // (IOService.OpenAndRecover, ISS-096), until the end of this constructor.
                if (IsNew)
                    FaultInjection.Step("creating");
                XFile xodFile = this.ioService.OpenFileOrCreate<Database>(databaseFile, !this.options.InitialCreate, this.options.InitialCreate);
                if (xodFile == null)
                    exceptionService.Throw(new DatabaseFileException());
                else
                    this.path = xodFile.Path;

                // ISS-093: a database converted by XodMigrator keeps its name (a .xod converted to Binary), and was then
                // written in the encoding the extension picks. Before any table is read: nothing format-bound is held.
                // The record is a format's name as written (StorageFormat.ToString()), in any case; anything else (a
                // number, a list of names, which Enum.TryParse took) is no record, and the extension decides.
                string recordedName = xodFile?.Root()?.Attribute("Format")?.Value;
                string known = Enum.GetNames(typeof(StorageFormat)).FirstOrDefault(n => string.Equals(n, recordedName, StringComparison.OrdinalIgnoreCase));
                if (!formatGiven && known != null && Enum.Parse<StorageFormat>(known) != this.StorageFormat)
                {
                    this.StorageFormat = Enum.Parse<StorageFormat>(known);
                    this.ioService.StorageSerializer = StorageSerializerFactory.Get(this.StorageFormat);
                }

                this.propertyKeys = new PropertyKeyService(ioService, databaseFile, this.options.PropertyEncryptionKey, () => this.password);

                this.itemsCacheService = new ItemsCacheService();
                this.indexService = new IndexService(this.propertyService, this.ioService);

                this.autonumberService = new AutonumberService(
                    path,
                    this.propertyService,
                    this.ioService,
                    this.exceptionService)
                {
                    HighestUsedDelegate = HighestAutonumberUsed
                };

                this.logService = new LogService(root);
                this.LazyLoad = this.options.LazyLoad;
                this.LazyLoadParent = this.options.LazyLoadParent;

                // A new database's file is on disk, recording its format: others may open it.
                this.ioService.ShareHoldOnceCreated();
            }
            catch
            {
                // A failed open (a wrong password, a missing file) lets go of the database it took hold of.
                this.ioService.Dispose();
                throw;
            }
        }

        // A database file's extension (DatabaseFiles.Resolve names only .xod, .xodj and .xodb files).
        private static StorageFormat InferStorageFormat(string databaseFile)
        {
            string ext = System.IO.Path.GetExtension(databaseFile)?.ToLowerInvariant();
            return ext switch
            {
                ".xodj" => StorageFormat.Json,
                ".xodb" => StorageFormat.Binary,
                _ => StorageFormat.Xml
            };
        }

        private void EnsureIndexHydrated(Type type)
        {
            if (!options.EnableIndexing || type == null || indexService.IsTypeHydrated(type))
                return;

            lock (hydrationLock)
            {
                if (indexService.IsTypeHydrated(type))
                    return;

                propertyService.LoadType(type);
                if (!indexService.HasIndexedProperties(type))
                {
                    indexService.MarkTypeHydrated(type);
                    return;
                }

                // Fast path: load persisted .xidx sidecar index file without scanning .xpag data pages
                if (indexService.TryLoadFromDisk(type))
                {
                    return;
                }

                // The index now holds every row on the pages: whole, so it may be saved. Not by a context that
                // writes nothing (WritesRefused): a read would fail on the refused write. The index is a derived
                // cache, so it is only rebuilt again at the next open.
                if (indexService.BuildFromPages(type) && WritesRefused() == null)
                    indexService.SaveBuilt(type);
            }
        }

        private ReaderWriterLockSlim GetTableLock(Type type) => GetTableLock(type?.FullName ?? "__global__");

        // A table's lock by its file's name (Type.FullName): SQL reads tables it may have no class for.
        private ReaderWriterLockSlim GetTableLock(string fullName) =>
            tableLocks.GetOrAdd(fullName, _ => new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion));

        // The table locks this thread holds (each with its table and how many times it entered it), kept by the
        // helpers that take and give them back: EnterTableReadLock, EnterTableWriteLock, their exits, and SQL's
        // (SqlSource). SQL reads it to take its own in order around what the thread already holds. Not the lock's
        // IsReadLockHeld: its documentation reserves that for asserts and debugging (ISS-101).
        private readonly ThreadLocal<Dictionary<ReaderWriterLockSlim, (string Table, int Count)>> heldTableLocks = new(() => new());

        private void Holding(ReaderWriterLockSlim tableLock, string table)
        {
            var held = heldTableLocks.Value;
            held[tableLock] = held.TryGetValue(tableLock, out var h) ? (h.Table, h.Count + 1) : (table, 1);
        }

        private void LettingGo(ReaderWriterLockSlim tableLock)
        {
            var held = heldTableLocks.Value;
            if (!held.TryGetValue(tableLock, out var h)) return;
            if (h.Count == 1) held.Remove(tableLock);
            else held[tableLock] = (h.Table, h.Count - 1);
        }

        private bool IsStandaloneTable(Type type)
        {
            if (type == null || type == typeof(object))
                return false;

            var meta = propertyService.GetMetadata(type);
            return meta != null && meta.IsStandalone;
        }

        // Every lock helper here either returns holding all it took or throws holding nothing: the caller's try, which
        // gives the locks back, starts only after it returns (ISS-080, ISS-086).
        private void EnterTableReadLock(Type type, out ReaderWriterLockSlim tableLock)
        {
            // Before the lock: this loads the type, which may refuse it (its attributes).
            bool standalone = IsStandaloneTable(type);
            EnterRead();
            if (standalone)
            {
                tableLock = GetTableLock(type);
                EnterSecond(tableLock.EnterReadLock, rwLock.ExitReadLock);
                Holding(tableLock, type.FullName);
            }
            else
            {
                tableLock = null;
            }
            // Last, and it can't throw: ExitTableReadLock ends it (ISS-138).
            itemsCacheService.BeginRead();
        }

        // The engine-wide read lock (refused once closed, ISS-103).
        private void EnterRead()
        {
            rwLock.EnterReadLock();
            if (disposed)
            {
                rwLock.ExitReadLock();
                throw Closed();
            }
        }

        // The second lock of a pair: if taking it throws, the first is given back.
        private static void EnterSecond(Action enter, Action exitFirst)
        {
            try
            {
                enter();
            }
            catch
            {
                exitFirst();
                throw;
            }
        }

        // A thread reading a table (inside a query's predicate, or enumerating its results) can't write it: a read
        // lock can't be upgraded to a write lock. Said in XOD's terms rather than the lock's.
        private static void EnterWrite(Action enter)
        {
            try
            {
                enter();
            }
            catch (LockRecursionException e)
            {
                throw new XodException("This can't run inside a read on the same thread (inside a query's predicate, for " +
                    "example): the read holds back the lock it needs. Run it after the read has returned.", e);
            }
        }

        private void ExitTableReadLock(ReaderWriterLockSlim tableLock)
        {
            itemsCacheService.EndRead();
            try
            {
                if (tableLock != null)
                {
                    LettingGo(tableLock);
                    tableLock.ExitReadLock();
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// The one decision that this context writes nothing more, checked when a write operation starts (so it
        /// changes nothing, in memory either) and again before every file write. A commit that failed and couldn't
        /// be undone: the next open undoes it from its journal, and would undo anything written now with it. A
        /// statement whose undo failed: what the context holds may not be what the disk holds. A context without a
        /// hold on the database: it can't tell whether others have it open (IOService). Then, unless the file about to be
        /// written is an index, no write beside another context's commit journal (NotBesideAnotherJournal).
        /// </summary>
        /// <param name="file">The file about to be written; null when a write operation starts.</param>
        private void ThrowIfWritesRefused(string file = null)
        {
            var refused = WritesRefused();
            if (refused != null)
                throw refused;
            // An index file is a derived cache: a journal never names one (CaptureBeforeMutate), and the undo of a
            // journal leaves every index untrusted and drops them all (RecoverWalJournalIfPresent, LeaveIndexesBehind),
            // so a save beside a journal undoes nothing acknowledged. A read rebuilds and saves one, and reads go on
            // beside a journal that won't finish.
            if (!IOService.IsIndex(file))
                NotBesideAnotherJournal();
        }

        /// <summary>
        /// ISS-147: another context's commit journal is looked for when a write operation starts and before each file
        /// write that passes the engine's hook (BeforeFileMutate), an index file's save aside. That journal is undone,
        /// with the files it names put back as they were before its commit, if the commit doesn't finish; whatever this
        /// context wrote meanwhile would be undone with it, after the caller was told it was made. A commit under way
        /// (its context holds the journal) is waited for, up to TransactionTimeoutMs, the limit on waiting for a commit:
        /// across processes nothing signals its end, so its journal is asked again, as an open asks for a held lock file
        /// (IOService.OpenOneLockFile). The wait holds this context's own locks. One that won't finish (nobody holds it)
        /// refuses every write until an open that has the database to itself undoes it. This context's own commit holds
        /// the one journal its writes go beside.
        /// <para>
        /// A mitigation, not a guarantee (ISS-147's named debt): a look is not a hold. A commit that writes its journal
        /// after this look and before the file write it guards still has that write beside it, and its undo takes the
        /// write back; the window is wider for writes made on other threads (parallel statements, pages written from
        /// the pool). Nor does it keep apart a write made before the other commit wrote its journal, to a file that
        /// commit had already read: that commit's success overwrites it. Two contexts writing one database
        /// aren't kept apart (a folder belongs to one process; the site's Processes section); the fix that would is a
        /// commit gate every write holds shared and a commit exclusive, filed as ISS-164.
        /// </para>
        /// </summary>
        private void NotBesideAnotherJournal()
        {
            if (activeTransaction != null && activeTransaction.HoldsJournal)
                return;
            int limitMs = options.TransactionTimeoutMs > 0 ? options.TransactionTimeoutMs : 30000;
            var waited = System.Diagnostics.Stopwatch.StartNew();
            int delayMs = 5;
            while (true)
            {
                switch (XodTransaction.JournalIn(root))
                {
                    case XodTransaction.JournalState.None:
                        return;
                    case XodTransaction.JournalState.WontFinish:
                        throw new XodException("A commit that didn't finish (cut short by a crash, or failed and not undone) left its journal " +
                            "'transaction.xwal' in '" + root + "'. The next open that has the database to itself undoes it, and would undo with it " +
                            "anything written meanwhile, so nothing is written beside it: close every context of the database and open it again. " +
                            "(On a network file system, or with file locking switched off, a commit still under way reads this way too: " +
                            "its context's hold on the journal can't be seen. Writing again once it has ended works.)");
                    default:
                        int remainingMs = limitMs - (int)waited.ElapsedMilliseconds;
                        if (remainingMs <= 0)
                            throw new XodException("Another context's commit is under way in '" + root + "' (it holds its journal, " +
                                "'transaction.xwal') and didn't end within TransactionTimeoutMs (" + limitMs + " ms). This write is refused " +
                                "rather than made beside it, where an undo of that commit would undo it too.");
                        System.Threading.Thread.Sleep(Math.Min(delayMs, remainingMs));
                        delayMs = Math.Min(delayMs * 2, 50);
                        break;
                }
            }
        }

        // Why this context writes nothing more, or null: the one list of the reasons that are this context's own,
        // which ThrowIfWritesRefused throws and Dispose asks before its last flush. Another context's journal is not
        // one of them: it refuses a write only while it is there (NotBesideAnotherJournal). The last flush still passes
        // the hook, so a buffered context closed beside a journal that won't finish has its unflushed writes refused,
        // and the close throws (ISS-147's named debt).
        private Exception WritesRefused()
        {
            if (transactionFault != null)
                return new TransactionException(transactionFault);
            if (statementFault != null)
                return new XodException(statementFault);
            if (ioService.NoHoldReason != null)
                return new XodException(ioService.NoHoldReason);
            return null;
        }

        /// <summary>Takes the engine-wide write lock (refused from inside a read on this thread, and once closed).</summary>
        private void EnterExclusive()
        {
            EnterWrite(rwLock.EnterWriteLock);
            if (disposed)
            {
                rwLock.ExitWriteLock();
                throw Closed();
            }
            exclusiveDepth++;
            itemsCacheService.WriteStarted();
        }

        private void ExitExclusive()
        {
            exclusiveDepth--;
            rwLock.ExitWriteLock();
        }

        // This thread holds the engine-wide write lock and is inside none of its own writes, so no write of this
        // context is under way, on this thread or another (ISS-101).
        private bool NoWriteUnderWay => exclusiveDepth > 0 && globalWriteDepth == 0;

        // ISS-103: a context's entry points refuse once it is closed. Asked after the lock is taken: a call that
        // waited for it while the close held it finds the context closed.
        private static ObjectDisposedException Closed() => new ObjectDisposedException(nameof(XodContext), "The database context is closed.");

        // This thread is inside a standalone table's write (no relations: the engine's read lock and the table's write
        // lock), where its statement runs beside others on other tables (BeginStatement, ISS-120).
        private readonly ThreadLocal<int> standaloneWriteDepth = new ThreadLocal<int>();

        private void EnterTableWriteLock(Type type, out ReaderWriterLockSlim tableLock, out bool usedGlobalWriteLock)
        {
            ThrowIfWritesRefused();
            if (BeforeAction == null && AfterAction == null &&
                (activeTransaction == null || !activeTransaction.IsActive) &&
                IsStandaloneTable(type))
            {
                usedGlobalWriteLock = false;
                EnterRead();
                var table = GetTableLock(type);
                EnterSecond(() => EnterWrite(table.EnterWriteLock), rwLock.ExitReadLock);
                tableLock = table;
                standaloneWriteDepth.Value++;
                Holding(table, type.FullName);
                // A standalone table's write can be made inside a read of another table on this thread (a query's
                // predicate): what that read has shared ends here (ISS-138). The global write lock does the same.
                itemsCacheService.WriteStarted();
            }
            else
            {
                usedGlobalWriteLock = true;
                EnterExclusive();
                globalWriteDepth++;
                tableLock = null;
            }

            // Every write records its tables' schema (PLAN-060 section 2.4), under the locks the write holds, as a
            // change to the table file like any other: a transaction commits or rolls it back with the rows.
            try
            {
                RecordSchemas(type);
            }
            catch
            {
                ExitTableWriteLock(tableLock, usedGlobalWriteLock);
                throw;
            }
        }

        private void ExitTableWriteLock(ReaderWriterLockSlim tableLock, bool usedGlobalWriteLock)
        {
            try
            {
                // The statement's end (the outermost write: a trigger's nests one deeper): in direct mode what it saved
                // and its owner didn't write (Create, UpdateItem) is written now, after its index changes (ISS-099).
                // Before the locks go, so no write of another thread comes between. Not by a context that writes
                // nothing more: its cache may not be what the disk should hold (ISS-091).
                if ((!usedGlobalWriteLock || globalWriteDepth == 1) && WritesRefused() == null)
                    ioService.WriteSaved();
            }
            finally
            {
                try
                {
                    if (tableLock != null)
                    {
                        standaloneWriteDepth.Value--;
                        LettingGo(tableLock);
                        tableLock.ExitWriteLock();
                    }
                }
                finally
                {
                    if (usedGlobalWriteLock)
                    {
                        globalWriteDepth--;
                        ExitExclusive();
                    }
                    else
                    {
                        rwLock.ExitReadLock();
                    }
                }
            }
        }

        public void Dispose()
        {
            if (disposed || closing)
                return;

            // The close flushes under the engine-wide write lock, so no write of another thread is under way when its
            // indexes are called caught up (ISS-043, FlushToDisk). A close from inside a read on this thread can't
            // take it (a read lock isn't upgraded: EnterWriteLock throws LockRecursionException, per its
            // documentation): it flushes without it, and leaves its marker for the next open (the indexes rebuilt, no
            // row lost). It used to ask ReaderWriterLockSlim.IsReadLockHeld, reserved for asserts (ISS-101).
            bool locked;
            try
            {
                rwLock.EnterWriteLock();
                locked = true;
            }
            catch (LockRecursionException)
            {
                locked = false;
            }
            // A second close that waited for the lock while the first closed returns (review of TASK-068: it closed
            // again, after the hold was let go).
            if (locked && disposed)
            {
                rwLock.ExitWriteLock();
                return;
            }
            if (locked)
                exclusiveDepth++;
            // From inside one of its own writes (a trigger), refused before anything is done: its flush would call the
            // indexes caught up in the middle of the statement, before the statement's page is saved, and the lock
            // couldn't be let go under the statement (ISS-101).
            if (locked && globalWriteDepth > 0)
            {
                ExitExclusive();
                throw new InvalidOperationException("A database context can't be closed from inside one of its own writes (a trigger, " +
                    "for example): close it once the write has returned.");
            }
            closing = true;

            try
            {
                if (activeTransaction != null && activeTransaction.IsActive)
                {
                    activeTransaction.Rollback();
                    // The rollback's autonumber marks (ISS-083) wait for a flush, and this is the last one.
                    if (MayFlushAtClose())
                        FlushToDisk();
                }
                else if (MayFlushAtClose())
                {
                    FlushToDisk();
                    indexService.FoldLargeLogs();
                }
            }
            finally
            {
                // ISS-103: the hold and the marker go while the write lock is still held, so no write of another
                // thread lands after them; one that waited for the lock then finds the context closed (EnterExclusive,
                // EnterRead). Marked closed only here: the close's own rollback takes the lock again. A failed flush or
                // rollback still surfaces to the caller, but files and locks are let go.
                disposed = true;
                try
                {
                    DisposeResources();
                }
                finally
                {
                    if (locked)
                        ExitExclusive();
                }
            }
        }

        // Set by the close, under the write lock when it can take it; read by every entry once it has its lock.
        private volatile bool disposed;
        // The close is under way (on the thread that holds the lock, or inside a read): a second close returns.
        private bool closing;

        // A context that writes nothing more (WritesRefused) writes nothing at its close either: each write would be
        // refused, and the close would throw for what the caller has already been told.
        private bool MayFlushAtClose() => ioService != null && indexService != null && WritesRefused() == null;

        // Each resource is let go even if one before it fails: the hold above all (ioService).
        private void DisposeResources()
        {
            try
            {
                ioService?.Dispose();
            }
            finally
            {
                // The locks and the transaction gate aren't disposed: the close may hold rwLock, and another
                // thread may still wait on any of them (it then finds the context closed); disposing a lock held
                // or waited on throws (SynchronizationLockException). What they hold beyond memory is the wait
                // handles they create under contention, SafeHandles the finalizer releases.
                autonumberService?.Dispose();
            }
        }
    }
}
