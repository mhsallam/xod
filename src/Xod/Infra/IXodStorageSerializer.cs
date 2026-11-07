using System.Xml.Linq;

namespace Xod.Infra
{
    /// <summary>
    /// Abstraction for physical serialization of XOD++ database, table, and page documents.
    /// Enables XML (.xod/.xtab/.xpag), JSON, and Binary engines to share identical relational semantics.
    /// </summary>
    public interface IXodStorageSerializer
    {
        StorageFormat Format { get; }
        byte[] Serialize(XDocument document);
        XDocument Deserialize(byte[] data);
    }
}
