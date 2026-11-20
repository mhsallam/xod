using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xod.Helpers;
using Xod.Services;

namespace Xod.Engines.Xml
{
    public partial class XmlEngine
    {
        /// <summary>
        /// Removes the entry naming <paramref name="childCode"/> from the parent row's lists of
        /// <paramref name="childType"/>, and saves the parent's page: the one place an entry leaves a parent's list.
        /// True when the row held one.
        /// </summary>
        private bool RemoveChildEntry(XRow parentRow, Type parentType, Type childType, string childCode)
        {
            XElement parentEntity = parentRow?.Row?.Element(parentType.Name);
            if (parentEntity == null)
                return false;

            foreach (var pcp in propertyService.Properties(parentType.FullName)
                .Where(s => s.CollectionItemType == childType && s.ReferenceType == PropertyReferenceType.Children))
            {
                XElement entry = parentEntity.Element(pcp.PropertyName)?.Elements().FirstOrDefault(s => s.Value == childCode);
                if (entry != null)
                {
                    entry.Remove();
                    ioService.Save(parentRow.Page);
                    return true;
                }
            }
            return false;
        }

        /// <summary>The row an item code (<c>page.row</c>) names, or null when there is none: a relation's stored code
        /// may name a row that is gone (a table dropped, a referenced row deleted), and its callers then find the row by
        /// its keys or take it as gone (ISS-210).</summary>
        private XRow RowByItemCode(Type type, string itemCode)
        {
            int dot = itemCode?.IndexOf('.') ?? -1;
            if (dot <= 0 || dot >= itemCode.Length - 1)
                return null;
            XFile pageFile = ioService.OpenFileOrCreate<TablePage>(itemCode.Substring(0, dot) + ".xpag", openOnly: true);
            XElement row = pageFile?.GetRowByCode(itemCode.Substring(dot + 1));
            return row == null ? null : new XRow { Page = pageFile, Row = row };
        }

        private bool RecursionError(ReadWriteTrack track, ReadWriteTrack parent)
        {
            if (parent == null) return false;
            if (track.Code == parent.Code) return true;
            return RecursionError(track, parent.Parent);
        }

        /// <summary>
        /// The parent property of <paramref name="childType"/> that the <c>[Children]</c> list
        /// <paramref name="childrenProp"/> of <paramref name="parentType"/> pairs with: the one it names
        /// (<c>[Children(RemoteParentProperty = ...)]</c>), else the child's first parent property of the parent's type.
        /// The one rule for which list a child is in and which parent a list makes: a child with two parent properties
        /// of one type, or a parent with two lists of one type, was decided by type alone (ISS-203).
        /// </summary>
        private PropertyInfoItem ChildParentProperty(PropertyInfoItem childrenProp, Type parentType, Type childType) =>
            childrenProp == null || childrenProp.ReferenceType != PropertyReferenceType.Children ? null
            : !string.IsNullOrEmpty(childrenProp.ChildParentProperty)
                ? propertyService.Properties(childType.FullName).FirstOrDefault(s => s.PropertyName == childrenProp.ChildParentProperty)
                : propertyService.Properties(childType.FullName).FirstOrDefault(s => s.PropertyType == parentType && s.ReferenceType == PropertyReferenceType.Parent);

