using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xod.Engines.Serializers;
using Xod.Infra;

namespace Xod.Services
{
    internal partial class IOService : IDisposable
    {
        private const int CACHE_SIZE = 65536;

        // Cached pages are bounded by bytes as well as by count. A Binary page is a few tens of KB on
        // disk but half a megabyte once expanded, while a JSON page is as big on disk as in memory, so
        // a count-only cap made the cache behave completely differently per storage format and let a
        // 1 GB database thrash: 20 indexed queries took 245 s (Binary) / 62 s (JSON) with a 1,024-page
        // cap against 2.7 s / 7.9 s when the working set fits (ISS-038).
        private static readonly long CACHE_BYTES = ComputeCacheBudget();
        private long cachedFileBytes;

        private static long ComputeCacheBudget()
        {
            try
            {
                long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (available <= 0) available = 1024L * 1024 * 1024;
                return Math.Clamp(available / 4, 64L * 1024 * 1024, 4096L * 1024 * 1024);
            }
            catch
            {
                return 512L * 1024 * 1024;
            }
        }
        private static readonly byte[] EmptyXmlPageBytes = System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><TablePage Full=\"False\"><Rows dataType=\"Xod.PageRow\" collType=\"generic\" refType=\"complex\"></Rows></TablePage>");
        private static readonly byte[] XmlPageTrailerBytes = System.Text.Encoding.UTF8.GetBytes("</Rows></TablePage>");

        private readonly object syncLock = new object();
        private readonly Dictionary<string, FileCache> cachedFiles = new Dictionary<string, FileCache>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RawPageBuffer> cachedRawPages = new Dictionary<string, RawPageBuffer>(StringComparer.OrdinalIgnoreCase);

        private readonly string path;
        private readonly string root;
        private string password;
        private readonly PropertyService propertyService;
        private IXodStorageSerializer storageSerializer;
        private DatabaseHold lockStream;

        private int batchDepth;
        private readonly bool bufferedDurability;
        private readonly PendingFiles dirtyBatchFiles = new PendingFiles();
        private readonly PendingFiles dirtyBufferedFiles = new PendingFiles();
        private readonly HashSet<string> dirtyBatchRawPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> dirtyBufferedRawPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Direct mode (no batch, no buffer): what each statement under way saved, by its thread, a subset of the two
        // sets above, written when it ends (WriteSaved). What changed without a save waits for the next flush.
        private readonly StatementSaves saved = new StatementSaves();

        // ISS-166: a transaction changes no data file before its commit, deletes included: each stays on disk, whole, and
        // is gone for this context (DeletedHere, asked by each of its reads of the disk); never an index file, which the
        // index service deletes at the commit (IndexService.DeleteIndexFile). Its commit deletes them, with their bytes in
        // its journal (EndBatch); a rollback, or a commit refused before its journal, forgets them (DiscardBatch,
        // DiscardPendingWrites). Since ISS-196 every data-file delete waits so (batchDeletes, bufferedDeletes, below).
        // Before, they were renamed aside at once
        // ("<file>.xdel", ISS-107): another context then saw a dropped table gone and made a new one at its fixed name,
        // the rollback couldn't put the old one back over it, and the next open dropped the old one with its rows. An
        // older version's crash can still leave such a file; the open puts it back (PutBackSetAsideFiles).
        internal const string SetAsideSuffix = ".xdel";
        // ISS-196: every data-file delete waits, as the transaction's did, for the write of the files that stop naming
        // it, and is made after them (WriteInOrderUnlocked): a page deleted at once, before the table that named it was
        // written without it, left a crash between the two with a table naming a page that wasn't there. Two sets, as
        // the writes have two: a delete made while a batch is open waits for that batch's end (EndBatch; a rollback
        // forgets it, DiscardBatch), any other for the buffer's flush or, in direct mode, the statement's end
        // (WriteSaved). Each is gone for this context meanwhile (DeletedHere).
        private readonly HashSet<string> batchDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> bufferedDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Their own lock: pages are read from disk on worker threads (the prefetches in IOService.RawPages) while the
        // thread that started them may hold syncLock.
        private readonly object pendingDeletesLock = new object();

