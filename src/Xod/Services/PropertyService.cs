using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;

namespace Xod.Services
{
    internal class PropertyService
    {
        internal List<Type> LoadedTypes { get; set; }
        string[] keywords = { "dataType", "collType", "refType", "hostProp" };
        internal List<PropertyInfoItem> PropertyItems { get; set; }

        internal PropertyService()
        {
            this.PropertyItems = new List<PropertyInfoItem>();
            this.LoadedTypes = new List<Type>();
        }
        internal void LoadType(Type type)
        {
            if (type == null || type == typeof(object))
                return;

            List<PropertyInfoItem> typeProps = new List<PropertyInfoItem>();

            if (!this.LoadedTypes.Contains(type) && GetRefType(type) != PropertyTypeCategory.None)
            {
                this.LoadedTypes.Add(type);
                List<Type> innerTypes = new List<Type>();
                var props = type.GetProperties().Where(s => s.GetAccessors(false).Any());
                foreach (var prop in props)
                {
                    var atts = prop.GetCustomAttributes(false);
                    NotMappedAttribute notMappedAtt = (NotMappedAttribute)atts
                        .FirstOrDefault(s => s.GetType() == typeof(NotMappedAttribute));
                    if (null != notMappedAtt)
                        continue;

                    PropertyTypeCategory propTypeCategory = GetRefType(prop.PropertyType);
                    PropertyInfoItem propInfoItem = new PropertyInfoItem()
                    {
                        Type = type,
                        TypeCategory = propTypeCategory,
                        Property = prop,
                        PropertyName = prop.Name,
                        PropertyType = prop.PropertyType,
                        IsGenericType = prop.PropertyType == typeof(object)
                    };

                    var primaryKeyAtt = atts.FirstOrDefault(s => s.GetType() == typeof(PrimaryKeyAttribute));
                    propInfoItem.IsPrimaryKey = null != primaryKeyAtt;

                    var foreignKeyAtts = atts.Where(s => s.GetType() == typeof(ForeignKeyAttribute));
                    if (foreignKeyAtts.Any())
                        propInfoItem.ForeignKeys = foreignKeyAtts.Cast<ForeignKeyAttribute>().ToList();

                    var parentKeyAtts = atts.Where(s => s.GetType() == typeof(ParentKeyAttribute));
                    if (parentKeyAtts.Any())
                        propInfoItem.ParentKeys = parentKeyAtts.Cast<ParentKeyAttribute>().ToList();

                    PropertyAttribute propertyAtt = (PropertyAttribute)atts
                        .FirstOrDefault(s => s.GetType() == typeof(PropertyAttribute));
                    if (null != propertyAtt)
                    {
                        propInfoItem.Cascade = propertyAtt.Cascade;
                        propInfoItem.IsAutoNumber = propertyAtt.AutoNumber;
                        propInfoItem.ForceAutoNumber = propertyAtt.OverrideAutoNumber;
                        propInfoItem.IsIndexed = propertyAtt.Indexed;
                        propInfoItem.ValuePosition = propertyAtt.Position;
                        propInfoItem.IdentityIncrement = propertyAtt.IdentityIncrement;
                        propInfoItem.IdentitySeed = propertyAtt.IdentitySeed;
                    }

                    RequiredAttribute requiredAtt = (RequiredAttribute)atts
                        .FirstOrDefault(s => s.GetType() == typeof(RequiredAttribute));
                    propInfoItem.IsRequired = null != requiredAtt;

                    UniqueKeyAttribute uniqueKeyAtt = (UniqueKeyAttribute)atts
                        .FirstOrDefault(s => s.GetType() == typeof(UniqueKeyAttribute));
                    propInfoItem.IsUnique = null != uniqueKeyAtt;

                    MarkupAttribute markupAtt = (MarkupAttribute)atts
                        .FirstOrDefault(s => s.GetType() == typeof(MarkupAttribute));
                    propInfoItem.IsMarkup = null != markupAtt;

                    CryptoAttribute cryptoAtt = (CryptoAttribute)atts
                        .FirstOrDefault(s => s.GetType() == typeof(CryptoAttribute));
                    propInfoItem.Encryption = (null != cryptoAtt) ? cryptoAtt.Method : CryptoMethod.None;

                    ChildrenAttribute childrenAtt = (ChildrenAttribute)atts
                        .FirstOrDefault(s => s.GetType() == typeof(ChildrenAttribute));
                    //InheritedAttribute inheritedAtt = (InheritedAttribute)atts
                    //    .FirstOrDefault(s => s.GetType() == typeof(InheritedAttribute));
                    if (null != childrenAtt)
                    {
                        propInfoItem.ReferenceType = PropertyReferenceType.Children;
                        propInfoItem.Cascade = CascadeOptions.Delete;
                        propInfoItem.ChildParentProperty = childrenAtt.RemoteParentProperty;
                    }

                    GenericTypePropertyAttribute genericTypeAtt = (GenericTypePropertyAttribute)atts
                        .FirstOrDefault(s => s.GetType() == typeof(GenericTypePropertyAttribute));
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
                            if (!atts.Any() && !prop.PropertyType.GetProperties()
                                .Where(s => null != s.GetCustomAttribute<PrimaryKeyAttribute>(false)).Any())
                            {
                                propInfoItem.ReferenceType = PropertyReferenceType.Complex;
                                propInfoItem.Cascade = CascadeOptions.Delete;
                            }
                            else
                                propInfoItem.ReferenceType = PropertyReferenceType.Reference;

                        }
                    }

                    if (propTypeCategory == PropertyTypeCategory.Array)
                        propInfoItem.CollectionItemType = prop.PropertyType.GetElementType();
                    else if (propTypeCategory == PropertyTypeCategory.GenericCollection)
                        propInfoItem.CollectionItemType = prop.PropertyType.GetGenericArguments().FirstOrDefault();

                    typeProps.Add(propInfoItem);

                    if (propTypeCategory == PropertyTypeCategory.Class ||
                        propTypeCategory == PropertyTypeCategory.Array ||
                        propTypeCategory == PropertyTypeCategory.GenericCollection)
                    {
                        if (prop.PropertyType.IsArray && prop.PropertyType.GetArrayRank() == 1)
                            innerTypes.Add(prop.PropertyType.GetElementType());
                        else if (null != prop.PropertyType.GetInterface("ICollection"))
                            innerTypes.Add(prop.PropertyType.GetGenericArguments().FirstOrDefault());
                        else if (prop.PropertyType.IsClass)
                            innerTypes.Add(prop.PropertyType);
                    }
                }

