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

        // ISS-107: the files a transaction deleted, set aside under "<file>.xdel" until it ends (full paths, Delete):
        // deleted once its commit has written its files (EndBatch), put back when it is rolled back or its commit is
        // refused (DiscardBatch), and by the next open alone after a crash (RecoverInterruptedWrites).
        internal const string SetAsideSuffix = ".xdel";
        private readonly List<string> setAside = new List<string>();

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
            bool lockDatabase = false,
            int lockRetryTimeoutMs = 0,
            bool bufferedDurability = false,
            bool creating = false,
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
                OpenAndRecover(lockDatabase, lockRetryTimeoutMs, creating, checkFolder);
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
            List<string> aside = null;
            lock (syncLock)
            {
                if (batchDepth > 0)
                    batchDepth--;

                if (batchDepth == 0)
                {
                    aside = TakeSetAside();
                    FlushPending(dirtyBatchFiles, dirtyBatchRawPages);
                }
            }

            // Once every file of the batch is written: until then its commit can still be undone, from its journal,
            // which holds these files' bytes. One that can't be deleted fails the commit, which is then undone; one
            // left by a failed write is deleted by the next open alone, its file put back by the journal.
            DeleteSetAside(aside);
        }

        private List<string> TakeSetAside()
        {
            if (setAside.Count == 0)
                return null;
            var taken = setAside.ToList();
            setAside.Clear();
            return taken;
        }

        private static void DeleteSetAside(List<string> aside)
        {
            if (aside == null)
                return;
            Exception first = null;
            foreach (string file in aside)
            {
                try
                {
                    File.Delete(file + SetAsideSuffix);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    first ??= e;
                }
            }
            if (first != null)
                throw new IOException("A file the transaction deleted couldn't be deleted at its commit: " + first.Message, first);
        }

        // Each put back where it was; one that can't be is left for the next open alone (RecoverInterruptedWrites).
        private static void PutBackSetAside(List<string> aside)
        {
            if (aside == null)
                return;
            Exception first = null;
            foreach (string file in aside)
            {
                try
                {
                    File.Move(file + SetAsideSuffix, file, overwrite: false);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    first ??= e;
                }
            }
            if (first != null)
                throw new IOException("A file the transaction deleted couldn't be put back: " + first.Message, first);
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
                if (toFlush.Count > 0)
                    FlushFilesList(toFlush);
                if (rawToFlush.Count > 0)
                    FlushRawPagesList(rawToFlush);
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
                if (files.Count > 0)
                    FlushFilesList(files);
                if (rawPages.Count > 0)
                    FlushRawPagesList(rawPages);
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
            List<string> aside = null;
            lock (syncLock)
            {
                if (batchDepth > 0)
                    batchDepth--;

                if (batchDepth == 0)
                {
                    dirtyBatchFiles.Clear();
                    dirtyBatchRawPages.Clear();
                    aside = TakeSetAside();
                }
            }

            // The files the batch deleted come back (ISS-107): nothing of it was committed.
            PutBackSetAside(aside);
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
                ClearCurrentCache();
            }
        }

        internal void ClearAllCache()
        {
            ClearCurrentCache();
        }

        // The hold is let go even when the last flush fails: the context is gone either way, and a hold kept would
        // refuse every later open with LockDatabase, and every password change, until the garbage collector ran.
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
                }
            }
        }
    }
}