        private void ParseChildren(Type type, object item, Type childType, IEnumerable<object> children, PropertyInfoItem prop, XElement xProp, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            propertyService.LoadType(childType);

            if (prop.ReferenceType == PropertyReferenceType.Children)
            {
                PropertyInfoItem childParentPropItem = ChildParentProperty(prop, type, childType);

                var parentKeys = childParentPropItem?.ParentKeys;
                if (parentKeys == null || !parentKeys.Any())
                {
                    var parentPkProp = propertyService.Properties(type.FullName).FirstOrDefault(s => s.IsPrimaryKey);
                    if (parentPkProp != null)
                    {
                        var conventionChildKey = propertyService.Properties(childType.FullName)
                            .FirstOrDefault(s => ValueHelper.IsScalarType(s.PropertyType) &&
                                s.PropertyName.Equals($"{type.Name}{parentPkProp.PropertyName}", StringComparison.OrdinalIgnoreCase));
                        if (conventionChildKey != null)
                        {
                            parentKeys = new List<ParentKeyAttribute>
                            {
                                new ParentKeyAttribute(conventionChildKey.PropertyName, parentPkProp.PropertyName)
                            };
                        }
                    }
                }

                if (childParentPropItem != null || (parentKeys != null && parentKeys.Any()))
                {
                    foreach (var child in children)
                    {
                        if (parentKeys != null)
                        {
                            foreach (var parentKeyPropAtt in parentKeys)
                            {
                                object parentKeyValue = null;
                                PropertyInfoItem parentKeyProp = propertyService.Properties(type.FullName)
                                    .FirstOrDefault(s => s.PropertyName == parentKeyPropAtt.RemoteProperty);
                                if (parentKeyProp != null)
                                    parentKeyValue = parentKeyProp.Property.GetValue(item);

                                if (!ValueHelper.IsNoValue(parentKeyValue, parentKeyProp?.PropertyType) && !string.IsNullOrEmpty(parentKeyPropAtt.LocalProperty))
                                {
                                    PropertyInfoItem childKeyProp = propertyService.Properties(childType.FullName)
                                        .FirstOrDefault(s => s.PropertyName == parentKeyPropAtt.LocalProperty);
                                    if (childKeyProp != null)
                                    {
                                        object childKeyValue = childKeyProp.Property.GetValue(child);
                                        if (!ValueHelper.IsNoValue(childKeyValue, childKeyProp.PropertyType))
                                        {
                                            if (!childKeyValue.Equals(parentKeyValue))
                                                exceptionService.Throw(new ReservedChildException());
                                        }
                                        childKeyProp.Property.SetValue(child, parentKeyValue);
                                    }
                                }
                            }
                        }

                        string refCode = SetChildItem(childType, child, prop, GetPrimaryValues(childType, child), lazyLoad, writeTrack);
                        if (refCode != null)
                        {
                            var xpe = new XElement(childType.Name, refCode);
                            var itmKeySet = GetPrimaryValues(childType, child);
                            foreach (var itmKey in itmKeySet)
                                xpe.Add(new XAttribute(itmKey.Key, EntryKeyText(itmKey.Value)));
                            xProp.Add(xpe);
                        }
                    }
                    xProp.SetAttributeValue("refType", "children");
                }
                else
                {
                    exceptionService.Throw(new MissingParentKeyException());
                }
            }
            else
            {
                // An item of a list of object is an object too: no collection reads back from one, so none is written,
                // before any item is (ISS-217: one was written as a row of its own type, which failed with a raw
                // XmlException, a generic type's name holding a backtick).
                if (childType == typeof(object))
                {
                    foreach (var itm in children)
                    {
                        if (itm != null && ValueHelper.CollectionInObject(itm.GetActualType()) is Type held)
                            exceptionService.Throw(new XodException(PropertyService.ObjectCollectionMessage(prop.PropertyName, type.Name, held)));
                    }
                }

                bool isRefChild = ListLinksStoredRows(childType);
                xProp.SetAttributeValue("refType", isRefChild ? "reference" : "complex");

                foreach (var itm in children)
                {
                    string refCode = null;
                    if (isRefChild)
                    {
                        var pkVals = GetPrimaryValues(childType, itm);
                        if (pkVals != null && pkVals.Count > 0)
                        {
                            var existing = GetRow(childType, itm);
                            if (existing?.Row != null)
                            {
                                refCode = $"{System.IO.Path.GetFileNameWithoutExtension(existing.Page.Path)}.{existing.Row.Attribute("code").Value}";
                            }
                        }
                    }

                    refCode ??= Create(childType, itm);
                    if (refCode != null)
                    {
                        var xpe = new XElement(childType.Name, refCode);
                        if (isRefChild)
                        {
                            var itmKeySet = GetPrimaryValues(childType, itm);
                            foreach (var itmKey in itmKeySet)
                                xpe.Add(new XAttribute(itmKey.Key, EntryKeyText(itmKey.Value)));
                        }
                        xProp.Add(xpe);
                    }
                }
            }
        }

        /// <summary>
        /// A list entry's key attribute: the key as it is stored (<see cref="ValueHelper.FormatScalar"/>). Each writer
        /// of an entry writes it so, and <see cref="EntryKeyMatches"/> reads it (ISS-204: written by <c>XAttribute</c>'s
        /// own conversion and compared with <c>ToString</c>, a <c>TimeSpan</c>, a <c>bool</c> or a <c>DateTime</c> key
        /// never matched).
        /// </summary>
        private static string EntryKeyText(object key) => ValueHelper.FormatScalar(key);

