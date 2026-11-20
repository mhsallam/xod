using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xod.Helpers;
using Xod.Services;

namespace Xod.Sql
{
    /// <summary>
    /// A table as SQL sees it: its names, columns and relations, from the schema manifest (PLAN-046 section 10).
    /// The manifest of a class the program has is built by <see cref="SchemaManifest.Build"/>, the code that writes
    /// it into the table file, and read back by <see cref="SchemaManifest.Read"/>, as a stored one is: SQL sees a
    /// table the same way whether the class or the file described it.
    /// </summary>
    internal sealed class TableSchema
    {
        public string FullName { get; init; }
        public string ShortName { get; init; }
        public List<ColumnSchema> Columns { get; } = new();
        public List<RelationSchema> Relations { get; } = new();

        /// <summary>False for a table with neither a class nor a manifest: every column is text (warning XQ013).</summary>
        public bool Typed { get; init; } = true;

        /// <summary>The class, when the schema came from it: its columns are read with the library's own decoder.</summary>
        public Type ClrType { get; init; }

        public ColumnSchema Column(string name) => Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        public RelationSchema Relation(string name) => Relations.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed class ColumnSchema
    {
        public string Name { get; init; }
        /// <summary>The manifest type (<c>int32</c>, <c>text</c>, <c>enum</c>, <c>map</c>, ...).</summary>
        public string StoredType { get; init; }
        public SqlType Type => SqlTypes.OfStored(StoredType);
        public bool Nullable { get; init; }
        public bool IsAttribute { get; init; }
        public int Key { get; init; }
        public bool Autonumber { get; init; }
        public bool Unique { get; init; }
        public bool Indexed { get; init; }
        public bool Required { get; init; }
        /// <summary><c>md5</c>, <c>sha1</c>, <c>sha256</c>, <c>sha512</c>, <c>aes256</c>, or null.</summary>
        public string Crypto { get; init; }
        public bool Markup { get; init; }
        /// <summary>The text of what a read gives the column when its row leaves it out, or null (null, or the type's
        /// default). For a map or list, <c>""</c> is an empty one.</summary>
        public string Default { get; init; }
        /// <summary>An enum's members (and a map's values' or a list's items' when they are an enum).</summary>
        public List<KeyValuePair<string, string>> Members { get; init; }
        public bool Flags { get; init; }
        /// <summary>A map's key and value types, a list's item type (manifest types).</summary>
        public string KeyType { get; init; }
        public string ValueType { get; init; }
        public string ItemType { get; init; }
        /// <summary>A null list item or map value reads as NULL; otherwise as its type's default, as the library reads it.</summary>
        public bool ItemNullable { get; init; }
        public bool ValueNullable { get; init; }

        /// <summary>The type of a map's values or a list's items as SQL sees it.</summary>
        public SqlType ElementType => StoredType == "map" ? SqlTypes.OfStored(ValueType) : StoredType == "list" ? SqlTypes.OfStored(ItemType) : SqlType.Null;

        /// <summary>The property, when the schema came from the class.</summary>
        public PropertyInfoItem Property { get; init; }
    }

    internal sealed class RelationSchema
    {
        public string Name { get; init; }
        /// <summary><c>reference</c>, <c>complex</c>, <c>children</c> or <c>parent</c>.</summary>
        public string Kind { get; init; }
        public string Table { get; init; }
        public string[] LocalKeys { get; init; } = Array.Empty<string>();
        public string[] RemoteKeys { get; init; } = Array.Empty<string>();
        /// <summary><c>generic</c> or <c>array</c> for a collection; null for one item.</summary>
        public string List { get; init; }
        public string Cascade { get; init; }

        public bool IsCollection => List != null;
        public bool HasKeys => LocalKeys.Length > 0 && LocalKeys.Length == RemoteKeys.Length;
    }

