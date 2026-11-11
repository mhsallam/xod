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
            rwLock.EnterReadLock();
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

                foreach (var f in Directory.GetFiles(root))
                {
                    stats.TotalSizeBytes += new FileInfo(f).Length;
                }

                var xtabFiles = Directory.GetFiles(root, "*.xtab");
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
                        if (string.IsNullOrEmpty(pageFileName) || !ioService.FileExists(pageFileName))
                            continue;

                        tableStat.PageCount++;
                        stats.TotalPageCount++;

                        string fullPagePath = System.IO.Path.Combine(root, pageFileName);
                        if (File.Exists(fullPagePath))
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
                rwLock.ExitReadLock();
            }
        }

        public void ChangePassword(string currentPassword, string newPassword) => SwitchPassword(
            () =>
            {
                securityService.CheckCurrentPassword(currentPassword);
                securityService.CheckCanSecure(newPassword, replacingCurrent: true);
            },
            Helpers.CryptoHelper.GetSHA256HashData(newPassword),
            () => securityService.ChangePassword(currentPassword, newPassword));

        public void Loose(string pwd) => SwitchPassword(
            () => securityService.CheckCurrentPassword(pwd),
            null,
            () => securityService.Loose(pwd));

        public void Secure(string pwd) => SwitchPassword(
            () => securityService.CheckCanSecure(pwd),
            Helpers.CryptoHelper.GetSHA256HashData(pwd),
            () => securityService.Secure(pwd));

        // Setting, changing and removing the password: the checks, the AES256 key, then the files (all or
        // nothing, XodSecurityService), then this context follows the password the files are under.
        private void SwitchPassword(Action check, string newPassword, Action switchFiles)
        {
            rwLock.EnterWriteLock();
            try
            {
                // A rollback would restore files as they were before the switch, under the old password.
                if (activeTransaction != null && activeTransaction.IsActive)
                    throw new TransactionException("A database password can't be set, changed or removed inside a transaction.");

                // A change this context made earlier that couldn't replace every file: finished before anything
                // below reads the files, or this one is refused.
                securityService.FinishPendingSwitch();
                check();
                // Everything this context holds goes to disk first: the switch rewrites the files on disk, and
                // nothing may be written over them afterwards that the switch didn't see.
                FlushToDisk();
                propertyKeys.PasswordChanging(password, newPassword);
                try
                {
                    switchFiles();
                }
                finally
                {
                    // The files' password is the security service's: the old one if the switch failed before
                    // its commit, the new one from the commit on (even when a file is replaced only at the next open).
                    password = securityService.Password;
                    ioService.Password = password;
                    ClearCache();
                }
                // Only after a finished switch: removes the key's protection by the old password. A switch that
                // failed leaves at most a protection by the password it tried (inside the database file, under
                // the password that still opens it); the next finished change removes that too.
                propertyKeys.PasswordChanged();
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        public IEnumerable<Type> RegisteredTypes()
        {
            var types = new List<Type>();
            if (!Directory.Exists(root)) return types;

            var files = Directory.GetFiles(root, "*.xtab");
            foreach (var file in files)
            {
                string typeName = System.IO.Path.GetFileNameWithoutExtension(file);
                Type t = propertyService.RegisteredType(typeName) ?? Type.GetType(typeName);
                if (t != null)
                    types.Add(t);
            }
            return types;
        }

        public void RegisterType(Type type)
        {
            propertyService.LoadType(type);
        }

        public void Flush()
        {
            rwLock.EnterWriteLock();
            try
            {
                FlushToDisk();
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Writes everything this context holds to disk: buffered files, then the indexes. Under a password the
        /// index service saves an index through the buffered files, so those are flushed again after it; without
        /// one it writes the index file itself. Pages go first: the index is a cache of them, and the open trusts
        /// an index no older than its pages (ISS-043).
        /// </summary>
        private void FlushToDisk()
        {
            ioService.FlushBufferedFiles();
            indexService.FlushDirtyIndexes();
            ioService.FlushBufferedFiles();
        }

        public void ClearCache()
        {
            rwLock.EnterWriteLock();
            try
            {
                if (activeTransaction == null || !activeTransaction.IsActive)
                    FlushToDisk();
                ioService.ClearCurrentCache();
                itemsCacheService.ClearCurrentCache();
                indexService.ClearAll();
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        public void ClearCaches()
        {
            rwLock.EnterWriteLock();
            try
            {
                if (activeTransaction == null || !activeTransaction.IsActive)
                    FlushToDisk();
                ioService.ClearAllCache();
                itemsCacheService.ClearAllCache();
                indexService.ClearAll();
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }
    }
}
