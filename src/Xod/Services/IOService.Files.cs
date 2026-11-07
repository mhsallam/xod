using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xod.Engines.Serializers;
using Xod.Helpers;

namespace Xod.Services
{
    internal partial class IOService
    {
        private void RecoverInterruptedWrites()
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return;

            try
            {
                foreach (var tmpFile in Directory.GetFiles(root, "*.tmp"))
                {
                    string targetPath = tmpFile.Substring(0, tmpFile.Length - 4);
                    var tmpInfo = new FileInfo(tmpFile);
                    if (File.Exists(targetPath) || tmpInfo.Length == 0)
                    {
                        File.Delete(tmpFile);
                    }
                    else
                    {
                        File.Move(tmpFile, targetPath, overwrite: true);
                    }
                }

                XodTransaction.RecoverWalJournalIfPresent(root);
            }
            catch
            {
                // Ignore recovery cleanup errors if directory is read-only
            }
        }

        internal bool FileExists(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string normalizedName = Path.GetFileName(fileName);
            lock (syncLock)
            {
                if (cachedFiles.ContainsKey(normalizedName) || cachedRawPages.ContainsKey(normalizedName))
                    return true;
            }
            string fullPath = Path.IsPathRooted(fileName) ? fileName : Path.Combine(root, fileName);
            return File.Exists(fullPath);
        }

        internal XFile OpenFileOrCreate<T>(string fileName, bool openOnly = false, bool preSave = false)
        {
            return OpenFileOrCreate(fileName, typeof(T), openOnly, preSave);
        }

        internal XFile OpenFileOrCreate(string fileName, Type type = null, bool openOnly = false, bool preSave = false)
        {
            string normalizedName = Path.GetFileName(fileName);
            string filePath = Path.IsPathRooted(fileName) ? fileName : Path.Combine(root, normalizedName);

            lock (syncLock)
            {
                if (cachedFiles.TryGetValue(normalizedName, out var item))
                {
                    item.LastCheckout = DateTime.UtcNow;
                    return item.File;
                }

                XFile file = null;
                if (cachedRawPages.TryGetValue(normalizedName, out var rawPage) && rawPage?.Bytes != null)
                {
                    cachedRawPages.Remove(normalizedName);
                    XDocument doc = storageSerializer.Deserialize(rawPage.ToExactArray());
                    rawPage.ReleasePooledBuffer();
                    file = new XFile(doc, type, filePath, password);
                    if (dirtyBatchRawPages.Remove(normalizedName))
                    {
                        dirtyBatchFiles.Add(file);
                    }
                    if (dirtyBufferedRawPages.Remove(normalizedName))
                    {
                        dirtyBufferedFiles.Add(file);
                    }
                }
                else if (File.Exists(filePath))
                {
                    file = Load(type, filePath, password);
                }
                else if (!openOnly && type != null)
                {
                    XDocument doc = ItemWriterDelegate(type);
                    file = new XFile(doc, type, filePath, password);
                    if (preSave)
                        Save(file);
                }

                if (file != null)
                {
                    CacheFile(normalizedName, file, filePath, FileCacheStatus.Locked);
                    return file;
                }
            }

            return null;
        }

        private static long SafeFileLength(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }

        /// <summary>
        /// What a parsed page costs in memory. On disk a Binary page is compressed, so its file length
        /// understates what it occupies once expanded; use the page size as the floor for that format.
        /// </summary>
        private long EstimateCacheCost(XFile file, string filePath)
        {
            // No file I/O here: this runs on the save/load path for every page. A page that has not
            // been saved yet is assumed to occupy a full page, which is the honest upper bound.
            long cost = file.LastSavedSize > 0 ? file.LastSavedSize : 512L * 1024;
            if (storageSerializer.Format == StorageFormat.Binary)
                cost = Math.Max(cost, 512L * 1024);
            return cost;
        }

        /// <summary>
        /// The one place a parsed page enters the cache. Evicts least-recently-used pages until both the
        /// entry cap and the byte budget fit, so no call site can apply a different policy.
        /// </summary>
        private void CacheFile(string name, XFile file, string filePath, FileCacheStatus status)
        {
            long cost = EstimateCacheCost(file, filePath);

            while (cachedFiles.Count >= CACHE_SIZE
                   || (cachedFiles.Count > 0 && cachedFileBytes + cost > CACHE_BYTES))
            {
                if (!EvictLeastActiveCacheEntry())
                    break;
            }

            cachedFiles[name] = new FileCache
            {
                File = file,
                FileName = name,
                LastCheckout = DateTime.UtcNow,
                Status = status,
                CostBytes = cost
            };
            cachedFileBytes += cost;
        }

