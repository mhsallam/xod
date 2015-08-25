using AcesDevelopers.Xod.Helpers;
using AcesDevelopers.Xod.Infra;
using AcesDevelopers.Xod.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AcesDevelopers.Xod.Engines.Xml
{
    public class XmlEngine : IXodEngine, IDisposable
    {
        const int PAGE_SIZE = 262144;
        const int CACHE_SIZE = 9;

        string Password { get; set; }
        string xodRoot = null;
        string xodPath = null;
        List<XFile> cachedFiles = null;

        //services
        XodFileService fileService = null;
        IXodSecurityService securityService = null;
        PropertyService propertyService = null;
        AutoNumberService autoNumberService = null;
        //create dictionary and combine each read operation with code related to a separate opsCacheService instance
        OperationsCacheService opsCacheService = null;


        public XmlEngine(string file, string password = null, DatabaseOptions defaultOptions = null)
        {
            if (string.IsNullOrEmpty(file))
                throw new FileNotFoundException();

            if (!string.IsNullOrEmpty(password) && (password.Length < 1 || password.Length > 256))
                throw new SecurityException("Password length should be between 1 and 256.");

            if (!file.EndsWith(".xod"))
            {
                if (Directory.Exists(file))
                {
                    string[] files = System.IO.Directory.GetFiles(file,
                        "*.xod", SearchOption.TopDirectoryOnly);
                    if (files.Length > 0)
                        file = files[0];
                    else
                        file = System.IO.Path.Combine(file, "Xod");
                }
            }

            if (null == defaultOptions)
                defaultOptions = new DatabaseOptions()
                {
                    InitialCreate = true
                };

            this.propertyService = new PropertyService();
            this.propertyService.LoadType(typeof(Table));
            this.opsCacheService = new OperationsCacheService();
            this.autoNumberService = new AutoNumberService();

            this.cachedFiles = new List<XFile>();
            this.LazyLoad = defaultOptions.LazyLoad;
            //Compressed = defaultOptions.Compressed;

            if (OpenFileOrCreate<Database>(file, !defaultOptions.InitialCreate, defaultOptions.InitialCreate) != null)
            {
                xodPath = file;
                xodRoot = new FileInfo(file).DirectoryName;
            }
            else
                throw new DatabaseFileException();


            if (!string.IsNullOrEmpty(password))
                this.Password = CryptoHelper.GetSHA256HashData(password);

            string dir = System.IO.Path.GetDirectoryName(file);
            if (!Directory.Exists(dir) && defaultOptions.InitialCreate)
                Directory.CreateDirectory(dir);

            IsNew = !File.Exists(file);

            this.fileService = new XodFileService(xodRoot);
            this.securityService = new XodSecurityService(xodPath, this.Password);

            //clear cached file on security state changes
            this.securityService.Changed += (sender, e) =>
            {
                foreach (var f in this.cachedFiles.ToArray())
                {
                    f.Document = null;
                    this.cachedFiles.Remove(f);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
            };

        }


        private string Create(Type type, object item, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            lazyLoad = (lazyLoad) ? true : LazyLoad;
            if (null == item)
                throw new ArgumentNullException();

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

            //set auto number values
            List<string> autoNumberedProps = new List<string>();
            var autoNumberProps = this.propertyService.PropertyItems.Where(s => s.Type == type && s.IsAutoNumber);
            foreach (var autoNumberProp in autoNumberProps)
            {
                if (!ValueHelper.IsNumeric(autoNumberProp.PropertyType) && !autoNumberProp.PropertyType.Equals(typeof(Guid)))
                    throw new AutoNumberDataTypeException();

                if (!autoNumberProp.ForceAutoNumber ||
                    ValueHelper.DefaultOf(autoNumberProp.PropertyType).Equals(autoNumberProp.Property.GetValue(item)))
                {
                    if (!autoNumberProp.PropertyType.Equals(typeof(Guid)))
                    {
                        dynamic nn = NextNumber(type, autoNumberProp);
                        autoNumberProp.Property.SetValue(item, nn);
                    }
                    else
                        autoNumberProp.Property.SetValue(item, Guid.NewGuid());

                    autoNumberedProps.Add(autoNumberProp.Property.Name);
                }
            }

            //check if primary key properties has been auto numbered
            bool autoNumbered = true;
            var primPropAtts = this.propertyService.PropertyItems.Where(s => s.Type == type && s.IsPrimaryKey);
            foreach (var primPropAtt in primPropAtts)
                if (!autoNumberedProps.Contains(primPropAtt.PropertyName))
                {
                    autoNumbered = false;
                    break;
                }

            var reserved = ReservedValue(type, item);
            if (!autoNumbered && reserved == ReservedPropertyType.Primary)
                throw new ReservedPrimaryKeyException();
            else if (reserved == ReservedPropertyType.Unique)
                throw new ReservedUniqueKeyException();

            string refCode = AddToPage(type, item, lazyLoad, writeTrack);


            if (null != AfterAction)
                AfterAction(this, trigger);

            return refCode;
        }
        private string AddToPage(Type type, object item, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            string itemCode = null;
            string itemPath = null;

            string typeName = type.FullName;
            if (typeName == "System.Object")
                typeName = item.GetActualType().FullName;

            string tableFileName = string.Format("{0}.{1}", typeName, "xtab");

            XFile tableFile = OpenFileOrCreate(tableFileName, typeof(Table));
            if (tableFile != null)
            {
                XElement page = tableFile.Document.Root.Element("Pages").Elements("Page").FirstOrDefault(delegate(XElement s)
                {
                    XAttribute fullAtt = s.Attribute("full");
                    return null == fullAtt || fullAtt.Value != "true";
                });

                string pageFileName = "";
                if (null == page || string.IsNullOrEmpty(page.Value))
                {
                    pageFileName = string.Format("{0}.{1}", ValueHelper.PickCode(), "xpag");
                    XElement tp = new XElement("Page", pageFileName);
                    tp.Add(new XAttribute("full", false));
                    tableFile.Document.Root.Element("Pages").Add(tp);
                }
                else
                    pageFileName = page.Value;

                XFile pageFile = OpenFileOrCreate(pageFileName, typeof(TablePage));
                if (pageFile != null)
                {
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
                        pageFile = OpenFileOrCreate(pageFileName, typeof(TablePage));
                        pageFile.Document.Root.Element("Rows").Add(re);
                        if (pageFile.Size() >= PAGE_SIZE)
                        {

                            var fullPage = tableFile.Document.Root.Element("Pages").Elements("Page")
                                .FirstOrDefault(s => pageFile.Path.EndsWith(s.Value));

                            if (null != fullPage)
                                fullPage.Attribute("full").Value = "true";
                        }
                        pageFile.Save();
                    }
                    tableFile.Save();
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
            IEnumerable<PropertyInfoItem> props = this.propertyService.PropertyItems.Where(s => s.Type == type && !s.IsNotMapped).OrderByDescending(s =>
                null != s.PropertyType && (
                s.PropertyType.IsValueType ||
                s.PropertyType == typeof(string) ||
                s.PropertyType == typeof(DateTime)));

            if (null != filter && null != filter.Properties)
            {
                PropertyInfoItem[] filterProps = null;
                if (filter.Behavior == UpdateFilterBehavior.Skip)
                    filterProps = props.Where(s => !filter.Properties.Contains(s.PropertyName)).ToArray();
                else
                    filterProps = props.Where(s => filter.Properties.Contains(s.PropertyName)).ToArray();

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
                throw new RequiredPropertyException();
                //Rollback();
            }
            XElement propertyElement = null;

            if (prop.IsGenericType && null != value)
                prop.PropertyType = value.GetActualType();

            //for string, primitive, or enum values
            if (prop.PropertyType.IsValueType || prop.PropertyType == typeof(string))
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
                (null != prop.PropertyType.GetInterface("ICollection")))
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
                if (itmType.IsValueType || itmType == typeof(string))
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
            else if (!lazyLoad && prop.PropertyType.IsClass)
            {
                if (prop.IsGenericType)
                    prop.PropertyType = value.GetActualType();

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
            var props = selectedProps.Where(s => !s.IsNotMapped).OrderByDescending(s => s.PropertyType.IsValueType || s.PropertyType == typeof(string) || s.PropertyType == typeof(DateTime));
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
        private object Read(Type type, XElement element, bool lazyLoad = false, ReadWriteTrack track = null)
        {
            lazyLoad = (lazyLoad) ? true : LazyLoad;
            if (element == null)
                return null;

            if (type.Name != element.Name)
                type = this.propertyService.LoadedTypes.FirstOrDefault(s => s.Name == element.Name);

            if (null != track &&
                null != track.Parent &&
                RecursionError(track, track.Parent))
            {
                track = null;
                lazyLoad = true;
            }

            object item = Activator.CreateInstance(type);

            var props = this.propertyService.PropertyItems.Where(s =>
                s.Type == type &&
                !s.IsNotMapped).OrderByDescending(s =>
                null != s.PropertyType && (
                s.PropertyType.IsValueType ||
                s.PropertyType == typeof(string) ||
                s.PropertyType == typeof(DateTime))).ToArray();
            foreach (var prop in props)
            {
                if (!prop.Property.CanWrite)
                    continue;

                XElement e = element.Element(prop.PropertyName);
                if (null != e)
                {
                    //parsing collection types
                    XAttribute isColl = e.Attribute("collType");
                    if (null != isColl)
                    {
                        string methodName = null;
                        Type itemType = null;
                        if (prop.PropertyType.IsArray)
                        {
                            itemType = prop.PropertyType.GetElementType();
                            methodName = "AddRangeFromString";
                        }
                        else
                        {
                            itemType = prop.PropertyType.GetGenericArguments().FirstOrDefault();
                            methodName = "AddRange";
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
                            .GetMethod(methodName)
                            .MakeGenericMethod(new Type[] { itemType });

                        if (itemType.IsValueType || itemType == typeof(string))
                        {
                            args = new object[] { coll, collElements };
                            mi.Invoke(coll, args);

                            if (!prop.PropertyType.IsArray)
                                prop.Property.SetValue(item, coll, null);
                            else
                                prop.Property.SetValue(item, coll.ToArray(), null);
                        }
                        //ignore reference collection and array type if lazyLoad is true
                        else if (!lazyLoad)
                        {
                            var collCodes = collElements.Select(s => GetRefRow(s)).Where(s => null != s);
                            int i = 0;
                            var collQuery = collCodes.Select(delegate(XElement s)
                            {
                                i++;
                                XAttribute rowCode = s.Attribute("code");
                                if (null == rowCode)
                                    return null;

                                string childCode = string.Format("{0}.{1}", GetRefPage(itemType, rowCode.Value), rowCode.Value);

                                object obj = null;

                                obj = this.opsCacheService.GetItem(childCode, lazyLoad);
                                if (null != obj)
                                    return obj;

                                ReadWriteTrack childTrack = new ReadWriteTrack()
                                {
                                    Code = childCode,
                                    Type = itemType,
                                    Parent = track
                                };
                                obj = Read(itemType, s.Element(itemType.Name), false, childTrack);

                                return obj;
                            }).Where(s => s != null);

                            args = new object[] { coll, collQuery.ToArray() };
                            mi.Invoke(coll, args);

                            if (!prop.PropertyType.IsArray)
                                prop.Property.SetValue(item, coll, null);
                            else
                                prop.Property.SetValue(item, coll.ToArray(), null);
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
                            prop.Property.SetValue(item, value, null);
                        }
                        else
                        {
                            stringValue = element.Element(prop.PropertyName).Value;
                            if (string.IsNullOrEmpty(stringValue))
                                continue;

                            if (prop.IsGenericType)
                            {
                                if (string.IsNullOrEmpty(prop.GenericTypeProperty))
                                {
                                    string[] itemPath = stringValue.Split('.');
                                    if (itemPath.Length == 2)
                                        prop.PropertyType = GetPageType(itemPath[0]);
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
                                            var genericType = this.propertyService.LoadedTypes.FirstOrDefault(s => s.FullName == genericPropName);
                                            if (null != genericType)
                                                prop.PropertyType = genericType;
                                        }
                                    }
                                }

                                if (null == prop.PropertyType)
                                    throw new AnynomousTypeException();
                            }

                            value = ReadValue(prop, stringValue);

                            if (null == value && !lazyLoad && prop.PropertyType.IsClass)
                            {
                                object cachedItem = this.opsCacheService.GetItem(stringValue, lazyLoad);
                                if (null == cachedItem)
                                {

                                    ReadWriteTrack refTrack = new ReadWriteTrack()
                                    {
                                        Code = stringValue,
                                        Type = prop.PropertyType,
                                        Parent = track
                                    };

                                    XElement xRefItem = GetRefRow(stringValue);
                                    if (null != xRefItem)
                                    {
                                        value = Read(prop.PropertyType, xRefItem.Element(prop.PropertyType.Name), false, refTrack);
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
                                        value = GetRefItem(prop.PropertyType, localRefKeyValues);
                                    }
                                }
                                else
                                    value = cachedItem;
                            }

                            if (null != value)
                                prop.Property.SetValue(item, value);
                        }
                    }
                }
                else if (null != prop.DefaultValue && !prop.DefaultValue.Equals(ValueHelper.DefaultOf(prop.PropertyType)))
                    prop.Property.SetValue(item, prop.DefaultValue, null);
            }

            if (null != track)
            {
                this.opsCacheService.AddItem(type, track.Code, item, lazyLoad);
            }

            return item;
        }

        private object ReadValue(PropertyInfoItem prop, string stringValue)
        {
            object value = null;

            //parse enum values
            if (prop.PropertyType.IsEnum)
                value = Enum.Parse(prop.PropertyType, stringValue);
            //parse primitive, string and datetime values
            else if (prop.PropertyType.IsPrimitive ||
                prop.PropertyType == typeof(string) ||
                prop.PropertyType == typeof(DateTime))
                value = Convert.ChangeType(stringValue, prop.PropertyType);
            else if (prop.PropertyType == typeof(Guid))
                value = Guid.Parse(stringValue);
            else if (prop.PropertyType.IsGenericType &&
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
                throw new ArgumentNullException();

            if (null != track &&
                null != track.Parent &&
                RecursionError(track, track.Parent))
                return false;

            bool value = false;
            //try
            //{
            XRowTree rowTree = FirstRowTree(type, item);
            if (null != rowTree)
            {
                PropertyInfoItem[] props = null;

                if (null != filter && null != filter.Properties)
                {
                    if (filter.Behavior == UpdateFilterBehavior.Skip)
                        props = this.propertyService.PropertyItems.Where(s => s.Type == type && !s.IsNotMapped && !filter.Properties.Contains(s.PropertyName)).OrderByDescending(s =>
                            null != s.PropertyType && (
                            s.PropertyType.IsValueType ||
                            s.PropertyType == typeof(string) ||
                            s.PropertyType == typeof(DateTime))).ToArray();
                    else
                        props = this.propertyService.PropertyItems.Where(s => s.Type == type && !s.IsNotMapped && filter.Properties.Contains(s.PropertyName)).OrderByDescending(s =>
                            null != s.PropertyType && (
                            s.PropertyType.IsValueType ||
                            s.PropertyType == typeof(string) ||
                            s.PropertyType == typeof(DateTime))).ToArray();
                }
                else
                    props = this.propertyService.PropertyItems.Where(s => s.Type == type && !s.IsNotMapped).OrderByDescending(s =>
                        null != s.PropertyType && (
                        s.PropertyType.IsValueType ||
                        s.PropertyType == typeof(string) ||
                        s.PropertyType == typeof(DateTime))).ToArray();

                string pageCode = null;
                if (null != rowTree.Page)
                    pageCode = rowTree.Page.GetFileCode();

                foreach (var row in rowTree.Rows)
                {
                    //check if it doesn't remove children .. I think it does
                    XAttribute rowCodeAtt = row.Attribute("code");
                    string itemCode = (null != rowCodeAtt && null != pageCode) ?
                        string.Format("{0}.{1}", pageCode, rowCodeAtt.Value) : "";

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
                    if (!row.Element(type.Name).Equals(xNewRow))
                    {
                        //inforce cascade delete for complex reference and child items
                        Dictionary<Type, string> delItemsCode = new Dictionary<Type, string>();
                        var cascadeDelProps = props.Where(s => s.Cascade.HasFlag(CascadeOptions.Delete)).Select(s => s.PropertyName);
                        var cascadeDelPropNames = cascadeDelProps.ToArray();
                        string[] delRefTypes = { "complex", "children" };

                        var rowCollRefNodes = row.Element(type.Name).Elements().Where(delegate(XElement s)
                        {
                            XAttribute refTypeAtt = s.Attribute("refType");
                            XAttribute collTypeAtt = s.Attribute("collType");
                            bool result = null != collTypeAtt && null != refTypeAtt &&
                                (delRefTypes.Contains(refTypeAtt.Value) ||
                                (refTypeAtt.Value == "reference" && cascadeDelPropNames.Contains(s.Name.LocalName)));
                            return result;
                        }).SelectMany(s => s.Elements());
                        var newCollRefNodes = xNewRow.Elements().Where(delegate(XElement s)
                        {
                            XAttribute refTypeAtt = s.Attribute("refType");
                            XAttribute collTypeAtt = s.Attribute("collType");
                            bool result = null != collTypeAtt && null != refTypeAtt &&
                                (delRefTypes.Contains(refTypeAtt.Value) ||
                                (refTypeAtt.Value == "reference" && cascadeDelPropNames.Contains(s.Name.LocalName)));
                            return result;
                        }).SelectMany(s => s.Elements());

                        var newCollRefNodesString = newCollRefNodes.Select(s => s.ToString()).ToArray();
                        var delRefNodes = rowCollRefNodes.Where(s => !newCollRefNodesString.Contains(s.ToString()));
                        foreach (var code in delRefNodes)
                        {
                            XElement xDelRef = GetRefRow(code.Value);
                            if (xDelRef == null)
                                continue;

                            Type delRefType = GetRefType(code.Value);
                            if (delRefType == null)
                                continue;

                            object delRef = Read(delRefType, xDelRef.Elements().FirstOrDefault());
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
                                        if (!oldKeyValue.Equals(newKeyValue))
                                        {
                                            sameParent = false;
                                            break;
                                        }
                                    }
                                }

                                if (!sameParent)
                                {
                                    //remove XML-child-reference in old-parent XML-record
                                    //XElement transferredChild = RemoveFromParent(type, itemCode, propRefProp, oldRefItem);
                                    RemoveFromParent(type, itemCode, propRefProp, oldRefItem);

                                    ////add XML-child-reference to new-parent XML-record
                                    //if (transferredChild != null && newRefItem != null)
                                    //{
                                    //    string hostProp = null;
                                    //    XAttribute xHostProp = xOldRow.Element("Master").Attribute("hostProp");
                                    //    if(xHostProp != null)
                                    //    {
                                    //        hostProp = xHostProp.Value;
                                    //        var xPropRefProp = xNewRow.Element(propRefProp.PropertyName);
                                    //        if (xPropRefProp != null && xPropRefProp.Attribute("hostProp") != null)
                                    //            xPropRefProp.SetAttributeValue("hostProp", hostProp);
                                    //        else
                                    //            xPropRefProp.Add(new XAttribute("hostProp", hostProp));

                                    //        row.ReplaceNodes(xNewRow);
                                    //    }

                                    //    TransferToParent(type, transferredChild, hostProp, propRefProp.PropertyType, newRefItem);
                                    //}
                                }
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
                    rowTree.Page.Save();
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
                XRow oldParentXRow = SelectXRow(parentChildrenProp.PropertyType, parent);
                if (null != oldParentXRow)
                {
                    //PropertyInfoItem parentChildrenProp = null;
                    IEnumerable<PropertyInfoItem> parentChildrenProps = this.propertyService.PropertyItems
                        .Where(s => s.Type == parentChildrenProp.PropertyType && s.CollectionItemType == childType && s.ReferenceType == PropertyReferenceType.Children);

                    foreach (var pcp in parentChildrenProps)
                    {
                        var oldParentChildren = oldParentXRow.Row.Element(parentChildrenProp.PropertyType.Name).Element(pcp.PropertyName);
                        var parentItemRef = oldParentChildren.Elements().FirstOrDefault(s => s.Value == childCode);
                        if (null != parentItemRef)
                        {
                            item = parentItemRef;
                            parentItemRef.Remove();
                            if (oldParentXRow.Page.Size() < PAGE_SIZE)
                            {
                                XAttribute fullAtt = oldParentXRow.Page.Document.Root.Attribute("full");
                                if (null != fullAtt)
                                    fullAtt.Value = "false";
                            }
                            oldParentXRow.Page.Save();
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
            XRow newParentXRow = SelectXRow(parentType, parent);
            if (null != newParentXRow)
            {
                PropertyInfoItem parentChildrenProp = null;
                if (hostProp != null)
                    parentChildrenProp = this.propertyService.PropertyItems
                        .Where(s => s.Type == parentType && s.ReferenceType == PropertyReferenceType.Children &&
                            s.CollectionItemType == childType && s.PropertyName == hostProp).FirstOrDefault();
                else
                    parentChildrenProp = this.propertyService.PropertyItems
                        .Where(s => s.Type == parentType && s.ReferenceType == PropertyReferenceType.Children &&
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
                newParentXRow.Page.Save();
            }
        }

        private bool RecursionError(ReadWriteTrack track, ReadWriteTrack parent)
        {
            if (null == parent)
                return false;
            else if (track.Code == parent.Code)
                return true;
            else
                return RecursionError(track, parent.Parent);
        }


        private Dictionary<string, object> GetPrimaryValues(Type type, object item)
        {
            Dictionary<string, object> values = new Dictionary<string, object>();
            var primaryPros = this.propertyService.PropertyItems.Where(s => s.Type == type && s.IsPrimaryKey).OrderBy(s => s.PropertyName).Select(s => s.Property);
            foreach (var primaryProp in primaryPros)
            {
                if ((primaryProp.PropertyType.IsClass && primaryProp.PropertyType != typeof(string)) ||
                    primaryProp.PropertyType.IsGenericType ||
                    primaryProp.PropertyType.IsArray ||
                    primaryProp.PropertyType == typeof(DateTime))
                    throw new PrimaryKeyDataTypeException();

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
            var primaryPros = this.propertyService.PropertyItems.Where(s => s.Type == type && s.IsPrimaryKey).OrderBy(s => s.PropertyName);
            foreach (var primaryProp in primaryPros)
            {
                if (primaryProp.PropertyType.IsClass ||
                    primaryProp.PropertyType.IsGenericType ||
                    primaryProp.PropertyType.IsArray ||
                    primaryProp.PropertyType == typeof(DateTime))
                    throw new PrimaryKeyDataTypeException();

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

        private ReservedPropertyType ReservedValue(Type type, object item, string[] uniqueKeys = null)
        {
            Dictionary<string, object> keyValues = GetPrimaryValues(type, item);
            Dictionary<string, object> uniqueValues = ValueHelper.GetPropertyValues(type, item, uniqueKeys);
            if (!keyValues.Any() && !uniqueValues.Any())
                return ReservedPropertyType.None;

            var items = Select(type, true);
            foreach (var it in items)
            {
                if (keyValues != null)
                {
                    foreach (var keyValue in keyValues)
                    {
                        PropertyInfo primaryProp = type.GetProperty(keyValue.Key);
                        if (keyValue.Value.Equals(primaryProp.GetValue(it)))
                            return ReservedPropertyType.Primary;
                    }
                }

                if (uniqueValues != null)
                {
                    foreach (var uniqueValue in uniqueValues)
                    {
                        PropertyInfo primaryProp = type.GetProperty(uniqueValue.Key);
                        if (uniqueValue.Value.Equals(primaryProp.GetValue(it)))
                            return ReservedPropertyType.Unique;
                    }
                }
            }

            return ReservedPropertyType.None;
        }
        private dynamic NextNumber(Type type, PropertyInfoItem prop)
        {
            dynamic defValue = ValueHelper.DefaultOf(prop.PropertyType);
            if (!prop.IsAutoNumber)
                return defValue;

            dynamic value = defValue;
            if (this.autoNumberService.Contains(type, prop.PropertyName))
            {
                value = this.autoNumberService.GetNext(type, prop.PropertyName, prop.IdentityIncrement);
                if (value < prop.IdentitySeed)
                {
                    value = prop.IdentitySeed;
                    this.autoNumberService.Update(type, prop.PropertyName, value);
                }
            }
            else
            {
                MethodInfo sumMethod = typeof(NumericOperations)
                    .GetMethod("Sum")
                    .MakeGenericMethod(new Type[] { prop.PropertyType });

                var last = Select(type, true).LastOrDefault();
                if (last == null)
                    value = prop.IdentitySeed;
                else
                {
                    value = sumMethod.Invoke(
                        null,
                        new object[] {
                            new object[]
                            {
                                prop.Property.GetValue(last),
                                prop.IdentityIncrement
                            }});
                }
                this.autoNumberService.Set(type, prop.PropertyName, sumMethod, value);
            }
            return value;
        }

        private void ParseChildren(Type type, object item, Type childType, IEnumerable<object> children, PropertyInfoItem prop, XElement xProp, bool lazyLoad = false, ReadWriteTrack writeTrack = null)
        {
            if (prop.ReferenceType == PropertyReferenceType.Children)
            {
                PropertyInfoItem childParentPropItem = null;
                if (!string.IsNullOrEmpty(prop.ChildParentProperty))
                    childParentPropItem = this.propertyService.PropertyItems.FirstOrDefault(s => s.Type == childType && s.PropertyName == prop.ChildParentProperty);
                else
                    childParentPropItem = this.propertyService.PropertyItems.FirstOrDefault(s => s.Type == childType && s.PropertyType == type && s.ReferenceType == PropertyReferenceType.Parent);

                if (null != childParentPropItem)
                {
                    foreach (var child in children)
                    {
                        //object childParentPropValue = childParentProp.GetValue(child);
                        //if (null != childParentPropValue && !childParentPropValue.Equals())
                        //    throw new ReservedChildException();

                        if (null != childParentPropItem.ParentKeys)
                        {
                            foreach (var parentKeyPropAtt in childParentPropItem.ParentKeys)
                            {
                                object parentKeyValue = null;
                                PropertyInfoItem parentKeyProp = this.propertyService.PropertyItems.FirstOrDefault(s => s.Type == type && s.PropertyName == parentKeyPropAtt.RemoteProperty);
                                if (null != parentKeyProp)
                                    parentKeyValue = parentKeyProp.Property.GetValue(item);

                                if (null != parentKeyValue)
                                {
                                    PropertyInfoItem childKeyProp = this.propertyService.PropertyItems.FirstOrDefault(s => s.Type == childType && s.PropertyName == parentKeyPropAtt.LocalProperty);
                                    if (null != childKeyProp)
                                    {
                                        object childKeyValue = childKeyProp.Property.GetValue(child);
                                        if (null != childKeyValue && !childKeyValue.Equals(ValueHelper.DefaultOf(childKeyProp.PropertyType)))
                                        {
                                            PropertyInfoItem keyProp = this.propertyService.PropertyItems.FirstOrDefault(s => s.Type == type && s.PropertyName == parentKeyPropAtt.RemoteProperty);
                                            if (null != keyProp && !childKeyValue.Equals(keyProp.Property.GetValue(item)))
                                                throw new ReservedChildException();
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
                    throw new MissingParentKeyException();
                    //Rollback();
                }
            }
            else
            {
                bool isRefChild = this.propertyService.PropertyItems.Where(s => s.Type == childType && s.IsPrimaryKey).Any();
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
                refItem = Select(type).FirstOrDefault(delegate(object s)
                {
                    foreach (var itemPrimarySet in itemPrimarySets)
                    {
                        object sValue = null;
                        object itemPrimaryKeyValue = null;
                        if (!itemPrimarySets.TryGetValue(itemPrimarySet.Key, out itemPrimaryKeyValue))
                            return false;

                        PropertyInfo primaryKeyProp = type.GetProperty(itemPrimarySet.Key);
                        sValue = primaryKeyProp.GetValue(s);
                        if (sValue == null || !sValue.Equals(itemPrimaryKeyValue))
                            return false;
                    }
                    return true;
                });
            }
            string refCode = null;
            XRow xRow = null;
            if (null != refItem)
            {
                xRow = SelectXRow(type, refItem);
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
        private void ParseReference(Type type, object item, object value, PropertyInfoItem prop, XElement xItem, ReadWriteTrack writeTrack)
        {
            if (value != null && prop.ReferenceType == PropertyReferenceType.Foreign && prop.ForeignKeys != null)
            {
                Dictionary<string, object> foreignRefKeySets = ValueHelper.GetPropertyValues(prop.PropertyType, value, prop.ForeignKeys.Select(s => s.RemoteProperty));
                if (foreignRefKeySets.Any())
                    LinkItem(type, item, value, prop, xItem, foreignRefKeySets);
                else
                    ParseItem(type, item, value, prop, xItem, prop.ForeignKeys, writeTrack);
            }
            else if (value != null && prop.ReferenceType != PropertyReferenceType.Parent)
            {
                Dictionary<string, object> refKeySets = GetPrimaryValues(prop.PropertyType, value);
                if (refKeySets.Any())
                    LinkItem(type, item, value, prop, xItem, refKeySets);
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
                value = GetRefItem(prop.PropertyType, foreignRefKeySets);
                LinkRef(type, prop.PropertyType, value, prop, xItem, foreignRefKeySets);
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
            else if (prop.ReferenceType == PropertyReferenceType.Parent && prop.ParentKeys != null)
            {
                string relationshipName = null;
                if (writeTrack.Parent != null && writeTrack.Parent.RootProperty != null)
                    relationshipName = writeTrack.Parent.RootProperty.PropertyName;

                //child has been assigned to parent
                if (null != writeTrack && null != writeTrack.Parent && prop.PropertyType == writeTrack.Parent.Type)
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

                    if (null == writeTrack.Parent || prop.PropertyType != writeTrack.Parent.Type) //WHY?!! for the second part of the condition
                    {
                        object childParentItem = prop.Property.GetValue(item);
                        var localToRemoteParentProps = prop.ParentKeys.ToDictionary(s => s.LocalProperty, s => s.RemoteProperty);
                        var remoteParentValues = ValueHelper.GetPropertyValues(type, item, localToRemoteParentProps);

                        if (childParentItem == null && remoteParentValues.Any())
                        {
                            childParentItem = GetRefItem(prop.PropertyType, remoteParentValues);
                            if (childParentItem != null)
                                prop.Property.SetValue(item, childParentItem);
                        }
                        else if (childParentItem != null && !remoteParentValues.Any())
                        {
                            //find the mapping values from remote props and assign it to local props
                            var remoteParentProps = this.propertyService.PropertyItems
                                .Where(s => s.Type == prop.PropertyType);
                            var localParentProps = this.propertyService.PropertyItems
                                .Where(s => s.Type == type);

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

                        XRow xParentRow = SelectXRow(prop.PropertyType, childParentItem);
                        if (null != xParentRow)
                        {
                            var parentChildrenProps = this.propertyService.PropertyItems
                                .Where(s => s.Type == prop.PropertyType &&
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
                                xParentRow.Page.Save();
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
                                    throw new ReservedChildException();
                            }
                        }
                        xItem.Add(xpe);
                    }
                }
            }
        }
        private void ParseItem(Type type, object item, object value, PropertyInfoItem prop, XElement xItem, IEnumerable<ForeignKeyAttribute> foreignKeyAtts, ReadWriteTrack writeTrack = null)
        {
            Dictionary<string, object> foreignKeyRefValues = ValueHelper.GetPropertyValues(prop.PropertyType, value, foreignKeyAtts.Select(s => s.RemoteProperty));
            XElement xProp = WriteItem(prop.PropertyName, prop.PropertyType, value, foreignKeyRefValues, writeTrack);
            if (null != xProp)
                xItem.Add(xProp);

            SetMappingValues(type, item, prop.PropertyType, value, xItem, foreignKeyAtts.ToDictionary(s => s.RemoteProperty, s => s.LocalProperty));
        }
        private void LinkItem(Type type, object item, object value, PropertyInfoItem prop, XElement xItem, Dictionary<string, object> refKeySets)
        {
            object refItem = null;
            if (null != item)
            {
                refItem = GetRefItem(prop.PropertyType, refKeySets);
                if (null != refItem && prop.Property.GetValue(item).Equals(null))
                    prop.Property.SetValue(item, refItem);
            }

            if (null != refItem)
            {
                Dictionary<string, string> mappingProps = LinkRef(type, prop.PropertyType, refItem, prop, xItem, refKeySets);
                if (null != mappingProps)
                    SetMappingValues(type, item, prop.PropertyType, refItem, xItem, mappingProps);
            }
        }
        private Dictionary<string, string> LinkRef(Type type, Type propType, object propItem, PropertyInfoItem prop, XElement xItem, Dictionary<string, object> refKeySets)
        {
            Dictionary<string, string> mappingProps = null;
            XRow xRow = SelectXRow(propType, propItem);
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

        private void SetMappingValues(Type type, object item, Type propType, object value, XElement xItem, Dictionary<string, string> relationshipProps)
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
            var refKeyProps = propertyService.PropertyItems.Where(s => s.Type == type && s.IsPrimaryKey);

            string refCode = null;
            object refItem = null;
            if (null != refKeySets)
            {
                refItem = GetRefItem(type, refKeySets);
                XRow xRow = null;
                if (null != refItem)
                {
                    xRow = SelectXRow(type, refItem);
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
        private object GetRefItem(Type type, Dictionary<string, object> keyPropSets)
        {
            if (null == type || !keyPropSets.Any())
                return null;

            object refItem = Select(type).FirstOrDefault(delegate(object s)
            {
                foreach (var refKeySet in keyPropSets)
                {
                    object sValue = null;
                    PropertyInfo p = type.GetProperty(refKeySet.Key);
                    if (null != p)
                    {
                        sValue = p.GetValue(s);
                        if (sValue == null || !sValue.Equals(refKeySet.Value))
                            return false;
                    }
                    else
                        return false;
                }
                return true;
            });

            return refItem;
        }
        private XElement GetRefRow(string code)
        {
            XElement element = null;

            try
            {
                string[] nameParts = code.Split(new char[] { '.' });
                XFile file = OpenFileOrCreate(string.Format("{0}.xpag", nameParts[0]), onlyFile: true);
                if (file != null)
                {
                    element = file.Document.Root.Element("Rows").Elements("Row")
                        .FirstOrDefault(s => s.Attribute("code").Value == nameParts[1]);
                }
            }
            catch
            {
            }

            return element;
        }
        private Type GetRefType(string code)
        {
            string[] codeParts = code.Split('.');
            string[] files = System.IO.Directory.GetFiles(xodRoot, "*.xtab");
            foreach (var file in files)
            {
                XFile tableFile = OpenFileOrCreate(file);
                if (tableFile.Document.Root.Element("Pages").Elements("Page")
                    .Where(s => s.Value == codeParts[0] + ".xpag").Any())
                {
                    string typeString = System.IO.Path.GetFileNameWithoutExtension(file);
                    return this.propertyService.LoadedTypes.FirstOrDefault(s => s.FullName == typeString);
                }
            }
            return null;
        }
        private string GetRefPage(Type type, string code)
        {
            XFile tableFile = OpenFileOrCreate(type.FullName + ".xtab");
            foreach (var file in tableFile.Document.Root.Element("Pages").Elements("Page").Select(s => s.Value))
            {
                XFile pageFile = OpenFileOrCreate(file);
                if (pageFile.Document.Root.Element("Rows").Elements("Row").Where(delegate(XElement s)
                {
                    XAttribute sa = s.Attribute("code");
                    return null != sa && sa.Value == code;
                }).Any())
                {
                    return file.Replace(".xpag", "");
                }
            }
            return null;
        }
        private Type GetPageType(string xpagCode)
        {
            string[] files = System.IO.Directory.GetFiles(xodRoot, "*.xtab");
            foreach (var file in files)
            {
                XFile tableFile = OpenFileOrCreate(file);
                if (tableFile.Document.Root.Element("Pages").Elements("Page").Where(s => s.Value == xpagCode + ".xpag").Any())
                    return this.propertyService.LoadedTypes.FirstOrDefault(s => s.FullName == System.IO.Path.GetFileNameWithoutExtension(tableFile.Path));
            }
            return null;
        }

        private XRow SelectXRow(Type type, object item)
        {
            if (null == type || item == null)
                return null;

            XRow result = null;
            string docFileName = string.Format("{0}\\{1}.xtab", xodRoot, type.ToString());
            XFile tableFile = OpenFileOrCreate(docFileName, type, true);

            if (tableFile != null)
            {
                var props = this.propertyService.PropertyItems.Where(s => s.Type == type);
                string itemContents = string.Empty;
                Dictionary<string, object> itemKeys = null;
                if (props.Where(s => s.IsPrimaryKey).Any())
                    itemKeys = GetPrimaryValues(type, item);
                else
                    itemContents = BaseMarkup(OrderElement(Write(type, item, true)));

                var pages = tableFile.Document.Root.Element("Pages").Elements("Page").Select(s => OpenFileOrCreate(s.Value, typeof(TablePage)));
                foreach (var page in pages)
                {
                    XElement treeRow = null;
                    if (null != itemKeys)
                        treeRow = page.Document.Root.Element("Rows").Elements("Row").FirstOrDefault(delegate(XElement s)
                        {
                            return itemKeys.DictionaryEqual<string, object>(GetPrimaryNodes(type, s));
                        });
                    else
                        treeRow = page.Document.Root.Element("Rows").Elements("Row").FirstOrDefault(delegate(XElement s)
                        {
                            string e = BaseMarkup(OrderElement(s.Element(type.Name)));
                            return e == itemContents;
                        });

                    if (treeRow != null)
                    {
                        result = new XRow()
                        {
                            Table = tableFile,
                            Page = page,
                            Row = treeRow
                        };
                        break;
                    }
                }
            }

            return result;
        }
        private XRowTree FirstRowTree(Type type, object item)
        {
            if (item == null)
                return null;

            XRowTree result = null;
            string docFileName = string.Format("{0}\\{1}.xtab", xodRoot, type.ToString());
            XFile tableFile = OpenFileOrCreate(docFileName, type, true);

            if (tableFile != null)
            {
                string itemContents = BaseMarkup(Write(type, item, true));
                var pages = from row in tableFile.Document.Root.Element("Pages").Elements("Page") select OpenFileOrCreate(row.Value, typeof(TablePage));
                foreach (var page in pages)
                {
                    XElement xItem = null;
                    var primaryProps = this.propertyService.PropertyItems.Where(s => s.Type == type && s.IsPrimaryKey);
                    if (primaryProps.Any())
                        xItem = page.Document.Root.Element("Rows").Elements("Row").FirstOrDefault(s => TestAgainstXElement(type, item, s, primaryProps));
                    else
                        xItem = page.Document.Root.Element("Rows").Elements("Row").FirstOrDefault(s => BaseMarkup(s.Element(type.Name)) == itemContents);

                    if (xItem != null)
                    {
                        result = new XRowTree()
                        {
                            Table = tableFile,
                            Page = page,
                            Rows = new List<XElement> { xItem }
                        };
                        break;
                    }
                }
            }

            return result;
        }
        private List<XRowTree> RowTrees(Type type, object item)
        {
            if (item == null)
                return null;

            List<XRowTree> result = new List<XRowTree>();
            string docFileName = string.Format("{0}\\{1}.xtab", xodRoot, type.ToString());
            XFile tableFile = OpenFileOrCreate(docFileName, type, true);

            if (tableFile != null)
            {
                string itemContents = BaseMarkup(Write(type, item, true));
                var pages = from row in tableFile.Document.Root.Element("Pages").Elements("Page") select OpenFileOrCreate(row.Value, typeof(TablePage));
                foreach (var page in pages)
                {
                    List<XElement> xItems = null;
                    var primaryProps = this.propertyService.PropertyItems.Where(s => s.Type == type && s.IsPrimaryKey);
                    if (primaryProps.Any())
                        xItems = page.Document.Root.Element("Rows").Elements("Row").Where(s => TestAgainstXElement(type, item, s, primaryProps)).ToList();
                    else
                        xItems = page.Document.Root.Element("Rows").Elements("Row").Where(s => BaseMarkup(s.Element(type.Name)) == itemContents).ToList();

                    if (xItems != null)
                    {
                        result.Add(new XRowTree()
                        {
                            Table = tableFile,
                            Page = page,
                            Rows = xItems
                        });
                    }
                }
            }

            return result;
        }
        private bool TestAgainstXElement(Type type, object item, XElement xItem, IEnumerable<PropertyInfoItem> primaryProps)
        {
            foreach (var prop in primaryProps)
            {
                object pkv = prop.Property.GetValue(item);
                if (null != pkv)
                {
                    XElement elm = XElement.Parse(BaseMarkup(xItem.Element(type.Name)));
                    string pkvString = pkv.ToString();
                    if (prop.ValuePosition != ValuePosition.Body)
                    {
                        XAttribute valueXAtt = elm.Attribute(prop.PropertyName);
                        if (null == valueXAtt && pkvString != valueXAtt.Value)
                            return false;
                    }
                    else
                    {
                        XElement valueXElm = elm.Element(prop.PropertyName);
                        if (null == valueXElm || (prop.ValuePosition == ValuePosition.Body && pkvString != valueXElm.Value))
                            return false;
                    }
                }
            }
            return true;
        }
        private string BaseMarkup(XElement element)
        {
            if (element == null)
                return string.Empty;

            var baseElements = element.Elements().Where(delegate(XElement s)
            {
                if (null != s.Attribute("refType"))
                    return false;

                XAttribute collTypeAtt = s.Attribute("collType");
                if (null != collTypeAtt)
                {
                    XAttribute dataTypeAtt = s.Attribute("dataType");
                    return null != dataTypeAtt && (
                        this.propertyService.GetRefType(this.propertyService.LoadedTypes.FirstOrDefault(s2 => s2.FullName == dataTypeAtt.Value)).HasFlag(PropertyTypeCategory.ValueTypeArray) ||
                        this.propertyService.GetRefType(this.propertyService.LoadedTypes.FirstOrDefault(s2 => s2.FullName == dataTypeAtt.Value)).HasFlag(PropertyTypeCategory.ValueTypeCollection));
                }

                return true;
            });

            XElement resultElm = new XElement(element.Name, element.Attributes());
            foreach (var e in baseElements)
                resultElm.Add(e);

            return resultElm.ToString();
        }
        private XElement OrderElement(XElement xElement)
        {
            XElement result = new XElement(xElement.Name, xElement.Attributes());
            var ordered = xElement.Elements().OrderBy(s => s.Name.LocalName);
            foreach (var elm in ordered)
            {
                if (elm.HasElements)
                    result.Add(OrderElement(elm));
                else
                    result.Add(elm);
            }
            return result;
        }

        private XFile OpenFileOrCreate<T>(string fileName, bool onlyFile = false, bool autoSave = false)
        {
            return OpenFileOrCreate(fileName, typeof(T), onlyFile, autoSave);
        }
        private XFile OpenFileOrCreate(string fileName, Type type = null, bool onlyFile = false, bool autoSave = false)
        {
            var files = cachedFiles.ToArray();
            XFile file = files.FirstOrDefault(s => s.Path.EndsWith(fileName));
            if (xodRoot != null && !fileName.StartsWith(xodRoot))
                fileName = string.Format("{0}\\{1}", xodRoot, fileName);

            if (file != null)
                return file;

            if (File.Exists(fileName))
            {
                if (string.IsNullOrEmpty(this.Password))
                    file = XFile.Load(fileName, type);
                else
                    file = XFile.Load(fileName, type, this.Password);
            }
            else if (!onlyFile)
            {
                file = new XFile(new XDocument(Write(type, Activator.CreateInstance(type))), fileName, type, this.Password);
                if (autoSave)
                    file.Save(fileName);
            }

            if (file != null)
            {
                if (cachedFiles.Count >= CACHE_SIZE)
                    cachedFiles.RemoveAt(0);

                file.Changed += file_Changed;
                cachedFiles.Add(file);
            }
            return file;
        }

        private void file_Changed(object sender, EventArgs e)
        {
            XFile file = (XFile)sender;
            XFile old = cachedFiles.FirstOrDefault(s => s.Path.Equals(file.Path));
            if (null != old)
            {
                cachedFiles.Remove(old);
                cachedFiles.Add(file);
            }
        }


        #region IXodEngine Implementation

        public event EventHandler<TriggerEventArgs> BeforeAction;
        public event EventHandler<TriggerEventArgs> AfterAction;

        public string Path { get; private set; }
        public bool IsNew { get; private set; }
        public bool LazyLoad { get; set; }

        public IEnumerable<object> Select(Type type, bool lazyLoad = false)
        {
            this.opsCacheService.Clear();
            this.propertyService.LoadType(type);
            lazyLoad = (lazyLoad) ? true : LazyLoad;
            IEnumerable<object> result = null;
            string docFileName = string.Format("{0}.xtab", type.ToString());
            XFile tableFile = OpenFileOrCreate(docFileName, type, true);
            if (tableFile != null)
            {
                var allRows = tableFile.Document.Root.Element("Pages").Elements("Page")
                    .Where(s => this.fileService.FileExists(s.Value))
                    .SelectMany(s => OpenFileOrCreate<TablePage>(s.Value, true).Document.Root.Element("Rows").Elements("Row"));
                result = allRows.Select(s => Read(type, s.Element(type.Name), lazyLoad));
            }

            if (null == result)
                return Enumerable.Empty<object>();

            return result;
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
            object refItem = GetRefItem(type, primValues);
            if (null != refItem)
                Update(type, refItem, item, filter);
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
                throw new ArgumentNullException();

            var primProps = propertyService.PropertyItems.Where(s => s.Type == type && s.IsPrimaryKey);
            if (!primProps.Any())
                throw new MissingPrimaryKeyValueException();

            object old = Select(type).FirstOrDefault(delegate(object oldItem)
            {
                foreach (var primProp in primProps)
                {
                    object ov = primProp.Property.GetValue(oldItem);
                    object nv = primProp.Property.GetValue(item);
                    if (!ov.Equals(nv))
                        return false;
                }
                return true;
            });
            if (null != old)
                return Update(type, old, item, filter);

            return false;
        }
        public bool Delete(Type type, object item)
        {
            this.propertyService.LoadType(type);
            if (null == item)
                throw new ArgumentNullException();

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

            XRowTree rowTree = FirstRowTree(type, item);
            if (rowTree != null)
            {
                foreach (var row in rowTree.Rows)
                {
                    row.Remove();
                    if (rowTree.Page.Document.Root.Elements("Rows") == null)
                    {
                        File.Delete(rowTree.Page.Path);
                        XElement tablePage = rowTree.Table.Document.Root.Element("Pages").Elements("Page")
                                                .FirstOrDefault(s => rowTree.Page.Path.EndsWith(s.Value));

                        if (tablePage != null)
                        {
                            tablePage.Remove();
                            File.Delete(rowTree.Page.Path);

                            if (rowTree.Table.Document.Root.Element("Pages").Elements("Page") == null)
                                File.Delete(rowTree.Table.Path);
                        }
                        value = true;
                    }
                    else
                    {
                        if (rowTree.Page.Size() < PAGE_SIZE)
                        {
                            XAttribute fullAtt = rowTree.Page.Document.Root.Attribute("full");
                            if (null != fullAtt)
                                fullAtt.Value = "false";
                        }
                        rowTree.Page.Save();
                    }

                    //Delete all applicable reference items
                    var propRefProps = this.propertyService.PropertyItems.Where(s =>
                        s.Type == type &&
                        s.ReferenceType != PropertyReferenceType.Parent &&
                        s.ReferenceType != PropertyReferenceType.SelfForeign &&
                            (s.Cascade.HasFlag(CascadeOptions.Delete) &&
                            s.TypeCategory != PropertyTypeCategory.None)).ToArray();

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

                    //Delete item reference from parents
                    var parRefProps = this.propertyService.PropertyItems.Where(s =>
                        s.Type == type &&
                        s.ReferenceType == PropertyReferenceType.Parent);

                    var itemPrmVals = GetPrimaryValues(type, item);
                    if (null == itemPrmVals)
                        continue;

                    foreach (var parRefProp in parRefProps)
                    {
                        object parent = parRefProp.Property.GetValue(item);
                        if (parent == null)
                            continue;

                        var chdRefProps = this.propertyService.PropertyItems.Where(s =>
                            s.Type == parRefProp.PropertyType &&
                            s.ReferenceType == PropertyReferenceType.Children &&
                            s.CollectionItemType == type);

                        bool parentUpdate = false;

                        foreach (var chdRefProp in chdRefProps)
                        {
                            var chdRefItem = chdRefProp.Property.GetValue(parent);

                            if (null == chdRefItem)
                                continue;

                            Type childType = null;
                            if (chdRefProp.TypeCategory == PropertyTypeCategory.Array)
                                childType = chdRefProp.PropertyType.GetElementType();
                            else if (chdRefProp.TypeCategory == PropertyTypeCategory.GenericCollection)
                                childType = chdRefProp.PropertyType.GetGenericArguments().FirstOrDefault();

                            if (null == childType)
                                continue;

                            MethodInfo dmi = chdRefProp.PropertyType.GetMethod("Remove");
                            var children = from child in chdRefItem as IEnumerable<object> where itemPrmVals.DictionaryEqual(GetPrimaryValues(childType, child)) select child;
                            if (children.Any())
                            {
                                dmi.Invoke(chdRefItem, new object[] { children.FirstOrDefault() });
                                parentUpdate = true;
                                chdRefProp.Property.SetValue(parent, chdRefItem);
                            }
                        }
                        if (parentUpdate)
                            Update(parRefProp.PropertyType, parent);
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

            string docFileName = string.Format("{0}\\{1}.xtab", xodRoot, type.ToString());
            XFile tableFile = OpenFileOrCreate(docFileName, type, true, false);

            if (tableFile != null)
            {
                //go through all pages and delete all node of this type unless
                //it's attributed as required/parent for other types then raise
                //an exception

                var pages = from row in tableFile.Document.Root.Element("Pages").Elements("Page") select OpenFileOrCreate(row.Value, typeof(TablePage));
                foreach (var page in pages)
                {
                    if (cachedFiles.Contains(page))
                        cachedFiles.Remove(page);
                    page.Delete();
                }

                if (cachedFiles.Contains(tableFile))
                    cachedFiles.Remove(tableFile);
                tableFile.Delete();

                this.propertyService.UnloadType(type);
                this.autoNumberService.Remove(type);
                this.propertyService.LoadedTypes.Remove(type);
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
            var files = System.IO.Directory.GetFiles(xodRoot, "*.xtab");
            foreach (var file in files)
                types.Add(Type.GetType(System.IO.Path.GetFileNameWithoutExtension(file)));

            return types;
        }
        public void RegisterType(Type type)
        {
            this.propertyService.LoadType(type);
        }
        public void Dispose()
        {
            this.opsCacheService = null;
            this.securityService = null;
            this.propertyService = null;
            this.autoNumberService = null;
        }

        #endregion

    }
}