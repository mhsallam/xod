using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Xod.Engines.Xml
{
    public partial class XmlEngine
    {
        public DatabaseStatistics GetStatistics()
        {
            // The engine-wide write lock: the flush writes files (the counters' marks among them), which a read lock
            // doesn't cover (review of TASK-063: another thread's write to a table could run beside it).
            EnterExclusive();
            try
            {
                FlushToDisk();
                var stats = new DatabaseStatistics
                {
                    DatabasePath = path,
                    StorageFormat = StorageFormat,
                    IsEncrypted = !string.IsNullOrEmpty(password)
                };

                if (!Directory.Exists(root))
                    return stats;

                // A file this context's open transaction deleted is gone for it, though still on disk (ISS-166).
                foreach (var f in Directory.GetFiles(root).Where(f => !ioService.DeletedHere(f)))
                {
                    stats.TotalSizeBytes += new FileInfo(f).Length;
                }

                var xtabFiles = Directory.GetFiles(root, "*.xtab").Where(f => !ioService.DeletedHere(f)).ToArray();
                stats.TableCount = xtabFiles.Length;

                foreach (var xtabPath in xtabFiles)
                {
                    string tableFileName = System.IO.Path.GetFileName(xtabPath);
                    string typeName = System.IO.Path.GetFileNameWithoutExtension(xtabPath);
                    var tableFile = ioService.OpenFileOrCreate(tableFileName, typeof(Table), openOnly: true);
                    if (tableFile == null) continue;

                    var tableStat = new TableStatistics
                    {
                        TableName = typeName,
                        SizeBytes = new FileInfo(xtabPath).Length
                    };

                    var regType = propertyService.RegisteredType(typeName);
                    if (regType != null)
                    {
                        tableStat.IndexedPropertyCount = propertyService.Properties(regType.FullName)
                            .Count(p => p.IsIndexed || p.IsPrimaryKey || p.IsUnique);
                    }

                    foreach (var pageElm in tableFile.Pages())
                    {
                        string pageFileName = pageElm.Attribute("file")?.Value;
                        if (string.IsNullOrEmpty(pageFileName))
                            continue;
                        ioService.RequirePage(tableFile, pageFileName);

                        tableStat.PageCount++;
                        stats.TotalPageCount++;

                        string fullPagePath = System.IO.Path.Combine(root, pageFileName);
                        if (ioService.OnDiskHere(fullPagePath))
                            tableStat.SizeBytes += new FileInfo(fullPagePath).Length;

                        if (pageElm.Attribute("full")?.Value == "true" &&
                            int.TryParse(pageElm.Attribute("count")?.Value, out int cachedRowsInPage) &&
                            cachedRowsInPage >= 0)
                        {
                            tableStat.RowCount += cachedRowsInPage;
                            stats.TotalRowCount += cachedRowsInPage;
                        }
                        else
                        {
                            var pageFile = ioService.OpenFileOrCreate<TablePage>(pageFileName, openOnly: true);
                            if (pageFile != null)
                            {
                                int rowsInPage = pageFile.Rows().Count();
                                tableStat.RowCount += rowsInPage;
                                stats.TotalRowCount += rowsInPage;
                            }
                        }
                    }

                    stats.Tables.Add(tableStat);
                }

                return stats;
            }
            finally
            {
                ExitExclusive();
            }
        }

        public void ChangePassword(string currentPassword, string newPassword) => SwitchPassword(
            () =>
            {
                passwordSwitch.CheckCurrentPassword(currentPassword);
                passwordSwitch.CheckCanSecure(newPassword, replacingCurrent: true);
            },
            Helpers.CryptoHelper.GetSHA256HashData(newPassword));

        public void Loose(string pwd) => SwitchPassword(
            () => passwordSwitch.CheckCurrentPassword(pwd),
            null);

        public void Secure(string pwd) => SwitchPassword(
            () => passwordSwitch.CheckCanSecure(pwd),
            Helpers.CryptoHelper.GetSHA256HashData(pwd));

        // The one way the password changes (the public XodSecurityService comes here through a context): the
        // checks, the AES256 key, then the files (all or nothing, PasswordSwitch), then this context follows the
        // password the files are under.
        private void SwitchPassword(Action check, string newPassword)
        {
            EnterExclusive();
            try
            {
                // A rollback would restore files as they were before the switch, under the old password.
                if (activeTransaction != null && activeTransaction.IsActive)
                    throw new TransactionException("A database password can't be set, changed or removed inside a transaction.");
                // Nor inside a write (a trigger): the switch rewrites every file itself, and the write's undo, if it
                // failed, would put its files back as they were before the switch, under the old password.
                if (globalWriteDepth > 0)
                    throw new InvalidOperationException("A database password can't be set, changed or removed inside a write (a " +
                        "trigger, for example): change it once the write has returned.");
                check();

                // No other context may have the database open: one would go on writing under the old password.
                ioService.WithDatabaseToItself(() =>
                {
                    // A change this context made earlier that couldn't replace every file is finished before anything
                    // below reads the files, and what a crash left refuses this one, before anything below changes
                    // (the AES256 key's protections are written first).
                    passwordSwitch.CheckCanSwitch();
                    // Everything this context holds goes to disk first: the switch rewrites the files on disk,
                    // and nothing may be written over them afterwards that the switch didn't see.
                    FlushToDisk();
                    propertyKeys.PasswordChanging(password, newPassword);
                    try
                    {
                        passwordSwitch.SwitchTo(newPassword);
                    }
                    finally
                    {
                        // The files' password is the security service's: the old one if the switch failed before
                        // its commit, the new one from the commit on (even when a file is replaced only at the
                        // next open).
                        password = passwordSwitch.Password;
                        ioService.Password = password;
                        ClearCache();
                    }
                    // Only after a finished switch: removes the key's protection by the old password. A switch
                    // that failed leaves at most a protection by the password it tried (inside the database file,
                    // under the password that still opens it); the next finished change removes that too.
                    propertyKeys.PasswordChanged();
                });
            }
            finally
            {
                ExitExclusive();
            }
        }

        public IEnumerable<Type> RegisteredTypes()
        {
            // A read like any other (ISS-175): the tables a transaction of another flow has dropped are gone only at its
            // commit, so it waits for that transaction rather than see its drops (DeletedHere) before then.
            EnterWithTurn(EnterRead, ExitRead);
            try
            {
                var types = new List<Type>();
                if (!Directory.Exists(root)) return types;

                // The tables this context sees, one not written yet included (in buffered mode before a flush, or in
                // a transaction that made it): the table files on disk alone missed those, and DropAll kept them.
                foreach (var file in ioService.TableFileNames())
                {
                    string typeName = System.IO.Path.GetFileNameWithoutExtension(file);
                    Type t = propertyService.RegisteredType(typeName) ?? Type.GetType(typeName);
                    if (t != null)
                        types.Add(t);
                }
                return types;
            }
            finally
            {
                try
                {
                    ExitRead();
                }
                finally
                {
                    GiveTurn();
                }
            }
        }

        public void RegisterType(Type type)
        {
            propertyService.LoadType(type);
        }

        public void Flush()
        {
            EnterExclusive();
            try
            {
                FlushToDisk();
            }
            finally
            {
                ExitExclusive();
            }
        }

        /// <summary>
        /// Writes everything this context holds to disk: buffered files, then the indexes. Under a password the
        /// index service saves an index through the buffered files, so those are flushed again after it; without
        /// one it writes the index file itself. Pages go first: the index is a cache of them.
        /// <para>
        /// ISS-043: its indexes on disk then hold every row its pages do, and the marker that said otherwise is
        /// emptied, only if nothing of this context can be behind: no write under way (this thread holds the
        /// engine-wide write lock, which every writer's lock excludes, and is inside none of its own writes: a flush
        /// from a trigger comes in the middle of a statement, ISS-101), no batch open (a transaction's pages wait in
        /// its batch), and nothing either service still has to write (a write that failed stays to be made). Decided
        /// from that state, not by the caller: callers that said so were wrong after a failed save and inside a
        /// transaction (review of TASK-064).
        /// </para>
        /// </summary>
        private void FlushToDisk()
        {
            if (autonumberMarksPending)
                RecordCountersInMarks();
            ioService.FlushBufferedFiles();
            indexService.FlushDirtyIndexes();
            ioService.FlushBufferedFiles();
            if (NoWriteUnderWay && ioService.NothingPending)
                ioService.PagesSettled(); // ISS-209: see WriteStatement
            if (NoWriteUnderWay && ioService.NothingPending && indexService.AllSaved)
                ioService.IndexesCaughtUp();
        }

        public void ClearCache() => DropCaches();

        public void ClearCaches() => DropCaches();

        /// <summary>
        /// Drops what this context caches. Outside a transaction its writes go to disk first, and the indexes are
        /// dropped too (loaded again when needed). Inside one, its writes and the indexes stay: they hold the
        /// transaction's changes, which a dropped cache used to lose, in part.
        /// </summary>
        private void DropCaches()
        {
            EnterExclusive();
            try
            {
                bool inTransaction = activeTransaction != null && activeTransaction.IsActive;
                if (!inTransaction)
                    FlushToDisk();
                ioService.ClearCurrentCache();
                if (!inTransaction)
                    indexService.ClearAll();
            }
            finally
            {
                ExitExclusive();
            }
        }

        /// <summary>
        /// The one place this context forgets what it holds, writes still pending included: after a rollback or
        /// a failed commit the disk is what counts, and nothing of the transaction may be shown or written later.
        /// </summary>
        // Everything but the autonumber counters: a number handed out is gone whatever becomes of its row, since
        // the caller holds it (the insert gave it back), so a rollback mustn't hand it out again (ISS-083). The
        // counters only rise, but for a drop's reset, which a rollback takes back (AutonumberService.RaiseTo);
        // their highs reach the tables' marks at the next flush (RecordCountersInMarks).
        private void ForgetEverythingHeld()
        {
            ioService.DiscardPendingWrites();
            indexService.ClearAll();
            propertyKeys.Reset();
        }

        // Set by a rollback (or an undone commit): numbers it handed out are held only by the counters now.
        private bool autonumberMarksPending;

        /// <summary>
        /// The counters' highs into the tables' marks (PLAN-046 section 6), after a rollback took away the rows
        /// that held the numbers it handed out: only a mark keeps them from being handed out after a reopen. Run
        /// by the next flush (the next transaction, Flush, or the close); a crash before then loses it, as it
        /// loses any write still waiting.
        /// </summary>
        private void RecordCountersInMarks()
        {
            foreach (var type in autonumberService.CountedTypes())
            {
                string tableFileName = $"{type.FullName}.xtab";
                // A table the rollback took away entirely keeps its mark in a table with no pages, as an emptied
                // table does.
                XFile tableFile = ioService.OpenFileOrCreate(tableFileName, typeof(Table), openOnly: true)
                    ?? ioService.OpenFileOrCreate(tableFileName, typeof(Table));
                if (tableFile != null && RaiseAutonumberMarks(type, tableFile, null))
                    ioService.Save(tableFile);
            }
            autonumberMarksPending = false;
        }
    }
}