        private bool EvictLeastActiveCacheEntry()
        {
            string oldestKey = null;
            DateTime oldestTime = DateTime.MaxValue;

            foreach (var kvp in cachedFiles)
            {
                if (dirtyBatchFiles.Contains(kvp.Value.File) || dirtyBufferedFiles.Contains(kvp.Value.File))
                    continue;

                if (kvp.Value.LastCheckout < oldestTime)
                {
                    oldestTime = kvp.Value.LastCheckout;
                    oldestKey = kvp.Key;
                }
            }

            if (oldestKey == null)
                return false;

            cachedFileBytes -= cachedFiles[oldestKey].CostBytes;
            cachedFiles.Remove(oldestKey);
            return true;
        }

        internal Type GetItemType(string referenceCode)
        {
            if (string.IsNullOrEmpty(referenceCode)) return null;
            string[] codeParts = referenceCode.Split('.');
            if (codeParts.Length == 2)
                return GetPageType(codeParts[0]);
            return null;
        }

        internal Type GetPageType(string xpagCode)
        {
            if (!Directory.Exists(root)) return null;
            string[] files = Directory.GetFiles(root, "*.xtab");
            foreach (var file in files)
            {
                XFile tableFile = OpenFileOrCreate(Path.GetFileName(file), typeof(Table), openOnly: true);
                if (tableFile != null && tableFile.Pages().Any(s => s.Attribute("file")?.Value == xpagCode + ".xpag"))
                {
                    string typeString = Path.GetFileNameWithoutExtension(file);
                    return propertyService.RegisteredType(typeString);
                }
            }
            return null;
        }

        internal string GetItemPage(Type type, string code)
        {
            XFile tableFile = OpenFileOrCreate(type.FullName + ".xtab", typeof(Table), openOnly: true);
            if (tableFile == null) return null;

            foreach (var file in tableFile.Pages().Select(s => s.Attribute("file")?.Value).Where(f => f != null))
            {
                XFile pageFile = OpenFileOrCreate(file, typeof(TablePage), openOnly: true);
                if (pageFile?.GetRowByCode(code) != null)
                {
                    return Path.GetFileNameWithoutExtension(file);
                }
            }
            return null;
        }

        internal void Save(XFile file)
        {
            if (file?.Document == null)
                return;

            lock (syncLock)
            {
                string normalizedName = Path.GetFileName(file.Path);
                if (cachedRawPages.TryGetValue(normalizedName, out var oldRawPage))
                {
                    cachedRawPages.Remove(normalizedName);
                    oldRawPage?.ReleasePooledBuffer();
                }
                if (!cachedFiles.TryGetValue(normalizedName, out var item))
                {
                    CacheFile(normalizedName, file, file.Path, FileCacheStatus.Shared);
                }
                else
                {
                    long previous = item.CostBytes;
                    item.File = file;
                    item.LastCheckout = DateTime.UtcNow;
                    item.Status = FileCacheStatus.Shared;
                    item.CostBytes = EstimateCacheCost(file, file.Path);
                    cachedFileBytes += item.CostBytes - previous;
                }

                if (batchDepth > 0)
                {
                    BeforeFileMutate?.Invoke(file.Path);
                    dirtyBatchFiles.Add(file);
                    return;
                }

                if (bufferedDurability)
                {
                    dirtyBufferedFiles.Add(file);
                    return;
                }

                FlushFileToDisk(file);
            }
        }

        private void FlushFileToDisk(XFile file)
        {
            BeforeFileMutate?.Invoke(file.Path);
            FlushFileToDiskCore(file);
        }

