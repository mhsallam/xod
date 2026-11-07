using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Xod
{
    internal class XFile : IDisposable
    {
        private readonly object rowMapLock = new object();
        private XDocument document;
        private Dictionary<string, XElement> rowMap;

        public XDocument Document => document;
        public string Path { get; private set; }
        public Type Type { get; private set; }

        public long LastSavedSize { get; set; }

        public XFile(XDocument document, Type type, string path, string password = null)
        {
            this.document = document;
            this.Type = type;
            this.Path = path;
        }

        public XElement Root() => document?.Root;

        public IEnumerable<XElement> Pages() => Get("Pages", "Page");

        public IEnumerable<XElement> Rows() => Get("Rows", "Row");

        public void EnsureRowMapBuilt()
        {
            if (document?.Root == null)
                return;

            lock (rowMapLock)
            {
                if (rowMap == null)
                {
                    rowMap = new Dictionary<string, XElement>(256, StringComparer.Ordinal);
                    foreach (var r in Rows())
                    {
                        string code = r.Attribute("code")?.Value;
                        if (!string.IsNullOrEmpty(code))
                            rowMap[code] = r;
                    }
                }
            }
        }

        public XElement GetRowByCode(string rowCode)
        {
            if (string.IsNullOrEmpty(rowCode) || document?.Root == null)
                return null;

            lock (rowMapLock)
            {
                if (rowMap == null)
                {
                    rowMap = new Dictionary<string, XElement>(256, StringComparer.Ordinal);
                    foreach (var r in Rows())
                    {
                        string code = r.Attribute("code")?.Value;
                        if (!string.IsNullOrEmpty(code))
                            rowMap[code] = r;
                    }
                }

                return rowMap.TryGetValue(rowCode, out var found) ? found : null;
            }
        }

        public void AddRow(string rowCode, XElement rowElement)
        {
            if (rowElement == null || document?.Root == null)
                return;

            lock (rowMapLock)
            {
                document.Root.Element("Rows")?.Add(rowElement);
                if (rowMap != null && !string.IsNullOrEmpty(rowCode))
                {
                    rowMap[rowCode] = rowElement;
                }
                LastSavedSize += EstimateElementSize(rowElement);
            }
        }

        private static long EstimateElementSize(XElement element)
        {
            long bytes = 24;
            XElement entity = element.HasElements ? element.Elements().FirstOrDefault() : null;
            if (entity != null)
            {
                bytes += (entity.Name.LocalName.Length * 2) + 8;
                foreach (var child in entity.Elements())
                {
                    bytes += (child.Name.LocalName.Length * 2) + (child.Value?.Length ?? 0) + 7;
                }
            }
            return bytes;
        }

        public void RemoveRow(XElement rowElement)
        {
            if (rowElement == null)
                return;

            lock (rowMapLock)
            {
                string code = rowElement.Attribute("code")?.Value;
                rowElement.Remove();
                if (rowMap != null && !string.IsNullOrEmpty(code))
                {
                    rowMap.Remove(code);
                }
            }
        }

        public string GetFileCode()
        {
            if (!string.IsNullOrEmpty(Path))
                return System.IO.Path.GetFileNameWithoutExtension(Path);
            return null;
        }

        public XFile Clone()
        {
            return new XFile(document != null ? new XDocument(document) : null, Type, Path);
        }

        public void ReplaceDocument(XDocument newDocument)
        {
            lock (rowMapLock)
            {
                this.document = newDocument;
                this.rowMap = null;
            }
        }

        public void Dispose()
        {
            lock (rowMapLock)
            {
                this.document = null;
                this.rowMap = null;
            }
        }

        private IEnumerable<XElement> Get(string parentName, string childrenName)
        {
            if (document?.Root != null)
            {
                var parentElement = document.Root.Element(parentName);
                if (parentElement != null)
                    return parentElement.Elements(childrenName);
            }
            return Enumerable.Empty<XElement>();
        }
    }
}
