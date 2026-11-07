using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Xod.Infra;

namespace Xod.Engines.Serializers
{
    public sealed class XmlStorageSerializer : IXodStorageSerializer
    {
        private static readonly XmlWriterSettings SharedWriterSettings = new XmlWriterSettings
        {
            Indent = false,
            CheckCharacters = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false
        };

        private static readonly XmlReaderSettings SharedReaderSettings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            CheckCharacters = false,
            DtdProcessing = DtdProcessing.Prohibit
        };

        public StorageFormat Format => StorageFormat.Xml;

        public byte[] Serialize(XDocument document)
        {
            using var ms = new MemoryStream(65536);
            using (var writer = XmlWriter.Create(ms, SharedWriterSettings))
            {
                document.Save(writer);
            }
            return ms.ToArray();
        }

        public XDocument Deserialize(byte[] data)
        {
            if (StorageSerializerFactory.DetectFormat(data) != StorageFormat.Xml)
                return StorageSerializerFactory.Get(StorageSerializerFactory.DetectFormat(data)).Deserialize(data);

            using var ms = new MemoryStream(data, writable: false);
            return XDocument.Load(ms, LoadOptions.None);
        }
    }

    public sealed class JsonStorageSerializer : IXodStorageSerializer
    {
        public StorageFormat Format => StorageFormat.Json;

        public byte[] Serialize(XDocument document)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("$engine", "XOD++ JSON Storage v2.0");
                if (document.Root != null)
                {
                    writer.WritePropertyName("$root");
                    WriteElement(writer, document.Root);
                }
                writer.WriteEndObject();
            }
            return ms.ToArray();
        }

        private static void WriteElement(Utf8JsonWriter writer, XElement element)
        {
            writer.WriteStartObject();
            writer.WriteString("$name", element.Name.LocalName);

            if (element.HasAttributes)
            {
                writer.WriteStartObject("$attrs");
                foreach (var attr in element.Attributes())
                {
                    writer.WriteString(attr.Name.LocalName, attr.Value);
                }
                writer.WriteEndObject();
            }

            if (element.HasElements)
            {
                writer.WriteStartArray("$children");
                foreach (var child in element.Elements())
                {
                    WriteElement(writer, child);
                }
                writer.WriteEndArray();
            }
            else if (element.FirstNode is XCData cdata)
            {
                writer.WriteBoolean("$cdata", true);
                writer.WriteString("$value", cdata.Value);
            }
            else if (!element.IsEmpty || !string.IsNullOrEmpty(element.Value))
            {
                writer.WriteString("$value", element.Value);
            }

            writer.WriteEndObject();
        }

        public XDocument Deserialize(byte[] data)
        {
            var detected = StorageSerializerFactory.DetectFormat(data);
            if (detected != StorageFormat.Json)
                return StorageSerializerFactory.Get(detected).Deserialize(data);

            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("$root", out JsonElement rootElem))
            {
                return new XDocument(ReadElement(rootElem));
            }
            throw new DatabaseFileException("Invalid XOD++ JSON file structure.");
        }

        private static XElement ReadElement(JsonElement json)
        {
            string name = json.GetProperty("$name").GetString();
            var element = new XElement(name);

            if (json.TryGetProperty("$attrs", out JsonElement attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in attrs.EnumerateObject())
                {
                    element.SetAttributeValue(prop.Name, prop.Value.GetString() ?? string.Empty);
                }
            }

            if (json.TryGetProperty("$children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    element.Add(ReadElement(child));
                }
            }
            else if (json.TryGetProperty("$value", out JsonElement val))
            {
                string strVal = val.GetString() ?? string.Empty;
                if (json.TryGetProperty("$cdata", out JsonElement isCdata) && isCdata.GetBoolean())
                {
                    element.Add(new XCData(strVal));
                }
                else
                {
                    element.Value = strVal;
                }
            }

            return element;
        }
    }

    public sealed class BinaryStorageSerializer : IXodStorageSerializer
    {
        private static readonly byte[] MagicBytes = new byte[] { (byte)'X', (byte)'O', (byte)'D', (byte)'B', 0x02 };

        public StorageFormat Format => StorageFormat.Binary;

        public byte[] Serialize(XDocument document)
        {
            using var output = new MemoryStream();
            output.Write(MagicBytes, 0, MagicBytes.Length);

            using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            using (var writer = new BinaryWriter(brotli, Encoding.UTF8, leaveOpen: true))
            {
                bool hasRoot = document?.Root != null;
                writer.Write(hasRoot);
                if (hasRoot)
                {
                    WriteBinaryElement(writer, document.Root);
                }
            }

            return output.ToArray();
        }

        private static void WriteBinaryElement(BinaryWriter writer, XElement element)
        {
            writer.Write(element.Name.LocalName);

            var attrs = element.Attributes().ToList();
            writer.Write(attrs.Count);
            foreach (var attr in attrs)
            {
                writer.Write(attr.Name.LocalName);
                writer.Write(attr.Value ?? string.Empty);
            }

            var children = element.Elements().ToList();
            writer.Write(children.Count);
            if (children.Count > 0)
            {
                foreach (var child in children)
                {
                    WriteBinaryElement(writer, child);
                }
            }
            else
            {
                if (element.FirstNode is XCData cdata)
                {
                    writer.Write((byte)2); // CDATA node
                    writer.Write(cdata.Value ?? string.Empty);
                }
                else if (!element.IsEmpty || !string.IsNullOrEmpty(element.Value))
                {
                    writer.Write((byte)1); // Text value
                    writer.Write(element.Value ?? string.Empty);
                }
                else
                {
                    writer.Write((byte)0); // Empty
                }
            }
        }

        public XDocument Deserialize(byte[] data)
        {
            var detected = StorageSerializerFactory.DetectFormat(data);
            if (detected != StorageFormat.Binary)
                return StorageSerializerFactory.Get(detected).Deserialize(data);

            using var input = new MemoryStream(data, MagicBytes.Length, data.Length - MagicBytes.Length);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var reader = new BinaryReader(brotli, Encoding.UTF8);

            bool hasRoot = reader.ReadBoolean();
            if (!hasRoot) return new XDocument();

            return new XDocument(ReadBinaryElement(reader));
        }

        private static XElement ReadBinaryElement(BinaryReader reader)
        {
            string name = reader.ReadString();
            var element = new XElement(name);

            int attrCount = reader.ReadInt32();
            for (int i = 0; i < attrCount; i++)
            {
                string attrName = reader.ReadString();
                string attrVal = reader.ReadString();
                element.SetAttributeValue(attrName, attrVal);
            }

            int childCount = reader.ReadInt32();
            if (childCount > 0)
            {
                for (int i = 0; i < childCount; i++)
                {
                    element.Add(ReadBinaryElement(reader));
                }
            }
            else
            {
                byte nodeKind = reader.ReadByte();
                if (nodeKind == 2)
                {
                    element.Add(new XCData(reader.ReadString()));
                }
                else if (nodeKind == 1)
                {
                    element.Value = reader.ReadString();
                }
            }

            return element;
        }
    }

    public static class StorageSerializerFactory
    {
        private static readonly XmlStorageSerializer XmlInstance = new XmlStorageSerializer();
        private static readonly JsonStorageSerializer JsonInstance = new JsonStorageSerializer();
        private static readonly BinaryStorageSerializer BinaryInstance = new BinaryStorageSerializer();

        public static IXodStorageSerializer Get(StorageFormat format) => format switch
        {
            StorageFormat.Json => JsonInstance,
            StorageFormat.Binary => BinaryInstance,
            _ => XmlInstance
        };

        public static StorageFormat DetectFormat(byte[] data)
        {
            if (data != null && data.Length >= 5 &&
                data[0] == (byte)'X' && data[1] == (byte)'O' && data[2] == (byte)'D' && data[3] == (byte)'B')
            {
                return StorageFormat.Binary;
            }

            if (data != null)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    byte b = data[i];
                    if (b == 0xEF || b == 0xBB || b == 0xBF || b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n')
                        continue;
                    if (b == (byte)'{')
                        return StorageFormat.Json;
                    if (b == (byte)'<')
                        return StorageFormat.Xml;
                    break;
                }
            }

            return StorageFormat.Xml;
        }
    }
}