        /// <summary>
        /// Whether an entry's key attribute holds <paramref name="key"/>: as <see cref="EntryKeyText"/> writes it, or as
        /// <c>XAttribute</c>'s own conversion wrote it before (<c>PT1H</c> for an hour, <c>true</c> for a bool): the
        /// entries an earlier version left are the ones taken over. Read only for an entry that names no row; an update
        /// of the child alone doesn't write its entry, so a key changed since it was listed isn't there (ISS-204).
        /// </summary>
        private static bool EntryKeyMatches(XAttribute attribute, object key) =>
            attribute != null && key != null &&
            (attribute.Value == EntryKeyText(key) || attribute.Value == new XAttribute(attribute.Name, key).Value);

        /// <summary>
        /// Whether a child of this type in a list can be linked to its stored row: its type has a key. A plain list
        /// links by it (<see cref="ParseChildren"/>), and an update of the parent updates such children by key after
        /// its write (<see cref="UpdateRows"/>); a child without a key is always written as a new row, which holds its
        /// values (ISS-179). A <c>[Children]</c> list decides per child, by its key values (<see cref="SetChildItem"/>),
        /// which comes to the same for a type without a key.
        /// </summary>
        private bool ListLinksStoredRows(Type childType) =>
            propertyService.Properties(childType.FullName).Any(s => s.IsPrimaryKey);

        private string SetChildItem(Type type, object item, PropertyInfoItem prop, Dictionary<string, object> itemPrimarySets, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            if (itemPrimarySets != null && itemPrimarySets.Count > 0)
            {
                var pkProps = propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).ToList();
                if (options.EnableIndexing && pkProps.Count > 0 && pkProps.Count == itemPrimarySets.Count)
                {
                    object example = PopulateItemProperties(type, itemPrimarySets);
                    if (TryLookupByKey(type, example, out var pkCodes))
                    {
                        if (pkCodes.Count > 0)
                        {
                            return pkCodes.First();
                        }
                    }
                }
                else
                {
                    object refItem = SelectItemsByExample(type, PopulateItemProperties(type, itemPrimarySets)).FirstOrDefault();
                    if (refItem != null)
                    {
                        XRow xRow = GetRow(type, refItem);
                        if (xRow?.Row != null)
                        {
                            return $"{System.IO.Path.GetFileNameWithoutExtension(xRow.Page.Path)}.{xRow.Row.Attribute("code").Value}";
                        }
                    }
                }
            }

            return Create(type, item, lazyLoad, writeTrack, via: prop.ReferenceType == PropertyReferenceType.Children ? prop : null);
        }

