using System.Collections.Generic;

namespace Xod
{
    /// <summary>
    /// Diagnostics and metrics for a XOD++ database.
    /// </summary>
    public class DatabaseStatistics
    {
        public string DatabasePath { get; set; }
        public StorageFormat StorageFormat { get; set; }
        public bool IsEncrypted { get; set; }
        public long TotalSizeBytes { get; set; }
        public int TableCount { get; set; }
        public int TotalPageCount { get; set; }
        public int TotalRowCount { get; set; }
        public List<TableStatistics> Tables { get; set; } = new List<TableStatistics>();
    }

    public class TableStatistics
    {
        public string TableName { get; set; }
        public int PageCount { get; set; }
        public int RowCount { get; set; }
        public int IndexedPropertyCount { get; set; }
        public long SizeBytes { get; set; }
    }
}
