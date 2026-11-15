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
        /// <summary>
        /// Recovers what a crash left in the database folder <paramref name="root"/>: a password change, an atomic
        /// write of a database file, a commit. Only with the database to the caller (an exclusive hold):
        /// a context's open, and <see cref="XodMigrator"/> before it converts.
        /// </summary>
        internal static void RecoverInterruptedWrites(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return;

            // A password change that can't be finished stops the open: it would leave files under two passwords.
            PasswordSwitch.RecoverSwitch(root);

            try
            {
                foreach (var tmpFile in Directory.GetFiles(root, "*.tmp").Where(IsAtomicWriteLeft))
                {
                    string targetPath = tmpFile.Substring(0, tmpFile.Length - 4);
                    var tmpInfo = new FileInfo(tmpFile);
                    // A journal not yet renamed into place belongs to a commit that hadn't started: nothing of it
                    // was written. Made the journal, it would write its before-images back over whatever was
                    // written since (other contexts' writes, when the recovery waited for them to close).
                    bool journal = targetPath.EndsWith(".xwal", StringComparison.OrdinalIgnoreCase);
                    if (journal || File.Exists(targetPath) || tmpInfo.Length == 0)
                    {
                        File.Delete(tmpFile);
                    }
                    else
                    {
                        File.Move(tmpFile, targetPath, overwrite: true);
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // An atomic write cut short that can't be tidied now (a read-only folder, a file open elsewhere):
                // a .tmp beside an existing file is ignored by everything that reads the database, and this step
                // runs again at the next open.
            }

            // A commit that didn't finish and can't be undone stops the open (the journal stays): opening a commit
            // half done would let in writes that the journal would later undo.
            XodTransaction.RecoverWalJournalIfPresent(root);

            // After the journal, which puts back what a commit that got past it had deleted.
            PutBackSetAsideFiles(root);

            // Last: the indexes of a context that stopped before they caught up with its pages (ISS-043).
            DropIndexesLeftBehind(root);
        }

        /// <summary>
        /// ISS-107: the files a transaction set aside (deleted, <c>&lt;file&gt;.xdel</c>) that it didn't get to delete
        /// or put back. One whose file is there again (a journal's undo put it back) is deleted; one whose file isn't
        /// is put back: its transaction never committed (a commit deletes them before its journal goes). One that
        /// can't be put back stops the open, as a journal that can't be undone does: the index files still name its
        /// rows.
        /// </summary>
        private static void PutBackSetAsideFiles(string root)
        {
            foreach (string aside in Directory.GetFiles(root, "*" + SetAsideSuffix).Where(IsSetAsideLeft))
            {
                string target = aside.Substring(0, aside.Length - SetAsideSuffix.Length);
                try
                {
                    if (File.Exists(target))
                        File.Delete(aside);
                    else
                        File.Move(aside, target, overwrite: false);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    throw new XodException("A file a transaction deleted before a crash can't be put back (" + e.Message +
                        "). The database opens once it can be; " + Path.GetFileName(aside) + " is kept.", e);
                }
            }
        }

        /// <summary>The names of the index files in the database folder.</summary>
        internal string[] IndexFileNames() =>
            Directory.GetFiles(root, "*.xidx").Select(f => Path.GetFileName(f)).ToArray();

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
                    saved.RawPageBecameFile(normalizedName, file);
                }
                else if (File.Exists(filePath))
                {
                    file = Load(type, filePath, password);
                }
                else if (!openOnly && type != null)
                {
                    XDocument doc = ItemWriterDelegate(type, normalizedName);
                    file = new XFile(doc, type, filePath, password);
                    // A new database's file is on disk when the open returns, in every mode: another open must find
                    // it, and a record another context writes into it at once (an AES256 key, SaveNow) must not be
                    // overwritten later by this context's copy waiting to be written (review of TASK-068).
                    if (preSave)
                        SaveNow(file);
                }

                if (file != null)
                {
                    CacheFile(normalizedName, file, filePath, FileCacheStatus.Locked);
                    return file;
                }
            }

            return null;
        }

        /// <summary>
        /// A page as this context holds it, for reading only, without changing what is cached: its cached document, a
        /// document made from a copy of its cached bytes, or the file. For a walk of a table's pages for its index
        /// (IndexService.ForEachPageRow). Opened through <see cref="OpenFileOrCreate"/>, a page held as bytes is
        /// taken out of that cache, and a writer that had found a row's offset in them before the walk then wrote to a
        /// page no longer there (ISS-113). Null when the page doesn't exist.
        /// </summary>
        internal XFile PageForReading(string pageFileName)
        {
            string normalizedName = Path.GetFileName(pageFileName);
            string filePath = Path.Combine(root, normalizedName);
            lock (syncLock)
            {
                if (cachedFiles.TryGetValue(normalizedName, out var item))
                    return item.File;
                if (cachedRawPages.TryGetValue(normalizedName, out var rawPage) && rawPage?.Bytes != null)
                    return new XFile(storageSerializer.Deserialize(rawPage.ToExactArray()), typeof(TablePage), filePath, password);
                return File.Exists(filePath) ? LoadUnlocked(typeof(TablePage), filePath, password) : null;
            }
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
            Watch(file);
        }

        /// <summary>
        /// ISS-099: a cached file is pending (a write still to make) from the moment its document changes, not from
        /// its save: the engine changes a page or table in the cache before it saves it, and a statement that failed
        /// in between (or a direct write that failed) left the change in the cache and in no set, so a flush called
        /// the indexes caught up while the cache held rows the disk didn't. The document reports every change in its
        /// tree (XObject.Changed), so no caller has to remember to.
        /// </summary>
        private void Watch(XFile file)
        {
            if (file.Watched || file.Document == null)
                return;
            file.Watched = true;
            file.Document.Changed += (_, _) =>
            {
                lock (syncLock)
                {
                    if (!file.HintChanging)
                        FilePendingUnlocked(file);
                }
            };
        }

        /// <summary>
        /// Changes a cached file's hints without making it a write to make: a page's row count in the table file,
        /// exact on disk only as of the file's last write (PLAN-046 section 6, ISS-055), which an insert raises in
        /// the cache and readers don't need exact (only a full page's count is used, and the table file is saved when
        /// a page fills). It goes to disk with the file's next write. Every other change is a write to make (Watch).
        /// Made one, the count had each flush write the table file again: twice the time of an insert with a flush
        /// after each (PLAN-057 section 4).
        /// </summary>
        internal void ChangeHint(XFile file, Action change)
        {
            lock (syncLock)
            {
                bool outer = !file.HintChanging;
                file.HintChanging = true;
                try
                {
                    change();
                }
                finally
                {
                    if (outer)
                        file.HintChanging = false;
                }
            }
        }

        /// <summary>
        /// The one place a change to a cached file becomes a write still to make: the batch's while one is open (the
        /// mutation hook runs as it joins: a transaction keeps the file's bytes before it), else the set written by
        /// the next flush, or at once by the save in direct mode (<see cref="Save"/>). Raw pages: <see cref="RawPagePendingUnlocked"/>.
        /// </summary>
        private void FilePendingUnlocked(XFile file)
        {
            if (batchDepth > 0)
            {
                // The hook first (a transaction keeps the file's bytes before it), and the file joins the batch only
                // once that is done: a capture that failed (another program holds the file) used to leave it in the
                // batch with nothing to put back, and no later change tried again, so a commit undone after writing
                // it kept the transaction's rows (review of TASK-068). Now the next change tries again.
                if (!dirtyBatchFiles.Contains(file))
                {
                    BeforeFileMutate?.Invoke(file.Path);
                    dirtyBatchFiles.Add(file);
                }
                return;
            }
            dirtyBufferedFiles.Add(file);
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
                CacheSavedUnlocked(file);
                // Pending whether or not its document was watched when it changed (a file read without the cache).
                FilePendingUnlocked(file);
                // Direct: written when the statement ends (WriteSaved, ISS-099).
                if (!IsBatchOrBuffered)
                    saved.AddFile(file);
            }
        }

        /// <summary>
        /// Writes the database file now, outside any batch, buffer or transaction: for a record that must outlast
        /// whatever is in progress (an AES256 data key: a stored key no value uses is harmless, values whose key was
        /// never stored are lost), and a new database's file. It passes the mutation hook like every write (a password
        /// switch this context left pending is finished first, or its staged copy would later replace this write), and
        /// a transaction keeps no image of the database file, so its rollback or undo doesn't take the key back
        /// (TransactionService.CaptureBeforeMutate). Only once written is the document the cached one, and the file no
        /// longer waiting to be written: a write that fails changes nothing this context reads, so a key whose save
        /// failed isn't found in the cache by the next attempt and used unsaved (second review of ISS-150); a change
        /// that made the file wait is in this write (it used to be written again by the next flush, outside the hold
        /// the key was made under). The caller passes a document of its own, not the cached one, which this caches
        /// (PropertyKeyService.DatabaseFile). Like every write here, it replaces the file by a rename and doesn't flush
        /// it to the device.
        /// </summary>
        internal void SaveNow(XFile file)
        {
            if (file?.Document == null)
                return;

            lock (syncLock)
            {
                BeforeFileMutate?.Invoke(file.Path);
                FlushFileToDiskCore(file);
                CacheSavedUnlocked(file);
                dirtyBufferedFiles.Remove(file);
                dirtyBatchFiles.Remove(file);
            }
        }

        /// <summary>
        /// Writes a cache file now, or not at all: an index just built from the pages for a read
        /// (IndexService.SaveBuilt). A failure throws to the caller, which gives the save up, and nothing of it waits
        /// to be written: queued (Save), a file another program holds failed every later write and the close (review
        /// of TASK-068). It passes the mutation hook, as any index write does (it may hold keys of rows not yet
        /// written: NoteFileMutation), and is cached only once written. When a write of the file already waits, this
        /// document takes its place instead, to be written with the rest (Save): giving that up would drop what the
        /// waiting one carries.
        /// </summary>
        internal void SaveCacheNow(XFile file)
        {
            if (file?.Document == null)
                return;

            lock (syncLock)
            {
                if (dirtyBatchFiles.Contains(file) || dirtyBufferedFiles.Contains(file))
                {
                    Save(file);
                    return;
                }
                BeforeFileMutate?.Invoke(file.Path);
                FlushFileToDiskCore(file);
                CacheSavedUnlocked(file);
            }
        }

        /// <summary>The file as it is on disk now, unless this context holds unsaved changes to it (then
        /// those). For a record another context may have changed since this one cached it.</summary>
        internal XFile Reopen(string fileName, Type type)
        {
            string normalizedName = Path.GetFileName(fileName);
            lock (syncLock)
            {
                if (cachedFiles.TryGetValue(normalizedName, out var item) &&
                    !dirtyBatchFiles.Contains(item.File) && !dirtyBufferedFiles.Contains(item.File))
                {
                    cachedFiles.Remove(normalizedName);
                    cachedFileBytes -= item.CostBytes;
                }
                return OpenFileOrCreate(normalizedName, type, openOnly: true);
            }
        }

        /// <summary>Every page of the database as this context sees it: those on disk and those it holds
        /// that aren't written yet (buffered, or in a batch or transaction).</summary>
        internal IReadOnlyCollection<string> PageFileNames()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(root, "*.xpag"))
                names.Add(Path.GetFileName(path));
            lock (syncLock)
            {
                foreach (string name in cachedFiles.Keys.Concat(cachedRawPages.Keys))
                    if (name.EndsWith(".xpag", StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
            }
            return names;
        }

        /// <summary>The table files this context sees, as <see cref="PageFileNames"/> lists pages: on disk, and those
        /// it holds not written yet. For SQL's list of tables (PLAN-060).</summary>
        internal IReadOnlyCollection<string> TableFileNames()
        {
            // Ordinal: two names that differ only by case are two tables (where the file system allows it), and a
            // query naming either is ambiguous (XQ003), not silently one of them.
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(root, "*.xtab"))
                names.Add(Path.GetFileName(path));
            lock (syncLock)
            {
                foreach (string name in cachedFiles.Keys)
                    if (name.EndsWith(".xtab", StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
            }
            return names;
        }

        private void CacheSavedUnlocked(XFile file)
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
            WriteRawBytesAtomic(file.Path, payloadBytes, payloadBytes.Length);
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

        /// <summary>
        /// The page a new row of a table goes to: the tail page while it has room, else the first page that
        /// has, else null (the caller then names a new one and adds it once its row exists). Both write paths ask
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

        private static string NewPageFileName() => $"{ValueHelper.PickCode()}.xpag";

        private static XElement AddPageElement(XElement pagesRoot, string pageFileName)
        {
            var page = new XElement(PageElementName,
                new XAttribute("file", pageFileName),
                new XAttribute("full", false),
                new XAttribute("count", 0));
            pagesRoot.Add(page);
            return page;
        }

        private static readonly XName PageElementName = "Page";

        /// <summary>
        /// The page the document writer's next row goes to, chosen without changing the table: the file
        /// of a page with room (<paramref name="pageElement"/> is its element), or the name of a new page
        /// (<paramref name="pageElement"/> is null). The writer builds the row, which names its page, and
        /// only then registers a new page with <see cref="AddPage"/>, so a row that fails to build leaves
        /// the table as it was (ISS-061).
        /// </summary>
        internal string ChooseAppendPage(XFile table, out XElement pageElement)
        {
            lock (syncLock)
            {
                pageElement = FindAppendPage(table.Root()?.Element("Pages"));
                return pageElement != null ? pageElement.Attribute("file").Value : NewPageFileName();
            }
        }

        /// <summary>Registers a new page in the table; the caller saves the table.</summary>
        internal XElement AddPage(XFile table, string pageFileName)
        {
            lock (syncLock)
            {
                return AddPageElement(table.Root().Element("Pages"), pageFileName);
            }
        }

        internal void Delete(string filePath)
        {
            string fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(root, filePath);
            string normalizedName = Path.GetFileName(fullPath);

            lock (syncLock)
            {
                BeforeFileMutate?.Invoke(fullPath);
                dirtyBatchFiles.Remove(normalizedName);
                dirtyBufferedFiles.Remove(normalizedName);
                dirtyBatchRawPages.Remove(normalizedName);
                dirtyBufferedRawPages.Remove(normalizedName);
                saved.Remove(normalizedName);
                cachedFiles.Remove(normalizedName);
                if (cachedRawPages.TryGetValue(normalizedName, out var oldRawPage))
                {
                    cachedRawPages.Remove(normalizedName);
                    oldRawPage?.ReleasePooledBuffer();
                }
                if (File.Exists(fullPath))
                {
                    // ISS-107: inside a transaction the file is set aside, not deleted: its batch holds the rest of
                    // the transaction's changes (the table that listed this page), and a rollback, or a commit refused
                    // before its journal, puts nothing back but what it set aside. Deleted at once, a page a delete
                    // emptied was lost though nothing was committed.
                    if (batchDepth > 0 && HasActiveTransaction != null && HasActiveTransaction())
                    {
                        File.Move(fullPath, fullPath + SetAsideSuffix, overwrite: true);
                        setAside.Add(fullPath);
                    }
                    else
                    {
                        File.Delete(fullPath);
                    }
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