        // The document of a file this service creates: its type's, for the file of that name (a new table file
        // records its rows' schema: XmlEngine.NewFileDocument).
        internal Func<Type, string, XDocument> ItemWriterDelegate { get; set; }
        internal Action<string> BeforeFileMutate { get; set; }
        // A data file on disk has changed, or may have from here (a write in place has opened it): called after a
        // whole file's rename into place, a delete, or the open of a write in place, never for a write that failed
        // before any of those. A failed commit's undo writes back the files this named, and any other the transaction
        // changed that isn't on disk as it found it (XodTransaction.NotAsBefore): one the commit never changed
        // (another program held it) is left alone, and an undo onto it would fail too.
        internal Action<string> FileChangedOnDisk { get; set; }
        internal Func<bool> HasActiveTransaction { get; set; }

        public IOService(
            string path,
            string password,
            PropertyService propertyService,
            StorageFormat storageFormat = StorageFormat.Xml,
            int lockRetryTimeoutMs = 0,
            bool bufferedDurability = false,
            Action checkFolder = null)
        {
            this.path = path;
            this.root = Path.GetDirectoryName(this.path);
            this.password = password;
            this.fileKeys = KeysFor(password);
            this.propertyService = propertyService;
            this.storageSerializer = StorageSerializerFactory.Get(storageFormat);
            this.bufferedDurability = bufferedDurability;

            if (!string.IsNullOrEmpty(this.root) && Directory.Exists(this.root))
            {
                OpenAndRecover(lockRetryTimeoutMs, checkFolder);
            }
            else
            {
                // No folder, no hold, and nothing written (the engine creates the folder first when it creates a
                // database, so an open gets here only to fail on its missing database file).
                NoHoldReason = "The database's folder '" + this.root + "' isn't there: this context holds no lock on the database.";
            }
        }

        internal string RootDirectory => root;
        internal bool IsBatchOrBuffered => batchDepth > 0 || bufferedDurability;

        internal string Password
        {
            get => password;
            set
            {
                password = value;
                fileKeys = KeysFor(value);
            }
        }

        // The keys of this context's files, under its password (ISS-152); null without one.
        private Helpers.FileKeys fileKeys;

        internal Helpers.FileKeys FileKeys => fileKeys;

        private Helpers.FileKeys KeysFor(string pwd) =>
            string.IsNullOrEmpty(pwd) ? null : pwd == password && fileKeys != null && fileKeys.Password == pwd ? fileKeys : new Helpers.FileKeys(pwd);

        internal IXodStorageSerializer StorageSerializer
        {
            get => storageSerializer;
            set => storageSerializer = value ?? StorageSerializerFactory.Get(StorageFormat.Xml);
        }

        internal void BeginBatch()
        {
            lock (syncLock)
            {
                batchDepth++;
            }
        }

        // The one way a batch's end counts (EndBatch, EndBatchUnwritten, DiscardBatch): the depth goes down, and only the
        // outermost end acts. Under syncLock.
        private bool OutermostBatchEndsUnlocked()
        {
            if (batchDepth > 0)
                batchDepth--;
            return batchDepth == 0;
        }

        // Files and pages whose write wasn't made wait for the next flush in the buffer's sets: after a write that
        // failed (FlushPending), and a batch ended unwritten (EndBatchUnwritten). Under syncLock.
        private void WaitInBufferUnlocked(IEnumerable<XFile> files, IEnumerable<string> rawPages)
        {
            dirtyBufferedFiles.PutBack(files);
            dirtyBufferedRawPages.UnionWith(rawPages);
        }

        internal void EndBatch()
        {
            lock (syncLock)
            {
                if (OutermostBatchEndsUnlocked())
                    FlushPending(dirtyBatchFiles, dirtyBatchRawPages, batchDeletes);
            }
        }

        /// <summary>
        /// True for a data file this context has deleted and whose delete waits for the write of the files that stop
        /// naming it (ISS-166, ISS-196). It is still on disk, for every other context, until then; for this context it
        /// is gone, and each read of the disk asks here first.
        /// </summary>
        internal bool DeletedHere(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            lock (pendingDeletesLock)
            {
                if (batchDeletes.Count == 0 && bufferedDeletes.Count == 0)
                    return false;
                string name = Path.GetFileName(fileName);
                return batchDeletes.Contains(name) || bufferedDeletes.Contains(name);
            }
        }

