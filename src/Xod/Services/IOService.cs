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
        // Full pages a batch writes on their own while it goes on (IOService.RawPages), each with its page.
        private readonly List<(System.Threading.Tasks.Task Write, string Page, RawPageBuffer Buffer)> pendingBatchWrites =
            new List<(System.Threading.Tasks.Task Write, string Page, RawPageBuffer Buffer)>();

        private readonly string path;
        private readonly string root;
        private string password;
        private readonly PropertyService propertyService;
        private IXodStorageSerializer storageSerializer;
        private FileStream lockStream;

        private int batchDepth;
        private readonly bool bufferedDurability;
        private readonly HashSet<XFile> dirtyBatchFiles = new HashSet<XFile>();
        private readonly HashSet<XFile> dirtyBufferedFiles = new HashSet<XFile>();
        private readonly HashSet<string> dirtyBatchRawPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> dirtyBufferedRawPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal Func<Type, XDocument> ItemWriterDelegate { get; set; }
        internal Action<string> BeforeFileMutate { get; set; }
        internal Func<bool> HasActiveTransaction { get; set; }

        public IOService(
            string path,
            string password,
            PropertyService propertyService,
            StorageFormat storageFormat = StorageFormat.Xml,
            bool lockDatabase = false,
            int lockRetryTimeoutMs = 0,
            bool bufferedDurability = false)
        {
            this.path = path;
            this.root = Path.GetDirectoryName(this.path);
            this.password = password;
            this.propertyService = propertyService;
            this.storageSerializer = StorageSerializerFactory.Get(storageFormat);
            this.bufferedDurability = bufferedDurability;

            if (!string.IsNullOrEmpty(this.root) && Directory.Exists(this.root))
            {
                OpenAndRecover(lockDatabase, lockRetryTimeoutMs);
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
            List<(System.Threading.Tasks.Task Write, string Page, RawPageBuffer Buffer)> writes = null;
            try
            {
                lock (syncLock)
                {
                    if (batchDepth > 0)
                        batchDepth--;

                    if (batchDepth == 0)
                    {
                        if (pendingBatchWrites.Count > 0)
                        {
                            writes = pendingBatchWrites.ToList();
                            pendingBatchWrites.Clear();
                        }
                        FlushPending(dirtyBatchFiles, dirtyBatchRawPages);
                    }
                }
            }
            finally
            {
                // Waited for even when the writes above failed: one of these that failed is made again too.
                AwaitBatchWrites(writes);
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
        private void FlushPending(HashSet<XFile> files, HashSet<string> rawPages)
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
                dirtyBufferedFiles.UnionWith(toFlush);
                dirtyBufferedRawPages.UnionWith(rawToFlush);
                throw;
            }
        }

        // A full page a batch wrote on its own counted as written when its write began (IOService.RawPages); one
        // whose write failed is written whole by the next flush instead (ISS-098).
        private void AwaitBatchWrites(List<(System.Threading.Tasks.Task Write, string Page, RawPageBuffer Buffer)> writes)
        {
            if (writes == null || writes.Count == 0)
                return;
            try
            {
                System.Threading.Tasks.Task.WaitAll(writes.Select(w => w.Write).ToArray());
            }
            catch (AggregateException)
            {
                lock (syncLock)
                {
                    foreach (var w in writes.Where(w => !w.Write.IsCompletedSuccessfully))
                    {
                        w.Buffer.DiskSyncedLength = -1;
                        w.Buffer.DirtyStartOffset = 0;
                        dirtyBufferedRawPages.Add(w.Page);
                    }
                }
                throw;
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
                    return batchDepth == 0 && pendingBatchWrites.Count == 0 &&
                        dirtyBatchFiles.Count == 0 && dirtyBufferedFiles.Count == 0 &&
                        dirtyBatchRawPages.Count == 0 && dirtyBufferedRawPages.Count == 0;
                }
            }
        }

        internal void DiscardBatch()
        {
            System.Threading.Tasks.Task[] tasksToWait = null;
            lock (syncLock)
            {
                if (batchDepth > 0)
                    batchDepth--;

                if (batchDepth == 0)
                {
                    if (pendingBatchWrites.Count > 0)
                    {
                        tasksToWait = pendingBatchWrites.Select(w => w.Write).ToArray();
                        pendingBatchWrites.Clear();
                    }
                    dirtyBatchFiles.Clear();
                    dirtyBatchRawPages.Clear();
                }
            }

            if (tasksToWait != null && tasksToWait.Length > 0)
            {
                try { System.Threading.Tasks.Task.WaitAll(tasksToWait); } catch { }
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
                }
            }
        }
    }
}