    /// <summary>
    /// The schema manifest (PLAN-046 section 10): the <c>&lt;Schema&gt;</c> element of a table file. Built here
    /// from a class, and read here into a <see cref="TableSchema"/>; the writer (XmlEngine.Manifest) puts it in
    /// the table file.
    /// </summary>
    internal static class SchemaManifest
    {
        public const string Element = "Schema";
        public const string Version = "1";

        /// <summary>The library version that writes a manifest, for diagnosis only.</summary>
        public static readonly string Writer = WriterVersion();

        private static string WriterVersion()
        {
            var asm = typeof(SchemaManifest).Assembly;
            string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString() ?? "unknown";
            int plus = v.IndexOf('+');
            return plus > 0 ? v.Substring(0, plus) : v;
        }

        /// <summary>The manifest of a class, from its metadata (PropertyService), in its properties' order.</summary>
        public static XElement Build(Type type, PropertyService properties)
        {
            properties.LoadType(type);
            var props = properties.Properties(type.FullName);
            var meta = properties.GetMetadata(type);
            var keyOrder = (meta?.PrimaryKeyProperties ?? props.Where(p => p.IsPrimaryKey).ToArray()).Select(p => p.PropertyName).ToList();

            var schema = new XElement(Element,
                new XAttribute("version", Version),
                new XAttribute("type", type.FullName),
                new XAttribute("element", type.Name),
                new XAttribute("writer", Writer));

            foreach (var p in props)
            {
                if (p.IsGenericType || p.IsNotMapped || p.IsReadOnly)
                    continue;
                XElement column = BuildColumn(p, keyOrder);
                if (column != null)
                {
                    schema.Add(column);
                    continue;
                }
                XElement relation = BuildRelation(type, p, properties);
                if (relation != null)
                    schema.Add(relation);
            }
            return schema;
        }

        private static XElement BuildColumn(PropertyInfoItem p, List<string> keyOrder)
        {
            Type t = p.PropertyType;
            var c = new XElement("Column", new XAttribute("name", p.PropertyName));

            if (p.ReferenceType == PropertyReferenceType.None && ValueHelper.IsScalarType(t))
            {
                string stored = SqlTypes.StoredTypeOf(t);
                c.Add(new XAttribute("type", stored));
                if (System.Nullable.GetUnderlyingType(t) != null) c.Add(new XAttribute("nullable", "true"));
                if (p.ValuePosition == ValuePosition.Attribute) c.Add(new XAttribute("at", "attribute"));
                int key = keyOrder.IndexOf(p.PropertyName);
                if (p.IsPrimaryKey && key >= 0) c.Add(new XAttribute("key", (key + 1).ToString(CultureInfo.InvariantCulture)));
                if (p.IsAutonumber) c.Add(new XAttribute("autonumber", "true"));
                if (p.IsUnique) c.Add(new XAttribute("unique", "true"));
                // Keys and unique columns are indexed anyway; the attribute says it was asked for.
                if (p.IsIndexed && !p.IsPrimaryKey && !p.IsUnique) c.Add(new XAttribute("indexed", "true"));
                if (p.IsRequired) c.Add(new XAttribute("required", "true"));
                if (p.Encryption != CryptoMethod.None) c.Add(new XAttribute("crypto", p.Encryption.ToString().ToLowerInvariant()));
                if (p.IsMarkup) c.Add(new XAttribute("markup", "true"));
                string def = DefaultText(p);
                if (def != null) c.Add(new XAttribute("default", def));
                AddMembers(c, t);
                return c;
            }

            if (p.TypeCategory == PropertyTypeCategory.Dictionary)
            {
                Type[] types = ValueHelper.MapArguments(t);
                Type keyType = types?[0] ?? typeof(string);
                Type valType = types?[1] ?? typeof(string);
                c.Add(new XAttribute("type", "map"),
                    new XAttribute("keyType", SqlTypes.StoredTypeOf(keyType)),
                    new XAttribute("valueType", SqlTypes.StoredTypeOf(valType)));
                if (System.Nullable.GetUnderlyingType(valType) != null) c.Add(new XAttribute("valueNullable", "true"));
                if (CollectionDefault(p) is string d) c.Add(new XAttribute("default", d));
                AddMembers(c, valType);
                return c;
            }

            if (p.TypeCategory == PropertyTypeCategory.ValueTypeArray || p.TypeCategory == PropertyTypeCategory.ValueTypeCollection)
            {
                Type itemType = t.IsArray ? t.GetElementType() : t.GetGenericArguments().FirstOrDefault();
                if (itemType == null) return null;
                c.Add(new XAttribute("type", "list"), new XAttribute("itemType", SqlTypes.StoredTypeOf(itemType)));
                if (System.Nullable.GetUnderlyingType(itemType) != null) c.Add(new XAttribute("itemNullable", "true"));
                if (CollectionDefault(p) is string d) c.Add(new XAttribute("default", d));
                AddMembers(c, itemType);
                return c;
            }
            return null;
        }

