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
        /// Recovers what a crash left in the folder of the database whose file is <paramref name="databaseFile"/>: a
        /// password change, an atomic write of a file of the database, a commit. Only with the database to the caller (an
        /// exclusive hold): a context's open, and <see cref="XodMigrator"/> before it converts.
        /// </summary>
        internal static void RecoverInterruptedWrites(string databaseFile)
        {
            Helpers.DatabaseFiles.RequireDatabaseFile(databaseFile);
            string root = Path.GetDirectoryName(databaseFile);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return;

            // A password change that can't be finished stops the open: it would leave files under two passwords.
            PasswordSwitch.RecoverSwitch(databaseFile);

            try
            {
                // Deleted, whether its file is there or not (ISS-134). Every writer moves its .tmp over its file, which
                // is never missing in between, so a .tmp left belongs to a write that hadn't finished, and nothing says
                // how far it got: one put in place of a missing file was half a page or a table cut short, which every
                // read of it failed on. A file never moved into place is as if its write hadn't started: its statement
                // didn't finish, and a commit's journal (below) undoes the rest. A journal's own .tmp: its commit hadn't
                // started.
                // Every file, filtered as WriteLeft filters (a glob's case rule is the platform's, and missed Foo.xtab.TMP
                // that WriteLeft reported).
                foreach (var tmpFile in Directory.GetFiles(root).Where(f => Helpers.DatabaseFiles.IsAtomicWrite(f, databaseFile)))
                    File.Delete(tmpFile);
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

            // After the journal, which puts back what a commit that got past it had deleted. Only an older version's
            // transaction leaves files set aside (ISS-166).
            PutBackSetAsideFiles(databaseFile);

            // Last: the indexes of a context that stopped before they caught up with its pages (ISS-043).
            DropIndexesLeftBehind(root);
        }

        /// <summary>
        /// ISS-209: deletes the pages a crash left that no table names. A page reaches the disk before any table names
        /// it and leaves only after the last table naming it is written without it (<see cref="WriteInOrderUnlocked"/>,
        /// ISS-196), so a crash in between leaves a page no table names: a table's first page written before its table
        /// file, a page a delete emptied or a dropped table's page not yet deleted after its table was written. No read
        /// of its table reaches it; a stored reference naming a row on it does (GetRawData), and reads as gone once the
        /// page is deleted, as a reference to any row that is gone does (ISS-210). Nothing else removed it.
        /// <para>
        /// Only a page the crashed context named in its marker (<see cref="NotePageInFlight"/>, which keeps a page's
        /// record only while the crash could leave it unnamed) is deleted, and only if no table names it now: "no table
        /// names it" alone isn't a crash's mark, since a table file can be missing for another reason (a copy, a sync or
        /// a restore not finished), and its pages are then kept. Each marker read is deleted once its pages are dealt
        /// with, whether or not they all could be: kept, it would leave every index file untrusted at every open (the
        /// review of TASK-104), for a page that only wastes space. A page left so stays, unnamed, as before ISS-209.
        /// </para>
        /// <para>
        /// Run by the open (XmlEngine), with the database held alone and before this context writes anything: after
        /// the recovery (<see cref="RecoverInterruptedWrites"/>, which may put a journal's tables back, and keeps these
        /// markers for this step) and once the database file has been read, since a table file is read with the
        /// database's password and format. Not by a context without a hold (<see cref="NoHoldReason"/>), and the
        /// markers aren't kept where the file system takes no locks (<see cref="KeptForTheSweep"/>). Every table file is
        /// read first; one that can't be read (damaged, or written by something else) names pages no one can know, so
        /// then no page is deleted, and the read of that table reports it. A page that can't be deleted now (held by
        /// another program) stays. Deleted as the other recovery steps delete what a crash left, straight from the disk:
        /// nothing of this context holds or names it, so no statement, savepoint or index takes part.
        /// </para>
        /// </summary>
        internal void DeleteUnnamedPages()
        {
            if (NoHoldReason != null || string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return;
            var markers = MarkersNamingPages();
            if (markers.Count == 0)
                return;

            HashSet<string> named = PagesTheTablesName();
            foreach (var (marker, pages) in markers)
            {
                // Null: a table couldn't be read, so no page is known to be unnamed (see the summary).
                foreach (string name in named == null ? Array.Empty<string>() : pages)
                {
                    string page = Path.Combine(root, name);
                    if (named.Contains(name) || !File.Exists(page))
                        continue;
                    try
                    {
                        FaultInjection.Step("unnamed-page-deleting", file: name);
                        File.Delete(page);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        // Held by another program: it stays, unnamed (see the summary).
                    }
                }
                try
                {
                    File.Delete(marker);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // Stays, and the next open reads it again; meanwhile the indexes are rebuilt from the pages.
                }
            }
        }

        // The pages the table files on disk name, or null when one of them can't be read (DeleteUnnamedPages).
        private HashSet<string> PagesTheTablesName()
        {
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string table in Directory.GetFiles(root, "*.xtab"))
            {
                XFile tableFile;
                try
                {
                    tableFile = OpenFileOrCreate(Path.GetFileName(table), typeof(Table), openOnly: true);
                }
                catch (Exception e) when (e is XodException || e is IOException || e is UnauthorizedAccessException || e is System.Xml.XmlException)
                {
                    return null;
                }
                if (tableFile?.Root() == null)
                    return null;
                foreach (string page in tableFile.Pages().Select(p => p.Attribute("file")?.Value).Where(f => !string.IsNullOrEmpty(f)))
                    named.Add(page);
            }
            return named;
        }

        /// <summary>
        /// ISS-107: the files a transaction of an older version set aside (deleted, <c>&lt;file&gt;.xdel</c>) and didn't
        /// get to delete or put back before a crash; since ISS-166 a transaction deletes nothing before its commit and
        /// sets nothing aside. One whose file is there again (a journal's undo put it back) is deleted; one whose file
        /// isn't is put back: its transaction never committed (a commit deleted them before its journal went). Its
        /// file there again may also be a table another context made at its fixed name, and the rows set aside are
        /// then lost: the bug ISS-166 removed for transactions of this version. One that can't be put back stops the
        /// open, as a journal that can't be undone does: the index files still name its rows.
        /// </summary>
        private static void PutBackSetAsideFiles(string databaseFile)
        {
            foreach (string aside in Directory.GetFiles(Path.GetDirectoryName(databaseFile)).Where(f => Helpers.DatabaseFiles.IsSetAside(f, databaseFile)))
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

        /// <summary>
        /// ISS-196: the one rule for a page a table names, for every read that walks a table's pages: it is there, as
        /// this context holds it (cached, or on disk and not deleted here), or the table is damaged, a
        /// <see cref="DatabaseFileException"/> naming both. A read skipped it, and the table read as fewer rows than it
        /// holds (fewer than <c>Count</c> said), with no error. This context's writes put a page on disk before any
        /// table names it and take it away after (<see cref="WriteInOrderUnlocked"/>, and a failed statement's undo), so
        /// a missing page was deleted or lost outside this context: by another program, or, for a context that holds
        /// nothing and opened for reading only, by a writer beside it that changed the table while it read
        /// (<c>ISS-173</c>).
        /// </summary>
        internal void RequirePage(XFile table, string pageFileName)
        {
            if (FileExists(pageFileName))
                return;
            string tableName = table?.Path != null ? Path.GetFileName(table.Path) : "its table file";
            throw new DatabaseFileException("The table file '" + tableName + "' names the page '" + Path.GetFileName(pageFileName) +
                "', which isn't in the database folder: the page file was deleted or lost, or another program changed the table while " +
                "this context read it. If reopening the database doesn't clear it, restore the folder from a backup.");
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
            return OnDiskHere(fullPath);
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
                    byte[] exact = rawPage.ToExactArray();
                    XDocument doc = storageSerializer.Deserialize(exact);
                    // Its bytes are the file's when nothing of it waits to be written. The page keeps them: another
                    // reader may hold it (RawPageBuffer.ReleasePooledBuffer).
                    bool asOnDisk = !rawPage.IsNewFile && rawPage.DirtyStartOffset >= rawPage.DiskSyncedLength && rawPage.Length == rawPage.DiskSyncedLength;
                    file = new XFile(doc, type, filePath, Password) { DiskBytes = asOnDisk ? exact : null };
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
                else if (OnDiskHere(filePath))
                {
                    file = Load(type, filePath);
                }
                else if (!openOnly && type != null)
                {
                    StatementChangesUnlocked();
                    XDocument doc = ItemWriterDelegate(type, normalizedName);
                    file = new XFile(doc, type, filePath, Password);
                    // A new database's file is on disk when the open returns, in every mode: the next open must find
                    // it, and a record written into it at once (an AES256 key, SaveNow) must not be overwritten later
                    // by a copy waiting to be written (review of TASK-068).
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
                    return new XFile(storageSerializer.Deserialize(rawPage.ToExactArray()), typeof(TablePage), filePath, Password);
                return OnDiskHere(filePath) ? LoadUnlocked(typeof(TablePage), filePath, FileKeys) : null;
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
            cost += file.DiskBytes?.Length ?? 0;
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
                StatementChangesUnlocked();
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
            StatementChangesUnlocked();
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

        // The table that lists a page, among the tables this context sees (TableFileNames): one not written yet
        // included. It looked at the table files on disk only, so in buffered mode a row of a table not flushed yet
        // had no type, and an update kept the rows it dropped, a cascade through an object property the rows it named.
        internal Type GetPageType(string xpagCode)
        {
            string table = PageTableName(xpagCode);
            return table == null ? null : propertyService.RegisteredType(Path.GetFileNameWithoutExtension(table));
        }

        /// <summary>The table file that lists a page (see <see cref="GetPageType"/>), or null when no table does: the
        /// page's rows are gone (its table dropped, its last row deleted) or were never named (ISS-209).</summary>
        internal string PageTableName(string xpagCode)
        {
            if (!Directory.Exists(root)) return null;
            foreach (var file in TableFileNames())
            {
                XFile tableFile = OpenFileOrCreate(file, typeof(Table), openOnly: true);
                if (tableFile != null && tableFile.Pages().Any(s => s.Attribute("file")?.Value == xpagCode + ".xpag"))
                    return file;
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
        /// those). For a record read afresh (the AES256 key's, PropertyKeyService).</summary>
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
                if (!DeletedHere(path))
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
        /// it holds not written yet. Every list of the tables goes through here: SQL's (PLAN-060), the registered types
        /// (DropAll) and the table of a page (PLAN-076).</summary>
        internal IReadOnlyCollection<string> TableFileNames()
        {
            // Ordinal: two names that differ only by case are two tables (where the file system allows it), and a
            // query naming either is ambiguous (XQ003), not silently one of them.
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(root, "*.xtab"))
                if (!DeletedHere(path))
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
            cachedRawPages.Remove(normalizedName); // its bytes stay with it (RawPageBuffer.ReleasePooledBuffer)
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

        /// <summary>
        /// Refuses a write of a data file while this context's transaction is open. A transaction's files are written
        /// by its commit alone (its batch is written once the transaction has completed, its deletes too: ISS-166), so before that nothing of it is on disk, and its rollback has nothing
        /// to take back (XodTransaction.Rollback). Index files and the database file are outside every journal and
        /// aren't asked.
        /// </summary>
        private void RefuseWriteInsideTransaction(string filePath)
        {
            if (IsIndex(filePath) || Helpers.DatabaseFiles.IsDatabaseFile(filePath))
                return;
            if (HasActiveTransaction != null && HasActiveTransaction())
                throw new InvalidOperationException("A write of '" + filePath + "' while its context's transaction is open: a transaction's " +
                    "files are written by its commit alone.");
        }

        private void FlushFileToDiskCore(XFile file)
        {
            RefuseWriteInsideTransaction(file.Path);
            byte[] serializedBytes = storageSerializer.Serialize(file.Document);
            byte[] payloadBytes = FileKeys is { } keys
                ? keys.Encrypt(serializedBytes)
                : serializedBytes;

            file.LastSavedSize = payloadBytes.Length;
            CaptureWhole(file);
            FaultInjection.Step("file-write", file: System.IO.Path.GetFileName(file.Path));
            WriteRawBytesAtomic(file.Path, payloadBytes, payloadBytes.Length);
            file.DiskBytes = payloadBytes;
            ChangedOnDisk(file.Path);
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

            ParallelWrites(toFlush, FlushFileToDiskCore);
        }

        /// <summary>
        /// Writes a page document a batch filled before the batch ends (PLAN-019), outside a transaction: a document
        /// waiting in the batch can't leave the file cache, a written one can, so a large batch doesn't hold every
        /// page it filled (the table file, and a page a later change puts back in the batch, still wait). The write is
        /// this context's, under the lock, from the document it owns. The page leaves the batch only once written
        /// (ISS-098): when the write fails or the hook refuses it, the page still waits, and the exception fails the
        /// row's statement, whose undo changes the page again (ISS-129's review; the order is held by reading: no
        /// fault injection reaches a page write).
        /// The raw path has no such write: a raw page stays in the page cache once written, so writing it early
        /// saves no memory (IOService.RawPages, ISS-129).
        /// </summary>
        internal void FlushFullPageInBatch(XFile pageFile)
        {
            if (pageFile == null)
                return;

            lock (syncLock)
            {
                if (batchDepth > 0 && dirtyBatchFiles.Waits(pageFile))
                {
                    FlushFileToDisk(pageFile);
                    dirtyBatchFiles.Remove(pageFile);
                }
            }
        }

        internal XFile Load(Type type, string filePath)
        {
            if (!OnDiskHere(filePath))
                return null;

            lock (syncLock)
            {
                return LoadUnlocked(type, filePath, FileKeys);
            }
        }

        private XFile LoadUnlocked(Type type, string filePath, FileKeys keys)
        {
            byte[] diskBytes = File.ReadAllBytes(filePath);
            byte[] rawBytes = diskBytes;
            string pwd = keys?.Password;
            if (keys != null)
            {
                rawBytes = keys.Decrypt(rawBytes);
            }
            else if (IsEncryptedPayload(rawBytes))
            {
                throw new SecurityException();
            }

            try
            {
                XDocument doc = storageSerializer.Deserialize(rawBytes);
                return new XFile(doc, type, filePath, pwd) { DiskBytes = diskBytes };
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

        // A new page's name, noted in this context's marker before anything of it is written (ISS-209: NotePageInFlight).
        private string NewPageFileName()
        {
            string name = $"{ValueHelper.PickCode()}.xpag";
            NotePageInFlight(name);
            return name;
        }

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

            // An index file is a cache outside every journal, deleted by the index service alone, which inside a
            // transaction leaves it for the commit's index flush and handles one another reader holds
            // (IndexService.DeleteIndexFile, ISS-042). Among the commit's deletes, a held one failed the commit (review
            // of PLAN-063). Refused before anything changes, so a refusal leaves nothing half done.
            if (IsIndex(fullPath) && HasActiveTransaction != null && HasActiveTransaction())
                throw new InvalidOperationException("A delete of the index file '" + normalizedName + "' while its context's transaction " +
                    "is open: the index service deletes it at the commit.");

            lock (syncLock)
            {
                // A file that isn't on disk, such as the page of a statement being undone that never reached it: only
                // what this context holds changes. A file this context's transaction deleted already is one of these:
                // gone for it, its delete waiting for the commit (ISS-166). An index file is deleted as it is.
                DeleteUnlocked(fullPath, normalizedName, onDisk: IsIndex(fullPath) || OnDiskHere(fullPath));
            }
        }

        // Delete's work, under syncLock. onDisk false: only what this context holds is dropped, without the mutation
        // hook either: its checks (this context's own refusals) and its records (a transaction's before-image, the
        // indexes behind) are about a change on disk, and there is none.
        private void DeleteUnlocked(string fullPath, string normalizedName, bool onDisk)
        {
            if (onDisk)
                BeforeFileMutate?.Invoke(fullPath);
            StatementChangesUnlocked();
            bool inTransaction = HasActiveTransaction != null && HasActiveTransaction();
            if (onDisk && !(batchDepth > 0 && inTransaction) && File.Exists(fullPath))
            {
                // Deleted at once, below: a statement's savepoint notes the file first, from what this context holds.
                if (cachedRawPages.TryGetValue(normalizedName, out var heldPage) && heldPage?.Bytes != null)
                {
                    KeepRawPage(fullPath, heldPage, 0);
                    Capture(fullPath, 0); // a page made by the statement, or one it couldn't take from the cache
                }
                else if (cachedFiles.TryGetValue(normalizedName, out var heldFile))
                    CaptureWhole(heldFile.File);
                else
                    Capture(fullPath, 0);
            }
            dirtyBatchFiles.Remove(normalizedName);
            dirtyBufferedFiles.Remove(normalizedName);
            dirtyBatchRawPages.Remove(normalizedName);
            dirtyBufferedRawPages.Remove(normalizedName);
            saved.Remove(normalizedName);
            cachedFiles.Remove(normalizedName);
            cachedRawPages.Remove(normalizedName); // its bytes stay with it (RawPageBuffer.ReleasePooledBuffer)
            if (onDisk && File.Exists(fullPath))
            {
                // A data file waits for the write of the files that stop naming it, and is deleted after them
                // (WriteInOrderUnlocked, ISS-196); until then it stays on disk, gone for this context (DeletedHere).
                // Inside a transaction that is its commit (ISS-166: deleted at once, a page a delete emptied was lost
                // though nothing was committed, ISS-107; renamed aside, 2.0.2, a crash left a .xdel for the next open
                // to put back). Outside one, the batch's end, the buffer's flush or the statement's end: deleted at
                // once, a page went before the table that named it was written without it. An index file is deleted
                // as it is: a cache, which no table names. With no writer to come (no batch, no buffer, no
                // statement), deleted at once.
                bool waits = !IsIndex(fullPath) && (batchDepth > 0 || bufferedDurability || savepoint != null);
                if (waits)
                {
                    // ISS-209: until it is deleted, the table written without it no longer names it.
                    if (IsPage(normalizedName))
                        NotePageInFlight(normalizedName);
                    lock (pendingDeletesLock)
                    {
                        (batchDepth > 0 ? batchDeletes : bufferedDeletes).Add(normalizedName);
                    }
                }
                else
                {
                    FaultInjection.Step("file-deleting", file: normalizedName);
                    File.Delete(fullPath);
                    ChangedOnDisk(fullPath);
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
            if (OnDiskHere(filePath))
            {
                return new FileInfo(filePath).Length;
            }
            return 0;
        }
    }
}
