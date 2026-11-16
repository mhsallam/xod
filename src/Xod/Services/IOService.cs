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

        // ISS-166: the data files this context's open transaction deleted, by name (Delete); never an index file, which
        // the index service deletes at the commit (IndexService.DeleteIndexFile). A transaction changes no data file
        // before its commit, deletes included: each stays on disk, whole, for every other context, and is gone for
        // this one alone (DeletedHere, asked by each of its reads of the disk). Its commit deletes them, holding the
        // commit gate exclusively, with their bytes in its journal (EndBatch); a rollback, or a commit refused before
        // its journal, forgets them (DiscardBatch, DiscardPendingWrites). Before, they were renamed aside at once
        // ("<file>.xdel", ISS-107): another context then saw a dropped table gone and made a new one at its fixed name,
        // the rollback couldn't put the old one back over it, and the next open dropped the old one with its rows. An
        // older version's crash can still leave such a file; the open puts it back (PutBackSetAsideFiles).
        internal const string SetAsideSuffix = ".xdel";
        private readonly HashSet<string> pendingDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Its own lock: pages are read from disk on worker threads (the prefetches in IOService.RawPages) while the
        // thread that started them may hold syncLock.
        private readonly object pendingDeletesLock = new object();

        // The document of a file this service creates: its type's, for the file of that name (a new table file
        // records its rows' schema: XmlEngine.NewFileDocument).
        internal Func<Type, string, XDocument> ItemWriterDelegate { get; set; }
        internal Action<string> BeforeFileMutate { get; set; }
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
            this.propertyService = propertyService;
            this.storageSerializer = StorageSerializerFactory.Get(storageFormat);
            this.bufferedDurability = bufferedDurability;

            if (!string.IsNullOrEmpty(this.root) && Directory.Exists(this.root))
            {
                OpenAndRecover(lockRetryTimeoutMs, checkFolder);
            }
            else
            {
                noFolderAtOpen = true;
            }
        }

        internal string RootDirectory => root;
        internal bool IsBatchOrBuffered => batchDepth > 0 || bufferedDurability;

        internal string Password
        {
            get => password;
            set => password = value;
        }

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

        internal void EndBatch()
        {
            lock (syncLock)
            {
                if (batchDepth > 0)
                    batchDepth--;

                if (batchDepth == 0)
                {
                    // The deletes, then the writes. A file the transaction deleted and then made again (a table
                    // dropped and made anew) isn't deleted: the flush replaces it. Deleted first, its name could be
                    // refused to the write while another program still had the old file open (Windows without POSIX
                    // delete, where a deleted file keeps its name until the last handle closes: DeleteFileW). Both
                    // are undone from the commit's journal if either fails.
                    DeletePending(TakePendingDeletes());
                    FlushPending(dirtyBatchFiles, dirtyBatchRawPages);
                }
            }
        }

        /// <summary>
        /// ISS-166: true for a file this context's open transaction deleted. It is still on disk, for every other
        /// context, until the commit; for this context it is gone, and each read of the disk asks here first.
        /// </summary>
        internal bool DeletedHere(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            lock (pendingDeletesLock)
            {
                return pendingDeletes.Count > 0 && pendingDeletes.Contains(Path.GetFileName(fileName));
            }
        }

        /// <summary>A file on disk as this context sees it: there, and not deleted by its open transaction.</summary>
        internal bool OnDiskHere(string fullPath) => !DeletedHere(fullPath) && File.Exists(fullPath);

        private List<string> TakePendingDeletes()
        {
            lock (pendingDeletesLock)
            {
                if (pendingDeletes.Count == 0)
                    return null;
                var taken = pendingDeletes.ToList();
                pendingDeletes.Clear();
                return taken;
            }
        }

        // The commit's deletes, holding the commit gate exclusively (RequireCommitGate asks), after its journal, which
        // holds each file's bytes: one that fails fails the commit, and its undo writes back every file it deleted. A
        // file the batch writes again is left to that write (EndBatch). Data files only: an index file never waits here.
        private void DeletePending(List<string> names)
        {
            if (names == null)
                return;
            foreach (string name in names)
            {
                if (dirtyBatchFiles.Contains(name) || dirtyBatchRawPages.Contains(name))
                    continue;
                string fullPath = Path.Combine(root, name);
                RequireCommitGate(fullPath);
                File.Delete(fullPath);
            }
        }

        /// <summary>
        /// Writes what waits in a batch or in the buffer. When a write fails, whatever this call took stays to be
        /// written, in the buffer (a batch is over by then): the next flush writes it (XmlEngine.FlushToDisk: Flush,
        /// the next transaction, the close), a rollback drops it (DiscardPendingWrites). ISS-098: the sets were
        /// emptied before the writes, so a write that failed was never made, though the context went on showing it.
        /// Writing again a file that was written is harmless: each is written whole, or from where its last write
        /// that succeeded ended (FlushRawPageToDisk). Called under syncLock.
        /// </summary>
        private void FlushPending(PendingFiles files, HashSet<string> rawPages)
        {
            if (files.Count == 0 && rawPages.Count == 0)
                return;
            var toFlush = files.ToList();
            var rawToFlush = rawPages.ToList();
            files.Clear();
            rawPages.Clear();
            try
            {
                InCommitGate(() =>
                {
                    if (toFlush.Count > 0)
                        FlushFilesList(toFlush);
                    if (rawToFlush.Count > 0)
                        FlushRawPagesList(rawToFlush);
                });
            }
            catch
            {
                dirtyBufferedFiles.PutBack(toFlush);
                dirtyBufferedRawPages.UnionWith(rawToFlush);
                throw;
            }
            saved.Written(toFlush, rawToFlush);
        }

        /// <summary>
        /// The statement on this thread failed: what it saved and didn't write waits for the next flush (or the next
        /// statement that saves it) rather than for its end, where a write failing again would replace the failure
        /// the caller should see (ISS-120: a page another program holds failed the statement, then its end).
        /// </summary>
        internal void LeaveSavedToFlush()
        {
            lock (syncLock)
            {
                saved.LeaveThisThreadsToFlush();
            }
        }

        /// <summary>
        /// ISS-099: in direct mode (no batch, no buffer) what a statement saved is written when the statement ends
        /// (XmlEngine.ExitTableWriteLock), after its index changes, not at each save in its middle: a write that failed
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
                // Only what this thread's statement saved (StatementSaves).
                var (files, rawPages) = saved.OfThisThread();
                if (files.Count == 0 && rawPages.Count == 0)
                    return;
                FaultInjection.Step("statement-saving");
                InCommitGate(() =>
                {
                    if (files.Count > 0)
                        FlushFilesList(files);
                    if (rawPages.Count > 0)
                        FlushRawPagesList(rawPages);
                });
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
                        dirtyBatchRawPages.Count == 0 && dirtyBufferedRawPages.Count == 0;
                }
            }
        }

        internal void DiscardBatch()
        {
            lock (syncLock)
            {
                if (batchDepth > 0)
                    batchDepth--;

                if (batchDepth == 0)
                {
                    dirtyBatchFiles.Clear();
                    dirtyBatchRawPages.Clear();
                    // Its deletes are forgotten: they never reached the disk (ISS-166).
                    TakePendingDeletes();
                }
            }
        }

        internal void FlushBufferedFiles()
        {
            lock (syncLock)
            {
                FlushPending(dirtyBufferedFiles, dirtyBufferedRawPages);
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
                    cachedRawPages[name]?.ReleasePooledBuffer();
                    cachedRawPages.Remove(name);
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
                TakePendingDeletes();
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
                    ReleaseCreationGate(); // a creation that failed before ShareHoldOnceCreated
                    ReleaseCommitGate();
                }
            }
        }
    }
}
