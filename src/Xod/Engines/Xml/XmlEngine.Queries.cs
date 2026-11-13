using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Xml.Linq;
using Xod.Helpers;

namespace Xod.Engines.Xml
{
    public partial class XmlEngine
    {
        public IEnumerable<object> Select(Type type, bool backward = false, string include = "*")
        {
            EnterTableReadLock(type, out var tLock);
            try
            {
                return SelectItems(type, null, backward, include).ToList();
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        public IEnumerable<object> Select(Type type, int skip, int take, bool backward = false, string include = "*")
        {
            if (take <= 0)
                return Enumerable.Empty<object>();

            EnterTableReadLock(type, out var tLock);
            try
            {
                propertyService.LoadType(type);
                string docFileName = $"{type.FullName}.xtab";
                XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
                if (tableFile == null)
                    return Enumerable.Empty<object>();

                var meta = propertyService.GetMetadata(type);
                IEnumerable<XElement> pages = tableFile.Pages();
                if (backward)
                    pages = pages.Reverse();

                var results = new List<object>(take);
                int remainingSkip = skip > 0 ? skip : 0;

                foreach (var pageElm in pages)
                {
                    string pageFileName = pageElm.Attribute("file")?.Value;
                    if (string.IsNullOrEmpty(pageFileName))
                        continue;

                    if (remainingSkip > 0 &&
                        pageElm.Attribute("full")?.Value == "true" &&
                        int.TryParse(pageElm.Attribute("count")?.Value, out int pageRowCount) &&
                        pageRowCount > 0 &&
                        remainingSkip >= pageRowCount)
                    {
                        remainingSkip -= pageRowCount;
                        continue;
                    }

                    if (!ioService.FileExists(pageFileName))
                        continue;

                    if (!backward)
                    {
                        if (meta != null && meta.IsPureScalarStandalone &&
                            ioService.TryReadEntitySliceFromRawPage(pageFileName, meta, ref remainingSkip, take - results.Count, results))
                        {
                            if (results.Count >= take)
                                return results;
                            continue;
                        }

                        var sliceRows = new List<XElement>(take - results.Count);
                        if (ioService.TryReadRowSliceFromRawPage(pageFileName, ref remainingSkip, take - results.Count, sliceRows))
                        {
                            for (int i = 0; i < sliceRows.Count; i++)
                            {
                                results.Add(Read(type, sliceRows[i], include));
                                if (results.Count >= take)
                                    return results;
                            }
                            continue;
                        }
                    }

                    XFile pageFile = ioService.OpenFileOrCreate<TablePage>(pageFileName, openOnly: true);
                    if (pageFile == null)
                        continue;

                    IEnumerable<XElement> pageRows = pageFile.Rows();
                    if (backward)
                        pageRows = pageRows.Reverse();

                    foreach (var row in pageRows)
                    {
                        if (remainingSkip > 0)
                        {
                            remainingSkip--;
                            continue;
                        }

                        results.Add(Read(type, row, include));
                        if (results.Count >= take)
                            return results;
                    }
                }

                return results;
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        public int Count(Type type)
        {
            EnterTableReadLock(type, out var tLock);
            try
            {
                propertyService.LoadType(type);
                string docFileName = $"{type.FullName}.xtab";
                XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
                if (tableFile == null)
                    return 0;

                int total = 0;
                foreach (var pageElm in tableFile.Pages())
                {
                    string pageFileName = pageElm.Attribute("file")?.Value;
                    if (string.IsNullOrEmpty(pageFileName))
                        continue;

                    if (pageElm.Attribute("full")?.Value == "true" &&
                        int.TryParse(pageElm.Attribute("count")?.Value, out int pageRowCount) &&
                        pageRowCount >= 0)
                    {
                        total += pageRowCount;
                        continue;
                    }

                    if (!ioService.FileExists(pageFileName))
                        continue;

                    if (ioService.TryCountRowsInRawPage(pageFileName, out int rawRowCount))
                    {
                        total += rawRowCount;
                        continue;
                    }

                    XFile pageFile = ioService.OpenFileOrCreate<TablePage>(pageFileName, openOnly: true);
                    if (pageFile != null)
                    {
                        total += pageFile.Rows().Count();
                    }
                }

                return total;
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        public IEnumerable<object> Query(Type type, Func<dynamic, bool> query = null, string include = "*")
        {
            EnterTableReadLock(type, out var tLock);
            try
            {
                return QueryItems(type, query, include).ToList();
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        public IEnumerable<T> QueryExpression<T>(Expression<Func<T, bool>> expression, string include = "*")
        {
            if (expression == null)
                return Select(typeof(T), false, include).Cast<T>();

            Type type = typeof(T);

            EnterTableReadLock(type, out var tLock);
            try
            {
                EnsureIndexHydrated(type);
                if (options.EnableIndexing && indexService.TryPlanExpression(expression, out var candidateCodes, out bool isFullyCovered))
                {
                    if (candidateCodes.Count >= 8)
                    {
                        ioService.PreloadPagesForCodes(candidateCodes);
                    }

                    Func<T, bool> residualFilter = isFullyCovered ? null : expression.Compile();
                    var meta = propertyService.GetMetadata(type);
                    bool useFastReader = meta != null && meta.IsPureScalarStandalone;
                    if (candidateCodes.Count >= 64 &&
                        residualFilter == null &&
                        meta != null &&
                        meta.IsStandalone &&
                        !meta.HasAttributeProperties &&
                        !meta.HasDefaultValueProperties)
                    {
                        var codeArray = candidateCodes as string[] ?? candidateCodes.ToArray();
                        var hydrated = new T[codeArray.Length];
                        System.Threading.Tasks.Parallel.For(0, codeArray.Length, i =>
                        {
                            if (useFastReader && TryReadEntityByCode(codeArray[i], meta, out object fastObj) && fastObj != null)
                            {
                                hydrated[i] = (T)fastObj;
                                return;
                            }

                            XElement rawRow = GetRawData(codeArray[i]);
                            if (rawRow != null)
                            {
                                hydrated[i] = (T)Read(type, rawRow, include);
                            }
                        });
                        var parallelResults = new List<T>(codeArray.Length);
                        for (int i = 0; i < hydrated.Length; i++)
                        {
                            if (hydrated[i] != null)
                                parallelResults.Add(hydrated[i]);
                        }
                        return parallelResults;
                    }

                    var results = new List<T>(candidateCodes.Count);
                    foreach (var code in candidateCodes)
                    {
                        if (useFastReader && TryReadEntityByCode(code, meta, out object fastObj) && fastObj != null)
                        {
                            var fastEntity = (T)fastObj;
                            if (residualFilter == null || residualFilter(fastEntity))
                                results.Add(fastEntity);
                            continue;
                        }

                        XElement rawRow = GetRawData(code);
                        if (rawRow != null)
                        {
                            var entity = (T)Read(type, rawRow, include);
                            if (entity != null && (residualFilter == null || residualFilter(entity)))
                                results.Add(entity);
                        }
                    }
                    return results;
                }

                Func<T, bool> compiled = expression.Compile();
                return SelectItems(type, obj => compiled((T)obj), false, include).Cast<T>().ToList();
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        public IEnumerable<object> QueryByExample(Type type, object example, string include = "*")
        {
            EnterTableReadLock(type, out var tLock);
            try
            {
                return SelectItemsByExample(type, example, include).ToList();
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        public IEnumerable<object> QueryByExample(Type type, object[] examples, string include = "*")
        {
            EnterTableReadLock(type, out var tLock);
            try
            {
                return SelectItemsByExamples(type, examples, include).ToList();
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        public object Find(Type type, Func<dynamic, bool> query, string include = "*")
        {
            EnterTableReadLock(type, out var tLock);
            try
            {
                return SelectItems(type, query, false, include).FirstOrDefault();
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        public object FindLast(Type type, Func<dynamic, bool> query, string include = "*")
        {
            EnterTableReadLock(type, out var tLock);
            try
            {
                return SelectItems(type, query, true, include).FirstOrDefault();
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        public object First(Type type, string include = "*")
        {
            EnterTableReadLock(type, out var tLock);
            try
            {
                return SelectItems(type, null, false, include).FirstOrDefault();
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        public object Last(Type type, string include = "*")
        {
            EnterTableReadLock(type, out var tLock);
            try
            {
                propertyService.LoadType(type);
                string docFileName = $"{type.FullName}.xtab";
                XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
                if (tableFile != null)
                {
                    XElement lastPageElm = tableFile.Pages().LastOrDefault();
                    string lastPageFileName = lastPageElm?.Attribute("file")?.Value;
                    if (!string.IsNullOrEmpty(lastPageFileName))
                    {
                        var meta = propertyService.GetMetadata(type);
                        if (meta != null && meta.IsPureScalarStandalone &&
                            ioService.TryReadLastEntityFromRawPage(lastPageFileName, meta, out object fastLast) &&
                            fastLast != null)
                        {
                            return fastLast;
                        }

                        XElement lastRow = ioService.ReadLastRowColdOrCached(lastPageFileName);
                        if (lastRow != null)
                        {
                            object entity = Read(type, lastRow, include);
                            if (entity != null)
                                return entity;
                        }
                    }
                }

                return SelectItems(type, null, true, include).FirstOrDefault();
            }
            finally
            {
                ExitTableReadLock(tLock);
            }
        }

        private IEnumerable<object> QueryItems(Type type, Func<dynamic, bool> query, string include = null)
        {
            return SelectItems(type, query, false, include);
        }

        private IEnumerable<object> SelectItems(Type type, Func<dynamic, bool> query = null, bool backward = false, string include = null)
        {
            propertyService.LoadType(type);
            string docFileName = $"{type.FullName}.xtab";
            XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
            if (tableFile == null)
                yield break;

            IEnumerable<XElement> pages = tableFile.Pages();
            if (backward)
                pages = pages.Reverse();

            foreach (var pageElm in pages)
            {
                string pageFileName = pageElm.Attribute("file")?.Value;
                if (string.IsNullOrEmpty(pageFileName) || !ioService.FileExists(pageFileName))
                    continue;

                XFile pageFile = ioService.OpenFileOrCreate<TablePage>(pageFileName, openOnly: true);
                if (pageFile == null)
                    continue;

                IEnumerable<XElement> rows = pageFile.Rows();
                if (backward)
                    rows = rows.Reverse();

                foreach (var r in rows)
                {
                    var entity = Read(type, r, include);
                    if (entity != null && (query == null || query(entity)))
                        yield return entity;
                }
            }
        }

        private object Read(Type type, XElement row, string include = null, ReadWriteTrack track = null)
        {
            if (row == null) return null;

            bool lazyLoad = include != "*" || LazyLoad;
            XElement element = row.Elements().FirstOrDefault();
            if (element == null) return null;

            if (type.Name != element.Name.LocalName)
            {
                var registered = propertyService.RegisteredTypeByName(element.Name.LocalName);
                if (registered != null)
                    type = registered;
            }

            var meta = propertyService.GetMetadata(type);
            object item = ValueHelper.CreateInstance(type);

            if (meta != null && meta.IsStandalone && !meta.HasAttributeProperties && !meta.HasDefaultValueProperties && track == null)
            {
                var propMap = meta.ReadPropertyMap;
                foreach (var e in element.Elements())
                {
                    if (!propMap.TryGetValue(e.Name.LocalName, out var prop))
                        continue;

                    if (!e.HasAttributes)
                    {
                        SetFromStoredScalar(item, prop, e);
                        continue;
                    }

                    XAttribute isColl = e.Attribute("collType");
                    if (isColl != null)
                    {
                        if (isColl.Value == "dictionary" && prop.TypeCategory == PropertyTypeCategory.Dictionary)
                        {
                            prop.SetValue(item, ReadStoredMap(prop, e));
                        }
                        else
                        {
                            Type itemType = StoredItemType(prop);
                            if (itemType != null && ValueHelper.IsScalarType(itemType))
                                prop.SetValue(item, ReadStoredScalarItems(prop, itemType, e));
                        }
                    }
                    else
                    {
                        SetFromStoredScalar(item, prop, e);
                    }
                }

                return item;
            }

            bool parentIsLeaf = false;
            if (track?.Parent != null && RecursionError(track, track.Parent))
            {
                parentIsLeaf = true;
                track = null;
                lazyLoad = true;
                include = null;
            }

            string[] includedRefProps = string.IsNullOrEmpty(include) || include == "*"
                ? Array.Empty<string>()
                : include.Replace(" ", "").Split(',');

            string itemCode = row.Attribute("code")?.Value;
            Guid readId = Guid.NewGuid();

            var props = meta != null
                ? meta.ReadProperties
                : propertyService.Properties(type.FullName)
                    .Where(s => !s.IsNotMapped && !s.IsReadOnly)
                    .OrderBy(s => s.TypeCategory)
                    .ToArray();

            foreach (var prop in props)
            {
                if (prop.ValuePosition == ValuePosition.Attribute)
                {
                    XAttribute attr = element.Attribute(prop.PropertyName);
                    if (attr != null)
                    {
                        object attrVal = ReadValue(prop, prop.PropertyType, attr.Value);
                        if (attrVal != null)
                            prop.SetValue(item, attrVal);
                    }
                    continue;
                }

                XElement e = element.Element(prop.PropertyName);
                if (e != null)
                {
                    XAttribute isColl = e.Attribute("collType");
                    if (isColl != null)
                    {
                        if (isColl.Value == "dictionary" && prop.TypeCategory == PropertyTypeCategory.Dictionary)
                        {
                            prop.SetValue(item, ReadStoredMap(prop, e));
                            continue;
                        }

                        Type itemType = StoredItemType(prop);
                        if (itemType == null)
                            continue;

                        if (ValueHelper.IsScalarType(itemType))
                        {
                            prop.SetValue(item, ReadStoredScalarItems(prop, itemType, e));
                        }
                        else if ((include == "*" && !LazyLoad) || includedRefProps.Contains(prop.PropertyName))
                        {
                            Type collGenType = typeof(List<>).MakeGenericType(itemType);
                            dynamic coll = Activator.CreateInstance(collGenType);
                            var collElements = e.Elements(itemType.Name).Select(s => (object)s.Value).ToArray();
                            propertyService.LoadType(itemType);
                            var collQuery = collElements
                                .Select(s =>
                                {
                                    string fullChildCode = s?.ToString();
                                    XElement rawChildRow = GetRawData(fullChildCode);
                                    if (rawChildRow == null)
                                        return null;

                                    XAttribute rowCode = rawChildRow.Attribute("code");
                                    if (rowCode == null)
                                        return null;

                                    object obj = itemsCacheService.Get(itemType, rowCode.Value, lazyLoad, includedRefProps);
                                    if (obj != null)
                                        return obj;

                                    var childTrack = new ReadWriteTrack
                                    {
                                        Code = fullChildCode,
                                        Type = itemType,
                                        Parent = track,
                                        ReadId = Guid.NewGuid()
                                    };

                                    return Read(itemType, rawChildRow, include, childTrack);
                                })
                                .Where(s => s != null)
                                .ToArray();

                            MethodInfo addObjRange = typeof(CollectionExtensions)
                                .GetMethod(nameof(CollectionExtensions.AddRange))
                                .MakeGenericMethod(itemType);
                            addObjRange.Invoke(coll, new object[] { coll, collQuery });

                            prop.SetValue(item, prop.PropertyType.IsArray ? coll.ToArray() : coll);
                        }
                    }
                    else
                    {
                        string stringValue = e.Value;
                        if (!prop.IsGenericType && ValueHelper.IsScalarType(prop.PropertyType) &&
                            (stringValue.Length == 0 || StoredValue.IsNil(e)))
                        {
                            SetFromStoredScalar(item, prop, e);
                            continue;
                        }
                        if (string.IsNullOrEmpty(stringValue))
                            continue;

                        Type targetPropType = prop.PropertyType;
                        string[] itemParts = null;

                        if (prop.IsGenericType)
                        {
                            if (string.IsNullOrEmpty(prop.GenericTypeProperty))
                            {
                                itemParts = stringValue.Split('.');
                                if (itemParts.Length == 2)
                                    targetPropType = ioService.GetPageType(itemParts[0]);
                            }
                            else
                            {
                                var genericProp = props.FirstOrDefault(s => s.PropertyName == prop.GenericTypeProperty);
                                if (genericProp != null)
                                {
                                    object genericPropValue = genericProp.GetValue(item);
                                    if (genericPropValue != null)
                                    {
                                        var genericType = propertyService.RegisteredType(genericPropValue.ToString());
                                        if (genericType != null)
                                            targetPropType = genericType;
                                    }
                                }
                            }

                            if (targetPropType == null || targetPropType == typeof(object))
                                exceptionService.Throw(new AnynomousTypeException());
                        }

                        object value = ReadValue(prop, targetPropType, stringValue);

                        if (value == null && targetPropType.IsClass &&
                            ((include == "*" && !LazyLoad) || includedRefProps.Contains(prop.PropertyName)) &&
                            (!LazyLoadParent || prop.ReferenceType != PropertyReferenceType.Parent || includedRefProps.Contains(prop.PropertyName)))
                        {
                            itemParts = stringValue.Split('.');
                            if (itemParts.Length != 2)
                                continue;

                            object cachedItem = itemsCacheService.Get(targetPropType, itemParts[1], lazyLoad, includedRefProps);
                            if (cachedItem == null)
                            {
                                var refTrack = new ReadWriteTrack
                                {
                                    Code = stringValue,
                                    Type = targetPropType,
                                    Parent = track,
                                    ReadId = Guid.NewGuid()
                                };

                                XElement xRefItem = GetRawData(stringValue);
                                if (xRefItem != null)
                                {
                                    value = Read(targetPropType, xRefItem, include, refTrack);
                                }
                                else if (prop.ForeignKeys != null)
                                {
                                    var localRefKeyValues = new Dictionary<string, object>();
                                    foreach (var foreignKeyAtt in prop.ForeignKeys)
                                    {
                                        PropertyInfo localKeyProp = type.GetProperty(foreignKeyAtt.LocalProperty);
                                        if (localKeyProp != null)
                                            localRefKeyValues[foreignKeyAtt.RemoteProperty] = localKeyProp.GetValue(item);
                                    }
                                    value = SelectItemsByExample(targetPropType, PopulateItemProperties(targetPropType, localRefKeyValues)).FirstOrDefault();
                                }
                            }
                            else
                            {
                                value = cachedItem;
                            }
                        }

                        if (value != null)
                            prop.SetValue(item, value);
                    }
                }
                else if (prop.DefaultValue != null && !prop.DefaultValue.Equals(ValueHelper.DefaultOf(prop.PropertyType)))
                {
                    prop.SetValue(item, prop.DefaultValue);
                }
            }

            var cache = new ItemCache
            {
                Type = type,
                Code = itemCode,
                Item = item,
                LazyLoaded = lazyLoad,
                IncludedReferenceProperties = include != "*" ? null : includedRefProps,
                ReadId = readId,
                ParentIsLeaf = parentIsLeaf
            };

            if (track != null)
                cache.ParentReadId = track.ReadId;

            itemsCacheService.Add(cache);
            return item;
        }

        // The value collections a row stores (PLAN-046 section 7), read here for both row readers (the standalone one
        // and the general one each had a copy). A map's entry and a list's item marked nil="true" is null (ISS-097).

        private static Type StoredItemType(PropertyInfoItem prop) =>
            prop.PropertyType.IsArray ? prop.PropertyType.GetElementType() : prop.PropertyType.GetGenericArguments().FirstOrDefault();

        private static IDictionary ReadStoredMap(PropertyInfoItem prop, XElement e)
        {
            // The key and value types its IDictionary<K, V> names (strings for a Hashtable); the property's own type
            // where it can be made, a Dictionary<K, V> otherwise. A Dictionary<,> was set on every map property, which
            // a SortedDictionary or a Hashtable can't hold: once an empty map was stored, reading one threw (review of
            // TASK-071; a map with entries always did).
            Type mapType = prop.PropertyType;
            Type generic = mapType.IsGenericType && mapType.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                ? mapType
                : mapType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            Type keyType = generic?.GetGenericArguments()[0] ?? typeof(string);
            Type valType = generic?.GetGenericArguments()[1] ?? typeof(string);
            bool ownType = !mapType.IsAbstract && !mapType.IsInterface && mapType.GetConstructor(Type.EmptyTypes) != null;
            var map = (IDictionary)ValueHelper.CreateInstance(ownType ? mapType : typeof(Dictionary<,>).MakeGenericType(keyType, valType));
            foreach (var entry in e.Elements("Entry"))
            {
                string key = entry.Attribute("key")?.Value;
                if (key == null)
                    continue;
                // A null where the value type has none reads as its default, as a list's item does (AddRange).
                object value = ValueHelper.ParseScalar(valType, StoredValue.ItemText(entry));
                map[ValueHelper.ParseScalar(keyType, key)] = value ?? (valType.IsValueType ? ValueHelper.DefaultOf(valType) : null);
            }
            return map;
        }

        private static object ReadStoredScalarItems(PropertyInfoItem prop, Type itemType, XElement e)
        {
            object[] texts = e.Elements(StoredValue.ItemName(itemType)).Select(s => (object)StoredValue.ItemText(s)).ToArray();
            dynamic list = Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
            typeof(CollectionExtensions)
                .GetMethod(prop.PropertyType.IsArray ? nameof(CollectionExtensions.AddRangeFromString) : nameof(CollectionExtensions.AddRange))
                .MakeGenericMethod(itemType)
                .Invoke(null, new object[] { list, texts });
            return prop.PropertyType.IsArray ? list.ToArray() : list;
        }

        /// <summary>
        /// Sets a property from its stored scalar element (ISS-060): <c>nil="true"</c> is null; an empty text is
        /// an empty string where the property has that form (StoredValue.HasEmptyForm) and nothing otherwise;
        /// any other text is read as a read reads it.
        /// </summary>
        private void SetFromStoredScalar(object item, PropertyInfoItem prop, XElement e)
        {
            if (StoredValue.IsNil(e))
            {
                prop.SetValue(item, null);
                return;
            }
            string text = e.Value;
            if (text.Length == 0 && !StoredValue.HasEmptyForm(prop))
                return;
            object val = ReadValue(prop, prop.PropertyType, text);
            if (val != null)
                prop.SetValue(item, val);
        }

        /// <summary>
        /// What a read gives a scalar property from a stored row's entity element (ISS-060), for a caller that
        /// needs the value rather than an item: <c>nil="true"</c> is null; an empty text is an empty string where the
        /// property has that form, and otherwise counts as left out; a value left out is what a read gives without
        /// it (StoredValue.ReadBackOfMissing). The rule <see cref="SetFromStoredScalar"/> applies to an item.
        /// </summary>
        private object StoredScalar(PropertyInfoItem prop, XElement entity)
        {
            string text;
            if (prop.ValuePosition == ValuePosition.Attribute)
            {
                text = entity?.Attribute(prop.PropertyName)?.Value;
            }
            else
            {
                XElement e = entity?.Element(prop.PropertyName);
                if (e != null && StoredValue.IsNil(e))
                    return null;
                text = e?.Value;
            }
            if (text == null || (text.Length == 0 && !StoredValue.HasEmptyForm(prop)))
                return StoredValue.ReadBackOfMissing(prop);
            return ReadValue(prop, prop.PropertyType, text);
        }

        private object ReadValue(PropertyInfoItem prop, Type targetType, string stringValue)
        {
            if (!ValueHelper.IsScalarType(targetType))
                return null;

            if (targetType == typeof(string) && prop.Encryption == CryptoMethod.AES256)
            {
                return propertyKeys.Decrypt(stringValue, prop.PropertyName);
            }

            return ValueHelper.ParseScalar(targetType, stringValue);
        }

        private static bool IsNonDefaultPropertyValue(PropertyInfoItem prop, object item)
        {
            switch (prop.ScalarKind)
            {
                case ScalarPropertyKind.Int32:
                    return prop.IntGetter(item) != 0;
                case ScalarPropertyKind.Int64:
                    return prop.LongGetter(item) != 0L;
                case ScalarPropertyKind.Decimal:
                    return prop.DecimalGetter(item) != 0m;
                case ScalarPropertyKind.Double:
                    return prop.DoubleGetter(item) != 0.0;
                case ScalarPropertyKind.Boolean:
                    return prop.BoolGetter(item);
                case ScalarPropertyKind.String:
                    return !string.IsNullOrEmpty(prop.StringGetter(item));
                case ScalarPropertyKind.Guid:
                    return prop.GuidGetter(item) != Guid.Empty;
                default:
                {
                    var val = prop.GetValue(item);
                    return val != null && !val.Equals(ValueHelper.DefaultOf(prop.PropertyType));
                }
            }
        }

        private bool TryLookupCodesByProperties(
            Type type,
            object item,
            IReadOnlyList<PropertyInfoItem> propsToMatch,
            out IReadOnlyCollection<string> candidateCodes,
            out bool isFullyCovered)
        {
            candidateCodes = null;
            isFullyCovered = false;

            if (!options.EnableIndexing || propsToMatch == null || propsToMatch.Count == 0)
                return false;

            EnsureIndexHydrated(type);

            if (propsToMatch.Count == 1)
            {
                var singleProp = propsToMatch[0];
                if ((singleProp.IsPrimaryKey || singleProp.IsUnique || singleProp.IsIndexed) &&
                    (singleProp.ScalarKind != ScalarPropertyKind.None || ValueHelper.IsScalarType(singleProp.PropertyType)) &&
                    singleProp.Encryption == CryptoMethod.None)
                {
                    candidateCodes = indexService.LookupExactProperty(type, singleProp, item);
                    isFullyCovered = true;
                    return true;
                }
                return false;
            }

            HashSet<string> intersected = null;
            int indexedCount = 0;

            foreach (var prop in propsToMatch)
            {
                if ((prop.IsPrimaryKey || prop.IsUnique || prop.IsIndexed) &&
                    (prop.ScalarKind != ScalarPropertyKind.None || ValueHelper.IsScalarType(prop.PropertyType)) &&
                    prop.Encryption == CryptoMethod.None)
                {
                    var matches = indexService.LookupExactProperty(type, prop, item);
                    indexedCount++;
                    if (matches.Count == 0)
                    {
                        candidateCodes = Array.Empty<string>();
                        isFullyCovered = true;
                        return true;
                    }

                    if (intersected == null)
                    {
                        intersected = new HashSet<string>(matches, StringComparer.Ordinal);
                    }
                    else
                    {
                        intersected.IntersectWith(matches);
                        if (intersected.Count == 0)
                        {
                            candidateCodes = Array.Empty<string>();
                            isFullyCovered = true;
                            return true;
                        }
                    }
                }
            }

            if (indexedCount == 0 || intersected == null)
                return false;

            candidateCodes = intersected;
            isFullyCovered = indexedCount == propsToMatch.Count;
            return true;
        }

        private IEnumerable<object> SelectItemsByExample(Type type, object example, string include = null)
        {
            if (type == null || example == null)
                return Enumerable.Empty<object>();

            propertyService.LoadType(type);
            EnsureIndexHydrated(type);

            string docFileName = $"{type.FullName}.xtab";
            XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
            if (tableFile != null)
            {
                var meta = propertyService.GetMetadata(type);
                var itemProps = meta != null
                    ? meta.ReadProperties
                    : propertyService.Properties(type.FullName).Where(s => !s.IsReadOnly).ToArray();

                var activeProps = new List<PropertyInfoItem>(2);
                for (int i = 0; i < itemProps.Length; i++)
                {
                    var itemProp = itemProps[i];
                    if (IsNonDefaultPropertyValue(itemProp, example))
                        activeProps.Add(itemProp);
                }

                if (TryLookupCodesByProperties(type, example, activeProps, out var candidateCodes, out bool isFullyCovered))
                {
                    if (candidateCodes.Count == 0)
                        return Enumerable.Empty<object>();

                    if (isFullyCovered)
                    {
                        var results = new List<object>(candidateCodes.Count);
                        bool useFastReader = meta != null && meta.IsPureScalarStandalone;
                        foreach (var code in candidateCodes)
                        {
                            if (useFastReader && TryReadEntityByCode(code, meta, out object fastEntity) && fastEntity != null)
                            {
                                results.Add(fastEntity);
                                continue;
                            }

                            XElement rawRow = GetRawData(code);
                            if (rawRow != null)
                                results.Add(Read(type, rawRow, include));
                        }
                        return results;
                    }

                    var skipPropsPartial = new List<string>(itemProps.Length);
                    for (int i = 0; i < itemProps.Length; i++)
                    {
                        if (!activeProps.Contains(itemProps[i]))
                            skipPropsPartial.Add(itemProps[i].PropertyName);
                    }

                    XElement partialTest = skipPropsPartial.Count > 0
                        ? Write(type, example, true, null, new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = skipPropsPartial.ToArray() })
                        : Write(type, example, true);

                    var filteredResults = new List<object>();
                    foreach (var code in candidateCodes)
                    {
                        XElement rawRow = GetRawData(code);
                        if (rawRow != null && partialTest.ElementMatch(rawRow.Elements().FirstOrDefault()))
                            filteredResults.Add(Read(type, rawRow, include));
                    }
                    return filteredResults;
                }

                var skipProps = new List<string>(itemProps.Length);
                for (int i = 0; i < itemProps.Length; i++)
                {
                    if (!activeProps.Contains(itemProps[i]))
                        skipProps.Add(itemProps[i].PropertyName);
                }

                XElement test = skipProps.Count > 0
                    ? Write(type, example, true, null, new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = skipProps.ToArray() })
                    : Write(type, example, true);

                var rows = tableFile.Pages()
                    .Where(s => ioService.FileExists(s.Attribute("file").Value))
                    .SelectMany(s => ioService.OpenFileOrCreate<TablePage>(s.Attribute("file").Value, openOnly: true).Rows());

                return rows.Where(r => test.ElementMatch(r.Elements().FirstOrDefault()))
                    .Select(r => Read(type, r, include))
                    .ToList();
            }

            return Enumerable.Empty<object>();
        }

        private IEnumerable<object> SelectItemsByExamples(Type type, object[] examples, string include = null)
        {
            if (type == null || examples == null || examples.Length == 0)
                return Enumerable.Empty<object>();

            propertyService.LoadType(type);
            string docFileName = $"{type.FullName}.xtab";
            XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
            if (tableFile != null)
            {
                var tests = new List<XElement>();
                foreach (var example in examples)
                {
                    var skipProps = new List<string>();
                    var itemProps = propertyService.Properties(type.FullName).Where(s => !s.IsReadOnly);
                    foreach (var itemProp in itemProps)
                    {
                        var examplePropVal = itemProp.Property.GetValue(example);
                        if (examplePropVal == null || examplePropVal.Equals(ValueHelper.DefaultOf(itemProp.PropertyType)))
                            skipProps.Add(itemProp.PropertyName);
                    }

                    XElement test = skipProps.Count > 0
                        ? Write(type, example, true, null, new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = skipProps.ToArray() })
                        : Write(type, example, true);

                    tests.Add(test);
                }

                var rows = tableFile.Pages()
                    .Where(s => ioService.FileExists(s.Attribute("file").Value))
                    .SelectMany(s => ioService.OpenFileOrCreate<TablePage>(s.Attribute("file").Value, openOnly: true).Rows());

                return rows.Where(r => tests.Any(t => t.ElementMatch(r.Elements().FirstOrDefault())))
                    .Select(r => Read(type, r, include))
                    .ToList();
            }

            return Enumerable.Empty<object>();
        }

        private XRow GetRow(Type type, object example)
        {
            if (type == null || example == null)
                return null;

            propertyService.LoadType(type);
            var pkProps = propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).ToList();
            if (options.EnableIndexing && pkProps.Count > 0 &&
                pkProps.All(p =>
                {
                    var val = p.GetValue(example);
                    return val != null && !val.Equals(ValueHelper.DefaultOf(p.PropertyType));
                }) &&
                TryLookupCodesByProperties(type, example, pkProps, out var pkCodes, out bool pkCovered) &&
                pkCovered)
            {
                if (pkCodes.Count == 0)
                    return null;

                string docFileName = $"{type.FullName}.xtab";
                XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
                if (tableFile != null)
                {
                    foreach (var code in pkCodes)
                    {
                        string[] parts = code.Split('.');
                        if (parts.Length == 2)
                        {
                            XFile pageFile = ioService.OpenFileOrCreate<TablePage>($"{parts[0]}.xpag", openOnly: true);
                            XElement row = pageFile?.GetRowByCode(parts[1]);
                            if (row != null)
                            {
                                return new XRow
                                {
                                    Table = tableFile,
                                    Page = pageFile,
                                    Row = row
                                };
                            }
                        }
                    }
                }
                return null;
            }

            object item = pkProps.Count > 0
                ? FilterItemPrimaryProperties(type, example)
                : FilterItemBaseProperties(type, example);

            return GetRowByExampleObject(type, item);
        }

        private XRow GetRowByExampleObject(Type type, object item)
        {
            if (type == null || item == null)
                return null;

            string docFileName = $"{type.FullName}.xtab";
            XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
            if (tableFile != null)
            {
                var result = new XRow();
                var skipProps = new List<string>();
                var activeProps = new List<PropertyInfoItem>();
                var itemProps = propertyService.Properties(type.FullName).Where(s => !s.IsReadOnly);
                foreach (var itemProp in itemProps)
                {
                    var examplePropVal = itemProp.Property.GetValue(item);
                    if (examplePropVal == null || examplePropVal.Equals(ValueHelper.DefaultOf(itemProp.PropertyType)))
                        skipProps.Add(itemProp.PropertyName);
                    else
                        activeProps.Add(itemProp);
                }

                if (TryLookupCodesByProperties(type, item, activeProps, out var candidateCodes, out bool isFullyCovered))
                {
                    if (candidateCodes.Count == 0)
                        return null;

                    if (isFullyCovered)
                    {
                        foreach (var code in candidateCodes)
                        {
                            string[] parts = code.Split('.');
                            if (parts.Length == 2)
                            {
                                XFile pageFile = ioService.OpenFileOrCreate<TablePage>($"{parts[0]}.xpag", openOnly: true);
                                XElement row = pageFile?.GetRowByCode(parts[1]);
                                if (row != null)
                                {
                                    return new XRow
                                    {
                                        Table = tableFile,
                                        Page = pageFile,
                                        Row = row
                                    };
                                }
                            }
                        }
                        return null;
                    }
                }

                XElement test = skipProps.Count > 0
                    ? Write(type, item, true, null, new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = skipProps.ToArray() })
                    : Write(type, item, true);

                var pages = tableFile.Pages().Where(s => ioService.FileExists(s.Attribute("file").Value));
                foreach (var page in pages)
                {
                    XFile pageFile = ioService.OpenFileOrCreate<TablePage>(page.Attribute("file").Value, openOnly: true);
                    if (pageFile != null)
                    {
                        bool found = pageFile.Rows().Any(r =>
                        {
                            if (test.ElementMatch(r.Elements().FirstOrDefault()))
                            {
                                result.Table = tableFile;
                                result.Page = pageFile;
                                result.Row = r;
                                return true;
                            }
                            return false;
                        });
                        if (found) break;
                    }
                }

                if (result.Row != null)
                    return result;
            }

            return null;
        }

        private XRowTree GetTree(Type type, object example)
        {
            if (type == null || example == null)
                return null;

            propertyService.LoadType(type);
            string docFileName = $"{type.FullName}.xtab";
            XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
            if (tableFile != null)
            {
                var pkProps = propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).ToList();
                if (options.EnableIndexing && pkProps.Count > 0 &&
                    pkProps.All(p =>
                    {
                        var val = p.GetValue(example);
                        return val != null && !val.Equals(ValueHelper.DefaultOf(p.PropertyType));
                    }) &&
                    TryLookupCodesByProperties(type, example, pkProps, out var pkCodes, out bool pkCovered) &&
                    pkCovered)
                {
                    if (pkCodes.Count == 0)
                        return null;

                    foreach (var code in pkCodes)
                    {
                        string[] parts = code.Split('.');
                        if (parts.Length == 2)
                        {
                            XFile pageFile = ioService.OpenFileOrCreate<TablePage>($"{parts[0]}.xpag", openOnly: true);
                            XElement row = pageFile?.GetRowByCode(parts[1]);
                            if (row != null)
                            {
                                return new XRowTree
                                {
                                    Table = tableFile,
                                    Page = pageFile,
                                    Rows = new List<XElement> { row }
                                };
                            }
                        }
                    }
                    return null;
                }

                object item = pkProps.Count > 0
                    ? FilterItemPrimaryProperties(type, example)
                    : FilterItemBaseProperties(type, example);

                var result = new XRowTree
                {
                    Rows = new List<XElement>()
                };

                var skipProps = new List<string>();
                var itemProps = propertyService.Properties(type.FullName).Where(s => !s.IsReadOnly);
                foreach (var itemProp in itemProps)
                {
                    var examplePropVal = itemProp.Property.GetValue(item);
                    if (examplePropVal == null || examplePropVal.Equals(ValueHelper.DefaultOf(itemProp.PropertyType)))
                        skipProps.Add(itemProp.PropertyName);
                }

                XElement test = skipProps.Count > 0
                    ? Write(type, item, true, null, new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = skipProps.ToArray() })
                    : Write(type, item, true);

                var pages = tableFile.Pages().Where(s => ioService.FileExists(s.Attribute("file").Value));
                foreach (var page in pages)
                {
                    XFile pageFile = ioService.OpenFileOrCreate<TablePage>(page.Attribute("file").Value, openOnly: true);
                    if (pageFile != null)
                    {
                        var matchedRows = pageFile.Rows()
                            .Where(r => test.ElementMatch(r.Elements().FirstOrDefault()))
                            .ToList();

                        if (matchedRows.Count > 0)
                        {
                            result.Rows.AddRange(matchedRows);
                            result.Table = tableFile;
                            result.Page = pageFile;
                            break;
                        }
                    }
                }

                if (result.Table != null)
                    return result;
            }

            return null;
        }

        private bool TryReadEntityByCode(string code, TypeMetadata meta, out object entity)
        {
            entity = null;
            if (string.IsNullOrEmpty(code) || meta == null || !meta.IsPureScalarStandalone)
                return false;

            int dot = code.IndexOf('.');
            if (dot <= 0 || dot >= code.Length - 1)
                return false;

            string pageFileName = string.Concat(code.AsSpan(0, dot), ".xpag");
            string rowCode = code.Substring(dot + 1);
            return ioService.TryReadEntityFromRawPage(pageFileName, rowCode, meta, out entity);
        }

        private XElement GetRawData(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            try
            {
                int dot = code.IndexOf('.');
                if (dot <= 0 || dot >= code.Length - 1) return null;

                string pageFileName = string.Concat(code.AsSpan(0, dot), ".xpag");
                string rowCode = code.Substring(dot + 1);
                return ioService.ReadRowColdOrCached(pageFileName, rowCode);
            }
            catch
            {
            }

            return null;
        }

        private object FilterItemProperties(Type type, object item, IEnumerable<string> filter)
        {
            object filterItem = ValueHelper.CreateInstance(type);
            var filterSet = filter.ToHashSet();
            var props = propertyService.Properties(type.FullName);
            foreach (var prop in props)
            {
                if (filterSet.Contains(prop.PropertyName))
                    prop.SetValue(filterItem, prop.GetValue(item));
                else
                    prop.SetValue(filterItem, ValueHelper.DefaultOf(prop.PropertyType));
            }
            return filterItem;
        }

        private object FilterItemBaseProperties(Type type, object item)
        {
            object filterItem = ValueHelper.CreateInstance(type);
            var props = propertyService.Properties(type.FullName);
            foreach (var prop in props)
            {
                if (prop.TypeCategory == PropertyTypeCategory.None ||
                    prop.TypeCategory == PropertyTypeCategory.ValueTypeArray ||
                    prop.TypeCategory == PropertyTypeCategory.ValueTypeCollection)
                {
                    prop.SetValue(filterItem, prop.GetValue(item));
                }
                else
                {
                    prop.SetValue(filterItem, null);
                }
            }
            return filterItem;
        }

        private object FilterItemPrimaryProperties(Type type, object item)
        {
            object filterItem = ValueHelper.CreateInstance(type);
            var props = propertyService.Properties(type.FullName);
            foreach (var prop in props)
            {
                if (prop.IsPrimaryKey)
                    prop.SetValue(filterItem, prop.GetValue(item));
                else
                    prop.SetValue(filterItem, ValueHelper.DefaultOf(prop.PropertyType));
            }
            return filterItem;
        }

        private object PopulateItemProperties(Type type, Dictionary<string, object> values)
        {
            if (values == null || values.Count == 0)
                return null;

            object item = ValueHelper.CreateInstance(type);
            var props = propertyService.Properties(type.FullName);
            foreach (var prop in props)
            {
                if (values.TryGetValue(prop.PropertyName, out var val))
                    prop.SetValue(item, val);
                else
                    prop.SetValue(item, ValueHelper.DefaultOf(prop.PropertyType));
            }
            return item;
        }
    }
}
