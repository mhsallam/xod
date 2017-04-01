using Xod.Helpers;
using Xod.Infra;
using Xod.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace Xod.Engines.Xml
{
    public class XmlEngine : IXodEngine
    {
        private static readonly object locker = new object();

        const int PAGE_SIZE = 524288;

        string password = null;
        string root = null;
        string path = null;

        //services
        IOService ioService = null;
        IXodSecurityService securityService = null;
        PropertyService propertyService = null;
        AutonumberService autonumberService = null;
        ItemsCacheService itemsCacheService = null;
        LogService logService = null;
        ExceptionService exceptionService = null;

        public XmlEngine(string file, string password = null, DatabaseOptions options = null)
        {
            if (string.IsNullOrEmpty(file))
                throw new ArgumentNullException();

            if (!string.IsNullOrEmpty(password) && (password.Length < 1 || password.Length > 256))
                throw new SecurityException("Password length should be between 1 and 256.");

            if (options == null)
                options = new DatabaseOptions()
                {
                    InitialCreate = true,
                    LazyLoadParent = true
                };


            lock (locker)
            {
                if (file.ToLower().EndsWith(".xod"))
                {
                    path = file;
                    root = System.IO.Path.GetDirectoryName(file);
                }
                else
                    root = file;

                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    if (Directory.Exists(root))
                    {
                        string[] files = Directory.GetFiles(root, "*.xod", SearchOption.TopDirectoryOnly);
                        if (files.Length > 0)
                            path = files[0];
                    }
                    else if (options.InitialCreate)
                    {
                        Directory.CreateDirectory(root);
                        path = System.IO.Path.Combine(root, "data.xod");
                        IsNew = true;
                    }
                }
            }


            //services
            this.propertyService = new PropertyService();
            this.propertyService.LoadType(typeof(Table));

            if (!string.IsNullOrEmpty(password))
                this.password = CryptoHelper.GetSHA256HashData(password);

            this.securityService = new XodSecurityService(path, this.password);
            this.ioService = new IOService(path, this.password, this.propertyService);
            this.ioService.ItemWriterDelegate = (t) =>
            {
                return new XDocument(Write(t, Activator.CreateInstance(t)));
            };


            //open or create database main file
            string databaseFile = System.IO.Path.GetFileName(path);
            XFile xodFile = this.ioService.OpenFileOrCreate<Database>(databaseFile, !options.InitialCreate, options.InitialCreate);
            if (xodFile == null)
                exceptionService.Throw(new DatabaseFileException());
            else
                this.path = xodFile.Path;

            //services
            this.itemsCacheService = new ItemsCacheService(path);
            this.exceptionService = new ExceptionService(path);

            this.autonumberService = new AutonumberService(path,
                this.propertyService,
                this.ioService,
                this.exceptionService);
            this.autonumberService.FindLastDelegate = (t) =>
            {
                return Last(t);
            };
            this.logService = new LogService(root);


            this.LazyLoad = options.LazyLoad;
            this.LazyLoadParent = options.LazyLoadParent;
            //Compressed = defaultOptions.Compressed;


            //clear cached file on security state changes
            //this.securityService.Changed += (sender, e) =>
            //{
            //    foreach (var f in this.cachedFiles.ToArray())
            //    {
            //        f.Dispose();
            //        this.cachedFiles.Remove(f);
            //    }

            //    GC.Collect();
            //    GC.WaitForPendingFinalizers();
            //};

        }

        private string Create(Type type, object item, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            lazyLoad = (lazyLoad) ? true : LazyLoad;
            if (null == item)
                exceptionService.Throw(new ArgumentNullException());

            TriggerEventArgs trigger = new TriggerEventArgs()
            {
                Item = item,
                Type = type,
                Action = DatabaseActions.Insert
            };

            if (null != BeforeAction)
                BeforeAction(this, trigger);

            if (trigger.Cancel)
                return null;

            //int guidKeys = 0;
            //int numericKeys = 0;


            //Dictionary<string, dynamic> lastSeeds = LastSeeds(type);

            //set auto number values
            Dictionary<string, dynamic> autonumbers = this.autonumberService.Autonumber(type, item);

            //Dictionary<string, string> autonumbers = new Dictionary<string, string>();
            //List<string> autonumberedProps = new List<string>();
            //var autonumberProps = this.propertyService.Properties(type.FullName).Where(s => s.IsAutonumber);
            //foreach (var autonumberProp in autonumberProps)
            //{
            //    if (!ValueHelper.IsNumeric(autonumberProp.PropertyType) && !autonumberProp.PropertyType.Equals(typeof(Guid)))
            //        exceptionService.Throw(new AutonumberDataTypeException());

            //    if (ValueHelper.DefaultOf(autonumberProp.PropertyType).Equals(autonumberProp.Property.GetValue(item)))
            //    {
            //        dynamic next = null;
            //        if (!autonumberProp.PropertyType.Equals(typeof(Guid)))
            //        {
            //            next = NextNumber(type, autonumberProp);
            //            autonumberProp.Property.SetValue(item, next);
            //            numericKeys++;
            //            autonumberedProps.Add(autonumberProp.Property.Name);
            //            //seeds.Add(autoNumberProp.Property.Name, nn.ToString());
            //        }
            //        else
            //        {
            //            next = Guid.NewGuid();
            //            autonumberProp.Property.SetValue(item, next);
            //            guidKeys++;
            //        }
            //        autonumbers.Add(autonumberProp.PropertyName, next);
            //    }
            //}

            ////check if primary key properties has been auto numbered
            //bool autoNumbered = true;
            //var primPropAtts = this.propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey);
            //foreach (var primPropAtt in primPropAtts)
            //    if (!autoNumberedProps.Contains(primPropAtt.PropertyName))
            //    {
            //        autoNumbered = false;
            //        break;
            //    }

            ////no ReservedKeyTest for Guid
            //if (guidKeys != 1)
            //    ReservedKeyTest(type, item);

            //var primaryKeysProps = this.propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).Select(s => s.PropertyName);
            //var uniqueKeysProps = this.propertyService.Properties(type.FullName).Where(s => s.IsUnique).Select(s => s.PropertyName);

            //if (uniqueKeysProps.Any() || primaryKeysProps.Any())
            //    ReservedKeyTest(type, item, primaryKeysProps, uniqueKeysProps);

            ReservedKeyTest(type, item);

            //var reserved = (guidKeys != 1) ? ReservedPropertyType.None : ReservedKeyTest(type, item);
            //if (!autoNumbered && reserved == ReservedPropertyType.Primary)
            //    exceptionService.Throw(new ReservedPrimaryKeyException());
            //else if (reserved == ReservedPropertyType.Unique)
            //    exceptionService.Throw(new ReservedUniqueKeyException());


            string refCode = AddToPage(type, item, autonumbers, lazyLoad, writeTrack);
            //string refCode = AddToPage(type, item, seeds, lazyLoad, writeTrack);

            if (null != AfterAction)
                AfterAction(this, trigger);

            return refCode;
        }

        private string AddToPage(Type type, object item, Dictionary<string, dynamic> autonumbers = null, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            string itemCode = null;
            string itemPath = null;

            string typeName = type.FullName;
            if (typeName == "System.Object")
                typeName = item.GetActualType().FullName;

            string tableFileName = string.Format("{0}.{1}", typeName, "xtab");

            XFile tableFile = this.ioService.OpenFileOrCreate(tableFileName, typeof(Table));
            if (tableFile != null)
            {
                XFile pageFile = this.ioService.OpenPageOrCreate(tableFile);

                //move this code to ioService
                //XElement page = tableFile.Pages().FirstOrDefault(delegate(XElement s)
                //{
                //    XAttribute fullAtt = s.Attribute("full");
                //    return fullAtt == null || fullAtt.Value != "true";
                //});
                //string pageFileName = null;
                //if (page == null)
                //{
                //    pageFileName = string.Format("{0}.{1}", ValueHelper.PickCode(), "xpag");
                //    page = new XElement("Page", new XAttribute("file", pageFileName));
                //    page.Add(new XAttribute("full", false));
                //    tableFile.Root().Element("Pages").Add(page);
                //}
                //else
                //{
                //    pageFileName = page.Attribute("file").Value;
                //}

                //this.indexService.Index(type, autonumbers, page);
                //XFile pageFile = this.ioService.OpenFileOrCreate(pageFileName, typeof(TablePage));

                if (pageFile != null)
                {
                    string pageFileName = System.IO.Path.GetFileName(pageFile.Path);
                    string pageCode = System.IO.Path.GetFileNameWithoutExtension(pageFile.Path);
                    itemCode = ValueHelper.PickCode();
                    itemPath = string.Format("{0}.{1}", pageCode, itemCode);

                    ReadWriteTrack itemCreateTrack = new ReadWriteTrack()
                    {
                        Item = item,
                        Type = type,
                        Code = itemPath,
                        Parent = writeTrack
                    };

                    XElement xItem = Write(type, item, lazyLoad, itemCreateTrack);
                    if (null != xItem)
                    {
                        XElement re = new XElement("Row", new XAttribute("code", itemCode), xItem);
                        pageFile = this.ioService.OpenFileOrCreate(pageFileName, typeof(TablePage));
                        pageFile.Root().Element("Rows").Add(re);
                        //if (pageFile.Size() >= PAGE_SIZE)
                        if (this.ioService.Size(pageFile.Path) >= PAGE_SIZE)
                        {
                            var fullPage = tableFile.Pages()
                                .FirstOrDefault(s => pageFile.Path.EndsWith(s.Attribute("file").Value));

                            if (null != fullPage)
                                fullPage.Attribute("full").Value = "true";
                        }
                        this.ioService.Save(pageFile);
                        //pageFile.Save();

                        ////record last autonumber values
                        //var xSeed = tableFile.Root().Element("Seed");
                        //if (xSeed == null)
                        //    xSeed = new XElement("Seed");

                        //if(seeds != null) {
                        //    foreach (var seed in seeds)
                        //    {
                        //        xSeed.Add(new XAttribute("property", seed.Key));
                        //        xSeed.Add(new XAttribute("value", seed.Value));
                        //    }

                        //    if (tableFile.Root().Element("Seed") == null)
                        //        tableFile.Root().Add(xSeed);
                        //    else
                        //        tableFile.Root().Element("Seed").ReplaceNodes(xSeed.Elements());
                        //}
                    }
                    this.ioService.Save(tableFile);
                    //tableFile.Save();
                }
            }
            return itemPath;
        }
        private XElement Write(Type type, object item, bool lazyLoad = false, ReadWriteTrack writeTrack = null, UpdateFilter filter = null)
        {
            lazyLoad = (lazyLoad) ? true : LazyLoad;
            if (null == item)
                return null;

            if (type == typeof(object))
                type = item.GetActualType();

            XElement xItem = new XElement(type.Name);
            XDocument itemFile = new XDocument(xItem);

            //all public properties
            IEnumerable<PropertyInfoItem> props = this.propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped).OrderByDescending(s =>
                s.PropertyType != null && (
                s.PropertyType.GetTypeInfo().IsValueType ||
                s.PropertyType == typeof(string) ||
                s.PropertyType == typeof(DateTime)));

            if (null != filter && null != filter.Properties)
            {
                IEnumerable<PropertyInfoItem> filterProps = null;
                if (filter.Behavior == UpdateFilterBehavior.Skip)
                    filterProps = props.Where(s => !filter.Properties.Contains(s.PropertyName));
                else
                    filterProps = props.Where(s => filter.Properties.Contains(s.PropertyName));

                foreach (var prop in filterProps)
                    WriteProperty(type, item, xItem, prop, lazyLoad, writeTrack);
            }
            else
            {
                foreach (var prop in props)
                    WriteProperty(type, item, xItem, prop, lazyLoad, writeTrack);
            }

            return xItem;
        }
        private XElement WriteProperty(Type type, object item, XElement xItem, PropertyInfoItem prop, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            object value = prop.Property.GetValue(item);
            //properties marked as required bu have null value will raise an exception
            if (prop.IsRequired && (null == value || value.Equals(ValueHelper.DefaultOf(prop.PropertyType))))
            {
                exceptionService.Throw(new RequiredPropertyException());
                //Rollback();
            }
            XElement propertyElement = null;

            if (prop.IsGenericType && null != value)
                prop.PropertyType = value.GetActualType();

            //for string, primitive, or enum values
            if (prop.PropertyType.GetTypeInfo().IsValueType || prop.PropertyType == typeof(string))
            {
                if (null == value || value.Equals(ValueHelper.DefaultOf(prop.PropertyType)))
                    return null;

                if (prop.ValuePosition == ValuePosition.Attribute)
                    xItem.Add(new XAttribute(prop.PropertyName, value));
                else if (prop.PropertyType == typeof(string))
                {
                    if (prop.Encryption == CryptoMethod.MD5)
                        value = CryptoHelper.GetMD5HashData(value.ToString());
                    else if (prop.Encryption == CryptoMethod.SHA1)
                        value = CryptoHelper.GetSHA1HashData(value.ToString());

                    if (!prop.IsMarkup)
                        propertyElement = new XElement(prop.PropertyName, value);
                    else
                        propertyElement = new XElement(prop.PropertyName, new XCData(value.ToString()));
                    xItem.Add(propertyElement);
                }
                else
                {
                    propertyElement = new XElement(prop.PropertyName, value);
                    xItem.Add(propertyElement);
                }
            }
            //for array and generic collection values
            else if ((prop.PropertyType.IsArray && prop.PropertyType.GetArrayRank() == 1) ||
                (null != prop.PropertyType.GetTypeInfo().GetInterface("ICollection")))
            {
                if (null == value)
                    return null;

                Type itmType = null;
                string collType = string.Empty;
                if (prop.PropertyType.IsArray)
                {
                    itmType = prop.PropertyType.GetElementType();
                    collType = "array";
                }
                else
                {
                    itmType = prop.PropertyType.GetGenericArguments().FirstOrDefault();
                    collType = "generic";
                }

                propertyElement = new XElement(prop.PropertyName, new XAttribute[] {
                    new XAttribute("dataType", itmType),
                    new XAttribute("collType", collType)
                });

                //collection that contains string, primitive, datetime or enum
                if (itmType.GetTypeInfo().IsValueType || itmType == typeof(string))
                {
                    IEnumerable items = null;
                    if (prop.PropertyType.IsArray)
                        items = value as Array;
                    else if (value != null)
                    {
                        try
                        {
                            items = value as IEnumerable;
                        }
                        catch
                        {

                        }
                    }

                    foreach (var itm in items)
                        if (itm != null)
                            propertyElement.Add(new XElement(itmType.Name, itm));
                }
                //for collection reference type
                else if (!lazyLoad)
                {
                    if (null == value)
                        return null;

                    var children = from row in value as IEnumerable<object> select row;
                    ParseChildren(type, item, itmType, children, prop, propertyElement, lazyLoad, writeTrack);
                }
                xItem.Add(propertyElement);
            }
            //for reference type values
            else if (!lazyLoad && prop.PropertyType.GetTypeInfo().IsClass)
            {
                ParseReference(type, item, value, prop, xItem, writeTrack);
            }

            return propertyElement;
        }
        private XElement Rewrite(Type type, object item, XElement xItem, List<PropertyInfoItem> selectedProps, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            lazyLoad = (lazyLoad) ? true : LazyLoad;
            if (null == item)
                return null;

            //all public properties
            var props = selectedProps.Where(s => !s.IsNotMapped).OrderByDescending(s => s.PropertyType.GetTypeInfo().IsValueType || s.PropertyType == typeof(string) || s.PropertyType == typeof(DateTime));
            foreach (var prop in props)
            {
                XElement pe = WriteProperty(type, item, xItem, prop, lazyLoad, writeTrack);
                if (null != pe)
                {
                    XElement te = xItem.Element(prop.PropertyName);
                    if (null != te)
                        te.ReplaceWith(pe);
                }
            }
            return xItem;
        }
        //private object Read(Type type, XElement row, bool lazyLoad = false, ReadWriteTrack track = null)
        //{
        //    lazyLoad = (lazyLoad) ? true : LazyLoad;
        //    XElement element = row.Elements().FirstOrDefault();

        //    if (type.Name != element.Name)
        //        type = this.propertyService.RegisteredTypeByName(element.Name.LocalName);

        //    bool parentIsLeaf = false;
        //    if (null != track &&
        //        null != track.Parent &&
        //        RecursionError(track, track.Parent))
        //    {
        //        parentIsLeaf = true;
        //        track = null;
        //        lazyLoad = true;
        //    }

        //    object item = Activator.CreateInstance(type);
        //    string itemCode = row.Attribute("code").Value;
        //    Guid readId = Guid.NewGuid();

        //    var props = this.propertyService.Properties(type.FullName).Where(s =>
        //        !s.IsNotMapped).OrderByDescending(s =>
        //        null != s.PropertyType && (
        //        s.PropertyType.IsValueType ||
        //        s.PropertyType == typeof(string) ||
        //        s.PropertyType == typeof(DateTime)));

        //    foreach (var prop in props)
        //    {
        //        if (prop.IsReadOnly)
        //            continue;

        //        XElement e = element.Element(prop.PropertyName);
        //        if (e != null)
        //        {
        //            //parsing collection types
        //            XAttribute isColl = e.Attribute("collType");
        //            if (null != isColl)
        //            {
        //                string addRangeMethod = null;
        //                Type itemType = null;
        //                if (prop.PropertyType.IsArray)
        //                {
        //                    itemType = prop.PropertyType.GetElementType();
        //                    addRangeMethod = "AddRangeFromString";
        //                }
        //                else
        //                {
        //                    itemType = prop.PropertyType.GetGenericArguments().FirstOrDefault();
        //                    addRangeMethod = "AddRange";
        //                }

        //                if (null == itemType)
        //                    continue;

        //                Type[] typeArgs = { itemType };
        //                var collType = typeof(List<>);
        //                Type collGenType = collType.MakeGenericType(typeArgs);
        //                dynamic coll = Activator.CreateInstance(collGenType);
        //                object[] args = null;
        //                var collElements = e.Elements(itemType.Name).Where(s => null != s).Select(s => s.Value).ToArray();


        //                //extension method that adds range of elements with casting operation
        //                MethodInfo mi = typeof(CollectionExtensions)
        //                    .GetMethod(addRangeMethod)
        //                    .MakeGenericMethod(new Type[] { itemType });

        //                if (itemType.IsValueType || itemType == typeof(string))
        //                {
        //                    args = new object[] { coll, collElements };
        //                    mi.Invoke(coll, args);

        //                    if (!prop.PropertyType.IsArray)
        //                        prop.Property.SetValue(item, coll);
        //                    else
        //                        prop.Property.SetValue(item, coll.ToArray());
        //                }
        //                //ignore reference collection and array type if lazyLoad is true
        //                else if (!lazyLoad)
        //                {
        //                    var collCodes = collElements.Select(s => GetRawData(s)).Where(s => null != s);
        //                    int i = 0;
        //                    var collQuery = collCodes.Select(delegate(XElement s)
        //                    {
        //                        i++;
        //                        XAttribute rowCode = s.Attribute("code");
        //                        if (null == rowCode)
        //                            return null;

        //                        string childCode = string.Format("{0}.{1}", this.ioService.GetItemPage(itemType, rowCode.Value), rowCode.Value);
        //                        object obj = null;

        //                        obj = this.itemsCacheService.Get(itemType, childCode, lazyLoad);
        //                        if (null != obj)
        //                            return obj;

        //                        ReadWriteTrack childTrack = new ReadWriteTrack()
        //                        {
        //                            Code = childCode,
        //                            Type = itemType,
        //                            Parent = track,
        //                            ReadId = Guid.NewGuid(),
        //                        };
        //                        obj = Read(itemType, s, false, childTrack);

        //                        return obj;
        //                    }).Where(s => s != null);

        //                    args = new object[] { coll, collQuery.ToArray() };
        //                    mi.Invoke(coll, args);

        //                    if (!prop.PropertyType.IsArray)
        //                        prop.Property.SetValue(item, coll);
        //                    else
        //                        prop.Property.SetValue(item, coll.ToArray());
        //                }
        //            }
        //            //parse non-collection/array types
        //            else
        //            {
        //                object value = null;
        //                string stringValue = string.Empty;

        //                if (prop.ValuePosition == ValuePosition.Attribute)
        //                {
        //                    stringValue = element.Attribute(prop.PropertyName).Value;
        //                    value = stringValue;
        //                    prop.Property.SetValue(item, value);
        //                }
        //                else
        //                {
        //                    stringValue = element.Element(prop.PropertyName).Value;
        //                    if (string.IsNullOrEmpty(stringValue))
        //                        continue;

        //                    if (prop.IsGenericType)
        //                    {
        //                        if (string.IsNullOrEmpty(prop.GenericTypeProperty))
        //                        {
        //                            string[] itemPath = stringValue.Split('.');
        //                            if (itemPath.Length == 2)
        //                                prop.PropertyType = this.ioService.GetPageType(itemPath[0]);
        //                        }
        //                        else
        //                        {
        //                            var genericProp = props.FirstOrDefault(s => s.PropertyName == prop.GenericTypeProperty);
        //                            if (null != genericProp)
        //                            {
        //                                object genericPropValue = genericProp.Property.GetValue(item);
        //                                if (null != genericPropValue)
        //                                {
        //                                    string genericPropName = genericProp.Property.GetValue(item).ToString();
        //                                    var genericType = this.propertyService.RegisteredType(genericPropName);
        //                                    if (null != genericType)
        //                                        prop.PropertyType = genericType;
        //                                }
        //                            }
        //                        }

        //                        if (null == prop.PropertyType)
        //                            exceptionService.Throw(new AnynomousTypeException());
        //                    }

        //                    value = ReadValue(prop, stringValue);

        //                    if (null == value && !lazyLoad && prop.PropertyType.GetTypeInfo().IsClass)
        //                    {
        //                        object cachedItem = this.itemsCacheService.Get(prop.PropertyType, stringValue, lazyLoad);
        //                        if (null == cachedItem)
        //                        {

        //                            ReadWriteTrack refTrack = new ReadWriteTrack()
        //                            {
        //                                Code = stringValue,
        //                                Type = prop.PropertyType,
        //                                Parent = track,
        //                                ReadId = Guid.NewGuid(),
        //                            };

        //                            XElement xRefItem = GetRawData(stringValue);
        //                            if (null != xRefItem)
        //                            {
        //                                value = Read(prop.PropertyType, xRefItem, false, refTrack);
        //                            }
        //                            else
        //                            {
        //                                Dictionary<string, object> localRefKeyValues = new Dictionary<string, object>();
        //                                if (null != prop.ForeignKeys)
        //                                    foreach (var foreignKeyAtt in prop.ForeignKeys)
        //                                    {
        //                                        PropertyInfo localKeyProp = prop.PropertyType.GetProperty(foreignKeyAtt.LocalProperty);
        //                                        if (null != localKeyProp)
        //                                            localRefKeyValues.Add(foreignKeyAtt.RemoteProperty, localKeyProp.GetValue(item));
        //                                    }
        //                                value = SelectItemsByExample(prop.PropertyType, PopulateItemProperties(prop.PropertyType, localRefKeyValues)).FirstOrDefault();
        //                                //value = GetReferenceByProperties(prop.PropertyType, localRefKeyValues);
        //                            }
        //                        }
        //                        else
        //                            value = cachedItem;
        //                    }

        //                    if (value != null)
        //                        prop.Property.SetValue(item, value);
        //                }
        //            }
        //        }
        //        else if (prop.DefaultValue != null && !prop.DefaultValue.Equals(ValueHelper.DefaultOf(prop.PropertyType)))
        //            prop.Property.SetValue(item, prop.DefaultValue);
        //    }

        //    ItemCache cache = new ItemCache()
        //    {
        //        Type = type,
        //        Code = itemCode,
        //        Item = item,
        //        LazyLoaded = lazyLoad,
        //        ReadId = readId,
        //        ParentIsLeaf = parentIsLeaf
        //    };

        //    if (track != null)
        //        cache.ParentReadId = track.ReadId;

        //    this.itemsCacheService.Add(cache);

        //    return item;
        //}
        private object Read(Type type, XElement row, string include = null, ReadWriteTrack track = null)
        {
            bool lazyLoad = include != "*" ? true : LazyLoad;
            XElement element = row.Elements().FirstOrDefault();

            if (type.Name != element.Name)
                type = this.propertyService.RegisteredTypeByName(element.Name.LocalName);

            bool parentIsLeaf = false;
            if (null != track &&
                null != track.Parent &&
                RecursionError(track, track.Parent))
            {
                parentIsLeaf = true;
                track = null;
                lazyLoad = true;
                include = null;
            }

            string[] includedRefProps = string.IsNullOrEmpty(include) || include == "*" ?
                new string[]{} : include.Replace(" ", "").Split(',');

            object item = Activator.CreateInstance(type);
            string itemCode = row.Attribute("code").Value;
            Guid readId = Guid.NewGuid();

            var props = this.propertyService.Properties(type.FullName).Where(s =>
                !s.IsNotMapped && !s.IsReadOnly).OrderBy(s => s.TypeCategory).ToArray();

            //var props = this.propertyService.Properties(type.FullName).Where(s =>
            //    !s.IsNotMapped).OrderByDescending(s =>
            //    null != s.PropertyType &&
            //    (
            //    s.PropertyType.IsValueType ||
            //    s.PropertyType == typeof(string) ||
            //    s.PropertyType == typeof(DateTime)));

            foreach (var prop in props)
            {
                XElement e = element.Element(prop.PropertyName);
                if (e != null)
                {
                    //parsing collection types
                    XAttribute isColl = e.Attribute("collType");
                    if (null != isColl)
                    {
                        string addRangeMethod = null;
                        Type itemType = null;
                        if (prop.PropertyType.IsArray)
                        {
                            itemType = prop.PropertyType.GetElementType();
                            addRangeMethod = "AddRangeFromString";
                        }
                        else
                        {
                            itemType = prop.PropertyType.GetGenericArguments().FirstOrDefault();
                            addRangeMethod = "AddRange";
                        }

                        if (null == itemType)
                            continue;

                        Type[] typeArgs = { itemType };
                        var collType = typeof(List<>);
                        Type collGenType = collType.MakeGenericType(typeArgs);
                        dynamic coll = Activator.CreateInstance(collGenType);
                        object[] args = null;
                        var collElements = e.Elements(itemType.Name).Where(s => null != s).Select(s => s.Value).ToArray();


                        //extension method that adds range of elements with casting operation
                        MethodInfo mi = typeof(CollectionExtensions)
                            .GetMethod(addRangeMethod)
                            .MakeGenericMethod(new Type[] { itemType });

                        if (itemType.GetTypeInfo().IsValueType || itemType == typeof(string))
                        {
                            args = new object[] { coll, collElements };
                            mi.Invoke(coll, args);

                            if (!prop.PropertyType.IsArray)
                                prop.Property.SetValue(item, coll);
                            else
                                prop.Property.SetValue(item, coll.ToArray());
                        }
                        //ignore reference collection and array type if lazyLoad is true
                        else if (include == "*" || includedRefProps.Contains(prop.PropertyName))
                        //else if (!lazyLoad)
                        {
                            var collCodes = collElements.Select(s => GetRawData(s)).Where(s => null != s);
                            int i = 0;
                            var collQuery = collCodes.Select(delegate(XElement s)
                            {
                                i++;
                                XAttribute rowCode = s.Attribute("code");
                                if (null == rowCode)
                                    return null;

                                string childCode = string.Format("{0}.{1}", this.ioService.GetItemPage(itemType, rowCode.Value), rowCode.Value);
                                object obj = null;

                                obj = this.itemsCacheService.Get(itemType, rowCode.Value, lazyLoad, includedRefProps);
                                //obj = this.itemsCacheService.Get(itemType, childCode, lazyLoad, includedRefProps);
                                if (obj != null)
                                    return obj;

                                ReadWriteTrack childTrack = new ReadWriteTrack()
                                {
                                    Code = childCode,
                                    Type = itemType,
                                    Parent = track,
                                    ReadId = Guid.NewGuid(),
                                };

                                obj = Read(itemType, s, include, childTrack);
                                return obj;
                            }).Where(s => s != null);

                            args = new object[] { coll, collQuery.ToArray() };
                            mi.Invoke(coll, args);

                            if (!prop.PropertyType.IsArray)
                                prop.Property.SetValue(item, coll);
                            else
                                prop.Property.SetValue(item, coll.ToArray());
                        }
                    }
                    //parse non-collection/array types
                    else
                    {
                        object value = null;
                        string stringValue = string.Empty;

                        if (prop.ValuePosition == ValuePosition.Attribute)
                        {
                            stringValue = element.Attribute(prop.PropertyName).Value;
                            value = stringValue;
                            prop.Property.SetValue(item, value);
                        }
                        else
                        {
                            stringValue = element.Element(prop.PropertyName).Value;
                            if (string.IsNullOrEmpty(stringValue))
                                continue;

                            string[] itemParts = null;

                            if (prop.IsGenericType)
                            {
                                if (string.IsNullOrEmpty(prop.GenericTypeProperty))
                                {
                                    itemParts = stringValue.Split('.');
                                    if (itemParts.Length == 2)
                                        prop.PropertyType = this.ioService.GetPageType(itemParts[0]);
                                }
                                else
                                {
                                    var genericProp = props.FirstOrDefault(s => s.PropertyName == prop.GenericTypeProperty);
                                    if (null != genericProp)
                                    {
                                        object genericPropValue = genericProp.Property.GetValue(item);
                                        if (null != genericPropValue)
                                        {
                                            string genericPropName = genericProp.Property.GetValue(item).ToString();
                                            var genericType = this.propertyService.RegisteredType(genericPropName);
                                            if (null != genericType)
                                                prop.PropertyType = genericType;
                                        }
                                    }
                                }

                                if (null == prop.PropertyType)
                                    exceptionService.Throw(new AnynomousTypeException());
                            }

                            value = ReadValue(prop, stringValue);

                            //if (null == value && !lazyLoad && prop.PropertyType.GetTypeInfo().IsClass)
                            if (value == null && prop.PropertyType.GetTypeInfo().IsClass && (
                                include == "*" || includedRefProps.Contains(prop.PropertyName)) && (
                                !this.LazyLoadParent || prop.ReferenceType != PropertyReferenceType.Parent))
                            {
                                itemParts = stringValue.Split('.');
                                if (itemParts.Length != 2)
                                    continue;

                                object cachedItem = null;
                                cachedItem = this.itemsCacheService.Get(prop.PropertyType, itemParts[1], lazyLoad, includedRefProps);
                                //cachedItem = this.itemsCacheService.Get(prop.PropertyType, stringValue, lazyLoad, includedRefProps);
                                if (cachedItem == null)
                                {
                                    ReadWriteTrack refTrack = new ReadWriteTrack()
                                    {
                                        Code = stringValue,
                                        Type = prop.PropertyType,
                                        Parent = track,
                                        ReadId = Guid.NewGuid(),
                                    };

                                    XElement xRefItem = GetRawData(stringValue);
                                    if (null != xRefItem)
                                    {
                                        value = Read(prop.PropertyType, xRefItem, include, refTrack);
                                    }
                                    else
                                    {
                                        Dictionary<string, object> localRefKeyValues = new Dictionary<string, object>();
                                        if (null != prop.ForeignKeys)
                                            foreach (var foreignKeyAtt in prop.ForeignKeys)
                                            {
                                                PropertyInfo localKeyProp = prop.PropertyType.GetProperty(foreignKeyAtt.LocalProperty);
                                                if (null != localKeyProp)
                                                    localRefKeyValues.Add(foreignKeyAtt.RemoteProperty, localKeyProp.GetValue(item));
                                            }
                                        value = SelectItemsByExample(prop.PropertyType, PopulateItemProperties(prop.PropertyType, localRefKeyValues)).FirstOrDefault();
                                        //value = GetReferenceByProperties(prop.PropertyType, localRefKeyValues);
                                    }
                                }
                                else
                                    value = cachedItem;
                            }

                            if (value != null)
                                prop.Property.SetValue(item, value);
                        }
                    }
                }
                else if (prop.DefaultValue != null && !prop.DefaultValue.Equals(ValueHelper.DefaultOf(prop.PropertyType)))
                    prop.Property.SetValue(item, prop.DefaultValue);
            }

            ItemCache cache = new ItemCache()
            {
                Type = type,
                Code = itemCode,
                Item = item,
                LazyLoaded = lazyLoad,
                IncludedReferenceProperties = (include != "*") ? null : includedRefProps,

                ReadId = readId,
                ParentIsLeaf = parentIsLeaf
            };

            if (track != null)
                cache.ParentReadId = track.ReadId;

            this.itemsCacheService.Add(cache);

            return item;
        }

        private object ReadValue(PropertyInfoItem prop, string stringValue)
        {
            object value = null;
            TypeInfo ti = prop.PropertyType.GetTypeInfo();

            //parse enum values
            if (prop.PropertyType == typeof(string))
                value = stringValue;
            //parse primitive, datetime values
            else if (ti.IsEnum)
                value = Enum.Parse(prop.PropertyType, stringValue);
            else if (ti.IsPrimitive ||
                prop.PropertyType == typeof(DateTime))
                value = Convert.ChangeType(stringValue, prop.PropertyType);
            else if (prop.PropertyType == typeof(Guid))
                value = Guid.Parse(stringValue);
            else if (ti.IsGenericType &&
                prop.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                Type nullType = Nullable.GetUnderlyingType(prop.PropertyType);
                value = Convert.ChangeType(stringValue, nullType);
            }

            return value;
        }
        private bool UpdateItem(Type type, object item, object newItem, ReadWriteTrack track = null, UpdateFilter filter = null)
        {
            this.propertyService.LoadType(type);
            if (null == item || null == newItem)
                exceptionService.Throw(new ArgumentNullException());

            if (null != track &&
                null != track.Parent &&
                RecursionError(track, track.Parent))
                return false;

            bool value = false;
            //try
            //{
            XRowTree rowTree = GetTree(type, item);
            if (null != rowTree)
            {
                IEnumerable<PropertyInfoItem> props = null;

                if (null != filter && null != filter.Properties)
                {
                    if (filter.Behavior == UpdateFilterBehavior.Skip)
                        props = this.propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped && !filter.Properties.Contains(s.PropertyName)).OrderByDescending(s =>
                            null != s.PropertyType && (
                            s.PropertyType.GetTypeInfo().IsValueType ||
                            s.PropertyType == typeof(string) ||
                            s.PropertyType == typeof(DateTime)));
                    else
                        props = this.propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped && filter.Properties.Contains(s.PropertyName)).OrderByDescending(s =>
                            null != s.PropertyType && (
                            s.PropertyType.GetTypeInfo().IsValueType ||
                            s.PropertyType == typeof(string) ||
                            s.PropertyType == typeof(DateTime)));
                }
                else
                    props = this.propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped).OrderByDescending(s =>
                        null != s.PropertyType && (
                        s.PropertyType.GetTypeInfo().IsValueType ||
                        s.PropertyType == typeof(string) ||
                        s.PropertyType == typeof(DateTime)));

                string pageCode = null;
                if (null != rowTree.Page)
                    pageCode = rowTree.Page.GetFileCode();

                foreach (var row in rowTree.Rows)
                {
                    //check if it doesn't remove children .. I think it does
                    XAttribute rowCodeAtt = row.Attribute("code");
                    string itemCode = (null != rowCodeAtt && null != pageCode) ?
                        string.Format("{0}.{1}", pageCode, rowCodeAtt.Value) : "";

                    //clear cached resources of this item and its related items
                    this.itemsCacheService.Clear(type, rowCodeAtt.Value);

                    ReadWriteTrack itemTrack = new ReadWriteTrack()
                    {
                        Item = item,
                        Type = type,
                        Code = itemCode,
                        Parent = track
                    };

                    XElement xOldRow = row.Element(type.Name);
                    XElement xNewRow = Write(type, newItem, false, itemTrack, filter);
                    if (null != filter && null != filter.Properties)
                    {
                        if (filter.Behavior == UpdateFilterBehavior.Skip)
                        {
                            foreach (var excProp in filter.Properties)
                            {
                                XElement excPropElm = xOldRow.Element(excProp);
                                if (null != excPropElm)
                                    xNewRow.Add(excPropElm);
                                else
                                {
                                    XElement remPropElm = xNewRow.Element(excProp);
                                    if (null != remPropElm)
                                        remPropElm.Remove();
                                }
                            }
                        }
                        else
                        {
                            foreach (var excPropElm in xOldRow.Elements().Where(s => !filter.Properties.Contains(s.Name.LocalName)))
                                xNewRow.Add(excPropElm);
                        }
                    }

                    //update only if there is a difference
                    if (!row.Element(type.Name).ElementEquals(xNewRow))
                    {
                        //inforce cascade delete for complex reference and child items
                        Dictionary<Type, string> delItemsCode = new Dictionary<Type, string>();
                        var cascadeDelProps = props.Where(s => s.Cascade.HasFlag(CascadeOptions.Delete)).Select(s => s.PropertyName);
                        string[] delRefTypes = { "complex", "children" };

                        var rowCollRefNodes = row.Element(type.Name).Elements().Where(delegate(XElement s)
                        {
                            XAttribute refTypeAtt = s.Attribute("refType");
                            XAttribute collTypeAtt = s.Attribute("collType");
                            bool result = null != collTypeAtt && null != refTypeAtt &&
                                (delRefTypes.Contains(refTypeAtt.Value) ||
                                (refTypeAtt.Value == "reference" && cascadeDelProps.Contains(s.Name.LocalName)));
                            return result;
                        }).SelectMany(s => s.Elements());
                        var newCollRefNodes = xNewRow.Elements().Where(delegate(XElement s)
                        {
                            XAttribute refTypeAtt = s.Attribute("refType");
                            XAttribute collTypeAtt = s.Attribute("collType");
                            bool result = null != collTypeAtt && null != refTypeAtt &&
                                (delRefTypes.Contains(refTypeAtt.Value) ||
                                (refTypeAtt.Value == "reference" && cascadeDelProps.Contains(s.Name.LocalName)));
                            return result;
                        }).SelectMany(s => s.Elements());

                        var newCollRefNodesString = newCollRefNodes.Select(s => s.ToString()).ToArray();
                        var delRefNodes = rowCollRefNodes.Where(s => !newCollRefNodesString.Contains(s.ToString()));
                        foreach (var code in delRefNodes)
                        {
                            XElement xDelRef = GetRawData(code.Value);
                            if (xDelRef == null)
                                continue;

                            Type delRefType = this.ioService.GetItemType(code.Value);
                            if (delRefType == null)
                                continue;

                            object delRef = Read(delRefType, xDelRef);
                            //object delRef = Read(delRefType, xDelRef, false);
                            Delete(delRefType, delRef);
                        }

                        row.ReplaceNodes(xNewRow);

                        //Update all reference items, except complex collection!!
                        var propRefProps = props.Where(s =>
                            s.TypeCategory != PropertyTypeCategory.None &&
                            s.ReferenceType != PropertyReferenceType.Complex &&
                            s.ReferenceType != PropertyReferenceType.SelfForeign);
                        foreach (var propRefProp in propRefProps)
                        {
                            object oldRefItem = propRefProp.Property.GetValue(item);
                            object newRefItem = propRefProp.Property.GetValue(newItem);

                            //update parent object if this item is a child and this is property is parent-reference
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
                                    if (baseParentKeys == null)
                                        baseParentKeys = newParentKeyValues.Select(s => s.Key).ToArray();
                                }

                                if (baseParentKeys != null)
                                {
                                    foreach (var parentKey in baseParentKeys)
                                    {
                                        var oldKeyValue = oldParentKeyValues != null ? oldParentKeyValues[parentKey] : null;
                                        var newKeyValue = newParentKeyValues != null ? newParentKeyValues[parentKey] : null;
                                        if (oldKeyValue != newKeyValue)
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

                            if (null == newRefItem)
                                continue;

                            if (oldRefItem == newRefItem || (null != oldRefItem && oldRefItem.Equals(newRefItem)))
                                continue;

                            if (null != oldRefItem && null != newRefItem &&
                                (propRefProp.TypeCategory == PropertyTypeCategory.ValueTypeArray ||
                                propRefProp.TypeCategory == PropertyTypeCategory.ValueTypeCollection))
                            {
                                List<object> oldRefItems = new List<object>();
                                List<object> newRefItems = new List<object>();

                                IEnumerable oldRefEnum = (IEnumerable)oldRefItem;
                                foreach (var itm in oldRefEnum)
                                    oldRefItems.Add(itm);

                                IEnumerable newRefEnum = (IEnumerable)oldRefItem;
                                foreach (var itm in oldRefEnum)
                                    oldRefItems.Add(itm);

                                if (TypeExtentions.ListsEqual<object>(oldRefItems, newRefItems))
                                    continue;
                            }

                            if (null != oldRefItem && propRefProp.TypeCategory == PropertyTypeCategory.ValueTypeCollection)
                            {
                                List<object> oldRefItems = (from r in oldRefItem as IEnumerable<object> select r).ToList();
                                if (null != newRefItem)
                                {
                                    List<object> newRefItems = (from r in newRefItem as IEnumerable<object> select r).ToList();
                                    if (TypeExtentions.ListsEqual<object>(oldRefItems, newRefItems))
                                        continue;
                                }
                            }

                            //reference items
                            if (propRefProp.TypeCategory == PropertyTypeCategory.Class)
                                UpdateItem(propRefProp.PropertyType, oldRefItem, newRefItem, itemTrack);
                            //collection reference items
                            else if (propRefProp.TypeCategory != PropertyTypeCategory.None &&
                                propRefProp.TypeCategory != PropertyTypeCategory.ValueTypeArray &&
                                propRefProp.TypeCategory != PropertyTypeCategory.ValueTypeCollection)
                            {
                                Type childType = null;
                                string collType = string.Empty;
                                if (propRefProp.TypeCategory == PropertyTypeCategory.Array)
                                    childType = propRefProp.PropertyType.GetElementType();
                                else
                                    childType = propRefProp.PropertyType.GetGenericArguments().FirstOrDefault();

                                var children = from child in newRefItem as IEnumerable<object> select child;
                                if (children.Any())
                                    foreach (var child in children)
                                        Update(childType, child);
                            }
                        }
                    }

                    //add more functionality for:
                    //3) testing validation roles like coupled parent
                    this.ioService.Save(rowTree.Page);
                    //rowTree.Page.Save();
                }
            }
            //}
            //catch
            //{
            //    value = false;
            //}

            return value;
        }

        //tags(xml-parsing)
        private XElement RemoveFromParent(Type childType, string childCode, PropertyInfoItem parentChildrenProp, object parent)
        {
            XElement item = null;
            //object oldParent = GetRefItem(propRefProp.PropertyType, oldParentKeyValues);
            if (null != parent)
            {
                XRow oldParentXRow = GetRow(parentChildrenProp.PropertyType, parent);
                if (null != oldParentXRow)
                {
                    //PropertyInfoItem parentChildrenProp = null;
                    var parentChildrenProps = this.propertyService.Properties(parentChildrenProp.PropertyType.FullName).Where(s => s.CollectionItemType == childType && s.ReferenceType == PropertyReferenceType.Children);

                    foreach (var pcp in parentChildrenProps)
                    {
                        var oldParentChildren = oldParentXRow.Row.Element(parentChildrenProp.PropertyType.Name).Element(pcp.PropertyName);
                        var parentItemRef = oldParentChildren.Elements().FirstOrDefault(s => s.Value == childCode);
                        if (null != parentItemRef)
                        {
                            item = parentItemRef;
                            parentItemRef.Remove();

                            //Full attribute should not be updated after turning true,
                            //it's a forward process for indexing purposes

                            //if (oldParentXRow.Page.Size() < PAGE_SIZE)
                            //{
                            //    XAttribute fullAtt = oldParentXRow.Page.Root().Attribute("full");
                            //    if (null != fullAtt)
                            //        fullAtt.Value = "false";
                            //}

                            this.ioService.Save(oldParentXRow.Page);
                            //oldParentXRow.Page.Save();
                            break;
                        }
                    }
                }
            }
            return item;
        }

        //tags(xml-parsing)
        private void TransferToParent(Type childType, XElement child, string hostProp, Type parentType, object parent)
        {
            XRow newParentXRow = GetRow(parentType, parent);
            if (null != newParentXRow)
            {
                PropertyInfoItem parentChildrenProp = null;
                if (hostProp != null)
                    parentChildrenProp = this.propertyService.Properties(parentType.FullName).Where(s => s.ReferenceType == PropertyReferenceType.Children &&
                            s.CollectionItemType == childType && s.PropertyName == hostProp).FirstOrDefault();
                else
                    parentChildrenProp = this.propertyService.Properties(parentType.FullName).Where(s => s.ReferenceType == PropertyReferenceType.Children &&
                            s.CollectionItemType == childType).FirstOrDefault();

                XElement element = newParentXRow.Row.Element(parentType.Name);
                var newParentChildren = element.Element(parentChildrenProp.PropertyName);
                if (newParentChildren == null)
                {
                    newParentChildren = new XElement(
                            parentChildrenProp.PropertyName,
                            new XAttribute("dataType", childType.FullName),
                            new XAttribute("collType",
                                ((parentChildrenProp.TypeCategory == PropertyTypeCategory.Array) ? "array" : "generic")),
                            new XAttribute("refType", "children"));
                    element.Add(newParentChildren);
                }

                newParentChildren.Add(child);
                this.ioService.Save(newParentXRow.Page);
                //newParentXRow.Page.Save();
            }
        }

        private bool RecursionError(ReadWriteTrack track, ReadWriteTrack parent)
        {
            if (parent == null)
                return false;
            else if (track.Code == parent.Code)
                return true;
            else
                return RecursionError(track, parent.Parent);
        }

        private Dictionary<string, object> GetPrimaryValues(Type type, object item)
        {
            if (item == null || type == null)
                return null;

            Dictionary<string, object> values = new Dictionary<string, object>();
            var primaryPros = this.propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).OrderBy(s => s.PropertyName).Select(s => s.Property);
            foreach (var primaryProp in primaryPros)
            {
                if ((primaryProp.PropertyType.GetTypeInfo().IsClass && primaryProp.PropertyType != typeof(string)) ||
                    primaryProp.PropertyType.GetTypeInfo().IsGenericType ||
                    primaryProp.PropertyType.IsArray ||
                    primaryProp.PropertyType == typeof(DateTime))
                    exceptionService.Throw(new PrimaryKeyDataTypeException());

                object value = primaryProp.GetValue(item);
                if (value != null && !value.Equals(ValueHelper.DefaultOf(primaryProp.PropertyType)))
                    values.Add(primaryProp.Name, primaryProp.GetValue(item));
                else
                {
                    values.Clear();
                    break;
                }
            }
            return values;
        }
        private Dictionary<string, object> GetPrimaryNodes(Type type, XElement item)
        {
            if (item == null)
                return null;

            Dictionary<string, object> values = new Dictionary<string, object>();
            var primaryPros = this.propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).OrderBy(s => s.PropertyName);
            foreach (var primaryProp in primaryPros)
            {
                TypeInfo ti = primaryProp.PropertyType.GetTypeInfo();

                if (ti.IsClass ||
                    ti.IsGenericType ||
                    primaryProp.PropertyType.IsArray ||
                    primaryProp.PropertyType == typeof(DateTime))
                    exceptionService.Throw(new PrimaryKeyDataTypeException());

                XElement propElm = item.Element(type.Name).Element(primaryProp.PropertyName);
                if (null == propElm)
                {
                    values.Clear();
                    break;
                }

                string stringValue = propElm.Value;
                object value = ReadValue(primaryProp, stringValue);
                if (value != null && !value.Equals(ValueHelper.DefaultOf(primaryProp.PropertyType)))
                    values.Add(primaryProp.PropertyName, value);
                else
                {
                    values.Clear();
                    break;
                }
            }
            return values;
        }

        //private void ReservedKeyTest(Type type, object item, string[] uniqueKeys = null)
        //{
        //    Dictionary<string, object> keyValues = GetPrimaryValues(type, item);
        //    Dictionary<string, object> uniqueValues = ValueHelper.GetPropertyValues(type, item, uniqueKeys);
        //    if (!keyValues.Any() && !uniqueValues.Any())
        //        return;

        //    var items = QueryFirst(type, delegate(dynamic s)
        //    {
        //        foreach (var keyValue in keyValues)
        //        {
        //            PropertyInfo primaryProp = type.GetProperty(keyValue.Key);
        //            if (keyValue.Value.Equals(primaryProp.GetValue(s)))
        //                exceptionService.Throw(new ReservedPrimaryKeyException());
        //        }

        //        foreach (var uniqueValue in uniqueValues)
        //        {
        //            PropertyInfo primaryProp = type.GetProperty(uniqueValue.Key);
        //            if (uniqueValue.Value.Equals(primaryProp.GetValue(s)))
        //                exceptionService.Throw(new ReservedUniqueKeyException());
        //        }

        //        return false;
        //    }, true);
        //}
        //private void ReservedKeyTest(Type type, object item, IEnumerable<string> primaryKeys, IEnumerable<string> uniqueKeys)
        //{
        //    Dictionary<string, object> primaryValues = ValueHelper.GetPropertyValues(type, item, primaryKeys);
        //    Dictionary<string, object> uniqueValues = ValueHelper.GetPropertyValues(type, item, uniqueKeys);
        //    if (!primaryValues.Any() && !uniqueValues.Any())
        //        return;

        //    var items = FirstMatch(type, delegate(dynamic s)
        //    {
        //        foreach (var uniqueValue in uniqueValues)
        //        {
        //            PropertyInfo primaryProp = type.GetProperty(uniqueValue.Key);
        //            if (uniqueValue.Value.Equals(primaryProp.GetValue(s)))
        //                exceptionService.Throw(new ReservedUniqueKeyException());
        //        }

        //        foreach (var primaryValue in primaryValues)
        //        {
        //            PropertyInfo primaryProp = type.GetProperty(primaryValue.Key);
        //            if (primaryValue.Value.Equals(primaryProp.GetValue(s)))
        //                exceptionService.Throw(new ReservedPrimaryKeyException());
        //        }

        //        return false;
        //    }, true);
        //}

        private void ParseChildren(Type type, object item, Type childType, IEnumerable<object> children, PropertyInfoItem prop, XElement xProp, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            if (prop.ReferenceType == PropertyReferenceType.Children)
            {
                PropertyInfoItem childParentPropItem = null;
                if (!string.IsNullOrEmpty(prop.ChildParentProperty))
                    childParentPropItem = this.propertyService.Properties(childType.FullName).FirstOrDefault(s => s.PropertyName == prop.ChildParentProperty);
                else
                    childParentPropItem = this.propertyService.Properties(childType.FullName).FirstOrDefault(s => s.PropertyType == type && s.ReferenceType == PropertyReferenceType.Parent);

                if (null != childParentPropItem)
                {
                    foreach (var child in children)
                    {
                        //object childParentPropValue = childParentProp.GetValue(child);
                        //if (null != childParentPropValue && !childParentPropValue.Equals())
                        //    exceptionService.Throw(new ReservedChildException());

                        if (null != childParentPropItem.ParentKeys)
                        {
                            foreach (var parentKeyPropAtt in childParentPropItem.ParentKeys)
                            {
                                object parentKeyValue = null;
                                PropertyInfoItem parentKeyProp = this.propertyService.Properties(type.FullName).FirstOrDefault(s => s.PropertyName == parentKeyPropAtt.RemoteProperty);
                                if (null != parentKeyProp)
                                    parentKeyValue = parentKeyProp.Property.GetValue(item);

                                if (null != parentKeyValue)
                                {
                                    PropertyInfoItem childKeyProp = this.propertyService.Properties(type.FullName).FirstOrDefault(s => s.PropertyName == parentKeyPropAtt.LocalProperty);
                                    if (null != childKeyProp)
                                    {
                                        object childKeyValue = childKeyProp.Property.GetValue(child);
                                        if (null != childKeyValue && !childKeyValue.Equals(ValueHelper.DefaultOf(childKeyProp.PropertyType)))
                                        {
                                            PropertyInfoItem keyProp = this.propertyService.Properties(type.FullName).FirstOrDefault(s => s.PropertyName == parentKeyPropAtt.RemoteProperty);
                                            if (null != keyProp && !childKeyValue.Equals(keyProp.Property.GetValue(item)))
                                                exceptionService.Throw(new ReservedChildException());
                                        }
                                        childKeyProp.Property.SetValue(child, parentKeyValue);
                                    }
                                }
                            }
                        }

                        string refCode = SetChildItem(childType, child, prop, GetPrimaryValues(childType, child), lazyLoad, writeTrack);
                        if (null != refCode)
                        {
                            XElement xpe = new XElement(childType.Name, refCode);
                            var itmKeySet = GetPrimaryValues(childType, child);
                            foreach (var itmKey in itmKeySet)
                                xpe.Add(new XAttribute(itmKey.Key, itmKey.Value));
                            xProp.Add(xpe);
                        }
                    }
                    xProp.Add(new XAttribute("refType", "children"));
                }
                else
                {
                    exceptionService.Throw(new MissingParentKeyException());
                    //Rollback();
                }
            }
            else
            {
                bool isRefChild = this.propertyService.Properties(childType.FullName).Any(s => s.IsPrimaryKey);
                if (isRefChild)
                    xProp.Add(new XAttribute("refType", "reference"));
                else
                    xProp.Add(new XAttribute("refType", "complex"));

                foreach (var itm in children)
                {
                    string refCode = Create(childType, itm);
                    if (refCode != null)
                    {
                        XElement xpe = new XElement(childType.Name, refCode);
                        if (isRefChild)
                        {
                            var itmKeySet = GetPrimaryValues(childType, itm);
                            foreach (var itmKey in itmKeySet)
                                xpe.Add(new XAttribute(itmKey.Key, itmKey.Value));
                        }
                        xProp.Add(xpe);
                    }
                }
            }
        }
        private string SetChildItem(Type type, object item, PropertyInfoItem prop, Dictionary<string, object> itemPrimarySets, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            object refItem = null;
            if (null != itemPrimarySets && itemPrimarySets.Any())
            {
                refItem = Query(type, delegate(object s)
                {
                    var childTypePorps = propertyService.Properties(type.FullName);
                    foreach (var itemPrimarySet in itemPrimarySets)
                    {
                        object sValue = null;
                        object itemPrimaryKeyValue = null;
                        if (!itemPrimarySets.TryGetValue(itemPrimarySet.Key, out itemPrimaryKeyValue))
                            return false;

                        var primaryKeyProp = childTypePorps.FirstOrDefault(p => p.PropertyName.Equals(itemPrimarySet.Key)).Property;
                        //PropertyInfo primaryKeyProp = type.GetProperty(itemPrimarySet.Key);
                        sValue = primaryKeyProp.GetValue(s);
                        if (sValue == null || !sValue.Equals(itemPrimaryKeyValue))
                            return false;
                    }
                    return true;
                }).FirstOrDefault();
            }
            string refCode = null;
            XRow xRow = null;
            if (null != refItem)
            {
                xRow = GetRow(type, refItem);
                if (null != xRow)
                    refCode = string.Format("{0}.{1}",
                        System.IO.Path.GetFileNameWithoutExtension(xRow.Page.Path),
                        xRow.Row.Attribute("code").Value);
            }
            else
            {
                writeTrack.RootProperty = prop;
                refCode = Create(type, item, lazyLoad, writeTrack);
            }

            return refCode;
        }

        /// <summary>
        /// Parses reference type properties by writing the XML represenation to the parent object and binding related objects
        /// </summary>
        /// <param name="type"></param>
        /// <param name="item"></param>
        /// <param name="value"></param>
        /// <param name="prop"></param>
        /// <param name="xItem"></param>
        /// <param name="writeTrack"></param>
        private void ParseReference(Type type, object item, object value, PropertyInfoItem prop, XElement xItem, ReadWriteTrack writeTrack)
        {
            if (value != null && prop.ReferenceType == PropertyReferenceType.Foreign && prop.ForeignKeys != null)
            {
                Dictionary<string, object> foreignRefKeySets = ValueHelper.GetPropertyValues(prop.PropertyType, value, prop.ForeignKeys.Select(s => s.RemoteProperty));
                if (foreignRefKeySets.Any())
                    MapToReference(type, item, value, prop, xItem, foreignRefKeySets);
                else
                    ParseComplex(type, item, value, prop, xItem, prop.ForeignKeys, writeTrack);
            }
            else if (value != null && prop.ReferenceType != PropertyReferenceType.Parent)
            {
                Dictionary<string, object> refKeySets = GetPrimaryValues(prop.PropertyType, value);
                if (refKeySets.Any())
                    MapToReference(type, item, value, prop, xItem, refKeySets);
                else
                {
                    string refCode = Create(prop.PropertyType, value);
                    if (!string.IsNullOrEmpty(refCode))
                        xItem.Add(new XElement(prop.PropertyName, new XAttribute("refType", "complex"), refCode));
                }
            }
            //else if (prop.ReferenceType == PropertyReferenceType.Foreign && null != prop.ForeignKeys)
            else if ((prop.ReferenceType == PropertyReferenceType.Foreign || prop.ReferenceType == PropertyReferenceType.SelfForeign) && prop.ForeignKeys != null)
            {
                Dictionary<string, object> foreignRefKeySets = ValueHelper.GetPropertyValues(type, item, prop.ForeignKeys.ToDictionary(s => s.LocalProperty, s => s.RemoteProperty));
                value = SelectItemsByExample(prop.PropertyType, PopulateItemProperties(prop.PropertyType, foreignRefKeySets)).FirstOrDefault();
                //value = GetReferenceByProperties(prop.PropertyType, foreignRefKeySets);
                ReferenceMap(type, prop.PropertyType, value, prop, xItem, foreignRefKeySets);
            }
            ////maybe: this is for object that have ForeignKey attribute for each other
            //if (null != writeTrack && null != writeTrack.Parent)
            //{
            //    XElement xpe = new XElement(prop.Name, new XAttribute("refType", "reference"), writeTrack.Parent.CodePath);
            //    foreach (var foreignKeyAtt in foreignKeyAtts)
            //    {
            //        PropertyInfo localProp = type.GetProperty(foreignKeyAtt.LocalProperty);
            //        PropertyInfo remoteProp = propType.GetProperty(foreignKeyAtt.RemoteProperty);
            //        if (null != remoteProp && null != localProp)
            //        {
            //            object remoteValue = remoteProp.GetValue(writeTrack.Parent.Item);
            //            if (null != remoteValue && !remoteValue.Equals(ValueHelper.DefaultOf(remoteProp.PropertyType)))
            //            {
            //                localProp.SetValue(item, remoteValue);
            //                xpe.Add(new XAttribute(remoteProp.Name, remoteValue));
            //                var xLocalValue = xItem.Descendants(localProp.Name).FirstOrDefault();
            //                if (null != xLocalValue)
            //                    xLocalValue.Value = remoteValue.ToString();
            //                else
            //                    xItem.Add(new XElement(localProp.Name, remoteValue.ToString()));
            //            }
            //        }
            //    }
            //    xItem.Add(xpe);
            //}
            else if (prop.ReferenceType == PropertyReferenceType.Parent && prop.ParentKeys != null && writeTrack != null)
            {
                string relationshipName = null;
                if (writeTrack.Parent != null && writeTrack.Parent.RootProperty != null)
                    relationshipName = writeTrack.Parent.RootProperty.PropertyName;

                //child has been assigned to parent
                if (writeTrack.Parent != null && prop.PropertyType == writeTrack.Parent.Type)
                {
                    XElement xpe = new XElement(prop.PropertyName,
                            new XAttribute("refType", "parent"),
                            writeTrack.Parent.Code);

                    if (relationshipName != null)
                        xpe.Add(new XAttribute("hostProp", relationshipName));

                    foreach (var parentKeyAtt in prop.ParentKeys)
                    {
                        PropertyInfo localProp = type.GetProperty(parentKeyAtt.LocalProperty);
                        PropertyInfo remoteProp = prop.PropertyType.GetProperty(parentKeyAtt.RemoteProperty);
                        if (null != remoteProp && null != localProp)
                        {
                            object remoteValue = remoteProp.GetValue(writeTrack.Parent.Item);
                            if (null != remoteValue && !remoteValue.Equals(ValueHelper.DefaultOf(remoteProp.PropertyType)))
                            {
                                localProp.SetValue(item, remoteValue);
                                xpe.Add(new XAttribute(remoteProp.Name, remoteValue));
                                var xLocalValue = xItem.Descendants(localProp.Name).FirstOrDefault();
                                if (null != xLocalValue)
                                    xLocalValue.Value = remoteValue.ToString();
                                else
                                    xItem.Add(new XElement(localProp.Name, remoteValue.ToString()));
                            }
                        }
                    }
                    xItem.Add(xpe);
                }
                //parent has been assign to the child
                else
                {
                    //parent has been assign to the child through parentkey property value
                    //if (value == null)
                    //{
                    //    Dictionary<string, object> parentRefKeySets = ValueHelper.GetPropertyValues(type, item, prop.ParentKeys.ToDictionary(s => s.LocalProperty, s => s.RemoteProperty));
                    //    value = GetRefItem(prop.PropertyType, parentRefKeySets);
                    //    LinkRef(type, prop.PropertyType, value, prop, xItem, parentRefKeySets);
                    //}

                    if (writeTrack.Parent == null || prop.PropertyType != writeTrack.Parent.Type) //WHY?!!
                    {
                        object childParentItem = prop.Property.GetValue(item);
                        var localToRemoteParentProps = prop.ParentKeys.ToDictionary(s => s.LocalProperty, s => s.RemoteProperty);
                        var remoteParentValues = ValueHelper.GetPropertyValues(type, item, localToRemoteParentProps);

                        if (childParentItem == null && remoteParentValues.Any())
                        {
                            childParentItem = SelectItemsByExample(prop.PropertyType, PopulateItemProperties(prop.PropertyType, remoteParentValues)).FirstOrDefault();
                            //childParentItem = GetReferenceByProperties(prop.PropertyType, remoteParentValues);
                            if (childParentItem != null)
                                prop.Property.SetValue(item, childParentItem);
                        }
                        else if (childParentItem != null && !remoteParentValues.Any())
                        {
                            //find the mapping values from remote props and assign it to local props
                            var remoteParentProps = this.propertyService.Properties(prop.PropertyType.FullName);
                            var localParentProps = this.propertyService.Properties(type.FullName);

                            foreach (var localToRemoteParentProp in localToRemoteParentProps)
                            {
                                var remoteParentKeyProp = remoteParentProps.FirstOrDefault(s => s.PropertyName == localToRemoteParentProp.Value);
                                var localParentKeyProp = localParentProps.FirstOrDefault(s => s.PropertyName == localToRemoteParentProp.Key);
                                if (remoteParentKeyProp != null && localParentKeyProp != null)
                                {
                                    var remoteParentKeyValue = remoteParentKeyProp.Property.GetValue(childParentItem);
                                    var localParentKeyPropObj = type.GetProperty(localToRemoteParentProp.Key);
                                    localParentKeyPropObj.SetValue(item, remoteParentKeyValue);

                                    WriteProperty(type, item, xItem, localParentKeyProp, true);
                                }
                            }
                        }

                        XRow xParentRow = GetRow(prop.PropertyType, childParentItem);
                        if (null != xParentRow)
                        {
                            var parentChildrenProps = this.propertyService.Properties(prop.PropertyType.FullName).Where(s =>
                                    s.ReferenceType == PropertyReferenceType.Children &&
                                    s.CollectionItemType == type);

                            PropertyInfoItem childrenProp = null;
                            var itemPrimaryKeyValues = GetPrimaryValues(type, item);
                            var childrenPropsLen = parentChildrenProps.Count();
                            if (childrenPropsLen > 1)
                            {
                                foreach (var parentChildrenProp in parentChildrenProps)
                                {
                                    var parentChildrenValue = (IEnumerable<object>)parentChildrenProp.Property.GetValue(childParentItem);
                                    if (parentChildrenValue != null)
                                    {
                                        var exist = parentChildrenValue.Where((object s) =>
                                        {
                                            var sPrimaryKeyValues = GetPrimaryValues(type, s);
                                            return itemPrimaryKeyValues.DictionaryEqual(sPrimaryKeyValues);
                                        }).Any();

                                        if (exist)
                                        {
                                            childrenProp = parentChildrenProp;
                                            break;
                                        }
                                    }
                                }
                            }
                            else
                                childrenProp = parentChildrenProps.FirstOrDefault();

                            //get childrenpros and find the item of writeTrack.Item and identify the right prop to move to
                            writeTrack.Parent = new ReadWriteTrack()
                            {
                                Code = string.Format("{0}.{1}",
                                    xParentRow.Page.GetFileCode(),
                                    xParentRow.Row.Attribute("code").Value),
                                Item = childParentItem,
                                Type = prop.PropertyType
                            };

                            //update parent with this child element
                            XElement xParent = xParentRow.Row.Element(prop.PropertyType.Name);
                            var childPrimProps = GetPrimaryValues(type, item);

                            bool parentChanged = false;

                            //if there is no RelationshipAttribute it means
                            //only one children property of the target type

                            //var childrenProp = this.propertyService.PropertyItems.FirstOrDefault(s =>
                            //    s.Type == prop.PropertyType &&
                            //    s.ReferenceType == PropertyReferenceType.Children &&
                            //    s.CollectionItemType == type &&
                            //    ((!string.IsNullOrEmpty(prop.RelationshipName) && s.RelationshipName == prop.RelationshipName) || true));

                            XElement childrenElement = xParent.Element(childrenProp.PropertyName);
                            if (null == childrenElement)
                            {
                                childrenElement = new XElement(
                                    childrenProp.PropertyName,
                                    new XAttribute("dataType", type.FullName),
                                    new XAttribute("collType",
                                        ((childrenProp.TypeCategory == PropertyTypeCategory.Array) ? "array" : "generic")),
                                    new XAttribute("refType", "children"));
                                xParent.Add(childrenElement);
                            }

                            //add this child to parent
                            if (null != childrenProp)
                            {
                                //find if this child reference already exists in parent
                                var xChild = childrenElement.Elements().FirstOrDefault(delegate(XElement s)
                                {
                                    foreach (var childPrimProp in childPrimProps)
                                    {
                                        var xAtt = s.Attribute(childPrimProp.Key);
                                        if (null == xAtt || xAtt.Value != childPrimProp.Value.ToString())
                                            return false;
                                    }
                                    return true;
                                });

                                //create a child reference in parent if it's not exists
                                if (null == xChild)
                                {
                                    xChild = new XElement(type.Name, writeTrack.Code);
                                    foreach (var childPrimProp in childPrimProps)
                                        xChild.Add(new XAttribute(childPrimProp.Key, childPrimProp.Value));
                                    childrenElement.Add(xChild);
                                    parentChanged = true;
                                }
                            }

                            if (parentChanged)
                                this.ioService.Save(xParentRow.Page);
                                //xParentRow.Page.Save();
                        }
                    }

                    if (writeTrack.Parent != null && prop.PropertyType == writeTrack.Parent.Type)
                    {
                        XElement xpe = new XElement(prop.PropertyName,
                                new XAttribute("refType", "parent"),
                                writeTrack.Parent.Code);

                        if (relationshipName != null)
                            xpe.Add(new XAttribute("hostProp", relationshipName));

                        foreach (var parentKeyAtt in prop.ParentKeys)
                        {
                            PropertyInfo localProp = type.GetProperty(parentKeyAtt.LocalProperty);
                            PropertyInfo remoteProp = prop.PropertyType.GetProperty(parentKeyAtt.RemoteProperty);
                            if (null != remoteProp && null != localProp)
                            {
                                object localValue = localProp.GetValue(item);
                                object remoteValue = remoteProp.GetValue(writeTrack.Parent.Item);

                                //check it later
                                if (null == localValue || localValue.Equals(ValueHelper.DefaultOf(localProp.PropertyType)))
                                    break;

                                if (localValue.Equals(remoteValue))
                                    xpe.Add(new XAttribute(remoteProp.Name, remoteValue));
                                else
                                    exceptionService.Throw(new ReservedChildException());
                            }
                        }
                        xItem.Add(xpe);
                    }
                }
            }
        }

        /// <summary>
        /// Writes the XML representation of a complex object property to the parent object
        /// </summary>
        /// <param name="type"></param>
        /// <param name="item"></param>
        /// <param name="value"></param>
        /// <param name="prop"></param>
        /// <param name="xItem"></param>
        /// <param name="foreignKeyAtts"></param>
        /// <param name="writeTrack"></param>
        private void ParseComplex(Type type, object item, object value, PropertyInfoItem prop, XElement xItem, IEnumerable<ForeignKeyAttribute> foreignKeyAtts, ReadWriteTrack writeTrack = null)
        {
            Dictionary<string, object> foreignKeyRefValues = ValueHelper.GetPropertyValues(prop.PropertyType, value, foreignKeyAtts.Select(s => s.RemoteProperty));
            XElement xProp = WriteItem(prop.PropertyName, prop.PropertyType, value, foreignKeyRefValues, writeTrack);
            if (null != xProp)
                xItem.Add(xProp);

            WritePropertyMap(type, item, prop.PropertyType, value, xItem, foreignKeyAtts.ToDictionary(s => s.RemoteProperty, s => s.LocalProperty));
        }

        /// <summary>
        /// Search for the existance of the given reference property value and them map it to the parent object
        /// </summary>
        /// <param name="type"></param>
        /// <param name="item"></param>
        /// <param name="value"></param>
        /// <param name="prop"></param>
        /// <param name="xItem"></param>
        /// <param name="refKeySets"></param>
        private void MapToReference(Type type, object item, object value, PropertyInfoItem prop, XElement xItem, Dictionary<string, object> refKeySets)
        {
            object reference = null;
            if (item != null)
            {
                reference = SelectItemsByExample(prop.PropertyType, PopulateItemProperties(prop.PropertyType, refKeySets)).FirstOrDefault();
                
                //if the parent object has no value set for this reference proptry set the value according to the reference search result
                if (reference != null && prop.Property.GetValue(item).Equals(null))
                    prop.Property.SetValue(item, reference);
            }

            if (reference != null)
            {
                Dictionary<string, string> mappingProps = ReferenceMap(type, prop.PropertyType, reference, prop, xItem, refKeySets);
                if (mappingProps != null)
                    WritePropertyMap(type, item, prop.PropertyType, reference, xItem, mappingProps);
            }
        }
        private Dictionary<string, string> ReferenceMap(Type type, Type propType, object propItem, PropertyInfoItem prop, XElement xItem, Dictionary<string, object> refKeySets)
        {
            Dictionary<string, string> mappingProps = null;
            XRow xRow = GetRow(propType, propItem);
            if (null != xRow)
            {
                string refCode = string.Format("{0}.{1}",
                    System.IO.Path.GetFileNameWithoutExtension(xRow.Page.Path),
                    xRow.Row.Attribute("code").Value);

                var xProp = xItem.Descendants(prop.PropertyName).FirstOrDefault();
                if (null != xProp)
                    xProp.Value = refCode;
                else
                {
                    xProp = new XElement(prop.PropertyName, new XAttribute("refType", "reference"), refCode);
                    xItem.Add(xProp);
                }

                foreach (var refKeySet in refKeySets)
                {
                    XAttribute xProAtt = xProp.Attribute(refKeySet.Key);
                    if (null != xProAtt)
                        xProAtt.Value = refKeySet.Value.ToString();
                    else
                        xProp.Add(new XAttribute(refKeySet.Key, refKeySet.Value));
                }

                if (prop.ReferenceType == PropertyReferenceType.Parent && null != prop.ParentKeys)
                    mappingProps = prop.ParentKeys.ToDictionary(s => s.RemoteProperty, s => s.LocalProperty);
                if (prop.ReferenceType == PropertyReferenceType.Foreign && null != prop.ForeignKeys)
                    mappingProps = prop.ForeignKeys.ToDictionary(s => s.RemoteProperty, s => s.LocalProperty);
            }
            return mappingProps;
        }

        private bool ReservedKeyTest(Type type, object item)
        {
            //var anyReservedKey = Query(type, FilterItemKeys(type, item), true).Any();

            List<object> filterItems = new List<object>();
            var keys = this.propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey || s.IsUnique);
            foreach (var key in keys)
            {
                object filterItem = Activator.CreateInstance(type);
                var value = key.Property.GetValue(item);
                key.Property.SetValue(filterItem, value);
                filterItems.Add(filterItem);
            }

            return SelectItemsByExamples(type, filterItems.ToArray()).Any();
        }
        private object FilterItemProperties(Type type, object item, IEnumerable<string> filter)
        {
            object filterItem = Activator.CreateInstance(type);
            var props = this.propertyService.Properties(type.FullName);
            foreach (var prop in props)
            {
                if (filter.Contains(prop.PropertyName))
                {
                    var value = prop.Property.GetValue(item);
                    prop.Property.SetValue(filterItem, value);
                }
                else
                    prop.Property.SetValue(filterItem, null);
            }

            return filterItem;
        }
        private object FilterItemBaseProperties(Type type, object item)
        {
            object filterItem = Activator.CreateInstance(type);
            var props = this.propertyService.Properties(type.FullName);
            foreach (var prop in props)
            {
                if (prop.TypeCategory == PropertyTypeCategory.None ||
                    prop.TypeCategory == PropertyTypeCategory.ValueTypeArray ||
                    prop.TypeCategory == PropertyTypeCategory.ValueTypeCollection)
                {
                    var value = prop.Property.GetValue(item);
                    prop.Property.SetValue(filterItem, value);
                }
                else
                    prop.Property.SetValue(filterItem, null);
            }

            return filterItem;
        }
        private object FilterItemPrimaryProperties(Type type, object item)
        {
            object filterItem = Activator.CreateInstance(type);
            var props = this.propertyService.Properties(type.FullName);
            foreach (var prop in props)
            {
                if (prop.IsPrimaryKey)
                {
                    var value = prop.Property.GetValue(item);
                    prop.Property.SetValue(filterItem, value);
                }
                else
                    prop.Property.SetValue(filterItem, null);
            }

            return filterItem;
        }
        private object PopulateItemProperties(Type type, Dictionary<string, object> values)
        {
            object item = Activator.CreateInstance(type);

            if (values == null || !values.Any())
                return null;

            var props = this.propertyService.Properties(type.FullName);
            foreach (var prop in props)
            {
                if(values.Keys.Contains(prop.PropertyName))
                    prop.Property.SetValue(item, values[prop.PropertyName]);
                else
                    prop.Property.SetValue(item, null);
            }

            return item;
        }

        private void WritePropertyMap(Type type, object item, Type propType, object value, XElement xItem, Dictionary<string, string> relationshipProps)
        {
            foreach (var relationshipProp in relationshipProps)
            {
                PropertyInfo remoteProp = propType.GetProperty(relationshipProp.Key);
                if (null != remoteProp)
                {
                    //get primary-key value
                    object remoteKeyValue = remoteProp.GetValue(value);
                    PropertyInfo localProp = type.GetProperty(relationshipProp.Value);
                    if (null != remoteKeyValue && null != localProp)
                    {
                        object localKeyValue = localProp.GetValue(item);
                        if (remoteKeyValue != ValueHelper.DefaultOf(localProp.PropertyType) && !remoteKeyValue.Equals(localKeyValue))
                        {
                            localProp.SetValue(item, remoteKeyValue);
                            var xLocalValue = xItem.Descendants(localProp.Name).FirstOrDefault();
                            if (null != xLocalValue)
                                xLocalValue.Value = remoteKeyValue.ToString();
                            else
                                xItem.Add(new XElement(localProp.Name, remoteKeyValue.ToString()));
                        }
                    }
                }
            }
        }
        private XElement WriteItem(string name, Type type, object item, Dictionary<string, object> refKeySets, ReadWriteTrack writeTrack = null)
        {
            var refKeyProps = propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey);

            string refCode = null;
            object refItem = null;
            if (refKeySets != null)
            {
                refItem = SelectItemsByExample(type, PopulateItemProperties(type, refKeySets)).FirstOrDefault();
                //refItem = GetReferenceByProperties(type, refKeySets);
                XRow xRow = null;
                if (refItem != null)
                {
                    xRow = GetRow(type, refItem);
                    if (null != xRow)
                        refCode = string.Format("{0}.{1}",
                            System.IO.Path.GetFileNameWithoutExtension(xRow.Page.Path),
                            xRow.Row.Attribute("code").Value);
                }
                else
                    refCode = Create(type, item, false, writeTrack);
            }

            Dictionary<string, object> keyValues = GetPrimaryValues(type, item);
            XElement xpe = new XElement(name, new XAttribute("refType", "reference"), refCode);
            if (!string.IsNullOrEmpty(refCode))
                foreach (var keyValue in keyValues)
                    xpe.Add(new XAttribute(keyValue.Key, keyValue.Value));

            //foreach (var foreignKeyAtt in foreignKeyAtts)
            //{
            //    PropertyInfo remoteProp = prop.PropertyType.GetProperty(foreignKeyAtt.RemoteProperty);
            //    if (null != remoteProp)
            //    {
            //        //get primary-key value
            //        object remoteKeyValue = remoteProp.GetValue(value);
            //        PropertyInfo localProp = type.GetProperty(foreignKeyAtt.LocalProperty);
            //        if (null != remoteKeyValue && null != localProp)
            //        {
            //            object localKeyValue = localProp.GetValue(item);
            //            if (remoteKeyValue != ValueHelper.DefaultOf(localProp.PropertyType) && !remoteKeyValue.Equals(localKeyValue))
            //            {
            //                localProp.SetValue(item, remoteKeyValue);
            //                var xLocalValue = xItem.Descendants(localProp.Name).FirstOrDefault();
            //                if (null != xLocalValue)
            //                    xLocalValue.Value = remoteKeyValue.ToString();
            //                else
            //                    xItem.Add(new XElement(localProp.Name, remoteKeyValue.ToString()));
            //            }
            //        }
            //    }
            //}

            return xpe;
        }

        //private bool TestAgainstXElement(Type type, object item, XElement xItem, IEnumerable<PropertyInfoItem> primaryProps)
        //{
        //    foreach (var prop in primaryProps)
        //    {
        //        object pkv = prop.Property.GetValue(item);
        //        if (null != pkv)
        //        {
        //            XElement elm = XElement.Parse(BaseMarkup(xItem.Element(type.Name)));
        //            string pkvString = pkv.ToString();
        //            if (prop.ValuePosition != ValuePosition.Body)
        //            {
        //                XAttribute valueXAtt = elm.Attribute(prop.PropertyName);
        //                if (null == valueXAtt && pkvString != valueXAtt.Value)
        //                    return false;
        //            }
        //            else
        //            {
        //                XElement valueXElm = elm.Element(prop.PropertyName);
        //                if (null == valueXElm || (prop.ValuePosition == ValuePosition.Body && pkvString != valueXElm.Value))
        //                    return false;
        //            }
        //        }
        //    }
        //    return true;
        //}
        //private string BaseMarkup(XElement element)
        //{
        //    if (element == null)
        //        return string.Empty;

        //    var baseElements = element.Elements().Where(delegate(XElement s)
        //    {
        //        if (null != s.Attribute("refType"))
        //            return false;

        //        XAttribute collTypeAtt = s.Attribute("collType");
        //        if (null != collTypeAtt)
        //        {
        //            XAttribute dataTypeAtt = s.Attribute("dataType");
        //            var dataType = this.propertyService.RegisteredTypeByName(dataTypeAtt.Value);
        //            var propertyTypeCategory = this.propertyService.GetPropertyTypeCategory(dataType);
        //            return null != dataTypeAtt && (
        //                propertyTypeCategory.HasFlag(PropertyTypeCategory.ValueTypeArray) ||
        //                propertyTypeCategory.HasFlag(PropertyTypeCategory.ValueTypeCollection));
        //        }

        //        return true;
        //    });

        //    XElement resultElm = new XElement(element.Name, element.Attributes());
        //    foreach (var e in baseElements)
        //        resultElm.Add(e);

        //    return resultElm.ToString();
        //}
        //private XElement OrderElement(XElement xElement)
        //{
        //    XElement result = new XElement(xElement.Name, xElement.Attributes());
        //    var ordered = xElement.Elements().OrderBy(s => s.Name.LocalName);
        //    foreach (var elm in ordered)
        //    {
        //        if (elm.HasElements)
        //            result.Add(OrderElement(elm));
        //        else
        //            result.Add(elm);
        //    }
        //    return result;
        //}

        //private object GetReferenceByProperties(Type type, Dictionary<string, object> keyPropSets, bool lazyLoad = false)
        //{
        //    return SelectItemsByExample(type, PopulateItemProperties(type, keyPropSets), lazyLoad).FirstOrDefault();
        //}
        //private object GetRefItem(Type type, Dictionary<string, object> keyPropSets, bool lazyLoad = false)
        //{
        //    if (type == null || keyPropSets == null || !keyPropSets.Any())
        //        return null;

        //    object refItem = Query(type, delegate(dynamic s)
        //    {
        //        foreach (var refKeySet in keyPropSets)
        //        {
        //            object sValue = null;
        //            PropertyInfo p = type.GetProperty(refKeySet.Key);
        //            if (null != p)
        //            {
        //                sValue = p.GetValue(s);
        //                if (sValue == null || !sValue.Equals(refKeySet.Value))
        //                    return false;
        //            }
        //            else
        //                return false;
        //        }
        //        return true;
        //    }, lazyLoad).FirstOrDefault();

        //    return refItem;
        //}

        ////returns Type by .xpag files reference code path
        //private Type GetItemType(string referenceCode)
        //{
        //    string[] codeParts = referenceCode.Split('.');
        //    if (codeParts.Length == 2)
        //        return GetPageType(codeParts[0]);

        //    return null;
        //    //string[] files = System.IO.Directory.GetFiles(root, "*.xtab");
        //    //foreach (var file in files)
        //    //{
        //    //    XFile tableFile = this.ioService.OpenOrCreate(file);
        //    //    if (tableFile.Pages()
        //    //        .Where(s => s.Attribute("file").Value == codeParts[0] + ".xpag").Any())
        //    //    {
        //    //        string typeString = System.IO.Path.GetFileNameWithoutExtension(file);
        //    //        return this.propertyService.RegisteredType(typeString);
        //    //    }
        //    //}
        //    //return null;
        //}
        ////returns the Type by .xpag file code
        //private Type GetPageType(string xpagCode)
        //{
        //    string[] files = System.IO.Directory.GetFiles(root, "*.xtab");
        //    foreach (var file in files)
        //    {
        //        XFile tableFile = this.ioService.OpenOrCreate(file);
        //        if (tableFile != null && tableFile.Pages().Where(s => s.Attribute("file").Value == xpagCode + ".xpag").Any())
        //        {
        //            string typeString = System.IO.Path.GetFileNameWithoutExtension(file);
        //            return this.propertyService.RegisteredType(typeString);
        //        }
        //    }
        //    return null;
        //}
        //private string GetItemPage(Type type, string code)
        //{
        //    XFile tableFile = this.ioService.OpenOrCreate(type.FullName + ".xtab");
        //    foreach (var file in tableFile.Pages().Select(s => s.Attribute("file").Value))
        //    {
        //        XFile pageFile = this.ioService.OpenOrCreate(file);
        //        if (pageFile.Rows().Where(delegate(XElement s)
        //        {
        //            XAttribute sa = s.Attribute("code");
        //            return null != sa && sa.Value == code;
        //        }).Any())
        //        {
        //            return file.Replace(".xpag", "");
        //        }
        //    }
        //    return null;
        //}



        //find XML representation of an item
        //private XRow GetRow(Type type, object item)
        //{
        //    if (null == type || item == null)
        //        return null;

        //    XRow result = null;
        //    string docFileName = string.Format("{0}\\{1}.xtab", root, type.ToString());
        //    XFile tableFile = this.ioService.OpenOrCreate(docFileName, type, true);

        //    if (tableFile != null)
        //    {
        //        var props = this.propertyService.Properties(type.FullName);
        //        string itemContents = string.Empty;
        //        Dictionary<string, object> itemKeys = null;
        //        if (props.Where(s => s.IsPrimaryKey).Any())
        //            itemKeys = GetPrimaryValues(type, item);
        //        else
        //            itemContents = BaseMarkup(OrderElement(Write(type, item, true)));

        //        var pages = tableFile.Pages().Select(s => this.ioService.OpenOrCreate(s.Attribute("file").Value, typeof(TablePage)));
        //        foreach (var page in pages)
        //        {
        //            XElement treeRow = null;
        //            if (null != itemKeys)
        //                treeRow = page.Rows().FirstOrDefault(delegate(XElement s)
        //                {
        //                    return itemKeys.DictionaryEqual<string, object>(GetPrimaryNodes(type, s));
        //                });
        //            else
        //                treeRow = page.Rows().FirstOrDefault(delegate(XElement s)
        //                {
        //                    string e = BaseMarkup(OrderElement(s.Element(type.Name)));
        //                    return e == itemContents;
        //                });

        //            if (treeRow != null)
        //            {
        //                result = new XRow()
        //                {
        //                    Table = tableFile,
        //                    Page = page,
        //                    Row = treeRow
        //                };
        //                break;
        //            }
        //        }
        //    }

        //    return result;
        //}
        //find XML the first representation of an item and its connected items
        //private XRowTree GetTree(Type type, object item)
        //{
        //    if (item == null)
        //        return null;

        //    XRowTree result = null;
        //    string docFileName = string.Format("{0}\\{1}.xtab", root, type.ToString());
        //    XFile tableFile = this.ioService.OpenOrCreate(docFileName, type, true);

        //    if (tableFile != null)
        //    {
        //        string itemContents = itemContents = BaseMarkup(Write(type, item, true));
        //        var pages = tableFile.Pages().Select(s => this.ioService.OpenOrCreate(s.Attribute("file").Value, typeof(TablePage)));
        //        var primaryProps = this.propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).ToArray();
        //        foreach (var page in pages)
        //        {
        //            XElement xItem = null;
        //            if (primaryProps.Any())
        //                xItem = page.Rows().FirstOrDefault(s => TestAgainstXElement(type, item, s, primaryProps));
        //            else
        //                xItem = page.Rows().FirstOrDefault(s => BaseMarkup(s.Element(type.Name)) == itemContents);

        //            if (xItem != null)
        //            {
        //                result = new XRowTree()
        //                {
        //                    Table = tableFile,
        //                    Page = page,
        //                    Rows = new List<XElement> { xItem }
        //                };
        //                break;
        //            }
        //        }
        //    }

        //    return result;
        //}
        //find XML all representations of an item and thier connected items
        //private List<XRowTree> GetTrees(Type type, object item)
        //{
        //    if (item == null)
        //        return null;

        //    List<XRowTree> result = new List<XRowTree>();
        //    string docFileName = string.Format("{0}\\{1}.xtab", root, type.ToString());
        //    XFile tableFile = this.ioService.OpenOrCreate(docFileName, type, true);

        //    if (tableFile != null)
        //    {
        //        string itemContents = BaseMarkup(Write(type, item, true));
        //        var pages = tableFile.Pages().Select(s => this.ioService.OpenOrCreate(s.Attribute("file").Value, typeof(TablePage)));
        //        foreach (var page in pages)
        //        {
        //            List<XElement> xItems = null;
        //            var primaryProps = this.propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey);
        //            if (primaryProps.Any())
        //                xItems = page.Rows().Where(s => TestAgainstXElement(type, item, s, primaryProps)).ToList();
        //            else
        //                xItems = page.Rows().Where(s => BaseMarkup(s.Element(type.Name)) == itemContents).ToList();

        //            if (xItems != null)
        //            {
        //                result.Add(new XRowTree()
        //                {
        //                    Table = tableFile,
        //                    Page = page,
        //                    Rows = xItems
        //                });
        //            }
        //        }
        //    }

        //    return result;
        //}

        //XML items comparison

        #region Querying private methods

        private IEnumerable<object> QueryItems(Type type, Func<dynamic, bool> query, string include = null)
        {
            //this.itemsCacheService.Clear();
            this.propertyService.LoadType(type);
            //lazyLoad = (lazyLoad) ? true : this.LazyLoad;
            string docFileName = string.Format("{0}.xtab", type.FullName);
            XFile tableFile = this.ioService.OpenFileOrCreate(docFileName, type, true);
            if (tableFile != null)
            {
                Dictionary<string, string[]> positions = new Dictionary<string, string[]>();;
                var result = tableFile.Pages()
                    .Where(s =>
                        this.ioService.FileExists(s.Attribute("file").Value))
                    .SelectMany(s =>
                        this.ioService.OpenFileOrCreate<TablePage>(s.Attribute("file").Value, true).Rows())
                    .Where(s =>
                        query(Read(type, s, include)))
                        //query(Read(type, s, true)))
                    .Select(s =>
                        Read(type, s, include));
                        //Read(type, s, lazyLoad));

                return result;

                //var result = tableFile.Pages()
                //    .Where(delegate(XElement s)
                //    {
                //        string fileCode = s.Attribute("file").Value;
                //        if (this.ioService.FileExists(fileCode))
                //        {
                //            List<string> rowCodes = new List<string>();
                //            var rows = this.ioService.OpenFileOrCreate<TablePage>(fileCode, true).Rows();
                //            if (rows.Where(delegate(XElement r)
                //            {
                //                string rowCode = r.Attribute("code").Value;
                //                if (query(Read(type, r, true)))
                //                {
                //                    rowCodes.Add(rowCode);
                //                    return true;
                //                }
                //                return false;
                //            }).Count() > 0)
                //            {
                //                if (!positions.ContainsKey(fileCode))
                //                    positions.Add(fileCode, rowCodes.ToArray());
                //                else
                //                    positions[fileCode] = rowCodes.ToArray();

                //                return true;
                //            }
                //        }

                //        return false;
                //    });

                //var pageRows = result.SelectMany(delegate(XElement s)
                //{
                //    string fileCode = s.Attribute("file").Value;
                //    return this.ioService.OpenFileOrCreate<TablePage>(fileCode, true).Rows()
                //        .Where(r => positions.Keys.Contains(fileCode) &&
                //            positions[fileCode].Contains(r.Attribute("code").Value));
                //});

                //var pageRows = result.SelectMany(s => this.ioService.OpenOrCreate<TablePage>(s.Attribute("file").Value, true).Rows());

                //result = pageRows.Where(r =>
                //    query(Read(type, r, lazyLoad)));

                //return result.Select(r => Read(type, r, lazyLoad));
            }

            return Enumerable.Empty<object>();
        }
        private IEnumerable<object> SelectItems(Type type, Func<dynamic, bool> query = null, bool backward = false, string include = null)
        {
            //this.itemsCacheService.Clear();
            this.propertyService.LoadType(type);
            string docFileName = string.Format("{0}.xtab", type.ToString());
            XFile tableFile = this.ioService.OpenFileOrCreate(docFileName, type, true);
            if (tableFile != null)
            {
                var rows = tableFile.Pages()
                    .Where(s =>
                        this.ioService.FileExists(s.Attribute("file").Value))
                    .SelectMany(s =>
                        this.ioService.OpenFileOrCreate<TablePage>(s.Attribute("file").Value, true).Rows());

                if (backward)
                    rows = rows.Reverse();

                IEnumerable<object> result = null;
                if (query != null)
                    result = rows.Where(r => query(Read(type, r, include)))
                        .Select(r =>
                            Read(type, r, include));
                else
                    result = rows.Select(r =>
                        Read(type, r, include));

                return result;
            }

            return Enumerable.Empty<object>();
        }
        private IEnumerable<object> SelectItemsByExample(Type type, object example, string include = null)
        {
            if (type == null || example == null)
                return Enumerable.Empty<object>();

            //this.itemsCacheService.Clear();
            this.propertyService.LoadType(type);
            string docFileName = string.Format("{0}.xtab", type.ToString());
            XFile tableFile = this.ioService.OpenFileOrCreate(docFileName, type, true);
            if (tableFile != null)
            {
                //object item = example.ToType(type);
                List<string> skipProps = new List<string>();
                var itemProps = this.propertyService.Properties(type.FullName).Where(s => !s.IsReadOnly);
                foreach (var itemProp in itemProps)
                {
                    var examplePropVal = itemProp.Property.GetValue(example);
                    if(examplePropVal == null || examplePropVal.Equals(ValueHelper.DefaultOf(itemProp.PropertyType)))
                        skipProps.Add(itemProp.PropertyName);
                    //if (itemProp.IsRequired)
                    //{
                    //}

                }

                XElement test = null;
                if(skipProps.Any())
                    test = Write(type, example, true, null, new UpdateFilter() { Behavior = UpdateFilterBehavior.Skip, Properties = skipProps.ToArray() });
                else
                    test = Write(type, example, true);

                var rows = tableFile.Pages()
                    .Where(s =>
                        this.ioService.FileExists(s.Attribute("file").Value))
                    .SelectMany(s =>
                        this.ioService.OpenFileOrCreate<TablePage>(s.Attribute("file").Value, true).Rows());

                IEnumerable<object> result = null;
                result = rows.Where(r => test.ElementMatch(r.Elements().FirstOrDefault()))
                    .Select(r =>
                        Read(type, r, include));

                return result;
            }

            return Enumerable.Empty<object>();
        }
        private IEnumerable<object> SelectItemsByExamples(Type type, object[] examples, string include = null)
        {
            if (type == null || examples == null || !examples.Any())
                return Enumerable.Empty<object>();

            //this.itemsCacheService.Clear();
            this.propertyService.LoadType(type);
            string docFileName = string.Format("{0}.xtab", type.ToString());
            XFile tableFile = this.ioService.OpenFileOrCreate(docFileName, type, true);
            if (tableFile != null)
            {
                List<XElement> tests = new List<XElement>();
                foreach (var example in examples)
                {
                    List<string> skipProps = new List<string>();
                    var itemProps = this.propertyService.Properties(type.FullName).Where(s => !s.IsReadOnly);
                    foreach (var itemProp in itemProps)
                    {
                        var examplePropVal = itemProp.Property.GetValue(example);
                        if (examplePropVal == null || examplePropVal.Equals(ValueHelper.DefaultOf(itemProp.PropertyType)))
                            skipProps.Add(itemProp.PropertyName);
                    }

                    XElement test = null;
                    if (skipProps.Any())
                        test = Write(type, example, true, null, new UpdateFilter() { Behavior = UpdateFilterBehavior.Skip, Properties = skipProps.ToArray() });
                    else
                        test = Write(type, example, true);

                    tests.Add(test);
                }

                var rows = tableFile.Pages()
                    .Where(s =>
                        this.ioService.FileExists(s.Attribute("file").Value))
                    .SelectMany(s =>
                        this.ioService.OpenFileOrCreate<TablePage>(s.Attribute("file").Value, true).Rows());

                IEnumerable<object> result = null;
                result = rows.Where(r =>
                    tests.Any(t => t.ElementMatch(r.Elements().FirstOrDefault())))
                    .Select(r => Read(type, r, include));

                return result;
            }

            return Enumerable.Empty<object>();
        }

        private XRow GetRow(Type type, object example)
        {
            if (type == null || example == null)
                return null;

            //this.itemsCacheService.Clear();
            this.propertyService.LoadType(type);
            string docFileName = string.Format("{0}.xtab", type.ToString());
            XFile tableFile = this.ioService.OpenFileOrCreate(docFileName, type, true);
            if (tableFile != null)
            {
                object item = null;
                if (this.propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).Any())
                    item = FilterItemPrimaryProperties(type, example);
                else
                    item = FilterItemBaseProperties(type, example);

                XRow result = new XRow();

                //object item = example.ToType(type);
                List<string> skipProps = new List<string>();
                var itemProps = this.propertyService.Properties(type.FullName).Where(s => !s.IsReadOnly);
                foreach (var itemProp in itemProps)
                {
                    var examplePropVal = itemProp.Property.GetValue(item);
                    if (examplePropVal == null || examplePropVal.Equals(ValueHelper.DefaultOf(itemProp.PropertyType)))
                        skipProps.Add(itemProp.PropertyName);
                }

                XElement test = null;
                if (skipProps.Any())
                    test = Write(type, item, true, null, new UpdateFilter() { Behavior = UpdateFilterBehavior.Skip, Properties = skipProps.ToArray() });
                else
                    test = Write(type, item, true);

                var pages = tableFile.Pages().Where(s => this.ioService.FileExists(s.Attribute("file").Value));
                foreach (var page in pages)
                {
                    XFile pageFile = this.ioService.OpenFileOrCreate<TablePage>(page.Attribute("file").Value, true);
                    if (pageFile != null)
                    {
                        pageFile.Rows().Any(delegate(XElement r)
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
                    }
                }

                return result;
            }

            return null;
        }
        private XRowTree GetTree(Type type, object example)
        {
            if (type == null || example == null)
                return null;

            //this.itemsCacheService.Clear();
            this.propertyService.LoadType(type);
            string docFileName = string.Format("{0}.xtab", type.ToString());
            XFile tableFile = this.ioService.OpenFileOrCreate(docFileName, type, true);
            if (tableFile != null)
            {
                object item = null;
                if (this.propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).Any())
                    item = FilterItemPrimaryProperties(type, example);
                else
                    item = FilterItemBaseProperties(type, example);

                XRowTree result = new XRowTree()
                {
                    Rows = new List<XElement>()
                };

                //object item = example.ToType(type);
                List<string> skipProps = new List<string>();
                var itemProps = this.propertyService.Properties(type.FullName).Where(s => !s.IsReadOnly);
                foreach (var itemProp in itemProps)
                {
                    var examplePropVal = itemProp.Property.GetValue(item);
                    if (examplePropVal == null || examplePropVal.Equals(ValueHelper.DefaultOf(itemProp.PropertyType)))
                        skipProps.Add(itemProp.PropertyName);
                }

                XElement test = null;
                if (skipProps.Any())
                    test = Write(type, item, true, null, new UpdateFilter() { Behavior = UpdateFilterBehavior.Skip, Properties = skipProps.ToArray() });
                else
                    test = Write(type, item, true);

                var pages = tableFile.Pages().Where(s => this.ioService.FileExists(s.Attribute("file").Value));
                foreach (var page in pages)
                {
                    XFile pageFile = this.ioService.OpenFileOrCreate<TablePage>(page.Attribute("file").Value, true);
                    if (pageFile != null)
                    {
                        if(pageFile.Rows().Where(delegate(XElement r)
                        {
                            if (test.ElementMatch(r.Elements().FirstOrDefault()))
                            {
                                result.Rows.Add(r);
                                return true;
                            }

                            return false;
                        }).Count() > 0)
                        {
                            result.Table = tableFile;
                            result.Page = pageFile;
                        }
                    }
                }

                if (result.Table != null)
                    return result;
            }

            return null;
        }
        private XElement GetRawData(string code)
        {
            XElement element = null;

            try
            {
                string[] nameParts = code.Split('.');
                XFile file = this.ioService.OpenFileOrCreate(string.Format("{0}.xpag", nameParts[0]), openOnly: true);
                if (file != null)
                {
                    element = file.Rows()
                        .FirstOrDefault(s => s.Attribute("code").Value == nameParts[1]);
                }
            }
            catch
            {
            }

            return element;
        }
        
        #endregion

        #region IXodEngine Implementation

        public event EventHandler<TriggerEventArgs> BeforeAction;
        public event EventHandler<TriggerEventArgs> AfterAction;

        public string Path { get; private set; }
        public bool IsNew { get; private set; }
        public bool LazyLoad { get; set; }
        public bool LazyLoadParent { get; set; }

        public IEnumerable<object> Select(Type type, bool backward = false, string include = null)
        {
            return SelectItems(type, null, backward, include);
        }
        public IEnumerable<object> Query(Type type, Func<dynamic, bool> query = null, string include = null)
        {
            return QueryItems(type, query, include);
        }
        public IEnumerable<object> QueryByExample(Type type, object example, string include = null)
        {
            return SelectItemsByExample(type, example, include);
        }
        public IEnumerable<object> QueryByExample(Type type, object[] examples, string include = null)
        {
            return SelectItemsByExamples(type, examples, include);
        }
        public object Find(Type type, Func<dynamic, bool> query, string include = null)
        {
            return SelectItems(type, query, false, include).FirstOrDefault();
        }
        public object FindLast(Type type, Func<dynamic, bool> query, string include = null)
        {
            return SelectItems(type, query, true, include).LastOrDefault();
        }
        public object First(Type type, string include = null)
        {
            return SelectItems(type, null, false, include).FirstOrDefault();
        }
        public object Last(Type type, string include = null)
        {
            return SelectItems(type, null, true, include).FirstOrDefault();
        }

        public object Insert(Type type, object item, bool lazyLoad = false)
        {
            this.propertyService.LoadType(type);
            Create(type, item, lazyLoad);
            return GetPrimaryValues(type, item);
        }
        public object InsertOrUpdate(Type type, object item, bool lazyLoad = false, UpdateFilter filter = null)
        {
            this.propertyService.LoadType(type);
            var primValues = GetPrimaryValues(type, item);

            object reference = null;
            if (primValues.Any())
                reference = SelectItemsByExample(type, PopulateItemProperties(type, primValues), "*").FirstOrDefault();

            if(reference != null)
                Update(type, reference, item, filter);
            else
                Create(type, item, lazyLoad);

            return GetPrimaryValues(type, item);
        }
        public bool Update(Type type, object item, object newItem, UpdateFilter filter = null)
        {
            return UpdateItem(type, item, newItem, null, filter);
        }
        public bool Update(Type type, object item, UpdateFilter filter = null)
        {
            this.propertyService.LoadType(type);
            if (null == item)
                exceptionService.Throw(new ArgumentNullException());

            var primProps = this.propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).Select(s => s.PropertyName);
            if (!primProps.Any())
                exceptionService.Throw(new MissingPrimaryKeyValueException());

            object old = SelectItemsByExample(type, FilterItemProperties(type, item, primProps), "*").FirstOrDefault();

            //object old = Query(type, delegate(dynamic oldItem)
            //{
            //    foreach (var primProp in primProps)
            //    {
            //        object ov = primProp.Property.GetValue(oldItem);
            //        object nv = primProp.Property.GetValue(item);
            //        if (!ov.Equals(nv))
            //            return false;
            //    }
            //    return true;
            //}).FirstOrDefault();

            if (null != old)
                return Update(type, old, item, filter);

            return false;
        }
        public bool Delete(Type type, object item)
        {
            this.propertyService.LoadType(type);
            if (null == item)
                exceptionService.Throw(new ArgumentNullException());

            bool value = false;
            TriggerEventArgs trigger = new TriggerEventArgs()
            {
                Item = item,
                Type = type,
                Action = DatabaseActions.Delete
            };

            if (BeforeAction != null)
                BeforeAction(this, trigger);

            if (trigger.Cancel)
                return false;

            XRowTree rowTree = GetTree(type, item);
            if (rowTree != null)
            {
                string pageCode = null;
                if (null != rowTree.Page)
                    pageCode = rowTree.Page.GetFileCode();

                foreach (var row in rowTree.Rows)
                {
                    XAttribute rowCodeAtt = row.Attribute("code");
                    string itemCode = (null != rowCodeAtt && null != pageCode) ?
                        string.Format("{0}.{1}", pageCode, rowCodeAtt.Value) : "";

                    //clear cached resources of this item and its related items
                    this.itemsCacheService.Clear(type, rowCodeAtt.Value);

                    //Delete all applicable reference items
                    var propRefProps = this.propertyService.Properties(type.FullName).Where(s =>
                        s.ReferenceType != PropertyReferenceType.Parent &&
                        s.ReferenceType != PropertyReferenceType.SelfForeign &&
                            (s.Cascade.HasFlag(CascadeOptions.Delete) &&
                            s.TypeCategory != PropertyTypeCategory.None));

                    foreach (var propRefProp in propRefProps)
                    {
                        object refItem = propRefProp.Property.GetValue(item);

                        if (null == refItem)
                            continue;

                        //reference items
                        if (propRefProp.TypeCategory == PropertyTypeCategory.Class)
                            Delete(propRefProp.PropertyType, refItem);
                        //collection reference items
                        Type childType = null;
                        if (propRefProp.TypeCategory == PropertyTypeCategory.Array)
                            childType = propRefProp.PropertyType.GetElementType();
                        else if (propRefProp.TypeCategory == PropertyTypeCategory.GenericCollection)
                            childType = propRefProp.PropertyType.GetGenericArguments().FirstOrDefault();

                        if (null == childType)
                            continue;

                        var children = from child in refItem as IEnumerable<object> select child;
                        if (children.Any())
                            foreach (var child in children)
                                Delete(childType, child);
                    }

                    var itemPrmVals = GetPrimaryValues(type, item);
                    if (null == itemPrmVals)
                        continue;

                    //Delete item reference from parents
                    var parRefProps = this.propertyService.Properties(type.FullName)
                        .Where(s => s.ReferenceType == PropertyReferenceType.Parent);

                    foreach (var parRefProp in parRefProps)
                    {
                        object parentValue = parRefProp.Property.GetValue(item);
                        Dictionary<string, object> parentPrmVals = null;

                        if (parentValue == null)
                        {
                            parentPrmVals = new Dictionary<string,object>();
                            foreach(var pk in parRefProp.ParentKeys) {
                                var pkProp = this.propertyService.Properties(type.FullName).FirstOrDefault(s => s.PropertyName == pk.LocalProperty);
                                if(pkProp != null) {
                                    parentPrmVals.Add(pk.RemoteProperty, pkProp.Property.GetValue(item));
                                }
                            }
                        }
                        else
                        {
                            parentPrmVals = GetPrimaryValues(type, parentValue);
                        }
                        //object parent = GetReferenceByProperties(parRefProp.PropertyType, parentPrmVals);
                        object parent = SelectItemsByExample(parRefProp.PropertyType, PopulateItemProperties(parRefProp.PropertyType, parentPrmVals), "*").FirstOrDefault();
                        if (parent == null)
                            continue;

                        var chdRefProps = this.propertyService.Properties(parRefProp.PropertyType.FullName)
                            .Where(s =>
                            s.ReferenceType == PropertyReferenceType.Children &&
                            s.CollectionItemType == type);

                        foreach (var chdRefProp in chdRefProps)
                        {
                            var chdRefItem = chdRefProp.Property.GetValue(parent);
                            if (chdRefItem == null)
                                continue;

                            Type childType = null;
                            if (chdRefProp.TypeCategory == PropertyTypeCategory.Array)
                                childType = chdRefProp.PropertyType.GetElementType();
                            else if (chdRefProp.TypeCategory == PropertyTypeCategory.GenericCollection)
                                childType = chdRefProp.PropertyType.GetGenericArguments().FirstOrDefault();

                            if (childType == null)
                                continue;

                            MethodInfo dmi = chdRefProp.PropertyType.GetMethod("Remove");
                            var children = from child in chdRefItem as IEnumerable<object> where itemPrmVals.DictionaryEqual(GetPrimaryValues(childType, child)) select child;
                            if (children.Any())
                            {
                                dmi.Invoke(chdRefItem, new object[] { children.FirstOrDefault() });
                                chdRefProp.Property.SetValue(parent, chdRefItem);
                                RemoveFromParent(type, itemCode, parRefProp, parent);
                            }
                        }
                    }

                    //delete actual file record
                    row.Remove();
                    if (!rowTree.Page.Root().HasElements)
                    {
                        File.Delete(rowTree.Page.Path);
                        XElement tablePage = rowTree.Table.Pages().FirstOrDefault(s =>
                            rowTree.Page.Path.EndsWith(s.Attribute("file").Value));

                        if (tablePage != null)
                        {
                            tablePage.Remove();
                            File.Delete(rowTree.Page.Path);

                            if (!rowTree.Table.Root().HasElements)
                                File.Delete(rowTree.Table.Path);
                        }
                        value = true;
                    }
                    else
                    {
                        //Full attribute should not be updated after turning true,
                        //it's a forward process for indexing purposes

                        //if (rowTree.Page.Size() < PAGE_SIZE)
                        //{
                        //    XAttribute fullAtt = rowTree.Page.Root().Attribute("full");
                        //    if (fullAtt != null)
                        //        fullAtt.Value = "false";
                        //}
                        this.ioService.Save(rowTree.Page);
                        //rowTree.Page.Save();
                    }
                }
            }

            if (null != AfterAction)
                AfterAction(this, new TriggerEventArgs() { Item = item, Type = type });

            if (value)
                item = null;

            return value;
        }
        public bool DropType(Type type)
        {
            TriggerEventArgs trigger = new TriggerEventArgs()
            {
                Cancel = false,
                Type = type,
                Action = DatabaseActions.Drop,
            };

            if (null != BeforeAction)
                BeforeAction(this, trigger);

            if (trigger.Cancel)
                return false;

            string docFileName = string.Format("{0}\\{1}.xtab", root, type.ToString());
            XFile tableFile = this.ioService.OpenFileOrCreate(docFileName, type, true);

            if (tableFile != null)
            {
                //go through all pages and delete all node of this type unless
                //it's attributed as required/parent for other types then raise
                //an exception

                //var pages = from row in tableFile.Pages() select OpenFileOrCreate(row.Value, typeof(TablePage));
                //foreach (var page in pages)
                //{
                //    if (cachedFiles.Contains(page))
                //        cachedFiles.Remove(page);
                //    page.Delete();
                //}

                //if (cachedFiles.Contains(tableFile))
                //    cachedFiles.Remove(tableFile);
                this.ioService.Delete(tableFile.Path);
                //tableFile.Delete();

                //this.propertyService.UnloadType(type);
                this.autonumberService.ClearType(type);
            }

            if (null != AfterAction)
                AfterAction(this, trigger);

            return false;
        }
        public void ChangePassword(string currentPassword, string newPassword)
        {
            this.securityService.ChangePassword(currentPassword, newPassword);
        }
        public void Loose(string password)
        {
            this.securityService.Loose(password);
        }
        public void Secure(string password)
        {
            this.securityService.Secure(password);
        }
        public IEnumerable<Type> RegisteredTypes()
        {
            List<Type> types = new List<Type>();
            var files = System.IO.Directory.GetFiles(root, "*.xtab");
            foreach (var file in files)
                types.Add(Type.GetType(System.IO.Path.GetFileNameWithoutExtension(file)));

            return types;
        }
        public void RegisterType(Type type)
        {
            this.propertyService.LoadType(type);
        }
        public void ClearCache()
        {
            this.ioService.ClearCurrentCache();
            this.itemsCacheService.ClearCurrentCache();
        }
        public void ClearCaches()
        {
            this.ioService.ClearAllCache();
            this.itemsCacheService.ClearAllCache();
        }
        public void Dispose()
        {
            this.itemsCacheService = null;
            this.securityService = null;
            this.propertyService = null;
            this.autonumberService = null;
        }

        #endregion
    }
}