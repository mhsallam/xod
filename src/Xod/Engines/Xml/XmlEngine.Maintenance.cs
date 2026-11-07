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
                ioService.FlushBufferedFiles();
                indexService.FlushDirtyIndexes();
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

        public void ChangePassword(string currentPassword, string newPassword)
        {
            rwLock.EnterWriteLock();
            try
            {
                indexService.FlushDirtyIndexes();
                securityService.ChangePassword(currentPassword, newPassword);
                password = securityService.Password;
                ioService.Password = password;
                ClearCache();
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        public void Loose(string pwd)
        {
            rwLock.EnterWriteLock();
            try
            {
                indexService.FlushDirtyIndexes();
                securityService.Loose(pwd);
                password = null;
                ioService.Password = null;
                ClearCache();
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        public void Secure(string pwd)
        {
            rwLock.EnterWriteLock();
            try
            {
                indexService.FlushDirtyIndexes();
                securityService.Secure(pwd);
                password = securityService.Password;
                ioService.Password = password;
                ClearCache();
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
                ioService.FlushBufferedFiles();
                indexService.FlushDirtyIndexes();
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        public void ClearCache()
        {
            rwLock.EnterWriteLock();
            try
            {
                if (activeTransaction == null || !activeTransaction.IsActive)
                {
                    ioService.FlushBufferedFiles();
                    indexService.FlushDirtyIndexes();
                }
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
                {
                    ioService.FlushBufferedFiles();
                    indexService.FlushDirtyIndexes();
                }
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