        private void ParseReference(Type type, object item, object value, PropertyInfoItem prop, Type effectivePropType, XElement xItem, ReadWriteTrack writeTrack)
        {
            if (value != null && prop.ReferenceType == PropertyReferenceType.Foreign && prop.ForeignKeys != null)
            {
                Dictionary<string, object> foreignRefKeySets = ValueHelper.GetPropertyValues(effectivePropType, value, prop.ForeignKeys.Select(s => s.RemoteProperty));
                if (foreignRefKeySets.Count > 0)
                    MapToReference(type, item, value, prop, effectivePropType, xItem, foreignRefKeySets);
                else
                    ParseComplex(type, item, value, prop, effectivePropType, xItem, prop.ForeignKeys, writeTrack);
            }
            else if (value != null && prop.ReferenceType != PropertyReferenceType.Parent)
            {
                Dictionary<string, object> refKeySets = GetPrimaryValues(effectivePropType, value);
                if (refKeySets != null && refKeySets.Count > 0)
                {
                    MapToReference(type, item, value, prop, effectivePropType, xItem, refKeySets);
                }
                else
                {
                    bool hasPk = propertyService.HasPrimaryKey(effectivePropType);
                    string refCode = Create(effectivePropType, value, false, writeTrack);
                    if (!string.IsNullOrEmpty(refCode))
                    {
                        var xpe = new XElement(prop.PropertyName,
                            new XAttribute("refType", hasPk ? "reference" : "complex"),
                            refCode);
                        if (hasPk)
                        {
                            var generatedKeys = GetPrimaryValues(effectivePropType, value);
                            foreach (var kv in generatedKeys)
                                xpe.Add(new XAttribute(kv.Key, kv.Value));
                        }
                        xItem.Add(xpe);
                    }
                }
            }
            else if ((prop.ReferenceType == PropertyReferenceType.Foreign || prop.ReferenceType == PropertyReferenceType.SelfForeign) && prop.ForeignKeys != null)
            {
                Dictionary<string, object> foreignRefKeySets = ValueHelper.GetPropertyValues(type, item, prop.ForeignKeys.ToDictionary(s => s.LocalProperty, s => s.RemoteProperty));
                if (foreignRefKeySets.Count > 0)
                {
                    value = SelectItemsByExample(effectivePropType, PopulateItemProperties(effectivePropType, foreignRefKeySets)).FirstOrDefault();
                    ReferenceMap(type, effectivePropType, value, prop, xItem, foreignRefKeySets);
                }
            }
            else if (prop.ReferenceType == PropertyReferenceType.Parent && prop.ParentKeys != null && writeTrack != null)
            {
                string relationshipName = writeTrack.Via?.PropertyName;

                // The parent the row is written through, for the parent property its list pairs with (ISS-203). It
                // took any parent of the property's type: a row a parent wrote through a reference was made that
                // parent's child, and a row with two parent properties of one type got the same parent for both.
                if (writeTrack.Parent != null && effectivePropType == writeTrack.Parent.Type &&
                    ChildParentProperty(writeTrack.Via, writeTrack.Parent.Type, type)?.PropertyName == prop.PropertyName)
                {
                    var xpe = new XElement(prop.PropertyName,
                        new XAttribute("refType", "parent"),
                        writeTrack.Parent.Code);

                    if (relationshipName != null)
                        xpe.Add(new XAttribute("hostProp", relationshipName));

                    foreach (var parentKeyAtt in prop.ParentKeys)
                    {
                        PropertyInfo localProp = string.IsNullOrEmpty(parentKeyAtt.LocalProperty) ? null : type.GetProperty(parentKeyAtt.LocalProperty);
                        PropertyInfo remoteProp = effectivePropType.GetProperty(parentKeyAtt.RemoteProperty);
                        if (remoteProp != null)
                        {
                            object remoteValue = remoteProp.GetValue(writeTrack.Parent.Item);
                            if (!ValueHelper.IsNoValue(remoteValue, remoteProp.PropertyType))
                            {
                                xpe.SetAttributeValue(remoteProp.Name, remoteValue);
                                if (localProp != null)
                                {
                                    localProp.SetValue(item, remoteValue);
                                    RewriteKeyProperty(type, item, xItem, localProp.Name);
                                }
                            }
                        }
                    }
                    xItem.Add(xpe);
                }
                else
                {
                    object childParentItem = prop.Property.GetValue(item);
                    var validParentKeys = prop.ParentKeys.Where(s => !string.IsNullOrEmpty(s.LocalProperty)).ToList();
                    var localToRemoteParentProps = validParentKeys.ToDictionary(s => s.LocalProperty, s => s.RemoteProperty);
                    var remoteParentValues = ValueHelper.GetPropertyValues(type, item, localToRemoteParentProps);

                    if (childParentItem == null && remoteParentValues.Count > 0)
                    {
                        childParentItem = SelectItemsByExample(effectivePropType, PopulateItemProperties(effectivePropType, remoteParentValues)).FirstOrDefault();
                        if (childParentItem != null)
                            prop.Property.SetValue(item, childParentItem);
                    }
                    else if (childParentItem != null && remoteParentValues.Count == 0 && localToRemoteParentProps.Count > 0)
                    {
                        var remoteParentProps = propertyService.Properties(effectivePropType.FullName);
                        var localParentProps = propertyService.Properties(type.FullName);

                        foreach (var localToRemoteParentProp in localToRemoteParentProps)
                        {
                            var remoteParentKeyProp = remoteParentProps.FirstOrDefault(s => s.PropertyName == localToRemoteParentProp.Value);
                            var localParentKeyProp = localParentProps.FirstOrDefault(s => s.PropertyName == localToRemoteParentProp.Key);
                            if (remoteParentKeyProp != null && localParentKeyProp != null)
                            {
                                var remoteParentKeyValue = remoteParentKeyProp.Property.GetValue(childParentItem);
                                localParentKeyProp.Property.SetValue(item, remoteParentKeyValue);
                                RewriteKeyProperty(type, item, xItem, localParentKeyProp.PropertyName);
                            }
                        }
                    }

                    XRow xParentRow = GetRow(effectivePropType, childParentItem);
                    if (xParentRow?.Row != null)
                    {
                        // The list that pairs with this parent property (ISS-203): it was the parent's first list
                        // of the child's type.
                        PropertyInfoItem childrenProp = propertyService.Properties(effectivePropType.FullName)
                            .FirstOrDefault(s => s.ReferenceType == PropertyReferenceType.Children && s.CollectionItemType == type &&
                                ChildParentProperty(s, effectivePropType, type)?.PropertyName == prop.PropertyName);
                        writeTrack.Parent = new ReadWriteTrack
                        {
                            Code = $"{xParentRow.Page.GetFileCode()}.{xParentRow.Row.Attribute("code").Value}",
                            Item = childParentItem,
                            Type = effectivePropType
                        };
                        writeTrack.Via = childrenProp;

                        if (childrenProp != null)
                        {
                            XElement xParent = xParentRow.Row.Element(effectivePropType.Name);
                            var childPrimProps = GetPrimaryValues(type, item);
                            bool parentChanged = false;

                            XElement childrenElement = xParent.Element(childrenProp.PropertyName);
                            if (childrenElement == null)
                            {
                                childrenElement = new XElement(
                                    childrenProp.PropertyName,
                                    new XAttribute("dataType", type.FullName),
                                    new XAttribute("collType", childrenProp.TypeCategory == PropertyTypeCategory.Array ? "array" : "generic"),
                                    new XAttribute("refType", "children"));
                                xParent.Add(childrenElement);
                            }

                            // The parent's entry for this row is the one that names its code (ISS-135). It was found by
                            // the child's key values, so a child type without a key matched the first entry and took its
                            // place, and a keyed child whose key is taken took the listed child's entry before the key
                            // check refused it.
                            var xChild = childrenElement.Elements().FirstOrDefault(s => s.Value == writeTrack.Code);
                            if (xChild == null && childPrimProps != null && childPrimProps.Count > 0)
                            {
                                var byKey = childrenElement.Elements().FirstOrDefault(s =>
                                {
                                    foreach (var childPrimProp in childPrimProps)
                                    {
                                        if (!EntryKeyMatches(s.Attribute(childPrimProp.Key), childPrimProp.Value))
                                            return false;
                                    }
                                    return true;
                                });
                                // An entry with this child's key that names no row: a child deleted before its unlink
                                // took it out of the list (2.0 and earlier left such entries). A key names one row, so
                                // the entry is this child's now; left as it was, it hid the child from its parent
                                // (review of TASK-063). An entry that names a row is left alone, and this row gets an
                                // entry of its own: whether its key is taken is the key check's to decide, after the
                                // write (its undo takes the entry out), not the entry's, whose key attributes an update
                                // of the child alone doesn't write.
                                if (byKey != null && RowByItemCode(type, byKey.Value)?.Row == null)
                                {
                                    byKey.Value = writeTrack.Code;
                                    statementLog?.Linked.Add((effectivePropType, writeTrack.Parent.Code, type, writeTrack.Code));
                                    parentChanged = true;
                                    xChild = byKey;
                                }
                            }

                            if (xChild == null)
                            {
                                xChild = new XElement(type.Name, writeTrack.Code);
                                foreach (var childPrimProp in childPrimProps)
                                    xChild.Add(new XAttribute(childPrimProp.Key, EntryKeyText(childPrimProp.Value)));
                                childrenElement.Add(xChild);
                                statementLog?.Linked.Add((effectivePropType, writeTrack.Parent.Code, type, writeTrack.Code));
                                parentChanged = true;
                            }

                            if (parentChanged)
                                ioService.Save(xParentRow.Page);
                        }
                    }

                    if (writeTrack.Parent != null && effectivePropType == writeTrack.Parent.Type)
                    {
                        var xpe = new XElement(prop.PropertyName,
                            new XAttribute("refType", "parent"),
                            writeTrack.Parent.Code);

                        if (relationshipName != null)
                            xpe.Add(new XAttribute("hostProp", relationshipName));

                        foreach (var parentKeyAtt in prop.ParentKeys)
                        {
                            PropertyInfo localProp = string.IsNullOrEmpty(parentKeyAtt.LocalProperty) ? null : type.GetProperty(parentKeyAtt.LocalProperty);
                            PropertyInfo remoteProp = effectivePropType.GetProperty(parentKeyAtt.RemoteProperty);
                            if (remoteProp != null && localProp != null)
                            {
                                object localValue = localProp.GetValue(item);
                                object remoteValue = remoteProp.GetValue(writeTrack.Parent.Item);

                                if (ValueHelper.IsNoValue(localValue, localProp.PropertyType))
                                    break;

                                if (localValue.Equals(remoteValue))
                                    xpe.SetAttributeValue(remoteProp.Name, remoteValue);
                                else
                                    exceptionService.Throw(new ReservedChildException());
                            }
                        }
                        xItem.Add(xpe);
                    }
                }
            }
        }

