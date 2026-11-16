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
        private StorageFormat storageFormat = StorageFormat.Xml;

        /// <summary>
        /// The encoding of the files this context writes (each file's own encoding is read from the file, so a
        /// folder can hold all three). Left unset, a database is written in the encoding its database file records
        /// (the one it was created with, or that <see cref="XodMigrator"/> converted it to), or, for a new database or
        /// a file that records none, in the one its database file's extension picks: <c>.xod</c> XML, <c>.xodj</c>
        /// JSON, <c>.xodb</c> Binary.
        /// </summary>
        public StorageFormat StorageFormat
        {
            get => storageFormat;
            set
            {
                storageFormat = value;
                StorageFormatSet = true;
            }
        }

        // Whether StorageFormat was set (ISS-093): unset, the database decides.
        internal bool StorageFormatSet { get; private set; }
        public int PageSize { get; set; } = 524288; // 512 KB default page size
        public bool EnableIndexing { get; set; } = true;
        /// <summary>Has no effect: nothing has ever read it (ISS-048). For smaller files use
        /// <see cref="StorageFormat.Binary"/>. To be removed in a major version.</summary>
        [System.Obsolete("EnableCompression has no effect. For smaller files use StorageFormat.Binary.")]
        public bool EnableCompression { get; set; }
        /// <summary>Has no effect: every context holds its database alone, from its open to its disposal, and any other
        /// open waits <see cref="LockRetryTimeoutMs"/> and is refused with <see cref="DatabaseLockedException"/>
        /// (PLAN-064). Kept so code that sets it still compiles.</summary>
        public bool LockDatabase { get; set; } = false;
        public int LockRetryTimeoutMs { get; set; } = 0;
        /// <summary>How long a transaction, or a write that isn't part of the open transaction, waits for that
        /// transaction to end before it throws <see cref="TransactionException"/> (ISS-132). 30 seconds by default.
        /// A transaction belongs to the code that began it (see <see cref="XodContext.BeginTransaction"/>).</summary>
        public int TransactionTimeoutMs { get; set; } = 30000;
        public DurabilityMode Durability { get; set; } = DurabilityMode.Immediate;

        /// <summary>
        /// A secret the application keeps, which protects the key of <c>[Crypto(AES256)]</c> properties.
        /// It is never stored. Without it, that key is protected by the database password; with neither,
        /// an <c>AES256</c> value can't be written, since anyone with XOD could read it back.
        /// </summary>
        public string PropertyEncryptionKey { get; set; }

        /// <summary>
        /// Where SQL (<see cref="XodContext.Sql(string, object[])"/>) takes each table's column types from: the
        /// program's classes when it has them (the default), or always the schema the table file records, as
        /// <c>xod query</c> reads a database without the classes.
        /// </summary>
        public SqlSchemaSource SqlSchema { get; set; } = SqlSchemaSource.Classes;
    }
}
