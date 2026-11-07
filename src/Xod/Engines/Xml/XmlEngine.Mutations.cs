using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xod.Engines.Serializers;
using Xod.Helpers;
using Xod.Infra;
using Xod.Services;

namespace Xod.Engines.Xml
{
    public partial class XmlEngine
    {
        private string Create(Type type, object item, bool shallowWrite = false, ReadWriteTrack writeTrack = null)
        {
            if (item == null)
                exceptionService.Throw(new ArgumentNullException(nameof(item)));

            if (type == typeof(object))
                type = item.GetActualType();

            propertyService.LoadType(type);
            EnsureIndexHydrated(type);

            TriggerEventArgs trigger = null;
            if (BeforeAction != null || AfterAction != null)
            {
                trigger = new TriggerEventArgs
                {
                    Item = item,
                    Type = type,
                    Action = DatabaseActions.Insert
                };

                BeforeAction?.Invoke(this, trigger);
                if (trigger.Cancel)
                    return null;
            }

            // Validate primary key types
            ValidatePrimaryKeyTypes(type);

            // Validate required properties before allocating autonumber sequence values
            ValidateRequiredProperties(type, item);

            // Set auto number values
            Dictionary<string, dynamic> autonumbers = autonumberService.Autonumber(type, item);

            // Enforce PrimaryKey and UniqueKey constraints
            long tUnique = WriteDiagnostics.Begin();
            ValidateUniqueAndPrimaryConstraints(type, item, existingCodeToIgnore: null);
            WriteDiagnostics.End(ref WriteDiagnostics.UniqueTicks, tUnique);

            string refCode = AddToPage(type, item, autonumbers, shallowWrite, writeTrack);

            if (trigger != null)
                AfterAction?.Invoke(this, trigger);
            return refCode;
        }

        private string AddToPage(Type type, object item, Dictionary<string, dynamic> autonumbers = null, bool shallowWrite = false, ReadWriteTrack writeTrack = null)
        {
            string itemCode = null;
            string itemPath = null;

            string typeName = type.FullName;
            if (typeName == "System.Object")
                typeName = item.GetActualType().FullName;

            string tableFileName = $"{typeName}.xtab";

            long tOpenTable = WriteDiagnostics.Begin();
            XFile tableFile = ioService.OpenFileOrCreate(tableFileName, typeof(Table));
            WriteDiagnostics.End(ref WriteDiagnostics.OpenTableTicks, tOpenTable);
            if (tableFile != null)
            {
                var meta = propertyService.GetMetadata(type);
                if (meta != null && meta.IsPureScalarStandalone && !shallowWrite && writeTrack == null)
                {
                    bool inTx = activeTransaction != null && activeTransaction.IsActive;
                    long tRaw = WriteDiagnostics.Begin();
                    bool rawOk = ioService.TryAppendRawRowToPage(tableFile, item, meta, exceptionService, pageSize, inTx, out itemPath, out XElement rawPageElement, out bool rawTableModified);
                    WriteDiagnostics.End(ref WriteDiagnostics.RawTicks, tRaw);
                    if (rawOk)
                    {
                        if (indexService.IndexItem(type, item, itemPath, rawPageElement))
                            rawTableModified = true;

                        if (rawTableModified)
                        {
                            long tSave = WriteDiagnostics.Begin();
                            ioService.Save(tableFile);
                            WriteDiagnostics.End(ref WriteDiagnostics.SaveTicks, tSave);
                            if (!ioService.IsBatchOrBuffered)
                                indexService.SaveToDisk(type);
                        }
                        return itemPath;
                    }
                }

                long tOpenPage = WriteDiagnostics.Begin();
                XFile pageFile = ioService.OpenPageOrCreate(tableFile, out bool tableModified, out XElement tablePageElement);
                WriteDiagnostics.End(ref WriteDiagnostics.OpenPageTicks, tOpenPage);
                if (pageFile != null)
                {
                    string pageFileName = System.IO.Path.GetFileName(pageFile.Path);
                    string pageCode = System.IO.Path.GetFileNameWithoutExtension(pageFile.Path);
                    long tPick = WriteDiagnostics.Begin();
                    itemCode = ValueHelper.PickCode();
                    WriteDiagnostics.End(ref WriteDiagnostics.PickCodeTicks, tPick);
                    itemPath = $"{pageCode}.{itemCode}";

                    var itemCreateTrack = new ReadWriteTrack
                    {
                        Item = item,
                        Type = type,
                        Code = itemPath,
                        Parent = writeTrack
                    };

                    long tWrite = WriteDiagnostics.Begin();
                    XElement xItem = Write(type, item, shallowWrite, itemCreateTrack);
                    WriteDiagnostics.End(ref WriteDiagnostics.WriteTicks, tWrite);
                    WriteDiagnostics.EndWrite();
                    WriteDiagnostics.Entity();
                    if (xItem != null)
                    {
                        long tAddRow = WriteDiagnostics.Begin();
                        var re = new XElement("Row", new XAttribute("code", itemCode), xItem);
                        pageFile.AddRow(itemCode, re);
                        Services.FaultInjection.RowWritten();
                        WriteDiagnostics.End(ref WriteDiagnostics.AddRowTicks, tAddRow);

                        if (tablePageElement != null)
                        {
                            XAttribute countAttr = tablePageElement.Attribute("count");
                            if (countAttr != null && int.TryParse(countAttr.Value, out int currentCount))
                            {
                                countAttr.Value = (currentCount + 1).ToString();
                            }
                            else
                            {
                                tablePageElement.SetAttributeValue("count", pageFile.Rows().Count());
                            }

                            if (ioService.IsBatchOrBuffered)
                                tableModified = true;
                        }

                        long tIndex = WriteDiagnostics.Begin();
                        if (indexService.IndexItem(type, item, itemPath, tablePageElement))
                            tableModified = true;
                        WriteDiagnostics.End(ref WriteDiagnostics.IndexTicks, tIndex);

                        long tPageSave = WriteDiagnostics.Begin();
                        ioService.Save(pageFile);
                        WriteDiagnostics.End(ref WriteDiagnostics.PageSaveTicks, tPageSave);
                        WriteDiagnostics.PageSaved();

                        long currentPageSize = pageFile.LastSavedSize > 0 ? pageFile.LastSavedSize : ioService.Size(pageFile.Path);
                        if (currentPageSize >= pageSize && tablePageElement != null)
                        {
                            tablePageElement.SetAttributeValue("full", "true");
                            tableModified = true;
                            if (activeTransaction == null)
                            {
                                ioService.FlushFullPageInBatch(pageFile);
                            }
                        }
                    }

                    if (tableModified)
                    {
                        long tTableSave = WriteDiagnostics.Begin();
                        ioService.Save(tableFile);
                        WriteDiagnostics.End(ref WriteDiagnostics.TableSaveTicks, tTableSave);
                        WriteDiagnostics.TableSaved();
                        if (!ioService.IsBatchOrBuffered)
                            indexService.SaveToDisk(type);
                    }
                }
            }
            return itemPath;
        }

