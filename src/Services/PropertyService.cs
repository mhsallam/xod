using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Xod.Services
{
    internal class PropertyService
    {
        private static readonly object locker = new object();
        private static Dictionary<string, List<PropertyInfoItem>> properties = null;

        string[] keywords = { "dataType", "collType", "refType", "hostProp" };

        internal PropertyService()
        {
            if (properties == null)
            {
                lock (locker)
                {
                    if(properties == null)
                        properties = new Dictionary<string, List<PropertyInfoItem>>();
                }
            }
        }

        internal void LoadType(Type type)
        {
            if (type == null || type == typeof(object))
                return;

            List<PropertyInfoItem> typeProps = null;
            List<PropertyInfo> props = null;
            List<Type> innerTypes = null;

            //double-check lock pattern; optemized thread-safe
            if (!properties.ContainsKey(type.FullName) && GetPropertyTypeCategory(type) == PropertyTypeCategory.Class)
            {
                lock (locker)
                {
                    innerTypes = new List<Type>();

                    if (!properties.ContainsKey(type.FullName) && GetPropertyTypeCategory(type) == PropertyTypeCategory.Class)
                    {
                        typeProps = new List<PropertyInfoItem>();

                        // props = System.ComponentModel.TypeDescriptor.GetProperties(type.GetProperties);
                        props = type.GetProperties().Where(s => s.GetAccessors(false).Any()).ToList();
                        foreach (var prop in props)
                        {
                            var atts = prop.GetCustomAttributes(false).ToArray();
                            if (atts.OfType<NotMappedAttribute>().Any())
                                continue;

                            PropertyTypeCategory propTypeCategory = GetPropertyTypeCategory(prop.PropertyType);
                            PropertyInfoItem propInfoItem = new PropertyInfoItem()
                            {
                                Type = type,
                                TypeCategory = propTypeCategory,
                                Property = prop,
                                PropertyName = prop.Name,
                                PropertyType = prop.PropertyType,
                                IsGenericType = prop.PropertyType == typeof(object),
                                IsReadOnly = !prop.CanWrite
                            };

                            var primaryKeyAtt = atts.OfType<PrimaryKeyAttribute>().FirstOrDefault();
                            propInfoItem.IsPrimaryKey = null != primaryKeyAtt;

                            var foreignKeyAtts = atts.OfType<ForeignKeyAttribute>();
                            if (foreignKeyAtts.Any())
                                propInfoItem.ForeignKeys = foreignKeyAtts.Cast<ForeignKeyAttribute>().ToList();

                            var parentKeyAtts = atts.OfType<ParentKeyAttribute>();
                            if (parentKeyAtts.Any())
                                propInfoItem.ParentKeys = parentKeyAtts.Cast<ParentKeyAttribute>().ToList();

                            PropertyAttribute propertyAtt = atts.OfType<PropertyAttribute>().FirstOrDefault();
                            if (null != propertyAtt)
                            {
                                propInfoItem.Cascade = propertyAtt.Cascade;
                                propInfoItem.IsAutonumber = propertyAtt.AutoNumber;
                                //propInfoItem.ForceAutoNumber = propertyAtt.OverrideAutoNumber;
                                propInfoItem.IsIndexed = propertyAtt.Indexed;
                                propInfoItem.ValuePosition = propertyAtt.Position;
                                propInfoItem.IdentityIncrement = propertyAtt.IdentityIncrement;
                                propInfoItem.IdentitySeed = propertyAtt.IdentitySeed;
                            }

                            RequiredAttribute requiredAtt = atts.OfType<RequiredAttribute>().FirstOrDefault();
                            propInfoItem.IsRequired = null != requiredAtt;

                            UniqueKeyAttribute uniqueKeyAtt = atts.OfType<UniqueKeyAttribute>().FirstOrDefault();
                            propInfoItem.IsUnique = null != uniqueKeyAtt;

                            MarkupAttribute markupAtt = atts.OfType<MarkupAttribute>().FirstOrDefault();
                            propInfoItem.IsMarkup = null != markupAtt;

                            CryptoAttribute cryptoAtt = atts.OfType<CryptoAttribute>().FirstOrDefault();
                            propInfoItem.Encryption = (null != cryptoAtt) ? cryptoAtt.Method : CryptoMethod.None;

                            ChildrenAttribute childrenAtt = atts.OfType<ChildrenAttribute>().FirstOrDefault();
                            //InheritedAttribute inheritedAtt = (InheritedAttribute)atts
                            //    .FirstOrDefault(s => s.GetType() == typeof(InheritedAttribute));
                            if (null != childrenAtt)
                            {
                                propInfoItem.ReferenceType = PropertyReferenceType.Children;
                                propInfoItem.Cascade = CascadeOptions.Delete;
                                propInfoItem.ChildParentProperty = childrenAtt.RemoteParentProperty;
                            }

                            GenericTypePropertyAttribute genericTypeAtt = atts.OfType<GenericTypePropertyAttribute>().FirstOrDefault();
                            if (prop.PropertyType == typeof(object) && null != genericTypeAtt)
                                propInfoItem.GenericTypeProperty = genericTypeAtt.Name;

                            //setting reference type
                            if (propInfoItem.ReferenceType != PropertyReferenceType.Children)
                            {
                                if (propTypeCategory == PropertyTypeCategory.None)
                                    propInfoItem.ReferenceType = PropertyReferenceType.None;
                                else if (foreignKeyAtts.Any())
                                {
                                    if (prop.PropertyType.GetProperties()
                                        .Where(s =>
                                            s.PropertyType == type &&
                                            null != s.GetCustomAttribute<ForeignKeyAttribute>(false)).Any())
                                        propInfoItem.ReferenceType = PropertyReferenceType.SelfForeign;
                                    else
                                        propInfoItem.ReferenceType = PropertyReferenceType.Foreign;
                                }
                                else if (parentKeyAtts.Any())
                                    propInfoItem.ReferenceType = PropertyReferenceType.Parent;
                                else
                                {
                                    propInfoItem.ReferenceType = PropertyReferenceType.Reference;

                                    // PropertyDescriptorCollection propTypeProps = TypeDescriptor.GetProperties(prop.PropertyType);
                                    var propTypeProps = type.GetProperties().Where(s => s.GetAccessors(false).Any()).ToList();
                                    
                                    System.Collections.IEnumerator propTypePropsItems = propTypeProps.GetEnumerator();
                                    foreach (var propTypeProp in propTypeProps)
                                    {
                                        var propTypePropAtts = propTypeProp.GetCustomAttributes(false).ToArray();
                                        if (propTypePropAtts.OfType<PrimaryKeyAttribute>().Any())
                                        {
                                            propInfoItem.ReferenceType = PropertyReferenceType.Complex;
                                            propInfoItem.Cascade = CascadeOptions.Delete;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (propTypeCategory == PropertyTypeCategory.Array)
                                propInfoItem.CollectionItemType = prop.PropertyType.GetElementType();
                            else if (propTypeCategory == PropertyTypeCategory.GenericCollection)
                                propInfoItem.CollectionItemType = prop.PropertyType.GetGenericArguments().FirstOrDefault();

                            typeProps.Add(propInfoItem);

                            if (prop.PropertyType != type && (
                                propTypeCategory == PropertyTypeCategory.Class ||
                                propTypeCategory == PropertyTypeCategory.Array ||
                                propTypeCategory == PropertyTypeCategory.GenericCollection))
                            {
                                if (prop.PropertyType.IsArray && prop.PropertyType.GetArrayRank() == 1)
                                    innerTypes.Add(prop.PropertyType.GetElementType());
                                else if (null != prop.PropertyType.GetTypeInfo().GetInterface("ICollection"))
                                    innerTypes.Add(prop.PropertyType.GetGenericArguments().FirstOrDefault());
                                else if (prop.PropertyType.GetTypeInfo().IsClass)
                                    innerTypes.Add(prop.PropertyType);
                            }
                        }

                        properties.Add(type.FullName, typeProps);

                        //if there is no PrimaryKey find a property with name Id and make it PrimaryKey
                        if (!typeProps.Any(s => s.IsPrimaryKey))
                        {
                            var primaryKeyProperty = typeProps.FirstOrDefault(s => s.PropertyName == "Id");
                            if (primaryKeyProperty != null)
                            {
                                primaryKeyProperty.IsPrimaryKey = true;
                                if (primaryKeyProperty.PropertyType != typeof(string))
                                    primaryKeyProperty.IsAutonumber = true;
                            }
                        }
                    }
                }

                //after loading all PropertyInfoItems validate them
                CheckReservedKeywords(type);

                //load types of inner reference type properties
                foreach (var innerType in innerTypes)
                    LoadType(innerType);

            }

            //else if (properties.ContainsKey(type.FullName))
            //{
            //    typeProps = Properties(type.FullName).ToList();
            //    props = System.ComponentModel.TypeDescriptor.GetProperties(type);
            //    foreach (PropertyDescriptor prop in props)
            //    {
            //        var propItems = typeProps.Select(s => s.Property).ToArray();
            //        if (propItems.Contains(prop))
            //            continue;

            //        var refType = GetPropertyTypeCategory(prop.PropertyType);
            //        if (refType == PropertyTypeCategory.Class ||
            //            refType == PropertyTypeCategory.Array ||
            //            refType == PropertyTypeCategory.GenericCollection)
            //        {
            //            if (prop.PropertyType.IsArray && prop.PropertyType.GetArrayRank() == 1)
            //                LoadType(prop.PropertyType.GetElementType());
            //            else if (null != prop.PropertyType.GetInterface("ICollection"))
            //                LoadType(prop.PropertyType.GetGenericArguments().FirstOrDefault());
            //            else if (prop.PropertyType.IsClass)
            //                LoadType(prop.PropertyType);
            //        }
            //    }
            //}
        }

        //internal void UnloadType(Type type)
        //{
        //    if (properties == null)
        //        return;

        //    var typeItems = properties.FirstOrDefault(s => s.Key.Equals(type.FullName));
        //    if (!typeItems.Equals(null) && typeItems.Value != null)
        //        properties.Remove(typeItems.Key);
        //}

        internal IEnumerable<PropertyInfoItem> Properties(string key)
        {
            if (properties == null)
                return new PropertyInfoItem[] { };

            var t = properties.FirstOrDefault(s => s.Key.Equals(key));
            if (!t.Equals(null) && t.Value != null)
                return t.Value.Where(s => !s.IsReadOnly && !s.IsNotMapped).ToList();
            else
                return Enumerable.Empty<PropertyInfoItem>();
        }
        internal Type RegisteredType(string key)
        {
            if (properties == null)
                return null;

            var t = properties.FirstOrDefault(s => s.Key.Equals(key));
            if (!t.Equals(null) && t.Value != null)
            {
                var fp = t.Value.FirstOrDefault();
                if (fp != null)
                    return fp.Type;
            }

            return null;
        }
        internal Type RegisteredTypeByName(string name)
        {
            if (properties == null)
                return null;

            var t = properties.FirstOrDefault(delegate(KeyValuePair<string, List<PropertyInfoItem>> s) {
                string[] nameParts = s.Key.Split('.');
                return nameParts != null && nameParts.Length > 0 && nameParts.Last().Equals(name);
            });

            if (!t.Equals(null) && t.Value != null)
            {
                var fp = t.Value.FirstOrDefault();
                if (fp != null)
                    return fp.Type;
            }

            return null;
        }
        internal PropertyTypeCategory GetPropertyTypeCategory(Type type)
        {
            Type childType = null;
            if (null == type)
                return PropertyTypeCategory.None;
            if (type.IsArray && type.GetArrayRank() == 1)
            {
                childType = type.GetElementType();
                if (childType.GetTypeInfo().IsClass && childType != typeof(string) && childType != typeof(DateTime))
                    return PropertyTypeCategory.Array;
                else
                    return PropertyTypeCategory.ValueTypeArray;
            }
            if (null != type.GetTypeInfo().GetInterface("ICollection"))
            {
                childType = type.GetGenericArguments().FirstOrDefault();
                if (childType.GetTypeInfo().IsClass && childType != typeof(string) && childType != typeof(DateTime))
                    return PropertyTypeCategory.GenericCollection;
                else
                    return PropertyTypeCategory.ValueTypeCollection;
            }
            if (type.GetTypeInfo().IsClass && type != typeof(string) && type != typeof(DateTime))
                return PropertyTypeCategory.Class;

            return PropertyTypeCategory.None;
        }

        internal void CheckReservedKeywords(Type type)
        {
            var primaryProps = Properties(type.FullName)
                .Where(s => s.IsPrimaryKey && IsReservedKeyword(s.PropertyName));
            if (primaryProps.Any())
                throw new ReservedKeyWordException();

            //var foreignProps = Properties(type.FullName).Where(s => s.ReferenceType == PropertyReferenceType.Foreign);
            //foreach (var foreignProp in foreignProps)
            //    if (null != foreignProp.ForeignKeys)
            //        foreach (var foreignAtt in foreignProp.ForeignKeys)
            //            if (IsReservedKeyword(foreignAtt.RemoteProperty))
            //                throw new ReservedKeyWordException();

            //var parentProps = Properties(type.FullName).Where(s => s.ReferenceType == PropertyReferenceType.Parent);
            //foreach (var parentProp in parentProps)
            //    if (null != parentProp.ParentKeys)
            //        foreach (var parentAtt in parentProp.ParentKeys)
            //            if (IsReservedKeyword(parentAtt.RemoteProperty))
            //                throw new ReservedKeyWordException();
        }
        internal bool IsReservedKeyword(string keyword)
        {
            return keywords.Contains(keyword);
        }
    }
}
