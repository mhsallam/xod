namespace Xod
{
    public enum DurabilityMode
    {
        Immediate = 0,
        Buffered = 1
    }

    /// <summary>
    /// Configuration options for a XOD++ database instance.
    /// </summary>
    public class DatabaseOptions
    {
        public bool InitialCreate { get; set; } = true;
        public bool LazyLoad { get; set; }
        public bool LazyLoadParent { get; set; } = true;
        public StorageFormat StorageFormat { get; set; } = StorageFormat.Xml;
        public int PageSize { get; set; } = 524288; // 512 KB default page size
        public bool EnableIndexing { get; set; } = true;
        public bool EnableCompression { get; set; }
        public bool LockDatabase { get; set; } = false;
        public int LockRetryTimeoutMs { get; set; } = 0;
        public int TransactionTimeoutMs { get; set; } = 30000;
        public DurabilityMode Durability { get; set; } = DurabilityMode.Immediate;
    }
}
