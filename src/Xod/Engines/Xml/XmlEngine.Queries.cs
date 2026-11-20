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
            EnterReadOperation(type);
            try
            {
                return SelectItems(type, null, backward, include).ToList();
            }
            finally
            {
                ExitReadOperation();
            }
        }

        public IEnumerable<object> Select(Type type, int skip, int take, bool backward = false, string include = "*")
        {
            if (take <= 0)
                return Enumerable.Empty<object>();

            EnterReadOperation(type);
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

                    ioService.RequirePage(tableFile, pageFileName);

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
                ExitReadOperation();
            }
        }

        public int Count(Type type)
        {
            EnterReadOperation(type);
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

                    ioService.RequirePage(tableFile, pageFileName);

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
                ExitReadOperation();
            }
        }

        public IEnumerable<object> Query(Type type, Func<dynamic, bool> query = null, string include = "*")
        {
            EnterReadOperation(type);
            try
            {
                return QueryItems(type, query, include).ToList();
            }
            finally
            {
                ExitReadOperation();
            }
        }

        public IEnumerable<T> QueryExpression<T>(Expression<Func<T, bool>> expression, string include = "*")
        {
            if (expression == null)
                return Select(typeof(T), false, include).Cast<T>();

            Type type = typeof(T);

            EnterReadOperation(type);
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
                        Xod.Services.ParallelReads.For(codeArray.Length, i =>
                        {
                            if (useFastReader && TryReadEntityByCode(codeArray[i], meta, out object fastObj) && fastObj != null)
                            {
                                hydrated[i] = (T)fastObj;
                                return;
                            }

                            hydrated[i] = (T)Read(type, IndexedRawRow(type, codeArray[i]), include);
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

                        var entity = (T)Read(type, IndexedRawRow(type, code), include);
                        if (entity != null && (residualFilter == null || residualFilter(entity)))
                            results.Add(entity);
                    }
                    return results;
                }

                Func<T, bool> compiled = expression.Compile();
                return SelectItems(type, obj => compiled((T)obj), false, include).Cast<T>().ToList();
            }
            finally
            {
                ExitReadOperation();
            }
        }

        public IEnumerable<object> QueryByExample(Type type, object example, string include = "*")
        {
            EnterReadOperation(type);
            try
            {
                return SelectItemsByExample(type, example, include).ToList();
            }
            finally
            {
                ExitReadOperation();
            }
        }

        public IEnumerable<object> QueryByExample(Type type, object[] examples, string include = "*")
        {
            EnterReadOperation(type);
            try
            {
                return SelectItemsByExamples(type, examples, include).ToList();
            }
            finally
            {
                ExitReadOperation();
            }
        }

        public object Find(Type type, Func<dynamic, bool> query, string include = "*")
        {
            EnterReadOperation(type);
            try
            {
                return SelectItems(type, query, false, include).FirstOrDefault();
            }
            finally
            {
                ExitReadOperation();
            }
        }

        public object FindLast(Type type, Func<dynamic, bool> query, string include = "*")
        {
            EnterReadOperation(type);
            try
            {
                return SelectItems(type, query, true, include).FirstOrDefault();
            }
            finally
            {
                ExitReadOperation();
            }
        }

        public object First(Type type, string include = "*")
        {
            EnterReadOperation(type);
            try
            {
                return SelectItems(type, null, false, include).FirstOrDefault();
            }
            finally
            {
                ExitReadOperation();
            }
        }

        public object Last(Type type, string include = "*")
        {
            EnterReadOperation(type);
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
                ExitReadOperation();
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

            foreach (var (_, r) in StoredRows(tableFile, backward))
            {
                var entity = Read(type, r, include);
                if (entity != null && (query == null || query(entity)))
                    yield return entity;
            }
        }

        /// <summary>
        /// A table's rows in stored order (D5), each with the code of the page it is in: the pages its table file
        /// lists, in that order, and each page's rows in order (backward: both reversed). Select and SQL read rows
        /// through this one walk. <paramref name="wantPage"/>, when given, skips the pages it refuses unopened.
        /// </summary>
        private IEnumerable<(string Page, XElement Row)> StoredRows(XFile tableFile, bool backward = false, Func<string, bool> wantPage = null)
        {
            IEnumerable<XElement> pages = tableFile.Pages();
            if (backward)
                pages = pages.Reverse();

            foreach (var pageElm in pages)
            {
                string pageFileName = pageElm.Attribute("file")?.Value;
                if (string.IsNullOrEmpty(pageFileName))
                    continue;
                string pageCode = System.IO.Path.GetFileNameWithoutExtension(pageFileName);
                if (wantPage != null && !wantPage(pageCode))
                    continue;
                ioService.RequirePage(tableFile, pageFileName);

                XFile pageFile = ioService.OpenFileOrCreate<TablePage>(pageFileName, openOnly: true);
                if (pageFile == null)
                    continue;

                IEnumerable<XElement> rows = pageFile.Rows();
                if (backward)
                    rows = rows.Reverse();

                foreach (var r in rows)
                    yield return (pageCode, r);
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

                    if (prop.TypeCategory == PropertyTypeCategory.Dictionary)
                    {
                        prop.SetValue(item, StoredValue.IsNil(e) ? null : ReadMapOfRow(type, row, prop, e));
                        continue;
                    }

                    if (!e.HasAttributes)
                    {
                        SetFromStoredScalar(item, prop, e);
                        continue;
                    }

                    XAttribute isColl = e.Attribute("collType");
                    if (isColl != null)
                    {
                        Type itemType = StoredItemType(prop);
                        if (itemType != null && ValueHelper.IsScalarType(itemType))
                            prop.SetValue(item, ReadStoredScalarItems(prop, itemType, e));
                    }
                    else
                    {
                        SetFromStoredScalar(item, prop, e);
                    }
                }

                return item;
            }

            // The row as a reference names it ("page.row"): the key of what the read shares (ItemsCacheService). A
            // row's code alone is unique only within its page. A row read for itself, not as a reference, has none.
            string referenceCode = track?.Code;
            if (track?.Parent != null && RecursionError(track, track.Parent))
            {
                track = null;
                lazyLoad = true;
                include = null;
            }

            string[] includedRefProps = string.IsNullOrEmpty(include) || include == "*"
                ? Array.Empty<string>()
                : include.Replace(" ", "").Split(',');


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
                if (e != null && prop.TypeCategory == PropertyTypeCategory.Dictionary)
                {
                    prop.SetValue(item, StoredValue.IsNil(e) ? null : ReadMapOfRow(type, row, prop, e));
                    continue;
                }
                if (e != null)
                {
                    XAttribute isColl = e.Attribute("collType");
                    if (isColl != null)
                    {

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
                                    // An entry whose row isn't there is left out, by design (ISS-210): XOD makes such
                                    // entries itself. A dropped table's rows go without the rows that name them, a row
                                    // deleted leaves the lists of the rows that aren't its parent, and a child with no
                                    // parent property leaves its entry after its delete (ISS-095), as 2.0 did for any. Not damage to report, unlike a page a table
                                    // names (RequirePage) or a row an index names (IndexedRawRow), which XOD keeps in step.
                                    if (rawChildRow == null)
                                        return null;

                                    XAttribute rowCode = rawChildRow.Attribute("code");
                                    if (rowCode == null)
                                        return null;

                                    object obj = itemsCacheService.Get(itemType, fullChildCode, lazyLoad, includedRefProps);
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
                                {
                                    // A reference whose page no table lists names a row that is gone (its table
                                    // dropped, its last row deleted): null, as for any reference whose row is gone
                                    // (ISS-210). It threw that the type isn't registered, and the whole row read
                                    // failed. A page a table lists whose type isn't registered still throws.
                                    string pageTable = ioService.PageTableName(itemParts[0]);
                                    if (pageTable == null)
                                        continue;
                                    targetPropType = propertyService.RegisteredType(System.IO.Path.GetFileNameWithoutExtension(pageTable));
                                }
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

                            object cachedItem = itemsCacheService.Get(targetPropType, stringValue, lazyLoad, includedRefProps);
                            if (cachedItem == null)
                            {
                                var refTrack = new ReadWriteTrack
                                {
                                    Code = stringValue,
                                    Type = targetPropType,
                                    Parent = track,
                                    ReadId = Guid.NewGuid()
                                };

                                // A reference whose row isn't there (deleted, or its table dropped: neither changes
                                // the rows that name it) is found again by its foreign key, or reads as null: by
                                // design, as for a list's entry above (ISS-210).
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

            itemsCacheService.Add(new ItemCache
            {
                Type = type,
                Code = referenceCode,
                Item = item,
                LazyLoaded = lazyLoad,
                IncludedReferenceProperties = include != "*" ? null : includedRefProps,
            });
            return item;
        }

        // The value collections a row stores (PLAN-046 section 7), read here for both row readers (the standalone one
        // and the general one each had a copy). A map's entry and a list's item marked nil="true" is null (ISS-097).

        private static Type StoredItemType(PropertyInfoItem prop) =>
            prop.PropertyType.IsArray ? prop.PropertyType.GetElementType() : prop.PropertyType.GetGenericArguments().FirstOrDefault();

        /// <summary>
        /// A map property's stored value that isn't null, for both row readers: one that isn't a map fails, naming the row
        /// (ISS-214). Both readers sent a list to the list reader, which set a list on the map property
        /// (<c>ArgumentException</c>). SQL reads it as NULL with a warning, as any value that isn't its column's type. An
        /// update of such a row reads it too where its type has relations, so the way out is to delete the row and insert
        /// it again.
        /// </summary>
        private static IDictionary ReadMapOfRow(Type type, XElement row, PropertyInfoItem prop, XElement e)
        {
            if (!StoredValue.IsStoredMap(e))
                throw new DatabaseFileException($"The row '{row.Attribute("code")?.Value}' of {type.Name} stores {prop.PropertyName} as a " +
                    "list, which a map property can't read: XOD 2.0.x stored an IDictionary<K, V> property so, each pair as its text, " +
                    "and couldn't read it back either. Delete the row and insert it again with the map set.");
            return ReadStoredMap(prop, e);
        }

        private static IDictionary ReadStoredMap(PropertyInfoItem prop, XElement e)
        {
            // The key and value types its IDictionary<K, V> or IReadOnlyDictionary<K, V> names (strings for a
            // Hashtable); the property's own type where it can be made, a Dictionary<K, V> otherwise (ValueHelper.MapToRead).
            // A Dictionary<,> was set on every map property, which a SortedDictionary or a Hashtable can't hold: once an
            // empty map was stored, reading one threw (review of TASK-071; a map with entries always did).
            Type[] types = ValueHelper.MapArguments(prop.PropertyType);
            Type keyType = types?[0] ?? typeof(string);
            Type valType = types?[1] ?? typeof(string);
            var map = (IDictionary)ValueHelper.CreateInstance(ValueHelper.MapToRead(prop.PropertyType));
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
        private object StoredScalar(PropertyInfoItem prop, XElement entity) =>
            StoredValue.ReadBack(prop, entity, text => ReadValue(prop, prop.PropertyType, text));

        /// <summary>
        /// A one-way value goes back as stored: noted, in a database said to hold 1.x digests, so a write of this very
        /// string knows it for a digest (ISS-171, <see cref="ReadDigests"/>). Every read that gives one back calls it.
        /// </summary>
        internal void NoteReadDigest(object value, CryptoMethod method)
        {
            if (options.Legacy1xDigests)
                ReadDigests.Note(value, method);
        }

        private object ReadValue(PropertyInfoItem prop, Type targetType, string stringValue)
        {
            if (!ValueHelper.IsScalarType(targetType))
                return null;

            if (targetType == typeof(string) && prop.Encryption == CryptoMethod.AES256)
            {
                return propertyKeys.Decrypt(stringValue, prop.PropertyName);
            }

            object value = ValueHelper.ParseScalar(targetType, stringValue);
            NoteReadDigest(value, prop.Encryption);
            return value;
        }

        /// <summary>
        /// The rows a key names, by the index: the one lookup by key. It answers only for an example that sets every key
        /// part (ISS-188): a part at its default is no value, and the index looks a value up literally, so an example
        /// without a key matched a row stored with a key part at its default, with indexing on and not off. False when
        /// the index can't answer; the caller then reads the rows.
        /// </summary>
        private bool TryLookupByKey(Type type, object example, out IReadOnlyCollection<string> codes)
        {
            codes = null;
            if (!options.EnableIndexing || example == null || KeyPartsSet(type, example) != KeyParts.All)
                return false;
            var meta = propertyService.GetMetadata(type);
            IReadOnlyList<PropertyInfoItem> pkProps = meta != null
                ? meta.PrimaryKeyProperties
                : propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).ToList();
            return TryLookupCodesByProperties(type, example, pkProps, out codes, out bool covered) && covered;
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
            if (tableFile == null)
                return Enumerable.Empty<object>();

            var meta = propertyService.GetMetadata(type);
            var test = ExampleTestFor(type, example);
            if (TryLookupCodesByProperties(type, example, test.Set, out var candidateCodes, out bool isFullyCovered))
            {
                if (candidateCodes.Count == 0)
                    return Enumerable.Empty<object>();

                var results = new List<object>(candidateCodes.Count);
                bool useFastReader = isFullyCovered && meta != null && meta.IsPureScalarStandalone;
                foreach (var code in candidateCodes)
                {
                    if (useFastReader && TryReadEntityByCode(code, meta, out object fastEntity) && fastEntity != null)
                    {
                        results.Add(fastEntity);
                        continue;
                    }

                    XElement rawRow = IndexedRawRow(type, code);
                    if (isFullyCovered || Matches(test, rawRow.Elements().FirstOrDefault()))
                        results.Add(Read(type, rawRow, include));
                }
                return results;
            }

            return TableRows(tableFile)
                .Where(r => Matches(test, r.Elements().FirstOrDefault()))
                .Select(r => Read(type, r, include))
                .ToList();
        }

        private IEnumerable<object> SelectItemsByExamples(Type type, object[] examples, string include = null)
        {
            if (type == null || examples == null || examples.Length == 0)
                return Enumerable.Empty<object>();

            propertyService.LoadType(type);
            string docFileName = $"{type.FullName}.xtab";
            XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
            if (tableFile == null)
                return Enumerable.Empty<object>();

            var tests = examples.Select(example => ExampleTestFor(type, example)).ToList();
            return TableRows(tableFile)
                .Where(r => tests.Any(t => Matches(t, r.Elements().FirstOrDefault())))
                .Select(r => Read(type, r, include))
                .ToList();
        }

        // Every row of a table, page by page.
        private IEnumerable<XElement> TableRows(XFile tableFile) =>
            tableFile.Pages()
                .Select(s => s.Attribute("file").Value)
                .SelectMany(file =>
                {
                    ioService.RequirePage(tableFile, file);
                    return ioService.OpenFileOrCreate<TablePage>(file, openOnly: true).Rows();
                });

        private XRow GetRow(Type type, object example)
        {
            if (type == null || example == null)
                return null;

            propertyService.LoadType(type);
            var pkProps = propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).ToList();
            if (TryLookupByKey(type, example, out var pkCodes))
            {
                if (pkCodes.Count == 0)
                    return null;

                string docFileName = $"{type.FullName}.xtab";
                XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
                if (tableFile != null)
                {
                    foreach (var code in pkCodes)
                        return IndexedRow(type, tableFile, code);
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
                var test = ExampleTestFor(type, item);

                if (TryLookupCodesByProperties(type, item, test.Set, out var candidateCodes, out bool isFullyCovered))
                {
                    if (candidateCodes.Count == 0)
                        return null;

                    if (isFullyCovered)
                    {
                        foreach (var code in candidateCodes)
                            return IndexedRow(type, tableFile, code);
                        return null;
                    }
                }

                foreach (var page in tableFile.Pages())
                {
                    ioService.RequirePage(tableFile, page.Attribute("file").Value);
                    XFile pageFile = ioService.OpenFileOrCreate<TablePage>(page.Attribute("file").Value, openOnly: true);
                    if (pageFile != null)
                    {
                        bool found = pageFile.Rows().Any(r =>
                        {
                            if (Matches(test, r.Elements().FirstOrDefault()))
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
                if (TryLookupByKey(type, example, out var pkCodes))
                {
                    if (pkCodes.Count == 0)
                        return null;

                    foreach (var code in pkCodes)
                    {
                        return new XRowTree
                        {
                            Table = tableFile,
                            Rows = new List<XRow> { IndexedRow(type, tableFile, code) }
                        };
                    }
                    return null;
                }

                object item = pkProps.Count > 0
                    ? FilterItemPrimaryProperties(type, example)
                    : FilterItemBaseProperties(type, example);

                // A type without a key: every page's matches, each with its page. The walk stopped at the first page
                // that had one, so an Update or a Delete by example left the rows on the other pages (ISS-136).
                // A type with a key is found by every part of its key (ISS-188: an example that sets only some is
                // refused), which names one row: the walk stops at the first page that has it.
                bool firstPageOnly = pkProps.Count > 0;
                var result = new XRowTree
                {
                    Rows = new List<XRow>()
                };

                var test = ExampleTestFor(type, item);

                foreach (var page in tableFile.Pages())
                {
                    ioService.RequirePage(tableFile, page.Attribute("file").Value);
                    XFile pageFile = ioService.OpenFileOrCreate<TablePage>(page.Attribute("file").Value, openOnly: true);
                    if (pageFile != null)
                    {
                        var matchedRows = pageFile.Rows()
                            .Where(r => Matches(test, r.Elements().FirstOrDefault()))
                            .ToList();

                        if (matchedRows.Count > 0)
                        {
                            result.Rows.AddRange(matchedRows.Select(r => new XRow { Table = tableFile, Page = pageFile, Row = r }));
                            result.Table = tableFile;
                            if (firstPageOnly)
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

        // A row by its code; a page that can't be read throws (DatabaseFileException). Any failure read as "no row"
        // here (ISS-144).
        /// <summary>
        /// ISS-196: the row an index names, for every read that takes its rows from an index (a query, a lookup by key
        /// or by example): it is on its page, or the index is out of date, a <see cref="DatabaseFileException"/>. A read
        /// dropped the row and went on, so a query returned fewer rows than the table holds, and a lookup by key found
        /// none, with no error. Not rebuilt here: an index that is wrong is a fault to see, and rebuilding at each such
        /// read would hide it. The message gives the way out: the index file deleted, it is built from the pages again
        /// when next needed.
        /// </summary>
        private XElement IndexedRawRow(Type type, string code) => GetRawData(code) ?? throw StaleIndex(type, code);

        /// <inheritdoc cref="IndexedRawRow"/>
        private XRow IndexedRow(Type type, XFile tableFile, string code)
        {
            string[] parts = code.Split('.');
            XFile pageFile = parts.Length == 2 ? ioService.OpenFileOrCreate<TablePage>($"{parts[0]}.xpag", openOnly: true) : null;
            XElement row = pageFile?.GetRowByCode(parts[1]);
            if (row == null)
                throw StaleIndex(type, code);
            return new XRow { Table = tableFile, Page = pageFile, Row = row };
        }

        private static DatabaseFileException StaleIndex(Type type, string code) =>
            new DatabaseFileException("The index of " + type.Name + " names the row '" + code + "', which isn't on its page: the index file '" +
                Xod.Services.IndexService.GetIndexFileName(type) + "' is out of date, or the page was lost. Close the database and delete that file: it " +
                "is built again from the pages when it is next needed, and a page that is missing is then named.");

        private XElement GetRawData(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            int dot = code.IndexOf('.');
            if (dot <= 0 || dot >= code.Length - 1) return null;

            string pageFileName = string.Concat(code.AsSpan(0, dot), ".xpag");
            string rowCode = code.Substring(dot + 1);
            return ioService.ReadRowColdOrCached(pageFileName, rowCode);
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
