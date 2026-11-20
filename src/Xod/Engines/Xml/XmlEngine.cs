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

        // How deep the thread holding rwLock's write lock is in WriteStatement's engine-wide write (a trigger's
        // write nests one deeper). Only that thread changes or reads it while it is above 0: every other thread that
        // reads it holds rwLock in some mode, which the write lock excludes. The statement undo asks it (ISS-062),
        // since ReaderWriterLockSlim.IsWriteLockHeld is for asserts and debugging only, per its documentation.
        private int globalWriteDepth;

        // How many times the thread holding rwLock's write lock has taken it, through EnterExclusiveLock (a write's
        // EnterExclusive, the transaction's own begin, commit and rollback) and the close: the one pair that takes and
        // lets go of it. Only that thread changes it, and every reader of it holds rwLock
        // in some mode, which the write lock excludes, so on any other thread it reads 0 (ISS-101: FlushToDisk read
        // ReaderWriterLockSlim.IsWriteLockHeld, which its documentation reserves for asserts and debugging).
        private int exclusiveDepth;
        // ISS-132: whose turn it is to write, the open transaction's flow or the others (TransactionTurns).
        private readonly TransactionTurns turns = new TransactionTurns();
        // The transaction this flow began: an AsyncLocal, so an await or a Task.Run inside it (the *Async wrappers)
        // is still in it, and every other flow is not. Compared with activeTransaction, which is the context's.
        private readonly AsyncLocal<XodTransaction> flowTransaction = new AsyncLocal<XodTransaction>();
        // This thread's reads and writes (nested: a trigger's write, a read or a write inside a query's predicate), and
        // the transaction the outermost decided it is part of: null when it is outside any, and then counted in turns
        // (TurnHolds checks the decision under its locks).
        private readonly ThreadLocal<int> turnDepth = new ThreadLocal<int>();
        private readonly ThreadLocal<XodTransaction> turnTransaction = new ThreadLocal<XodTransaction>();
        // The engine-wide read locks this thread holds (EnterRead, ExitRead). Not the lock's IsReadLockHeld: its
        // documentation reserves that for asserts and debugging (ISS-101).
        private readonly ThreadLocal<int> engineReadDepth = new ThreadLocal<int>();
        private readonly object hydrationLock = new object();
        private readonly int pageSize;

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
            // An undefined format is refused, by its owner, before anything is made (ISS-156: the folder was).
            StorageSerializerFactory.Get(this.StorageFormat);
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

            // The password the files are under has one owner, the password switch, which a change commits; the I/O
            // service, the AES256 keys and the statistics read it there (ISS-205: the engine, the I/O service and its
            // keys each kept a copy, and a change set them one after another).

            this.exceptionService = new ExceptionService(path);
            // No database file, none to create, and nothing a crash left of one (IOService.WriteLeft, by the predicates
            // the recovery takes): there is nothing to open, so no hold is taken (ISS-156: the refused open left its two
            // lock files in a folder that holds no database). Where a crash left something, the open holds and recovers
            // it as before: a database file set aside, for one, is put back.
            if (!this.options.InitialCreate && !File.Exists(resolvedPath) &&
                (!Directory.Exists(resolvedRoot) || IOService.WriteLeft(resolvedPath) == null))
                exceptionService.Throw(new DatabaseFileException());
            this.passwordSwitch = new PasswordSwitch(path, string.IsNullOrEmpty(password) ? null : CryptoHelper.GetSHA256HashData(password));
            this.ioService = new IOService(
                path,
                () => passwordSwitch.Password,
                this.propertyService,
                this.StorageFormat,
                this.options.LockRetryTimeoutMs,
                this.options.Durability == DurabilityMode.Buffered,
                checkFolder: () => DatabaseFiles.Recheck(file, resolvedPath));
            // Under the hold: another context may have created the file since it was looked for.
            IsNew = IsNew && !File.Exists(resolvedPath);
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
            // The one feed of what a commit changed on disk, so its undo writes back only those (XodTransaction).
            this.ioService.FileChangedOnDisk = f => activeTransaction?.NoteFileChanged(f);
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

                // ISS-209: the pages a crash left that no table names, once the database file is read (its password
                // and format are known) and before anything is written. Not in a new database: it has none.
                if (!IsNew)
                    this.ioService.DeleteUnnamedPages();

                this.propertyKeys = new PropertyKeyService(ioService, databaseFile, this.options.PropertyEncryptionKey, () => passwordSwitch.Password);

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

                // A new database's AES256 key is made now (ISS-150). With every context alone (PLAN-064) no other can make
                // its own beside it either way.
                if (IsNew)
                    this.propertyKeys.MakeKeyIfNone();

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

        // Every lock helper here either returns holding all it took or throws holding nothing: the caller's try, which
        // gives the locks back, starts only after it returns (ISS-080, ISS-086).

        /// <summary>
        /// The locks of a read: its turn, then the engine-wide read lock, whatever its table. Every write takes the
        /// engine-wide write lock (WriteStatement, ISS-125), which this keeps out. A read nested in another on its
        /// thread (a query's predicate) enters the read lock again without waiting, whatever waits for it, so nested
        /// reads can't wait for each other (ISS-163: a standalone table's read took its table's lock too, in any order).
        /// </summary>
        private void EnterReadOperation(Type type)
        {
            // Before the lock: this loads the type, which may refuse it (its attributes).
            if (type != null && type != typeof(object))
                propertyService.GetMetadata(type);
            EnterWithTurn(EnterRead, ExitRead);
            // Last, and it can't throw: ExitReadOperation ends it (ISS-138).
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
            engineReadDepth.Value++;
        }

        private void ExitRead()
        {
            engineReadDepth.Value--;
            rwLock.ExitReadLock();
        }

        private const string InsideReadMessage = "This can't run inside a read on the same thread (inside a query's predicate, for " +
            "example): the read holds back the lock it needs. Run it after the read has returned.";

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

        // A thread inside a read (a query's predicate, or enumerating its results) can't write: a read lock can't be
        // upgraded to a write lock. Said in XOD's terms rather than the lock's.
        private static void EnterWrite(Action enter)
        {
            try
            {
                enter();
            }
            catch (LockRecursionException e)
            {
                throw new XodException(InsideReadMessage, e);
            }
        }

        private void ExitReadOperation()
        {
            itemsCacheService.EndRead();
            try
            {
                ExitRead();
            }
            finally
            {
                GiveTurn();
            }
        }

        /// <summary>
        /// The one decision that this context writes nothing more, checked when a write operation starts (so it
        /// changes nothing, in memory either) and again before every file write. A commit that failed and couldn't
        /// be undone: the next open undoes it from its journal, and would undo anything written now with it. A
        /// statement whose undo failed: what the context holds may not be what the disk holds. A context without a
        /// hold on the database: nothing keeps a writer out beside it (IOService, ISS-173).
        /// </summary>
        private void ThrowIfWritesRefused()
        {
            var refused = WritesRefused();
            if (refused != null)
                throw refused;
        }

        // Why this context writes nothing more, or null: the one list of the reasons, which ThrowIfWritesRefused throws
        // and Dispose asks before its last flush.
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

        /// <summary>A write's turn, then the engine-wide write lock: waits for another flow's transaction (ISS-132).</summary>
        private void EnterExclusive()
        {
            EnterWithTurn(EnterExclusiveLock, ExitExclusiveLock);
        }

        private void ExitExclusive()
        {
            try
            {
                ExitExclusiveLock();
            }
            finally
            {
                GiveTurn();
            }
        }

        // This flow began the context's open transaction: its reads and writes are the transaction's.
        private bool InThisFlowsTransaction
        {
            get
            {
                var tx = activeTransaction;
                return tx != null && tx.IsActive && ReferenceEquals(flowTransaction.Value, tx);
            }
        }

        private int TransactionTimeoutMs => options.TransactionTimeoutMs > 0 ? options.TransactionTimeoutMs : 30000;

        /// <summary>
        /// The turn of a read or a write, then its locks (<paramref name="enterLocks"/>): the one way either takes the
        /// engine's locks (EnterReadOperation, WriteStatement, EnterExclusive, SQL's read). The turn is decided
        /// before the locks and checked under them (TurnHolds); when the transaction it decided for ended in between, the
        /// locks (<paramref name="exitLocks"/>) and the turn go, nothing done, and it starts over, outside any
        /// transaction now. Returns holding the turn and the locks, or throws holding neither.
        /// </summary>
        private void EnterWithTurn(Action enterLocks, Action exitLocks)
        {
            while (true)
            {
                TakeTurn();
                try
                {
                    enterLocks();
                }
                catch
                {
                    GiveTurn();
                    throw;
                }
                if (TurnHolds())
                    return;
                bool counted = turnTransaction.Value == null;
                try
                {
                    exitLocks();
                }
                finally
                {
                    GiveTurn();
                }
                ThrowIfCounted(counted);
            }
        }

        /// <summary>
        /// The turn of a read or a write of this context, before its locks. One of the open transaction's flow is the
        /// transaction's and goes on. Any other waits until no transaction is open and none that came before it waits
        /// (TransactionTurns), up to TransactionTimeoutMs, and is refused with nothing done if one is still open: a write
        /// used to join the transaction and be taken back by its rollback (ISS-132), a read to see its writes before
        /// the commit (ISS-175). While it is counted, no transaction begins. A nested one (a trigger's write, a read or
        /// a write inside a query's predicate) goes on in its outermost's turn: no transaction of another flow can be
        /// open under that one. Unless it is code of another flow run on this thread inside the transaction's own read
        /// or write (an await's continuation run inline, ExecutionContext.Run): that one isn't the transaction's, can't
        /// wait for it (the transaction can't end under its own operation), and is refused.
        /// </summary>
        private void TakeTurn()
        {
            if (turnDepth.Value > 0)
            {
                var outer = turnTransaction.Value;
                if (outer != null && outer.IsActive && !InThisFlowsTransaction)
                {
                    throw new TransactionException("This read or write runs on a thread inside a read or a write of a transaction it isn't part of (an await's continuation run inline there, for example), and can't wait for that transaction: it would wait for itself. Nothing of it was done; run it outside that transaction's work.");
                }
                turnDepth.Value++;
                return;
            }
            var transaction = InThisFlowsTransaction ? activeTransaction : null;
            FaultInjection.Step("turn");
            if (transaction == null)
            {
                int timeoutMs = TransactionTimeoutMs;
                if (!turns.TryBeginOperation(timeoutMs))
                {
                    throw new TransactionException("Waited " + timeoutMs + " ms (TransactionTimeoutMs) for a transaction this read or write isn't part of to end; nothing of it was done. A transaction belongs to the code that called BeginTransaction and what that code awaits or runs with Task.Run; a read or a write elsewhere, including in the caller of an async method that began it, waits for it.");
                }
            }
            turnTransaction.Value = transaction;
            turnDepth.Value = 1;
        }

        /// <summary>
        /// Under the locks: the transaction the turn decided it is part of is still the context's open one, or, for one
        /// outside any, none is open. The turn decides before the locks, and nothing keeps the transaction open between:
        /// its commit or rollback, from another thread of its flow, can end it, and another flow's transaction begin in
        /// the gap (it waits for counted turns only), whose batch a write would join and a read would see. A begin, commit
        /// or rollback from another thread takes the write lock, which the locks of a read or a write keep out, so the
        /// answer holds while they are held. One from this write's own trigger can still end the transaction (the lock is
        /// reentrant on its thread): the rest of the statement is then written outside any, and no other transaction
        /// begins before it ends.
        /// </summary>
        private bool TurnHolds()
        {
            if (turnDepth.Value > 1)
                return true;
            var tx = activeTransaction;
            var open = tx != null && tx.IsActive ? tx : null;
            return ReferenceEquals(open, turnTransaction.Value);
        }

        // After TurnHolds said no and the locks and the turn were let go: a counted turn holds every transaction out
        // (TransactionTurns), so one open under its locks is a broken invariant, and starting over would only decide the
        // same again. Thrown with nothing held.
        private static void ThrowIfCounted(bool counted)
        {
            if (counted)
                throw new InvalidOperationException("A transaction began while a read or a write outside it held its turn.");
        }

        private void GiveTurn()
        {
            if (--turnDepth.Value > 0)
                return;
            bool counted = turnTransaction.Value == null;
            turnTransaction.Value = null;
            if (counted)
                turns.EndOperation();
        }

        /// <summary>Takes the engine-wide write lock (refused from inside a read on this thread, and once closed). No
        /// turn: for the transaction's own begin, commit and rollback, and the close.</summary>
        private void EnterExclusiveLock()
        {
            EnterWrite(rwLock.EnterWriteLock);
            if (disposed)
            {
                rwLock.ExitWriteLock();
                throw Closed();
            }
            exclusiveDepth++;
            // Taken inside a read only by a thread that holds it already (a trigger's read): what that read has shared
            // ends here (ISS-138).
            itemsCacheService.WriteStarted();
        }

        private void ExitExclusiveLock()
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

        /// <summary>
        /// A write statement (Insert, InsertRange, InsertOrUpdate, Update, Delete): the one way each takes its locks and
        /// ends. Its turn, then the engine-wide write lock, for every table: one write at a time (ISS-125), so a statement
        /// that fails can put the context back as it was with no other write under way beside it. A standalone table's
        /// write used to take the engine's read lock and its table's write lock, beside the writes of other tables, and
        /// inside a read on its thread; a write inside a read is now refused, as a relational one always was (a read
        /// lock can't be upgraded).
        /// The outermost statement (a trigger's write nests in its statement) is all or nothing in direct mode (no
        /// transaction, no buffer): every data file it writes is noted before its first write (IOService.BeginStatement),
        /// and when it fails, wherever it fails (a trigger, a refused value, a file write at its end), the files are
        /// put back, the context forgets what it holds, and the indexes it touched are rebuilt from the pages
        /// (<see cref="UndoWriteStatement"/>). In a transaction or buffered mode nothing reaches the disk before the
        /// commit or the flush, and a failed statement's own undo takes its created rows back (ISS-062, UndoStatement).
        /// </summary>
        private T WriteStatement<T>(Type type, Func<T> body, bool recordSchemas = true)
        {
            // Started over, nothing is written: the lock goes with nothing to undo.
            EnterWithTurn(EnterGlobalWriteLock, ExitGlobalWriteLock);
            bool outermost = globalWriteDepth == 1;
            bool savepoint = false;
            try
            {
                savepoint = outermost && ioService.BeginStatement();
                if (savepoint)
                {
                    indexService.BeginStatement();
                    autonumberService.BeginStatement();
                }
                statementSavepoint = savepoint || (statementSavepoint && !outermost);

                // Every write records its tables' schema (PLAN-060 section 2.4), under the locks the write holds, as a
                // change to the table file like any other: a transaction commits or rolls it back with the rows. A drop
                // records none: it deletes its table, and leaves the tables it reaches as they were.
                if (recordSchemas)
                    RecordSchemas(type);
                T result = body();

                // The statement's end, the outermost one's: in direct mode what it saved and its owner didn't write
                // (Create, UpdateItem) is written now, after its index changes (ISS-099), before the lock goes, so no
                // other write comes between. Not by a context that writes nothing more: its cache may not be what the
                // disk should hold (ISS-091).
                if (outermost && WritesRefused() == null)
                    ioService.WriteSaved();
                if (savepoint)
                {
                    ioService.EndStatement();
                    indexService.EndStatement();
                    autonumberService.EndStatement();
                }
                // ISS-209: the outermost statement is over and written, so no page it chose or deleted can be left
                // unnamed by a crash now (IOService.PagesSettled). Only here and in FlushToDisk, where no write is
                // under way: not in the writers, which run in the middle of a statement too.
                if (outermost && ioService.NothingPending)
                    ioService.PagesSettled();
                return result;
            }
            catch (Exception failure) when (savepoint)
            {
                UndoWriteStatement(failure);
                throw;
            }
            finally
            {
                if (outermost)
                    statementSavepoint = false;
                try
                {
                    ExitGlobalWriteLock();
                }
                finally
                {
                    GiveTurn();
                }
            }
        }

        // The outermost write statement under way is all or nothing by its files (WriteStatement): the statement's
        // own undo of its created rows (ISS-062) is for the other modes. Changed only by the thread holding the write
        // lock, and read by it.
        private bool statementSavepoint;

        /// <summary>
        /// A statement that failed in direct mode (WriteStatement): its data files back as it found them, everything
        /// this context holds forgotten (IOService.UndoStatement), then the indexes it changed or saved dropped and
        /// rebuilt from the pages when next needed (IndexService.ForgetStatement). The counters stay where it left them,
        /// raised: the caller's items hold the numbers it handed out (ISS-083), recorded in the tables' marks by the next
        /// flush. A counter it cleared (a drop) is raised back to what it had (AutonumberService.UndoStatement, ISS-178).
        /// The failure is what the caller sees. If a file can't be put back (another program holds it), the caller gets
        /// an XodException carrying both, and the context writes nothing more until it is reopened (ISS-091): what the
        /// disk holds is no longer what any statement left.
        /// </summary>
        private void UndoWriteStatement(Exception failure)
        {
            var failures = new List<Exception>();
            try
            {
                ioService.UndoStatement();
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
            // After the files: dropping an index deletes its file, a write this refuses once writes are refused.
            try
            {
                indexService.ForgetStatement();
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
            autonumberService.UndoStatement();
            autonumberMarksPending = true;
            if (failures.Count == 0)
                return;
            statementFault = "A statement failed, and putting back the files it wrote failed too (" + failures[0].Message +
                "): what this context holds may not be what the disk holds, so it writes nothing more. Reopen the database.";
            failures.Insert(0, failure);
            throw new XodException("The statement failed, and the files it wrote couldn't all be put back: the database may " +
                "hold part of it. Reopen it to go on writing.", new AggregateException(failures));
        }

        // The lock of a write, under its turn (WriteStatement).
        private void EnterGlobalWriteLock()
        {
            ThrowIfWritesRefused();
            EnterExclusiveLock();
            globalWriteDepth++;
        }

        private void ExitGlobalWriteLock()
        {
            globalWriteDepth--;
            ExitExclusiveLock();
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
                ExitExclusiveLock();
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
                        ExitExclusiveLock();
                    // Every write or transaction waiting for its turn goes on and finds the context closed, also when
                    // the close couldn't roll its transaction back (from inside a read).
                    turns.Close();
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
                // The locks aren't disposed: the close may hold rwLock, and another
                // thread may still wait on any of them (it then finds the context closed); disposing a lock held
                // or waited on throws (SynchronizationLockException). What they hold beyond memory is the wait
                // handles they create under contention, SafeHandles the finalizer releases.
                autonumberService?.Dispose();
            }
        }
    }
}