        private void ParseComplex(Type type, object item, object value, PropertyInfoItem prop, Type effectivePropType, XElement xItem, IEnumerable<ForeignKeyAttribute> foreignKeyAtts, ReadWriteTrack writeTrack = null)
        {
            Dictionary<string, object> foreignKeyRefValues = ValueHelper.GetPropertyValues(effectivePropType, value, foreignKeyAtts.Select(s => s.RemoteProperty));
            XElement xProp = WriteItem(prop.PropertyName, effectivePropType, value, foreignKeyRefValues, writeTrack);
            if (xProp != null)
                xItem.Add(xProp);

            WritePropertyMap(type, item, effectivePropType, value, xItem, foreignKeyAtts.ToDictionary(s => s.RemoteProperty, s => s.LocalProperty));
        }

        private void MapToReference(Type type, object item, object value, PropertyInfoItem prop, Type effectivePropType, XElement xItem, Dictionary<string, object> refKeySets)
        {
            object reference = null;
            if (item != null)
            {
                reference = SelectItemsByExample(effectivePropType, PopulateItemProperties(effectivePropType, refKeySets)).FirstOrDefault();
                if (reference != null && prop.Property.GetValue(item) == null)
                    prop.Property.SetValue(item, reference);
            }

            if (reference != null)
            {
                Dictionary<string, string> mappingProps = ReferenceMap(type, effectivePropType, reference, prop, xItem, refKeySets);
                if (mappingProps != null)
                    WritePropertyMap(type, item, effectivePropType, reference, xItem, mappingProps);
            }
            else if (value != null)
            {
                XElement xProp = WriteItem(prop.PropertyName, effectivePropType, value, refKeySets);
                if (xProp != null)
                    xItem.Add(xProp);
                if (prop.ForeignKeys != null)
                {
                    WritePropertyMap(type, item, effectivePropType, value, xItem, prop.ForeignKeys.ToDictionary(s => s.RemoteProperty, s => s.LocalProperty));
                }
            }
        }

