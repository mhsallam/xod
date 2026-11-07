using Xod.Engines.Xml;

namespace Xod.Engines.Json
{
    /// <summary>
    /// JSON-backed XOD++ storage engine. Persists database, table (.xtab), and page (.xpag) files as human-readable JSON documents.
    /// </summary>
    public class JsonEngine : XmlEngine
    {
        public JsonEngine(string file, string password = null, DatabaseOptions options = null)
            : base(file, password, ConfigureJsonOptions(options))
        {
        }

        private static DatabaseOptions ConfigureJsonOptions(DatabaseOptions options)
        {
            options ??= new DatabaseOptions();
            options.StorageFormat = StorageFormat.Json;
            return options;
        }
    }
}