        /// <summary>A file on disk as this context sees it: there, and not deleted by it (<see cref="DeletedHere"/>).</summary>
        internal bool OnDiskHere(string fullPath) => !DeletedHere(fullPath) && File.Exists(fullPath);

        // Forgets the deletes of one set, or of both (null): they never reach the disk.
        private void ForgetDeletes(HashSet<string> deletes)
        {
            lock (pendingDeletesLock)
            {
                if (deletes == null)
                {
                    batchDeletes.Clear();
                    bufferedDeletes.Clear();
                }
                else
                {
                    deletes.Clear();
                }
            }
        }

        private bool HasDeletes(HashSet<string> deletes)
        {
            lock (pendingDeletesLock)
            {
                return deletes.Count > 0;
            }
        }

        /// <summary>
        /// ISS-196: the one order in which this context's writes reach the disk, for every writer (a statement's end, a
        /// batch's or a commit's, a buffer's flush): pages first, then the tables and the other files, then the deletes
        /// waiting with them (<paramref name="deletes"/>), tables before pages. A page is on disk before any table on
        /// disk names it, and leaves only after the last table that named it is written without it. A crash between
        /// two writes leaves at most a page no table names, which no read reaches (a read finds a table's pages by
        /// the table), and no table naming a page that isn't there. It was tables first, then pages, and a page a
        /// delete emptied, or a dropped table's pages, went at once. The deletes are made only once every write has
        /// succeeded: when one fails they stay, to be made with the writes or forgotten with them
        /// (<see cref="DiscardPendingWrites"/>). A file deleted and then made again (a table dropped and made anew)
        /// isn't deleted: its write replaces it, and deleted first, its name could be refused to the write while
        /// another program still had the old file open (Windows without POSIX delete, where a deleted file keeps its
        /// name until the last handle closes: DeleteFileW). In a commit, the journal holds each file's bytes, and a
        /// write or a delete that fails fails the commit, whose undo puts them all back. Under syncLock.
        /// </summary>
        private void WriteInOrderUnlocked(List<XFile> files, List<string> rawPages, HashSet<string> deletes)
        {
            var pages = new List<XFile>();
            var others = new List<XFile>();
            foreach (var file in files)
                (IsPage(file.Path) ? pages : others).Add(file);

            if (pages.Count > 0)
                FlushFilesList(pages);
            if (rawPages.Count > 0)
                FlushRawPagesList(rawPages);
            if (others.Count > 0)
                FlushFilesList(others);

            List<string> names;
            lock (pendingDeletesLock)
            {
                // A file made again since its delete, and now written, replaces the file the delete was for: no delete
                // of it waits any more, in either set (a table dropped in the buffer and written again by a batch). Only
                // a write counts: a file only held here, such as a table an insert that failed made in memory before
                // its row was refused, never replaced anything, and the old file goes with its pages (review of
                // ISS-196: it was kept, and named the pages deleted beside it).
                foreach (var file in files)
                {
                    string name = Path.GetFileName(file.Path);
                    batchDeletes.Remove(name);
                    bufferedDeletes.Remove(name);
                }
                foreach (string name in rawPages)
                {
                    batchDeletes.Remove(name);
                    bufferedDeletes.Remove(name);
                }
                if (deletes.Count == 0)
                    return;
                names = deletes.ToList();
            }
            foreach (string name in names.OrderBy(n => IsPage(n) ? 1 : 0))
            {
                // Made again and waiting for another writer: the delete waits too, cancelled by that write, or made
                // after it if the write is discarded.
                if (dirtyBatchFiles.Contains(name) || dirtyBufferedFiles.Contains(name) ||
                    dirtyBatchRawPages.Contains(name) || dirtyBufferedRawPages.Contains(name))
                    continue;
                string fullPath = Path.Combine(root, name);
                FaultInjection.Step("file-deleting", file: name);
                File.Delete(fullPath);
                ChangedOnDisk(fullPath);
                lock (pendingDeletesLock)
                {
                    deletes.Remove(name);
                }
            }
        }