        private static void AddMembers(XElement column, Type t)
        {
            t = System.Nullable.GetUnderlyingType(t) ?? t;
            if (!t.IsEnum) return;
            if (t.IsDefined(typeof(FlagsAttribute), false)) column.Add(new XAttribute("flags", "true"));
            foreach (var name in Enum.GetNames(t))
            {
                object value = Enum.Parse(t, name);
                string number = Convert.ToString(Convert.ChangeType(value, Enum.GetUnderlyingType(t), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                column.Add(new XElement("Member", new XAttribute("name", name), new XAttribute("value", number)));
            }
        }

        /// <summary>What a read gives a scalar its row leaves out (StoredValue.ReadBackOfMissing), as text, unless a
        /// reader without the class gives the same anyway: null, or a value type's default when it isn't nullable.</summary>
        private static string DefaultText(PropertyInfoItem p)
        {
            object missing = StoredValue.ReadBackOfMissing(p);
            if (missing == null)
                return null;
            Type t = p.PropertyType;
            Type underlying = System.Nullable.GetUnderlyingType(t);
            if (underlying == null && t.IsValueType && missing.Equals(ValueHelper.DefaultOf(t)))
                return null;
            return ValueHelper.FormatScalar(missing);
        }

        /// <summary>A map or list a read gives as an empty one when its row leaves it out: <c>""</c>.</summary>
        private static string CollectionDefault(PropertyInfoItem p)
        {
            object missing = StoredValue.ReadBackOfMissing(p);
            return missing is System.Collections.IEnumerable items && !items.Cast<object>().Any() ? "" : null;
        }

        private static XElement BuildRelation(Type owner, PropertyInfoItem p, PropertyService properties)
        {
            string kind;
            string table;
            string list = null;
            string[] local = Array.Empty<string>(), remote = Array.Empty<string>();

            switch (p.ReferenceType)
            {
                case PropertyReferenceType.Complex:
                    kind = "complex";
                    table = p.PropertyType.FullName;
                    break;
                case PropertyReferenceType.Parent:
                    kind = "parent";
                    table = p.PropertyType.FullName;
                    if (p.ParentKeys != null && p.ParentKeys.All(k => !string.IsNullOrEmpty(k.LocalProperty) && !string.IsNullOrEmpty(k.RemoteProperty)))
                    {
                        local = p.ParentKeys.Select(k => k.LocalProperty).ToArray();
                        remote = p.ParentKeys.Select(k => k.RemoteProperty).ToArray();
                    }
                    break;
                case PropertyReferenceType.Foreign:
                case PropertyReferenceType.SelfForeign:
                    kind = "reference";
                    table = p.PropertyType.FullName;
                    if (p.ForeignKeys != null && p.ForeignKeys.All(k => !string.IsNullOrEmpty(k.LocalProperty) && !string.IsNullOrEmpty(k.RemoteProperty)))
                    {
                        local = p.ForeignKeys.Select(k => k.LocalProperty).ToArray();
                        remote = p.ForeignKeys.Select(k => k.RemoteProperty).ToArray();
                    }
                    break;
                case PropertyReferenceType.Children:
                    kind = "children";
                    if (p.CollectionItemType == null) return null;
                    table = p.CollectionItemType.FullName;
                    list = p.PropertyType.IsArray ? "array" : "generic";
                    (local, remote) = ChildrenKeys(owner, p, properties);
                    break;
                case PropertyReferenceType.Reference:
                    kind = "reference";
                    if (p.TypeCategory == PropertyTypeCategory.Array || p.TypeCategory == PropertyTypeCategory.GenericCollection)
                    {
                        if (p.CollectionItemType == null) return null;
                        table = p.CollectionItemType.FullName;
                        list = p.PropertyType.IsArray ? "array" : "generic";
                    }
                    else
                    {
                        table = p.PropertyType.FullName;
                    }
                    break;
                default:
                    return null;
            }

            var r = new XElement("Relation", new XAttribute("name", p.PropertyName), new XAttribute("kind", kind), new XAttribute("table", table));
            if (local.Length > 0)
            {
                r.Add(new XAttribute("localKey", string.Join(",", local)));
                r.Add(new XAttribute("remoteKey", string.Join(",", remote)));
            }
            if (list != null) r.Add(new XAttribute("list", list));
            if (p.Cascade != CascadeOptions.None) r.Add(new XAttribute("cascade", p.Cascade.ToString().ToLowerInvariant().Replace(" ", "")));
            return r;
        }

        /// <summary>The keys that join a parent to its children: the parent's (local) and the child's (remote), from
        /// the child's parent property ([ParentKey]), or by the <c>{Parent}Id</c> convention; none when only the stored
        /// item codes link them.</summary>
        private static (string[] Local, string[] Remote) ChildrenKeys(Type owner, PropertyInfoItem p, PropertyService properties)
        {
            Type child = p.CollectionItemType;
            properties.LoadType(child);
            var childProps = properties.Properties(child.FullName);
            PropertyInfoItem parentProp = !string.IsNullOrEmpty(p.ChildParentProperty)
                ? childProps.FirstOrDefault(c => c.PropertyName == p.ChildParentProperty)
                : childProps.FirstOrDefault(c => c.ReferenceType == PropertyReferenceType.Parent && c.PropertyType.IsAssignableFrom(owner));
            if (parentProp?.ParentKeys != null && parentProp.ParentKeys.Count > 0 &&
                parentProp.ParentKeys.All(k => !string.IsNullOrEmpty(k.LocalProperty) && !string.IsNullOrEmpty(k.RemoteProperty)))
            {
                return (parentProp.ParentKeys.Select(k => k.RemoteProperty).ToArray(), parentProp.ParentKeys.Select(k => k.LocalProperty).ToArray());
            }

            var ownerKey = properties.Properties(owner.FullName).Where(x => x.IsPrimaryKey).ToList();
            var byName = childProps.FirstOrDefault(c => ValueHelper.IsScalarType(c.PropertyType) &&
                c.PropertyName.Equals(owner.Name + "Id", StringComparison.OrdinalIgnoreCase));
            if (parentProp == null && ownerKey.Count == 1 && byName != null)
                return (new[] { ownerKey[0].PropertyName }, new[] { byName.PropertyName });
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        /// <summary>A manifest as SQL reads it. Null when the element isn't one this version reads.</summary>
        public static TableSchema Read(XElement schema, string fullName, Type clrType = null, IReadOnlyList<PropertyInfoItem> properties = null)
        {
            if (schema == null || schema.Attribute("version")?.Value != Version)
                return null;
            var result = new TableSchema
            {
                FullName = fullName,
                ShortName = schema.Attribute("element")?.Value ?? ShortNameOf(fullName),
                ClrType = clrType,
            };
            foreach (var e in schema.Elements())
            {
                string name = e.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                if (e.Name.LocalName == "Column")
                {
                    result.Columns.Add(new ColumnSchema
                    {
                        Name = name,
                        StoredType = e.Attribute("type")?.Value ?? "text",
                        Nullable = Flag(e, "nullable"),
                        IsAttribute = e.Attribute("at")?.Value == "attribute",
                        Key = int.TryParse(e.Attribute("key")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) ? k : 0,
                        Autonumber = Flag(e, "autonumber"),
                        Unique = Flag(e, "unique"),
                        Indexed = Flag(e, "indexed"),
                        Required = Flag(e, "required"),
                        Crypto = e.Attribute("crypto")?.Value,
                        Markup = Flag(e, "markup"),
                        Default = e.Attribute("default")?.Value,
                        Flags = Flag(e, "flags"),
                        Members = e.Elements("Member").Select(m => new KeyValuePair<string, string>(m.Attribute("name")?.Value ?? "", m.Attribute("value")?.Value ?? "")).ToList(),
                        KeyType = e.Attribute("keyType")?.Value,
                        ValueType = e.Attribute("valueType")?.Value,
                        ItemType = e.Attribute("itemType")?.Value,
                        ItemNullable = Flag(e, "itemNullable"),
                        ValueNullable = Flag(e, "valueNullable"),
                        Property = properties?.FirstOrDefault(p => p.PropertyName == name),
                    });
                }
                else if (e.Name.LocalName == "Relation")
                {
                    result.Relations.Add(new RelationSchema
                    {
                        Name = name,
                        Kind = e.Attribute("kind")?.Value,
                        Table = e.Attribute("table")?.Value,
                        LocalKeys = Keys(e.Attribute("localKey")?.Value),
                        RemoteKeys = Keys(e.Attribute("remoteKey")?.Value),
                        List = e.Attribute("list")?.Value,
                        Cascade = e.Attribute("cascade")?.Value,
                    });
                }
            }
            return result;
        }

        /// <summary>A table whose table file records no schema and whose class the program hasn't: every element and
        /// attribute its rows hold is a text column (PLAN-046 section 10.3), relations left out.</summary>
        public static TableSchema Untyped(string fullName, IEnumerable<XElement> entities)
        {
            var columns = new List<ColumnSchema>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string element = null;
            foreach (var entity in entities)
            {
                element ??= entity.Name.LocalName;
                foreach (var a in entity.Attributes())
                    if (seen.Add(a.Name.LocalName))
                        columns.Add(new ColumnSchema { Name = a.Name.LocalName, StoredType = "text", IsAttribute = true });
                foreach (var e in entity.Elements())
                {
                    // A relation (refType) or a collection (collType) isn't a text column.
                    if (e.Attribute("refType") != null || e.Attribute("collType") != null) continue;
                    if (seen.Add(e.Name.LocalName))
                        columns.Add(new ColumnSchema { Name = e.Name.LocalName, StoredType = "text" });
                }
            }
            var result = new TableSchema { FullName = fullName, ShortName = element ?? ShortNameOf(fullName), Typed = false };
            result.Columns.AddRange(columns);
            return result;
        }

        /// <summary>The short name of a full type name: after its last <c>.</c> or <c>+</c>.</summary>
        public static string ShortNameOf(string fullName)
        {
            int at = Math.Max(fullName.LastIndexOf('.'), fullName.LastIndexOf('+'));
            return at >= 0 ? fullName.Substring(at + 1) : fullName;
        }

        /// <summary>True when two manifests say the same, whichever library wrote them (<c>writer</c> aside).</summary>
        public static bool SameContent(XElement a, XElement b)
        {
            if (a == null || b == null) return false;
            return XNode.DeepEquals(WithoutWriter(a), WithoutWriter(b));
        }

        private static XElement WithoutWriter(XElement e)
        {
            var copy = new XElement(e);
            copy.Attribute("writer")?.Remove();
            return copy;
        }

        private static bool Flag(XElement e, string name) => e.Attribute(name)?.Value == "true";

        private static string[] Keys(string text) =>
            string.IsNullOrEmpty(text) ? Array.Empty<string>() : text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
    }
}