        private void FlushFileToDiskCore(XFile file)
        {
            byte[] serializedBytes = storageSerializer.Serialize(file.Document);
            byte[] payloadBytes = !string.IsNullOrEmpty(password)
                ? FileCryptoHelper.EncryptBytes(serializedBytes, password)
                : serializedBytes;

            file.LastSavedSize = payloadBytes.Length;

            string tempPath = file.Path + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.None))
            {
                fs.Write(payloadBytes, 0, payloadBytes.Length);
                fs.Flush(flushToDisk: false);
            }

            File.Move(tempPath, file.Path, overwrite: true);
        }

        private void FlushFilesList(List<XFile> toFlush)
        {
            if (toFlush.Count < 4)
            {
                for (int i = 0; i < toFlush.Count; i++)
                {
                    FlushFileToDisk(toFlush[i]);
                }
                return;
            }

            if (BeforeFileMutate != null)
            {
                for (int i = 0; i < toFlush.Count; i++)
                {
                    BeforeFileMutate.Invoke(toFlush[i].Path);
                }
            }

            System.Threading.Tasks.Parallel.ForEach(toFlush, FlushFileToDiskCore);
        }

        internal void FlushFullPageInBatch(XFile pageFile)
        {
            if (pageFile == null)
                return;

            lock (syncLock)
            {
                if (batchDepth > 0 && dirtyBatchFiles.Remove(pageFile))
                {
                    FlushFileToDisk(pageFile);
                }
            }
        }

        internal XFile Load(Type type, string filePath, string pwd = null)
        {
            if (!File.Exists(filePath))
                return null;

            lock (syncLock)
            {
                return LoadUnlocked(type, filePath, pwd);
            }
        }

        private XFile LoadUnlocked(Type type, string filePath, string pwd)
        {
            byte[] rawBytes = File.ReadAllBytes(filePath);
            if (!string.IsNullOrEmpty(pwd))
            {
                rawBytes = FileCryptoHelper.DecryptBytes(rawBytes, pwd);
            }
            else if (IsEncryptedPayload(rawBytes))
            {
                throw new SecurityException();
            }

            try
            {
                XDocument doc = storageSerializer.Deserialize(rawBytes);
                return new XFile(doc, type, filePath, pwd);
            }
            catch (Exception ex) when (!(ex is XodException))
            {
                if (!string.IsNullOrEmpty(pwd))
                    throw new SecurityException();
                throw new DatabaseFileException($"Failed to parse database file '{filePath}': {ex.Message}");
            }
        }

        internal XFile OpenPageOrCreate(XFile table) => OpenPageOrCreate(table, out _, out _);

        internal XFile OpenPageOrCreate(XFile table, out bool tableModified) => OpenPageOrCreate(table, out tableModified, out _);

        /// <summary>
        /// The page a new row of a table goes to: the tail page while it has room, else the first page that
        /// has, else null (the caller then adds one with <see cref="AddPageElement"/>). Both write paths ask
        /// here (PLAN-041).
        ///
        /// The tail is read through <see cref="XContainer.LastNode"/>, a field LINQ to XML keeps for the
        /// last child: O(1). <c>Elements("Page").LastOrDefault()</c> walks every page, which made each
        /// insert O(pages). Only when the tail is full are the pages scanned, once per new page.
        /// </summary>
        private static XElement FindAppendPage(XElement pagesRoot)
        {
            if (pagesRoot == null)
                return null;

            XElement tail = pagesRoot.LastNode as XElement;
            if (tail == null || tail.Name != PageElementName)
                tail = pagesRoot.Elements(PageElementName).LastOrDefault();

            if (tail != null && !IsFullPage(tail))
                return tail;

            return pagesRoot.Elements(PageElementName).FirstOrDefault(s => !IsFullPage(s));
        }

        private static bool IsFullPage(XElement page) => page.Attribute("full")?.Value == "true";

        private static XElement AddPageElement(XElement pagesRoot, out string pageFileName)
        {
            pageFileName = $"{ValueHelper.PickCode()}.xpag";
            var page = new XElement(PageElementName,
                new XAttribute("file", pageFileName),
                new XAttribute("full", false),
                new XAttribute("count", 0));
            pagesRoot.Add(page);
            return page;
        }

        private static readonly XName PageElementName = "Page";

        internal XFile OpenPageOrCreate(XFile table, out bool tableModified, out XElement pageElement)
        {
            lock (syncLock)
            {
                XElement pagesRoot = table.Root()?.Element("Pages");
                XElement page = FindAppendPage(pagesRoot);

                string pageFileName;
                if (page == null)
                {
                    page = AddPageElement(table.Root().Element("Pages"), out pageFileName);
                    tableModified = true;
                }
                else
                {
                    pageFileName = page.Attribute("file").Value;
                    tableModified = false;
                }

                pageElement = page;
                return OpenFileOrCreate(pageFileName, typeof(TablePage));
            }
        }

        internal void Delete(string filePath)
        {
            string fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(root, filePath);
            string normalizedName = Path.GetFileName(fullPath);

            lock (syncLock)
            {
                BeforeFileMutate?.Invoke(fullPath);
                dirtyBatchFiles.RemoveWhere(f => string.Equals(Path.GetFileName(f.Path), normalizedName, StringComparison.OrdinalIgnoreCase));
                dirtyBufferedFiles.RemoveWhere(f => string.Equals(Path.GetFileName(f.Path), normalizedName, StringComparison.OrdinalIgnoreCase));
                dirtyBatchRawPages.Remove(normalizedName);
                dirtyBufferedRawPages.Remove(normalizedName);
                cachedFiles.Remove(normalizedName);
                if (cachedRawPages.TryGetValue(normalizedName, out var oldRawPage))
                {
                    cachedRawPages.Remove(normalizedName);
                    oldRawPage?.ReleasePooledBuffer();
                }
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
        }

        internal long Size(string filePath)
        {
            string normalizedName = Path.GetFileName(filePath);
            lock (syncLock)
            {
                if (cachedRawPages.TryGetValue(normalizedName, out var rawPage) && rawPage != null)
                    return rawPage.Length;
            }
            if (File.Exists(filePath))
            {
                return new FileInfo(filePath).Length;
            }
            return 0;
        }
    }
}