        private static bool IsPage(string fileName) => fileName.EndsWith(".xpag", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Writes what waits in a batch or in the buffer. When a write fails, whatever this call took stays to be
        /// written, in the buffer (a batch is over by then): the next flush writes it (XmlEngine.FlushToDisk: Flush,
        /// the next transaction, the close), a rollback drops it (DiscardPendingWrites). ISS-098: the sets were
        /// emptied before the writes, so a write that failed was never made, though the context went on showing it.
        /// Writing again a file that was written is harmless: each is written whole, or from where its last write
        /// that succeeded ended (FlushRawPageToDisk). Called under syncLock.
        /// </summary>
        private void FlushPending(PendingFiles files, HashSet<string> rawPages, HashSet<string> deletes)
        {
            if (files.Count == 0 && rawPages.Count == 0 && !HasDeletes(deletes))
                return;
            var toFlush = files.ToList();
            var rawToFlush = rawPages.ToList();
            files.Clear();
            rawPages.Clear();
            try
            {
                WriteInOrderUnlocked(toFlush, rawToFlush, deletes);
            }
            catch
            {
                WaitInBufferUnlocked(toFlush, rawToFlush);
                throw;
            }
            saved.Written(toFlush, rawToFlush);
        }

        // The statement under way in direct mode (ISS-125), null otherwise: its data-file writes note the bytes they
        // replace (Capture). One statement at a time: every write statement holds the engine-wide write lock.
        private StatementSavepoint savepoint;
        // The statement under way has changed what this context holds (a document, a page, a hint, a delete): set by
        // each of those, under syncLock. A statement refused before it changed anything (a key already used) has
        // nothing to forget, and the cache stays.
        private bool statementChanged;

        // Called under syncLock, by each change to what this context holds of a file.
        private void StatementChangesUnlocked()
        {
            if (savepoint != null)
                statementChanged = true;
        }

        /// <summary>
        /// The statement under way in direct mode has changed what this context holds: an index built from the pages
        /// now holds its changes (IndexService). False outside a statement, and in a transaction or buffered mode.
        /// </summary>
        internal bool StatementChangedAnything
        {
            get
            {
                lock (syncLock)
                {
                    return savepoint != null && statementChanged;
                }
            }
        }

        /// <summary>
        /// A write statement starts: in direct mode (no transaction, no batch, no buffer) every data file it writes is
        /// noted first, so its failure can put them back (<see cref="UndoStatement"/>). False in any other mode, where
        /// nothing reaches the disk before a commit or a flush, and the statement's own undo applies (ISS-062).
        /// </summary>
        internal bool BeginStatement()
        {
            lock (syncLock)
            {
                if (bufferedDurability || batchDepth > 0 || (HasActiveTransaction != null && HasActiveTransaction()))
                    return false;
                savepoint = new StatementSavepoint();
                statementChanged = false;
                return true;
            }
        }

        /// <summary>The statement succeeded: what it noted is let go.</summary>
        internal void EndStatement()
        {
            StatementSavepoint done;
            lock (syncLock)
            {
                done = savepoint;
                savepoint = null;
            }
            done?.Release();
        }

        /// <summary>
        /// The statement failed: every data file it wrote is put back as it found it, then everything this context
        /// holds is forgotten, written or not, as after a rollback (<see cref="DiscardPendingWrites"/>): the cached
        /// pages and table files it changed, and its saves still to write. The disk is what counts. Forgotten even when
        /// putting a file back fails, which throws: the caller then refuses every write (XmlEngine, ISS-091). Nothing
        /// is forgotten when the statement changed nothing this context holds.
        /// </summary>
        internal void UndoStatement()
        {
            StatementSavepoint undo;
            bool changed;
            lock (syncLock)
            {
                undo = savepoint;
                changed = statementChanged;
                savepoint = null;
                statementChanged = false;
            }
            try
            {
                undo?.Undo();
            }
            finally
            {
                if (changed)
                    DiscardPendingWrites();
            }
        }

        /// <summary>
        /// The one report that a file on disk has changed, or may have from here (a write in place has opened it):
        /// after a whole file's rename into place, a delete, or the open of a write in place, never for a write that
        /// failed before any of those. To the statement under way (its undo puts back only such files: one whose write
        /// failed before changing it, another program holding it, is left alone, and putting it back would fail too)
        /// and to the transaction committing (<see cref="FileChangedOnDisk"/>). May run on pool threads.
        /// </summary>
        private void ChangedOnDisk(string fullPath)
        {
            savepoint?.Changed(fullPath);
            FileChangedOnDisk?.Invoke(fullPath);
        }

        // Not an index file or the database file (StatementSavepoint says why).
        private StatementSavepoint Noting(string fullPath)
        {
            var noting = savepoint;
            return noting == null || IsIndex(fullPath) || Helpers.DatabaseFiles.IsDatabaseFile(fullPath) ? null : noting;
        }

        // Before a write of a data file that changes it from `from` to its end (0: the whole file, or its delete), when
        // what the context holds of it was noted as it changed (KeepRawPage) or isn't known: read from the file then.
        private void Capture(string fullPath, long from) => Noting(fullPath)?.BeforeWrite(fullPath, from);

        // Before a document is written whole: its bytes as last read or written (XFile.DiskBytes).
        private void CaptureWhole(XFile file) => Noting(file.Path)?.KeepWhole(file.Path, file.DiskBytes);

        // Before a cached page changes from `from` on: its cached bytes are the file's below every change still to be
        // written, so they are what is on disk from `from` when each such change is the statement's own (at or above
        // what it noted). A change from before the statement still to be written (a flush that failed): the file is read.
        private void KeepRawPage(string fullPath, RawPageBuffer rawPage, long from)
        {
            var noting = Noting(fullPath);
            if (noting == null || rawPage?.Bytes == null || rawPage.IsNewFile)
                return; // a page not on disk yet is noted at its write, as a file the statement made
            long synced = rawPage.DiskSyncedLength;
            if (rawPage.DirtyStartOffset < synced && !noting.Covers(fullPath, rawPage.DirtyStartOffset))
                noting.BeforeWrite(fullPath, from);
            else
                noting.Keep(fullPath, from, synced, rawPage.Bytes);
        }

        /// <summary>
        /// ISS-099: in direct mode (no batch, no buffer) what a statement saved is written when the statement ends
        /// (XmlEngine.WriteStatement), after its index changes, not at each save in its middle: a write that failed
        /// there threw before the index was changed, and left the cache (and soon the disk) ahead of the index: the
        /// row on its page, its key not in the index. Each is written, and only then no longer pending; one that fails
        /// stays, to be written at the next statement's end or flush, and the call that ended the statement throws.
        /// </summary>
        internal void WriteSaved()
        {
            lock (syncLock)
            {
                if (IsBatchOrBuffered)
                    return;
                // Only what this thread's statement saved (StatementSaves), and its deletes: one write statement at a
                // time, so every delete waiting outside a batch is its own.
                var (files, rawPages) = saved.OfThisThread();
                if (files.Count == 0 && rawPages.Count == 0 && !HasDeletes(bufferedDeletes))
                    return;
                FaultInjection.Step("statement-saving");
                WriteInOrderUnlocked(files, rawPages, bufferedDeletes);
                saved.Written(files, rawPages);
                dirtyBufferedFiles.Written(files);
                dirtyBufferedRawPages.ExceptWith(rawPages);
            }
        }

        /// <summary>
        /// No write of this context waits: no batch is open, and nothing waits in a batch or in the buffer, a write
        /// that failed included (ISS-043: only then can its indexes be caught up with its pages, XmlEngine.FlushToDisk).
        /// </summary>
        internal bool NothingPending
        {
            get
            {
                lock (syncLock)
                {
                    return batchDepth == 0 &&
                        dirtyBatchFiles.Count == 0 && dirtyBufferedFiles.Count == 0 &&
                        dirtyBatchRawPages.Count == 0 && dirtyBufferedRawPages.Count == 0 &&
                        !HasDeletes(batchDeletes) && !HasDeletes(bufferedDeletes);
                }
            }
        }

        /// <summary>
        /// The batch's statement failed (ISS-177): the batch ends and nothing of it is written. What it holds waits as a
        /// write still to make, in the buffer's sets, as after a batch write that failed (<see cref="FlushPending"/>): in
        /// direct mode the statement's undo forgets it with the rest (<see cref="UndoStatement"/>); in buffered mode the
        /// statement's own undo takes its rows back in those files, and the next flush writes them. Inside an outer batch
        /// (a transaction's) only the depth goes down: the outer batch keeps it all. Its deletes wait with its writes, in
        /// the buffer's set (ISS-196).
        /// </summary>
        internal void EndBatchUnwritten()
        {
            lock (syncLock)
            {
                if (OutermostBatchEndsUnlocked())
                {
                    WaitInBufferUnlocked(dirtyBatchFiles.ToList(), dirtyBatchRawPages);
                    dirtyBatchFiles.Clear();
                    dirtyBatchRawPages.Clear();
                    // Its deletes wait with its writes (ISS-196).
                    lock (pendingDeletesLock)
                    {
                        bufferedDeletes.UnionWith(batchDeletes);
                        batchDeletes.Clear();
                    }
                }
            }
        }

        internal void DiscardBatch()
        {
            lock (syncLock)
            {
                if (OutermostBatchEndsUnlocked())
                {
                    dirtyBatchFiles.Clear();
                    dirtyBatchRawPages.Clear();
                    // Its deletes are forgotten: they never reached the disk (ISS-166).
                    ForgetDeletes(batchDeletes);
                }
            }
        }

        internal void FlushBufferedFiles()
        {
            lock (syncLock)
            {
                // The buffer's writes, and the deletes waiting with them; not a batch's (a transaction's), which wait
                // for its end.
                FlushPending(dirtyBufferedFiles, dirtyBufferedRawPages, bufferedDeletes);
            }
        }

        /// <summary>
        /// Drops what this context caches of the files, but never a write it hasn't made yet: a file or page that
        /// waits in a batch or the buffer stays cached, since a later read must see it and a later write must
        /// change it, not a copy read again from disk. <see cref="DiscardPendingWrites"/> drops those.
        /// </summary>
        internal void ClearCurrentCache()
        {
            lock (syncLock)
            {
                foreach (string name in cachedRawPages.Keys.ToList())
                {
                    if (dirtyBatchRawPages.Contains(name) || dirtyBufferedRawPages.Contains(name))
                        continue;
                    cachedRawPages.Remove(name); // its bytes stay with it (RawPageBuffer.ReleasePooledBuffer)
                }
                foreach (string name in cachedFiles.Keys.ToList())
                {
                    var entry = cachedFiles[name];
                    if (dirtyBatchFiles.Contains(entry.File) || dirtyBufferedFiles.Contains(entry.File))
                        continue;
                    cachedFileBytes -= entry.CostBytes;
                    cachedFiles.Remove(name);
                }
            }
        }

        /// <summary>
        /// Forgets every write this context hasn't made yet (a batch's, the buffer's) and everything it caches,
        /// so none of it reaches the disk later: for a rollback, a failed commit, a close whose flush failed. The
        /// one place pending writes are dropped; a batch still open is ended first by its owner (DiscardBatch).
        /// </summary>
        internal void DiscardPendingWrites()
        {
            lock (syncLock)
            {
                dirtyBatchFiles.Clear();
                dirtyBufferedFiles.Clear();
                dirtyBatchRawPages.Clear();
                dirtyBufferedRawPages.Clear();
                saved.Clear();
                ForgetDeletes(null);
                ClearCurrentCache();
            }
        }

        internal void ClearAllCache()
        {
            ClearCurrentCache();
        }

        // The hold is let go even when the last flush fails: the context is gone either way, and a hold kept would
        // refuse every later open (PLAN-064) until the garbage collector ran.
        /// <summary>
        /// Lets go of the database. It writes nothing: the engine's close (XmlEngine.Dispose) is the one place that
        /// decides whether the close writes, and has flushed what it may; a context that writes nothing more writes
        /// nothing at its close (review of TASK-063: a refused context's buffer was written here, so the close threw
        /// and left the rest of the context undisposed). Writes still waiting (a last flush that failed, a refused
        /// context's buffer) are dropped, as at a crash.
        /// </summary>
        public void Dispose()
        {
            try
            {
                FaultInjection.Step("closing");
            }
            finally
            {
                DiscardPendingWrites();
                try
                {
                    CloseIndexesMarker();
                }
                finally
                {
                    ReleaseHold();
                }
            }
        }
    }
}
