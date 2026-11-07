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
        private static readonly byte[] EmptyXmlPageBytes = System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><TablePage full=\"false\"><Rows></Rows></TablePage>");
        private static readonly byte[] XmlPageTrailerBytes = System.Text.Encoding.UTF8.GetBytes("</Rows></TablePage>");

        private readonly object syncLock = new object();
        private readonly Dictionary<string, FileCache> cachedFiles = new Dictionary<string, FileCache>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RawPageBuffer> cachedRawPages = new Dictionary<string, RawPageBuffer>(StringComparer.OrdinalIgnoreCase);
        private readonly List<System.Threading.Tasks.Task> pendingBatchWrites = new List<System.Threading.Tasks.Task>();

        private readonly string path;
        private readonly string root;
        private string password;
        private readonly PropertyService propertyService;
        private IXodStorageSerializer storageSerializer;
        private FileStream lockStream;
        private string lockFilePath;

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

            if (lockDatabase && !string.IsNullOrEmpty(this.root) && Directory.Exists(this.root))
            {
                AcquireDatabaseLock(lockRetryTimeoutMs);
            }

            RecoverInterruptedWrites();
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
            System.Threading.Tasks.Task[] tasksToWait = null;
            lock (syncLock)
            {
                if (batchDepth > 0)
                    batchDepth--;

                if (batchDepth == 0)
                {
                    if (pendingBatchWrites.Count > 0)
                    {
                        tasksToWait = pendingBatchWrites.ToArray();
                        pendingBatchWrites.Clear();
                    }
                    if (dirtyBatchFiles.Count > 0)
                    {
                        var toFlush = dirtyBatchFiles.ToList();
                        dirtyBatchFiles.Clear();
                        FlushFilesList(toFlush);
                    }
                    if (dirtyBatchRawPages.Count > 0)
                    {
                        var rawToFlush = dirtyBatchRawPages.ToList();
                        dirtyBatchRawPages.Clear();
                        FlushRawPagesList(rawToFlush);
                    }
                }
            }

            if (tasksToWait != null && tasksToWait.Length > 0)
            {
                System.Threading.Tasks.Task.WaitAll(tasksToWait);
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
                        tasksToWait = pendingBatchWrites.ToArray();
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
                if (dirtyBufferedFiles.Count > 0)
                {
                    var toFlush = dirtyBufferedFiles.ToList();
                    dirtyBufferedFiles.Clear();
                    FlushFilesList(toFlush);
                }
                if (dirtyBufferedRawPages.Count > 0)
                {
                    var rawToFlush = dirtyBufferedRawPages.ToList();
                    dirtyBufferedRawPages.Clear();
                    FlushRawPagesList(rawToFlush);
                }
            }
        }

        internal void ClearCurrentCache()
        {
            lock (syncLock)
            {
                foreach (var rawPage in cachedRawPages.Values)
                {
                    rawPage?.ReleasePooledBuffer();
                }
                cachedFiles.Clear();
                cachedRawPages.Clear();
                dirtyBatchRawPages.Clear();
                dirtyBufferedRawPages.Clear();
            }
        }

        internal void ClearAllCache()
        {
            ClearCurrentCache();
        }

        public void Dispose()
        {
            FlushBufferedFiles();
            ClearCurrentCache();
            if (lockStream != null)
            {
                try
                {
                    lockStream.Dispose();
                    lockStream = null;
                    if (!string.IsNullOrEmpty(lockFilePath) && File.Exists(lockFilePath))
                    {
                        File.Delete(lockFilePath);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