        private Dictionary<string, string> ReferenceMap(Type type, Type propType, object propItem, PropertyInfoItem prop, XElement xItem, Dictionary<string, object> refKeySets)
        {
            Dictionary<string, string> mappingProps = null;
            XRow xRow = GetRow(propType, propItem);
            if (xRow?.Row != null)
            {
                string refCode = $"{System.IO.Path.GetFileNameWithoutExtension(xRow.Page.Path)}.{xRow.Row.Attribute("code").Value}";
                var xProp = xItem.Descendants(prop.PropertyName).FirstOrDefault();
                if (xProp != null)
                {
                    xProp.Value = refCode;
                }
                else
                {
                    xProp = new XElement(prop.PropertyName, new XAttribute("refType", "reference"), refCode);
                    xItem.Add(xProp);
                }

                foreach (var refKeySet in refKeySets)
                {
                    xProp.SetAttributeValue(refKeySet.Key, refKeySet.Value);
                }

                if (prop.ReferenceType == PropertyReferenceType.Parent && prop.ParentKeys != null)
                    mappingProps = prop.ParentKeys.Where(s => !string.IsNullOrEmpty(s.LocalProperty)).ToDictionary(s => s.RemoteProperty, s => s.LocalProperty);
                // The referenced row's keys are written to the local key columns, so the key and the element name one row.
                // A reference to a type that names this one back (SelfForeign) was left out: repointed by its key alone
                // while it held the object read, it stored the new key and the old row's element, and a delete that
                // follows the element (Cascade = Delete) took the old row.
                if ((prop.ReferenceType == PropertyReferenceType.Foreign || prop.ReferenceType == PropertyReferenceType.SelfForeign) &&
                    prop.ForeignKeys != null)
                    mappingProps = prop.ForeignKeys.ToDictionary(s => s.RemoteProperty, s => s.LocalProperty);
            }
            return mappingProps;
        }
    }
}
