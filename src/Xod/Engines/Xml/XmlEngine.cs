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
        private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> tableLocks = new(StringComparer.Ordinal);
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

            this.options = options ?? new DatabaseOptions
            {
                InitialCreate = true,
                LazyLoadParent = true,
                StorageFormat = InferStorageFormat(file)
            };

            this.StorageFormat = this.options.StorageFormat;
            this.pageSize = this.options.PageSize > 0 ? this.options.PageSize : 524288;

            var (resolvedPath, resolvedRoot) = DatabaseFiles.Resolve(file);
            if (!File.Exists(resolvedPath) && this.options.InitialCreate)
            {
                Directory.CreateDirectory(resolvedRoot);
                IsNew = true;
            }

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
                this.options.Durability == DurabilityMode.Buffered);
            // Every file mutation passes here. A password switch this context committed but couldn't finish is
            // finished first (or the write refused): the next open would put the staged copy, from before this
            // write, over it.
            this.ioService.BeforeFileMutate = f =>
            {
                ThrowIfWritesRefused();
                passwordSwitch.FinishPendingSwitch();
                activeTransaction?.CaptureBeforeMutate(f);
                ioService.NoteFileMutation(f);
            };
            this.ioService.HasActiveTransaction = () => activeTransaction != null && activeTransaction.IsActive;
            this.ioService.ItemWriterDelegate = t =>
            {
                object instance = ValueHelper.CreateInstance(t);
                if (instance is Database dbMeta)
                {
                    dbMeta.Format = this.StorageFormat.ToString();
                }
                return new XDocument(Write(t, instance));
            };

            try
            {
                string databaseFile = System.IO.Path.GetFileName(path);
                XFile xodFile = this.ioService.OpenFileOrCreate<Database>(databaseFile, !this.options.InitialCreate, this.options.InitialCreate);
                if (xodFile == null)
                    exceptionService.Throw(new DatabaseFileException());
                else
                    this.path = xodFile.Path;

                this.propertyKeys = new PropertyKeyService(ioService, databaseFile, this.options.PropertyEncryptionKey, () => this.password);

                this.itemsCacheService = new ItemsCacheService(path);
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
            }
            catch
            {
                // A failed open (a wrong password, a missing file) lets go of the database it took hold of.
                this.ioService.Dispose();
                throw;
            }
        }

        private static StorageFormat InferStorageFormat(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath)?.ToLowerInvariant();
            return ext switch
            {
                ".xodj" or ".json" => StorageFormat.Json,
                ".xodb" or ".bin" => StorageFormat.Binary,
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
                var indexedProps = propertyService.Properties(type.FullName)
                    .Where(p => p.IsIndexed || p.IsPrimaryKey || p.IsUnique)
                    .ToList();

                if (indexedProps.Count == 0)
                {
                    indexService.MarkTypeHydrated(type);
                    return;
                }

                // Fast path: load persisted .xidx sidecar index file without scanning .xpag data pages
                if (indexService.TryLoadFromDisk(type))
                {
                    return;
                }

                string tableFileName = $"{type.FullName}.xtab";
                XFile tableFile = ioService.OpenFileOrCreate(tableFileName, typeof(Table), openOnly: true);
                if (tableFile != null)
                {
                    foreach (var pageElm in tableFile.Pages())
                    {
                        string pageFileName = pageElm.Attribute("file")?.Value;
                        if (string.IsNullOrEmpty(pageFileName) || !ioService.FileExists(pageFileName))
                            continue;

                        XFile pageFile = ioService.OpenFileOrCreate<TablePage>(pageFileName, openOnly: true);
                        if (pageFile == null) continue;

                        string pageCode = pageFile.GetFileCode();
                        foreach (var row in pageFile.Rows())
                        {
                            indexService.IndexRowElement(type, row, pageCode);
                        }
                    }

                    // The index now holds every row on the pages: whole, so it may be saved. Not by a context that
                    // writes nothing (WritesRefused): a read would fail on the refused write. The index is a derived
                    // cache, so it is only rebuilt again at the next open.
                    indexService.MarkTypeHydrated(type);
                    if (WritesRefused() == null)
                        indexService.SaveToDisk(type);
                }

                indexService.MarkTypeHydrated(type);
            }
        }

        private ReaderWriterLockSlim GetTableLock(Type type)
        {
            string key = type?.FullName ?? "__global__";
            return tableLocks.GetOrAdd(key, _ => new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion));
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
            rwLock.EnterReadLock();
            if (standalone)
            {
                tableLock = GetTableLock(type);
                EnterSecond(tableLock.EnterReadLock, rwLock.ExitReadLock);
            }
            else
            {
                tableLock = null;
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
            try
            {
                tableLock?.ExitReadLock();
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
        /// hold on the database: it can't tell whether others have it open (IOService).
        /// </summary>
        private void ThrowIfWritesRefused()
        {
            var refused = WritesRefused();
            if (refused != null)
                throw refused;
        }

        // Why this context writes nothing more, or null: the one list of the reasons, which ThrowIfWritesRefused
        // throws and Dispose asks before its last flush.
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

        private void EnterTableWriteLock(Type type, out ReaderWriterLockSlim tableLock, out bool usedGlobalWriteLock)
        {
            ThrowIfWritesRefused();
            if (BeforeAction == null && AfterAction == null &&
                (activeTransaction == null || !activeTransaction.IsActive) &&
                IsStandaloneTable(type))
            {
                usedGlobalWriteLock = false;
                rwLock.EnterReadLock();
                var table = GetTableLock(type);
                EnterSecond(() => EnterWrite(table.EnterWriteLock), rwLock.ExitReadLock);
                tableLock = table;
            }
            else
            {
                usedGlobalWriteLock = true;
                EnterWrite(rwLock.EnterWriteLock);
                globalWriteDepth++;
                tableLock = null;
            }
        }

        private void ExitTableWriteLock(ReaderWriterLockSlim tableLock, bool usedGlobalWriteLock)
        {
            try
            {
                tableLock?.ExitWriteLock();
            }
            finally
            {
                if (usedGlobalWriteLock)
                {
                    globalWriteDepth--;
                    rwLock.ExitWriteLock();
                }
                else
                {
                    rwLock.ExitReadLock();
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            // The close flushes under the engine-wide write lock, so no write of another thread is under way when its
            // indexes are called caught up (ISS-043, FlushToDisk). A close from inside a read on this thread can't
            // take it: it flushes without, and leaves its marker for the next open (the indexes rebuilt, no row lost).
            bool locked = !rwLock.IsReadLockHeld;
            if (locked)
                rwLock.EnterWriteLock();
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
                try
                {
                    if (locked)
                        rwLock.ExitWriteLock();
                }
                finally
                {
                    // A failed flush or rollback still surfaces to the caller, but files and locks are released.
                    DisposeResources();
                }
            }
        }

        private bool disposed;

        // A context that writes nothing more (WritesRefused) writes nothing at its close either: each write would be
        // refused, and the close would throw for what the caller has already been told.
        private bool MayFlushAtClose() => ioService != null && indexService != null && WritesRefused() == null;

        // Each resource is let go even if one before it fails: the hold above all (ioService).
        private void DisposeResources()
        {
            try
            {
                itemsCacheService?.Dispose();
            }
            finally
            {
                try
                {
                    ioService?.Dispose();
                }
                finally
                {
                    autonumberService?.Dispose();
                    transactionGate?.Dispose();
                    foreach (var kvp in tableLocks)
                    {
                        kvp.Value?.Dispose();
                    }
                    tableLocks.Clear();
                    rwLock?.Dispose();
                }
            }
        }
    }
}