                this.PropertyItems.AddRange(typeProps);

                foreach (var innerType in innerTypes)
                    LoadType(innerType);

                CheckReservedKeywords(type);
            }
            else if (this.LoadedTypes.Contains(type))
            {
                typeProps = PropertyItems.Where(s => s.Type == type).ToList();

                var props = type.GetProperties().Where(s => s.GetAccessors(false).Any());
                foreach (var prop in props)
                {
                    var propItems = typeProps.Select(s => s.Property).ToArray();
                    if (propItems.Contains(prop))
                        continue;

                    var refType = GetRefType(prop.PropertyType);
                    if (refType == PropertyTypeCategory.Class ||
                        refType == PropertyTypeCategory.Array ||
                        refType == PropertyTypeCategory.GenericCollection)
                    {
                        if (prop.PropertyType.IsArray && prop.PropertyType.GetArrayRank() == 1)
                            LoadType(prop.PropertyType.GetElementType());
                        else if (null != prop.PropertyType.GetInterface("ICollection"))
                            LoadType(prop.PropertyType.GetGenericArguments().FirstOrDefault());
                        else if (prop.PropertyType.IsClass)
                            LoadType(prop.PropertyType);
                    }
                }

            }

            if (!typeProps.Any(s => s.IsPrimaryKey))
            {
                var primaryKeyProperty = typeProps.FirstOrDefault(s => s.PropertyName == "Id");
                if (primaryKeyProperty != null)
                {
                    primaryKeyProperty.IsPrimaryKey = true;
                    primaryKeyProperty.IsAutoNumber = true;
                }
            }
        }
        internal void UnloadType(Type type)
        {
            this.PropertyItems.RemoveAll(s => s.Type == type);
            this.LoadedTypes.Remove(type);
        }

        internal PropertyTypeCategory GetRefType(Type type)
        {
            Type childType = null;
            if (null == type)
                return PropertyTypeCategory.None;
            if (type.IsArray && type.GetArrayRank() == 1)
            {
                childType = type.GetElementType();
                if (childType.IsClass && childType != typeof(string) && childType != typeof(DateTime))
                    return PropertyTypeCategory.Array;
                else
                    return PropertyTypeCategory.ValueTypeArray;
            }
            if (null != type.GetInterface("ICollection"))
            {
                childType = type.GetGenericArguments().FirstOrDefault();
                if (childType.IsClass && childType != typeof(string) && childType != typeof(DateTime))
                    return PropertyTypeCategory.GenericCollection;
                else
                    return PropertyTypeCategory.ValueTypeCollection;
            }
            if (type.IsClass && type != typeof(string) && type != typeof(DateTime))
                return PropertyTypeCategory.Class;

            return PropertyTypeCategory.None;
        }
        internal PropertyTypeCategory GetRefType(string typeName)
        {
            var item = this.PropertyItems.FirstOrDefault(s => s.Type.FullName == typeName);
            if (null != item)
                return GetRefType(item.Type);
            else
                return PropertyTypeCategory.None;
        }

        internal void CheckReservedKeywords(Type type)
        {
            var primaryProps = this.PropertyItems.Where(s => s.Type == type && s.IsPrimaryKey).Select(s => s.Property);
            foreach (var primaryProp in primaryProps)
                if (IsReservedKeyword(primaryProp.Name))
                    throw new ReservedKeyWordException();

            var foreignProps = this.PropertyItems.Where(s => s.Type == type && s.ReferenceType == PropertyReferenceType.Foreign);
            foreach (var foreignProp in foreignProps)
                if (null != foreignProp.ForeignKeys)
                    foreach (var foreignAtt in foreignProp.ForeignKeys)
                        if (IsReservedKeyword(foreignAtt.RemoteProperty))
                            throw new ReservedKeyWordException();

            var parentProps = this.PropertyItems.Where(s => s.Type == type && s.ReferenceType == PropertyReferenceType.Parent);
            foreach (var parentProp in parentProps)
                if (null != parentProp.ParentKeys)
                    foreach (var parentAtt in parentProp.ParentKeys)
                        if (IsReservedKeyword(parentAtt.RemoteProperty))
                            throw new ReservedKeyWordException();

            //var propProps = type.GetProperties().Where(s => s.GetCustomAttributes<PropertyAttribute>(false).Any());
            //foreach (var prop in propProps)
            //    foreach (var propAtt in prop.GetCustomAttributes<PropertyAttribute>(false))
            //        if (propAtt.Position == ValuePosition.Attribute && IsReservedKeyword(prop.Name))
            //            throw new ReservedKeyWordException();
        }
        internal bool IsReservedKeyword(string keyword)
        {
            return keywords.Contains(keyword);
        }
    }
}
