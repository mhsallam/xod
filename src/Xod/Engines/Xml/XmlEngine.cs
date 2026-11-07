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
        private readonly IXodSecurityService securityService;
        private readonly PropertyService propertyService;
        private readonly AutonumberService autonumberService;
        private readonly ItemsCacheService itemsCacheService;
        private readonly IndexService indexService;
        private readonly LogService logService;
        private readonly ExceptionService exceptionService;
        private XodTransaction activeTransaction;
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

            string resolvedPath = null;
            string resolvedRoot = null;

            string ext = System.IO.Path.GetExtension(file)?.ToLowerInvariant();
            if (ext == ".xod" || ext == ".xodj" || ext == ".xodb")
            {
                resolvedPath = System.IO.Path.GetFullPath(file);
                resolvedRoot = System.IO.Path.GetDirectoryName(resolvedPath);
            }
            else
            {
                resolvedRoot = System.IO.Path.GetFullPath(file);
            }

            if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
            {
                if (Directory.Exists(resolvedRoot))
                {
                    string[] files = Directory.GetFiles(resolvedRoot, "*.xod*", SearchOption.TopDirectoryOnly);
                    if (files.Length > 0 && string.IsNullOrEmpty(resolvedPath))
                        resolvedPath = files[0];
                }

                if (string.IsNullOrEmpty(resolvedPath))
                {
                    resolvedPath = System.IO.Path.Combine(resolvedRoot, "data.xod");
                }

                if (!File.Exists(resolvedPath) && this.options.InitialCreate)
                {
                    Directory.CreateDirectory(resolvedRoot);
                    IsNew = true;
                }
            }

            this.path = resolvedPath;
            this.root = resolvedRoot;

            this.propertyService = new PropertyService();
            this.propertyService.LoadType(typeof(Database));
            this.propertyService.LoadType(typeof(Table));

            if (!string.IsNullOrEmpty(password))
                this.password = CryptoHelper.GetSHA256HashData(password);

            this.exceptionService = new ExceptionService(path);
            this.securityService = new XodSecurityService(path, this.password);
            this.ioService = new IOService(
                path,
                this.password,
                this.propertyService,
                this.StorageFormat,
                this.options.LockDatabase,
                this.options.LockRetryTimeoutMs,
                this.options.Durability == DurabilityMode.Buffered);
            this.ioService.BeforeFileMutate = f => activeTransaction?.CaptureBeforeMutate(f);
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

            string databaseFile = System.IO.Path.GetFileName(path);
            XFile xodFile = this.ioService.OpenFileOrCreate<Database>(databaseFile, !this.options.InitialCreate, this.options.InitialCreate);
            if (xodFile == null)
                exceptionService.Throw(new DatabaseFileException());
            else
                this.path = xodFile.Path;

            this.itemsCacheService = new ItemsCacheService(path);
            this.indexService = new IndexService(this.propertyService, this.ioService);

            this.autonumberService = new AutonumberService(
                path,
                this.propertyService,
                this.ioService,
                this.exceptionService)
            {
                FindLastDelegate = t => Last(t)
            };

            this.logService = new LogService(root);
            this.LazyLoad = this.options.LazyLoad;
            this.LazyLoadParent = this.options.LazyLoadParent;
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

                    // The index now holds every row on the pages: whole, so it may be saved.
                    indexService.MarkTypeHydrated(type);
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

        private void EnterTableReadLock(Type type, out ReaderWriterLockSlim tableLock)
        {
            rwLock.EnterReadLock();
            if (IsStandaloneTable(type))
            {
                tableLock = GetTableLock(type);
                tableLock.EnterReadLock();
            }
            else
            {
                tableLock = null;
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

        private void EnterTableWriteLock(Type type, out ReaderWriterLockSlim tableLock, out bool usedGlobalWriteLock)
        {
            if (BeforeAction == null && AfterAction == null &&
                (activeTransaction == null || !activeTransaction.IsActive) &&
                IsStandaloneTable(type))
            {
                usedGlobalWriteLock = false;
                rwLock.EnterReadLock();
                tableLock = GetTableLock(type);
                tableLock.EnterWriteLock();
            }
            else
            {
                usedGlobalWriteLock = true;
                rwLock.EnterWriteLock();
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
                    rwLock.ExitWriteLock();
                else
                    rwLock.ExitReadLock();
            }
        }

        public void Dispose()
        {
            try
            {
                if (activeTransaction != null && activeTransaction.IsActive)
                {
                    activeTransaction.Rollback();
                }
                else
                {
                    ioService?.FlushBufferedFiles();
                    indexService?.FlushDirtyIndexes();
                    indexService?.FoldLargeLogs();
                }
            }
            finally
            {
                // A failed flush or rollback still surfaces to the caller, but files and locks are released.
                DisposeResources();
            }
        }

        private void DisposeResources()
        {
            itemsCacheService?.Dispose();
            ioService?.Dispose();
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
