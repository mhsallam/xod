using Xod.Engines.Xml;

namespace Xod.Engines.Binary
{
    /// <summary>
    /// High-performance Binary XOD++ storage engine with Brotli compression.
    /// </summary>
    public class BinaryEngine : XmlEngine
    {
        public BinaryEngine(string file, string password = null, DatabaseOptions options = null)
            : base(file, password, ConfigureBinaryOptions(options))
        {
        }

        private static DatabaseOptions ConfigureBinaryOptions(DatabaseOptions options)
        {
            options ??= new DatabaseOptions();
            options.StorageFormat = StorageFormat.Binary;
            return options;
        }
    }
}
