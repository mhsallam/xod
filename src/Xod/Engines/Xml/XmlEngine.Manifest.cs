using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xod.Helpers;
using Xod.Sql;

namespace Xod.Engines.Xml
{
    // The schema manifest (PLAN-046 section 10, PLAN-060 section 2.4): each table file records the schema of the
    // class whose rows it holds, so a reader without the class (xod query) reads typed columns.
    public partial class XmlEngine
    {
        // Each class's manifest, built once: a class doesn't change while the program runs.
        private readonly ConcurrentDictionary<Type, XElement> builtManifests = new();
        // The tables each written class's rows may reach (its relations', transitively), itself first.
        private readonly ConcurrentDictionary<Type, Type[]> manifestTables = new();
        // The <Schema> elements found to say what the class says. A document read again (after a rollback, or from
        // disk) holds new elements, which are checked again.
        private readonly ConditionalWeakTable<XElement, object> verifiedManifests = new();

        /// <summary>The document of a file the IOService creates. A table file is created with its rows' schema.</summary>
        private XDocument NewFileDocument(Type type, string fileName)
        {
            object instance = ValueHelper.CreateInstance(type);
            if (instance is Database dbMeta)
                dbMeta.Format = this.StorageFormat.ToString();
            var doc = new XDocument(Write(type, instance));
            if (type == typeof(Table) && fileName.EndsWith(".xtab", StringComparison.OrdinalIgnoreCase))
            {
                Type rows = propertyService.RegisteredType(fileName.Substring(0, fileName.Length - ".xtab".Length));
                if (IsManifestClass(rows))
                {
                    var manifest = new XElement(BuiltManifest(rows));
                    doc.Root.Add(manifest);
                    verifiedManifests.AddOrUpdate(manifest, null);
                }
            }
            return doc;
        }

        /// <summary>
        /// At each write (EnterTableWriteLock), the tables it may write: its class's and those its relations reach.
        /// A table file whose manifest differs from the class's (or has none: written by 2.0.1 or earlier) gets the
        /// class's, saved as any change to the table file. A table without a file gets its manifest when a write
        /// creates it (<see cref="NewFileDocument"/>). Once checked, a table costs a reference lookup.
        /// </summary>
        private void RecordSchemas(Type type)
        {
            if (!IsManifestClass(type))
                return;
            foreach (var t in manifestTables.GetOrAdd(type, ManifestTables))
            {
                XFile tableFile = ioService.OpenFileOrCreate($"{t.FullName}.xtab", typeof(Table), openOnly: true);
                XElement root = tableFile?.Root();
                if (root == null)
                    continue;
                XElement stored = root.Element(SchemaManifest.Element);
                if (stored != null && verifiedManifests.TryGetValue(stored, out _))
                    continue;
                XElement built = BuiltManifest(t);
                if (!SchemaManifest.SameContent(stored, built))
                {
                    var manifest = new XElement(built);
                    if (stored != null)
                        stored.ReplaceWith(manifest);
                    else
                        root.Add(manifest);
                    stored = manifest;
                    ioService.Save(tableFile);
                }
                verifiedManifests.AddOrUpdate(stored, null);
            }
        }

        private XElement BuiltManifest(Type type) => builtManifests.GetOrAdd(type, t => SchemaManifest.Build(t, propertyService));

        private static bool IsManifestClass(Type type) =>
            type != null && type != typeof(object) && type.IsClass && !type.IsAbstract && type.Assembly != typeof(XmlEngine).Assembly;

        private Type[] ManifestTables(Type type)
        {
            var found = new List<Type> { type };
            for (int i = 0; i < found.Count; i++)
            {
                propertyService.LoadType(found[i]);
                foreach (var p in propertyService.Properties(found[i].FullName))
                {
                    if (p.ReferenceType == PropertyReferenceType.None || p.IsNotMapped)
                        continue;
                    Type target = p.CollectionItemType ?? p.PropertyType;
                    if (IsManifestClass(target) && !found.Contains(target))
                        found.Add(target);
                }
            }
            return found.ToArray();
        }
    }
}