        private XElement Write(Type type, object item, bool shallowWrite = false, ReadWriteTrack writeTrack = null, UpdateFilter filter = null)
        {
            if (item == null)
                return null;

            if (type == typeof(object))
                type = item.GetActualType();

            var meta = propertyService.GetMetadata(type);
            var xItem = new XElement(type.Name);

            PropertyInfoItem[] props = meta != null
                ? meta.WriteProperties
                : propertyService.Properties(type.FullName)
                    .Where(s => !s.IsNotMapped)
                    .OrderByDescending(s => s.PropertyType != null && ValueHelper.IsScalarType(s.PropertyType))
                    .ToArray();

            if (filter?.Properties != null)
            {
                IEnumerable<PropertyInfoItem> filterProps = filter.Behavior == UpdateFilterBehavior.Skip
                    ? props.Where(s => !filter.Properties.Contains(s.PropertyName))
                    : props.Where(s => filter.Properties.Contains(s.PropertyName));

                foreach (var prop in filterProps)
                    WriteProperty(type, item, xItem, prop, shallowWrite, writeTrack);
            }
            else
            {
                for (int i = 0; i < props.Length; i++)
                    WriteProperty(type, item, xItem, props[i], shallowWrite, writeTrack);
            }

            return xItem;
        }

        private static bool ShouldOmitScalarValue(object value, Type effectivePropType, PropertyInfoItem prop, bool shallowWrite)
        {
            if (value == null)
                return true;

            bool isTypeDefault = value.Equals(ValueHelper.DefaultOf(effectivePropType));
            if (shallowWrite || prop.IsPrimaryKey || prop.IsRelationshipKey)
            {
                return isTypeDefault;
            }

            if (isTypeDefault && prop.DefaultValue != null && !prop.DefaultValue.Equals(ValueHelper.DefaultOf(effectivePropType)))
            {
                return true;
            }

            return value switch
            {
                string s => string.IsNullOrEmpty(s),
                Guid g => g == Guid.Empty,
                DateTime dt => dt == default,
                DateOnly d => d == default,
                TimeSpan ts => ts == default,
                TimeOnly to => to == default,
                _ => false
            };
        }

        private XElement WriteProperty(Type type, object item, XElement xItem, PropertyInfoItem prop, bool shallowWrite = false, ReadWriteTrack writeTrack = null)
        {
            object value = prop.GetValue(item);
            if (prop.IsRequired && IsMissingRequiredValue(value))
            {
                exceptionService.Throw(new RequiredPropertyException(prop.PropertyName));
            }

            XElement propertyElement = null;
            Type effectivePropType = prop.PropertyType;

            if (prop.IsGenericType && value != null)
            {
                effectivePropType = value.GetActualType();
                propertyService.LoadType(effectivePropType);
            }

            // Scalar / value types
            if (ValueHelper.IsScalarType(effectivePropType))
            {
                if (ShouldOmitScalarValue(value, effectivePropType, prop, shallowWrite))
                    return null;

                string formattedValue = ValueHelper.FormatScalar(value);

                if (prop.ValuePosition == ValuePosition.Attribute)
                {
                    xItem.SetAttributeValue(prop.PropertyName, formattedValue);
                }
                else if (effectivePropType == typeof(string))
                {
                    string strVal = value.ToString();
                    strVal = prop.Encryption switch
                    {
                        CryptoMethod.MD5 or CryptoMethod.SHA1 or CryptoMethod.SHA256 or CryptoMethod.SHA512 =>
                            CryptoHelper.HashProperty(strVal, prop.Encryption),
                        CryptoMethod.AES256 => CryptoHelper.EncryptProperty(strVal, password),
                        _ => strVal
                    };

                    propertyElement = prop.IsMarkup
                        ? new XElement(prop.PropertyName, new XCData(strVal))
                        : new XElement(prop.PropertyName, strVal);

                    xItem.Add(propertyElement);
                }
                else
                {
                    propertyElement = new XElement(prop.PropertyName, formattedValue);
                    xItem.Add(propertyElement);
                }
            }
            // Dictionary<TKey, TValue> support
            else if (prop.TypeCategory == PropertyTypeCategory.Dictionary)
            {
                if (value is IDictionary dict && dict.Count > 0)
                {
                    propertyElement = new XElement(prop.PropertyName,
                        new XAttribute("collType", "dictionary"));

                    foreach (DictionaryEntry entry in dict)
                    {
                        if (entry.Key != null)
                        {
                            propertyElement.Add(new XElement("Entry",
                                new XAttribute("key", ValueHelper.FormatScalar(entry.Key)),
                                entry.Value != null ? ValueHelper.FormatScalar(entry.Value) : string.Empty));
                        }
                    }
                    xItem.Add(propertyElement);
                }
            }
            // Array and generic collection values
            else if ((effectivePropType.IsArray && effectivePropType.GetArrayRank() == 1) ||
                     (effectivePropType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(effectivePropType)))
            {
                if (value == null)
                    return null;

                Type itmType;
                string collType;
                if (effectivePropType.IsArray)
                {
                    itmType = effectivePropType.GetElementType();
                    collType = "array";
                }
                else
                {
                    itmType = effectivePropType.GetGenericArguments().FirstOrDefault();
                    collType = "generic";
                }

                if (itmType == null)
                    return null;

                propertyElement = new XElement(prop.PropertyName,
                    new XAttribute("dataType", itmType.FullName),
                    new XAttribute("collType", collType));

                if (ValueHelper.IsScalarType(itmType))
                {
                    if (value is IEnumerable items)
                    {
                        foreach (var itm in items)
                        {
                            if (itm != null)
                                propertyElement.Add(new XElement(itmType.Name, ValueHelper.FormatScalar(itm)));
                        }
                    }
                }
                else if (!shallowWrite)
                {
                    var children = (value as IEnumerable)?.Cast<object>();
                    if (children != null)
                        ParseChildren(type, item, itmType, children, prop, propertyElement, shallowWrite, writeTrack);
                }

                xItem.Add(propertyElement);
            }
            // Reference type values
            else if (!shallowWrite && effectivePropType.IsClass)
            {
                ParseReference(type, item, value, prop, effectivePropType, xItem, writeTrack);
            }

            return propertyElement;
        }

        private bool UpdateItem(Type type, object item, object newItem, ReadWriteTrack track = null, UpdateFilter filter = null)
        {
            propertyService.LoadType(type);
            EnsureIndexHydrated(type);

            if (item == null || newItem == null)
                exceptionService.Throw(new ArgumentNullException());

            if (track?.Parent != null && RecursionError(track, track.Parent))
                return false;

            var trigger = new TriggerEventArgs
            {
                Item = newItem,
                Type = type,
                Action = DatabaseActions.Update
            };

            BeforeAction?.Invoke(this, trigger);
            if (trigger.Cancel)
                return false;

            bool updated = false;
            var meta = propertyService.GetMetadata(type);

            if (meta != null &&
                meta.IsStandalone &&
                track == null &&
                options.EnableIndexing &&
                meta.PrimaryKeyProperties.Length > 0 &&
                TryLookupCodesByProperties(type, item, meta.PrimaryKeyProperties, out var fastPkCodes, out bool fastPkCovered) &&
                fastPkCovered)
            {
                if (fastPkCodes.Count == 0)
                    return false;

                foreach (var itemCode in fastPkCodes)
                {
                    int dot = itemCode.IndexOf('.');
                    if (dot <= 0 || dot >= itemCode.Length - 1)
                        continue;

                    string pageCode = itemCode.Substring(0, dot);
                    string pageFileName = pageCode + ".xpag";
                    string rowCode = itemCode.Substring(dot + 1);

                    if (ioService.TryGetRowFromRawPage(pageFileName, rowCode, out XElement rawRow, out int startOffset, out int byteLen))
                    {
                        ValidateUniqueAndPrimaryConstraints(type, newItem, existingCodeToIgnore: itemCode);

                        var itemTrack = new ReadWriteTrack
                        {
                            Item = item,
                            Type = type,
                            Code = itemCode,
                            Parent = null
                        };

                        XElement xOldRow = rawRow.Element(type.Name);
                        XElement xNewRow = Write(type, newItem, false, itemTrack, filter);
                        if (filter?.Properties != null)
                        {
                            if (filter.Behavior == UpdateFilterBehavior.Skip)
                            {
                                foreach (var excProp in filter.Properties)
                                {
                                    XElement excPropElm = xOldRow.Element(excProp);
                                    if (excPropElm != null)
                                        xNewRow.Add(new XElement(excPropElm));
                                    else
                                        xNewRow.Element(excProp)?.Remove();
                                }
                            }
                            else
                            {
                                foreach (var excPropElm in xOldRow.Elements().Where(s => !filter.Properties.Contains(s.Name.LocalName)))
                                    xNewRow.Add(new XElement(excPropElm));
                            }
                        }

                        if (!xOldRow.ElementEquals(xNewRow))
                        {
                            indexService.RemoveRowElement(type, rawRow, itemCode, persist: false);
                            indexService.IndexItem(type, newItem, itemCode);
                            var newRowWrapper = new XElement("Row", new XAttribute("code", rowCode), xNewRow);
                            ioService.SpliceRowInRawPage(pageFileName, startOffset, byteLen, newRowWrapper, rowCode, out _);
                        }

                        updated = true;
                        continue;
                    }
                }

                if (updated)
                {
                    AfterAction?.Invoke(this, trigger);
                    return true;
                }
            }

            XRowTree rowTree = GetTree(type, item);
            if (rowTree != null && rowTree.Rows.Count > 0)
            {
                IEnumerable<PropertyInfoItem> props;
                if (filter?.Properties != null)
                {
                    props = filter.Behavior == UpdateFilterBehavior.Skip
                        ? propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped && !filter.Properties.Contains(s.PropertyName))
                        : propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped && filter.Properties.Contains(s.PropertyName));
                    props = props.OrderByDescending(s => s.PropertyType != null && ValueHelper.IsScalarType(s.PropertyType)).ToList();
                }
                else if (meta != null)
                {
                    props = meta.WriteProperties;
                }
                else
                {
                    props = propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped)
                        .OrderByDescending(s => s.PropertyType != null && ValueHelper.IsScalarType(s.PropertyType)).ToList();
                }

                string pageCode = rowTree.Page?.GetFileCode();

                foreach (var row in rowTree.Rows)
                {
                    XAttribute rowCodeAtt = row.Attribute("code");
                    string itemCode = rowCodeAtt != null && pageCode != null
                        ? $"{pageCode}.{rowCodeAtt.Value}"
                        : "";

                    ValidateUniqueAndPrimaryConstraints(type, newItem, existingCodeToIgnore: itemCode);

                    if (meta == null || !meta.IsStandalone)
                        itemsCacheService.Clear(type, rowCodeAtt?.Value);

                    var itemTrack = new ReadWriteTrack
                    {
                        Item = item,
                        Type = type,
                        Code = itemCode,
                        Parent = track
                    };

                    XElement xOldRow = row.Element(type.Name);
                    XElement xNewRow = Write(type, newItem, false, itemTrack, filter);
                    if (filter?.Properties != null)
                    {
                        if (filter.Behavior == UpdateFilterBehavior.Skip)
                        {
                            foreach (var excProp in filter.Properties)
                            {
                                XElement excPropElm = xOldRow.Element(excProp);
                                if (excPropElm != null)
                                    xNewRow.Add(new XElement(excPropElm));
                                else
                                    xNewRow.Element(excProp)?.Remove();
                            }
                        }
                        else
                        {
                            foreach (var excPropElm in xOldRow.Elements().Where(s => !filter.Properties.Contains(s.Name.LocalName)))
                                xNewRow.Add(new XElement(excPropElm));
                        }
                    }

                    if (!row.Element(type.Name).ElementEquals(xNewRow))
                    {
                        if (meta != null && meta.IsStandalone)
                        {
                            indexService.RemoveRowElement(type, row, itemCode, persist: false);
                            row.ReplaceNodes(xNewRow);
                            indexService.IndexItem(type, newItem, itemCode);
                        }
                        else
                        {
                            var cascadeDelProps = props.Where(s => s.Cascade.HasFlag(CascadeOptions.Delete)).Select(s => s.PropertyName).ToHashSet();
                            string[] delRefTypes = { "complex", "children" };

                            var rowCollRefNodes = row.Element(type.Name).Elements().Where(s =>
                            {
                                XAttribute refTypeAtt = s.Attribute("refType");
                                XAttribute collTypeAtt = s.Attribute("collType");
                                return collTypeAtt != null && refTypeAtt != null &&
                                    (delRefTypes.Contains(refTypeAtt.Value) ||
                                    (refTypeAtt.Value == "reference" && cascadeDelProps.Contains(s.Name.LocalName)));
                            }).SelectMany(s => s.Elements());

                            var newCollRefNodes = xNewRow.Elements().Where(s =>
                            {
                                XAttribute refTypeAtt = s.Attribute("refType");
                                XAttribute collTypeAtt = s.Attribute("collType");
                                return collTypeAtt != null && refTypeAtt != null &&
                                    (delRefTypes.Contains(refTypeAtt.Value) ||
                                    (refTypeAtt.Value == "reference" && cascadeDelProps.Contains(s.Name.LocalName)));
                            }).SelectMany(s => s.Elements());

                            var newCollRefNodesString = newCollRefNodes.Select(s => s.ToString()).ToHashSet();
                            var delRefNodes = rowCollRefNodes.Where(s => !newCollRefNodesString.Contains(s.ToString())).ToList();
                            foreach (var code in delRefNodes)
                            {
                                XElement xDelRef = GetRawData(code.Value);
                                if (xDelRef == null) continue;

                                Type delRefType = ioService.GetItemType(code.Value);
                                if (delRefType == null) continue;

                                object delRef = Read(delRefType, xDelRef);
                                Delete(delRefType, delRef);
                            }

                            indexService.RemoveRowElement(type, row, itemCode, persist: false);
                            row.ReplaceNodes(xNewRow);
                            indexService.IndexItem(type, newItem, itemCode);

                            var propRefProps = props.Where(s =>
                                s.TypeCategory != PropertyTypeCategory.None &&
                                s.TypeCategory != PropertyTypeCategory.Dictionary &&
                                s.ReferenceType != PropertyReferenceType.Complex &&
                                s.ReferenceType != PropertyReferenceType.SelfForeign);

                            foreach (var propRefProp in propRefProps)
                            {
                                object oldRefItem = propRefProp.Property.GetValue(item);
                                object newRefItem = propRefProp.Property.GetValue(newItem);

                                if (propRefProp.ReferenceType == PropertyReferenceType.Parent)
                                {
                                    bool sameParent = true;
                                    Dictionary<string, object> oldParentKeyValues = null;
                                    Dictionary<string, object> newParentKeyValues = null;
                                    string[] baseParentKeys = null;

                                    if (oldRefItem != null)
                                    {
                                        oldParentKeyValues = GetPrimaryValues(propRefProp.PropertyType, oldRefItem);
                                        baseParentKeys = oldParentKeyValues.Select(s => s.Key).ToArray();
                                    }

                                    if (newRefItem != null)
                                    {
                                        newParentKeyValues = GetPrimaryValues(propRefProp.PropertyType, newRefItem);
                                        baseParentKeys ??= newParentKeyValues.Select(s => s.Key).ToArray();
                                    }

                                    if (baseParentKeys != null)
                                    {
                                        foreach (var parentKey in baseParentKeys)
                                        {
                                            var oldKeyValue = oldParentKeyValues != null && oldParentKeyValues.TryGetValue(parentKey, out var okv) ? okv : null;
                                            var newKeyValue = newParentKeyValues != null && newParentKeyValues.TryGetValue(parentKey, out var nkv) ? nkv : null;
                                            if (!Equals(oldKeyValue, newKeyValue))
                                            {
                                                sameParent = false;
                                                break;
                                            }
                                        }
                                    }

                                    if (!sameParent)
                                        RemoveFromParent(type, itemCode, propRefProp, oldRefItem);

                                    continue;
                                }

                                if (newRefItem == null)
                                    continue;

                                if (ReferenceEquals(oldRefItem, newRefItem) || (oldRefItem != null && oldRefItem.Equals(newRefItem)))
                                    continue;

                                if (oldRefItem != null &&
                                    (propRefProp.TypeCategory == PropertyTypeCategory.ValueTypeArray ||
                                     propRefProp.TypeCategory == PropertyTypeCategory.ValueTypeCollection))
                                {
                                    var oldRefItems = ((IEnumerable)oldRefItem).Cast<object>().ToList();
                                    var newRefItems = ((IEnumerable)newRefItem).Cast<object>().ToList();
                                    if (TypeExtentions.ListsEqual(oldRefItems, newRefItems))
                                        continue;
                                }

                                if (propRefProp.TypeCategory == PropertyTypeCategory.Class)
                                {
                                    if (oldRefItem != null)
                                        UpdateItem(propRefProp.PropertyType, oldRefItem, newRefItem, itemTrack);
                                }
                                else if (propRefProp.TypeCategory == PropertyTypeCategory.Array ||
                                         propRefProp.TypeCategory == PropertyTypeCategory.GenericCollection)
                                {
                                    Type childType = propRefProp.TypeCategory == PropertyTypeCategory.Array
                                        ? propRefProp.PropertyType.GetElementType()
                                        : propRefProp.PropertyType.GetGenericArguments().FirstOrDefault();

                                    var children = (newRefItem as IEnumerable)?.Cast<object>().ToList();
                                    if (children != null && children.Count > 0)
                                    {
                                        foreach (var child in children)
                                            Update(childType, child);
                                    }
                                }
                            }
                        }
                    }

                    ioService.Save(rowTree.Page);
                    updated = true;
                }
            }

            if (updated)
            {
                AfterAction?.Invoke(this, trigger);
            }

            return updated;
        }

        private void WritePropertyMap(Type type, object item, Type propType, object value, XElement xItem, Dictionary<string, string> relationshipProps)
        {
            foreach (var relationshipProp in relationshipProps)
            {
                PropertyInfo remoteProp = propType.GetProperty(relationshipProp.Key);
                if (remoteProp != null)
                {
                    object remoteKeyValue = remoteProp.GetValue(value);
                    PropertyInfo localProp = type.GetProperty(relationshipProp.Value);
                    if (remoteKeyValue != null && localProp != null)
                    {
                        object localKeyValue = localProp.GetValue(item);
                        if (!Equals(remoteKeyValue, ValueHelper.DefaultOf(localProp.PropertyType)) && !remoteKeyValue.Equals(localKeyValue))
                        {
                            localProp.SetValue(item, remoteKeyValue);
                            var xLocalValue = xItem.Descendants(localProp.Name).FirstOrDefault();
                            if (xLocalValue != null)
                                xLocalValue.Value = ValueHelper.FormatScalar(remoteKeyValue);
                            else
                                xItem.Add(new XElement(localProp.Name, ValueHelper.FormatScalar(remoteKeyValue)));
                        }
                    }
                }
            }
        }

        private XElement WriteItem(string name, Type type, object item, Dictionary<string, object> refKeySets, ReadWriteTrack writeTrack = null)
        {
            string refCode = null;
            if (refKeySets != null)
            {
                object refItem = null;
                if (refKeySets.Count > 0)
                {
                    refItem = SelectItemsByExample(type, PopulateItemProperties(type, refKeySets)).FirstOrDefault();
                }

                if (refItem != null)
                {
                    XRow xRow = GetRow(type, refItem);
                    if (xRow?.Row != null)
                        refCode = $"{System.IO.Path.GetFileNameWithoutExtension(xRow.Page.Path)}.{xRow.Row.Attribute("code").Value}";
                }
                else
                {
                    refCode = Create(type, item, false, writeTrack);
                }
            }

            Dictionary<string, object> keyValues = GetPrimaryValues(type, item);
            var xpe = new XElement(name, new XAttribute("refType", "reference"), refCode);
            if (!string.IsNullOrEmpty(refCode) && keyValues != null)
            {
                foreach (var keyValue in keyValues)
                    xpe.Add(new XAttribute(keyValue.Key, keyValue.Value));
            }
            return xpe;
        }

        public object Insert(Type type, object item, bool lazyLoad = false)
        {
            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                propertyService.LoadType(type);
                Create(type, item, lazyLoad);
                return GetPrimaryValues(type, item);
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public int InsertRange(Type type, IEnumerable<object> items, bool lazyLoad = false)
        {
            if (items == null) return 0;

            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                propertyService.LoadType(type);
                ioService.BeginBatch();
                int count = 0;
                try
                {
                    foreach (var item in items)
                    {
                        Create(type, item, lazyLoad);
                        count++;
                    }
                }
                finally
                {
                    ioService.EndBatch();
                    indexService.FlushDirtyIndexes();
                }
                return count;
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public object InsertOrUpdate(Type type, object item, bool lazyLoad = false, UpdateFilter filter = null)
        {
            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                propertyService.LoadType(type);
                var primValues = GetPrimaryValues(type, item);

                object reference = null;
                if (primValues != null && primValues.Count > 0)
                    reference = SelectItemsByExample(type, PopulateItemProperties(type, primValues), "*").FirstOrDefault();

                if (reference != null)
                    Update(type, reference, item, filter);
                else
                    Create(type, item, lazyLoad);

                return GetPrimaryValues(type, item);
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public bool Update(Type type, object item, object newItem, UpdateFilter filter = null)
        {
            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                return UpdateItem(type, item, newItem, null, filter);
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public bool Update(Type type, object item, UpdateFilter filter = null)
        {
            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                propertyService.LoadType(type);
                if (item == null)
                    exceptionService.Throw(new ArgumentNullException(nameof(item)));

                var meta = propertyService.GetMetadata(type);
                if (meta != null && meta.IsStandalone)
                {
                    if (meta.PrimaryKeyProperties.Length == 0)
                        exceptionService.Throw(new MissingPrimaryKeyValueException());

                    return UpdateItem(type, item, item, null, filter);
                }

                var primProps = propertyService.Properties(type.FullName)
                    .Where(s => s.IsPrimaryKey)
                    .Select(s => s.PropertyName)
                    .ToList();

                if (primProps.Count == 0)
                    exceptionService.Throw(new MissingPrimaryKeyValueException());

                object old = SelectItemsByExample(type, FilterItemProperties(type, item, primProps), "*").FirstOrDefault();
                if (old != null)
                    return UpdateItem(type, old, item, null, filter);

                return false;
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public bool Delete(Type type, object item) => Delete(type, item, false);

        public bool Delete(Type type, object item, bool isCascade)
        {
            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                propertyService.LoadType(type);
                if (item == null)
                    exceptionService.Throw(new ArgumentNullException(nameof(item)));

                var trigger = new TriggerEventArgs
                {
                    Item = item,
                    Type = type,
                    Action = DatabaseActions.Delete
                };

                BeforeAction?.Invoke(this, trigger);
                if (trigger.Cancel)
                    return false;

                bool shouldBatch = !isCascade && !ioService.IsBatchOrBuffered;
                if (shouldBatch)
                    ioService.BeginBatch();
                try
                {
                    bool deleted = false;
                    var meta = propertyService.GetMetadata(type);

                    if (meta != null &&
                        options.EnableIndexing &&
                        meta.PrimaryKeyProperties.Length > 0 &&
                        TryLookupCodesByProperties(type, item, meta.PrimaryKeyProperties, out var fastPkCodes, out bool fastPkCovered) &&
                        fastPkCovered)
                    {
                        if (fastPkCodes.Count == 0)
                            return false;

                        foreach (var itemCode in fastPkCodes)
                        {
                            if (DeleteByItemCode(type, itemCode, isCascade))
                                deleted = true;
                        }

                        if (deleted)
                        {
                            AfterAction?.Invoke(this, trigger);
                            return true;
                        }
                    }

                XRowTree rowTree = GetTree(type, item);
                if (rowTree != null && rowTree.Rows.Count > 0)
                {
                    string pageCode = rowTree.Page?.GetFileCode();

                    foreach (var row in rowTree.Rows.ToList())
                    {
                        XAttribute rowCodeAtt = row.Attribute("code");
                        string itemCode = rowCodeAtt != null && pageCode != null
                            ? $"{pageCode}.{rowCodeAtt.Value}"
                            : "";

                        if (meta == null || !meta.IsStandalone)
                            itemsCacheService.Clear(type, rowCodeAtt?.Value);
                        indexService.RemoveRowElement(type, row, itemCode);

                        if (meta == null || meta.HasCascadeDeleteOrParentProperties)
                        {
                            // Ensure full entity graph is loaded for cascade delete if caller passed a shell object
                            object fullEntity = Read(type, row, "*") ?? item;

                            var propRefProps = propertyService.Properties(type.FullName).Where(s =>
                                s.ReferenceType != PropertyReferenceType.Parent &&
                                s.ReferenceType != PropertyReferenceType.SelfForeign &&
                                s.Cascade.HasFlag(CascadeOptions.Delete) &&
                                s.TypeCategory != PropertyTypeCategory.None &&
                                s.TypeCategory != PropertyTypeCategory.Dictionary);

                            foreach (var propRefProp in propRefProps)
                            {
                                object refItem = propRefProp.Property.GetValue(fullEntity) ?? propRefProp.Property.GetValue(item);
                                if (refItem == null)
                                    continue;

                                if (propRefProp.TypeCategory == PropertyTypeCategory.Class)
                                {
                                    Delete(propRefProp.PropertyType, refItem, isCascade: true);
                                }
                                else
                                {
                                    Type childType = propRefProp.TypeCategory == PropertyTypeCategory.Array
                                        ? propRefProp.PropertyType.GetElementType()
                                        : propRefProp.PropertyType.GetGenericArguments().FirstOrDefault();

                                    if (childType == null)
                                        continue;

                                    var children = (refItem as IEnumerable)?.Cast<object>().ToList();
                                    if (children != null)
                                    {
                                        foreach (var child in children)
                                            Delete(childType, child, isCascade: true);
                                    }
                                }
                            }

                            if (!isCascade)
                            {
                                var itemPrmVals = GetPrimaryValues(type, fullEntity);
                                if (itemPrmVals != null && itemPrmVals.Count > 0)
                                {
                                    var parRefProps = propertyService.Properties(type.FullName)
                                        .Where(s => s.ReferenceType == PropertyReferenceType.Parent);

                                    foreach (var parRefProp in parRefProps)
                                    {
                                        object parentValue = parRefProp.Property.GetValue(fullEntity) ?? parRefProp.Property.GetValue(item);
                                        Dictionary<string, object> parentPrmVals;

                                        if (parentValue == null)
                                        {
                                            parentPrmVals = new Dictionary<string, object>();
                                            if (parRefProp.ParentKeys != null)
                                            {
                                                foreach (var pk in parRefProp.ParentKeys)
                                                {
                                                    var pkProp = propertyService.Properties(type.FullName)
                                                        .FirstOrDefault(s => s.PropertyName == pk.LocalProperty);
                                                    if (pkProp != null)
                                                    {
                                                        object pkVal = pkProp.Property.GetValue(fullEntity);
                                                        if (pkVal != null)
                                                            parentPrmVals[pk.RemoteProperty] = pkVal;
                                                    }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            // Fixed XOD 1.x bug: pass parRefProp.PropertyType (not type)
                                            parentPrmVals = GetPrimaryValues(parRefProp.PropertyType, parentValue);
                                        }

                                        if (parentPrmVals == null || parentPrmVals.Count == 0)
                                            continue;

                                        object parent = SelectItemsByExample(parRefProp.PropertyType, PopulateItemProperties(parRefProp.PropertyType, parentPrmVals), "*").FirstOrDefault();
                                        if (parent == null)
                                            continue;

                                        RemoveFromParent(type, itemCode, parRefProp, parent);
                                    }
                                }
                            }
                        }

                        rowTree.Page.RemoveRow(row);
                        deleted = true;

                        XElement tablePage = rowTree.Table.Pages().FirstOrDefault(s =>
                            rowTree.Page.Path.EndsWith(s.Attribute("file").Value, StringComparison.OrdinalIgnoreCase));

                        int remainingRows = rowTree.Page.Rows().Count();
                        if (remainingRows == 0)
                        {
                            ioService.Delete(rowTree.Page.Path);

                            if (tablePage != null)
                            {
                                tablePage.Remove();
                                if (!rowTree.Table.Pages().Any())
                                {
                                    ioService.Delete(rowTree.Table.Path);
                                    indexService.Remove(type);
                                }
                                else
                                {
                                    ioService.Save(rowTree.Table);
                                }
                            }
                        }
                        else
                        {
                            if (tablePage != null)
                            {
                                tablePage.SetAttributeValue("count", remainingRows);
                                ioService.Save(rowTree.Table);
                            }
                            ioService.Save(rowTree.Page);
                        }
                    }
                }

                    if (deleted)
                    {
                        AfterAction?.Invoke(this, trigger);
                    }

                    return deleted;
                }
                finally
                {
                    if (shouldBatch)
                        ioService.EndBatch();
                }
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public bool DropType(Type type)
        {
            rwLock.EnterWriteLock();
            try
            {
                var trigger = new TriggerEventArgs
                {
                    Cancel = false,
                    Type = type,
                    Action = DatabaseActions.Drop
                };

                BeforeAction?.Invoke(this, trigger);
                if (trigger.Cancel)
                    return false;

                string docFileName = $"{type.FullName}.xtab";
                XFile tableFile = ioService.OpenFileOrCreate(docFileName, typeof(Table), openOnly: true);

                if (tableFile != null)
                {
                    var pageFiles = tableFile.Pages()
                        .Select(p => p.Attribute("file")?.Value)
                        .Where(f => !string.IsNullOrEmpty(f))
                        .ToList();

                    foreach (var pageFile in pageFiles)
                    {
                        ioService.Delete(pageFile);
                    }

                    ioService.Delete(tableFile.Path);
                    autonumberService.ClearType(type);
                    indexService.Remove(type);
                    itemsCacheService.ClearCurrentCache();

                    AfterAction?.Invoke(this, trigger);
                    return true;
                }

                return false;
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }
        private bool DeleteByItemCode(Type type, string itemCode, bool isCascade)
        {
            int dot = itemCode.IndexOf('.');
            if (dot <= 0 || dot >= itemCode.Length - 1)
                return false;

            string pageCode = itemCode.Substring(0, dot);
            string pageFileName = pageCode + ".xpag";
            string rowCode = itemCode.Substring(dot + 1);

            if (ioService.TryDeleteRow(pageFileName, rowCode, out XElement rawRow, out bool pageEmpty))
            {
                var meta = propertyService.GetMetadata(type);
                if (meta == null || !meta.IsStandalone)
                    itemsCacheService.Clear(type, rowCode);

                indexService.RemoveRowElement(type, rawRow, itemCode);

                string docFileName = $"{type.FullName}.xtab";
                XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
                if (tableFile != null)
                {
                    XElement tablePage = tableFile.Pages().FirstOrDefault(s =>
                        string.Equals(s.Attribute("file")?.Value, pageFileName, StringComparison.OrdinalIgnoreCase));

                    if (pageEmpty)
                    {
                        ioService.Delete(pageFileName);
                        if (tablePage != null)
                        {
                            tablePage.Remove();
                            if (!tableFile.Pages().Any())
                            {
                                ioService.Delete(tableFile.Path);
                                indexService.Remove(type);
                            }
                            else
                            {
                                ioService.Save(tableFile);
                            }
                        }
                    }
                    else if (tablePage != null)
                    {
                        XAttribute cntAttr = tablePage.Attribute("count");
                        if (cntAttr != null && int.TryParse(cntAttr.Value, out int curCnt) && curCnt > 0)
                        {
                            cntAttr.Value = (curCnt - 1).ToString();
                            ioService.Save(tableFile);
                        }
                    }
                }

                if (meta == null || meta.HasCascadeDeleteOrParentProperties)
                {
                    CascadeDeleteChildElements(type, rawRow, isCascade);
                }

                return true;
            }

            return false;
        }

        private void CascadeDeleteChildElements(Type type, XElement rawRow, bool isCascade)
        {
            if (rawRow == null) return;

            var propRefProps = propertyService.Properties(type.FullName).Where(s =>
                s.ReferenceType != PropertyReferenceType.Parent &&
                s.ReferenceType != PropertyReferenceType.SelfForeign &&
                s.Cascade.HasFlag(CascadeOptions.Delete) &&
                s.TypeCategory != PropertyTypeCategory.None &&
                s.TypeCategory != PropertyTypeCategory.Dictionary);

            foreach (var prop in propRefProps)
            {
                Type childType = prop.TypeCategory == PropertyTypeCategory.Array
                    ? prop.PropertyType.GetElementType()
                    : prop.PropertyType.GetGenericArguments().FirstOrDefault() ?? prop.PropertyType;

                if (childType == null) continue;

                var childContainer = rawRow.Element(type.Name)?.Element(prop.PropertyName) ?? rawRow.Element(prop.PropertyName);
                if (childContainer != null)
                {
                    propertyService.LoadType(childType);
                    var childPkProps = propertyService.Properties(childType.FullName).Where(p => p.IsPrimaryKey).ToList();
                    foreach (var childElm in childContainer.Elements(childType.Name))
                    {
                        string childItemCode = childElm.Value;
                        if (!string.IsNullOrEmpty(childItemCode) && childItemCode.Contains('.'))
                        {
                            DeleteByItemCode(childType, childItemCode, isCascade: true);
                        }
                        else if (childPkProps.Count > 0)
                        {
                            object childShell = ValueHelper.CreateInstance(childType);
                            bool allPkFound = true;
                            foreach (var pkProp in childPkProps)
                            {
                                string attrVal = childElm.Attribute(pkProp.PropertyName)?.Value;
                                if (!string.IsNullOrEmpty(attrVal))
                                {
                                    object converted = ValueHelper.ParseScalar(pkProp.PropertyType, attrVal);
                                    pkProp.SetValue(childShell, converted);
                                }
                                else
                                {
                                    allPkFound = false;
                                    break;
                                }
                            }

                            if (allPkFound)
                            {
                                Delete(childType, childShell, isCascade: true);
                            }
                        }
                    }
                }
            }

            if (!isCascade)
            {
                RemoveFromParentElements(type, rawRow);
            }
        }

        private void RemoveFromParentElements(Type type, XElement rawRow)
        {
            var parRefProps = propertyService.Properties(type.FullName)
                .Where(s => s.ReferenceType == PropertyReferenceType.Parent);

            foreach (var parRefProp in parRefProps)
            {
                var parentPrmVals = new Dictionary<string, object>();
                if (parRefProp.ParentKeys != null)
                {
                    foreach (var pk in parRefProp.ParentKeys)
                    {
                        var pkProp = propertyService.Properties(type.FullName)
                            .FirstOrDefault(s => s.PropertyName == pk.LocalProperty);
                        if (pkProp != null)
                        {
                            string valStr = rawRow.Element(type.Name)?.Element(pkProp.PropertyName)?.Value
                                ?? rawRow.Element(pkProp.PropertyName)?.Value
                                ?? rawRow.Element(type.Name)?.Attribute(pkProp.PropertyName)?.Value
                                ?? rawRow.Attribute(pkProp.PropertyName)?.Value;
                            if (!string.IsNullOrEmpty(valStr))
                            {
                                parentPrmVals[pk.RemoteProperty] = ValueHelper.ParseScalar(pkProp.PropertyType, valStr);
                            }
                        }
                    }
                }

                if (parentPrmVals.Count > 0)
                {
                    object parent = SelectItemsByExample(parRefProp.PropertyType, PopulateItemProperties(parRefProp.PropertyType, parentPrmVals), "*").FirstOrDefault();
                    if (parent != null)
                    {
                        string rowCode = rawRow.Attribute("code")?.Value;
                        RemoveFromParent(type, rowCode, parRefProp, parent);
                    }
                }
            }
        }
    }
}
