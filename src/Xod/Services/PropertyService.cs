using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Xod.Helpers;

namespace Xod.Services
{
    internal class PropertyService
    {
        private static readonly object locker = new object();
        private static readonly ConcurrentDictionary<string, List<PropertyInfoItem>> properties = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, TypeMetadata> metadataCache = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, Type> registeredTypes = new(StringComparer.Ordinal);

        private readonly string[] keywords = { "dataType", "collType", "refType", "hostProp" };

        internal void LoadType(Type type)
        {
            if (type == null || type == typeof(object) || ValueHelper.IsScalarType(type))
                return;

            string fullName = type.FullName;
            if (string.IsNullOrEmpty(fullName))
                return;

            registeredTypes[fullName] = type;

            if (!properties.ContainsKey(fullName) && GetPropertyTypeCategory(type) == PropertyTypeCategory.Class)
            {
                var innerTypes = new List<Type>();

                lock (locker)
                {
                    if (!properties.ContainsKey(fullName) && GetPropertyTypeCategory(type) == PropertyTypeCategory.Class)
                    {
                        var typeProps = new List<PropertyInfoItem>();
                        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Where(s => s.GetIndexParameters().Length == 0 && s.GetMethod != null)
                            .ToList();

                        foreach (var prop in props)
                        {
                            var atts = prop.GetCustomAttributes(true).ToArray();
                            if (atts.OfType<NotMappedAttribute>().Any())
                                continue;

                            PropertyTypeCategory propTypeCategory = GetPropertyTypeCategory(prop.PropertyType);
                            var propInfoItem = new PropertyInfoItem
                            {
                                Type = type,
                                TypeCategory = propTypeCategory,
                                Property = prop,
                                PropertyName = prop.Name,
                                PropertyType = prop.PropertyType,
                                IsGenericType = prop.PropertyType == typeof(object),
                                IsReadOnly = !prop.CanWrite,
                                Getter = CompileGetter(type, prop),
                                Setter = CompileSetter(type, prop),
                                ScalarKind = ClassifyScalarKind(prop.PropertyType),
                                Utf8NameBytes = System.Text.Encoding.UTF8.GetBytes(prop.Name),
                                Utf8OpenTag = System.Text.Encoding.UTF8.GetBytes($"<{prop.Name}>"),
                                Utf8CloseTag = System.Text.Encoding.UTF8.GetBytes($"</{prop.Name}>")
                            };
                            PopulateTypedAccessors(type, prop, propInfoItem);

                            propInfoItem.IsPrimaryKey = atts.OfType<PrimaryKeyAttribute>().Any();

                            var foreignKeyAtts = atts.OfType<ForeignKeyAttribute>().ToList();
                            if (foreignKeyAtts.Count > 0)
                                propInfoItem.ForeignKeys = foreignKeyAtts;

                            var parentKeyAtts = atts.OfType<ParentKeyAttribute>().ToList();
                            if (parentKeyAtts.Count > 0)
                                propInfoItem.ParentKeys = parentKeyAtts;

                            var propertyAtt = atts.OfType<PropertyAttribute>().FirstOrDefault();
                            if (propertyAtt != null)
                            {
                                propInfoItem.Cascade = propertyAtt.Cascade;
                                propInfoItem.IsAutonumber = propertyAtt.AutoNumber;
                                propInfoItem.IsIndexed = propertyAtt.Indexed;
                                propInfoItem.ValuePosition = propertyAtt.Position;
                                propInfoItem.IdentityIncrement = propertyAtt.IdentityIncrement;
                                propInfoItem.IdentitySeed = propertyAtt.IdentitySeed;
                                propInfoItem.IntSeed = propertyAtt.IdentitySeed;
                                propInfoItem.IntIncrement = propertyAtt.IdentityIncrement;
                            }

                            propInfoItem.IsRequired = atts.OfType<RequiredAttribute>().Any();
                            propInfoItem.IsUnique = atts.OfType<UniqueKeyAttribute>().Any();
                            if (propInfoItem.IsPrimaryKey || propInfoItem.IsUnique)
                                propInfoItem.IsIndexed = true;

                            propInfoItem.IsMarkup = atts.OfType<MarkupAttribute>().Any();

                            var cryptoAtt = atts.OfType<CryptoAttribute>().FirstOrDefault();
                            propInfoItem.Encryption = cryptoAtt?.Method ?? CryptoMethod.None;

                            if (propTypeCategory == PropertyTypeCategory.Array)
                                propInfoItem.CollectionItemType = prop.PropertyType.GetElementType();
                            else if (propTypeCategory == PropertyTypeCategory.GenericCollection)
                                propInfoItem.CollectionItemType = prop.PropertyType.GetGenericArguments().FirstOrDefault();

                            var childrenAtt = atts.OfType<ChildrenAttribute>().FirstOrDefault();
                            if (childrenAtt != null)
                            {
                                propInfoItem.ReferenceType = PropertyReferenceType.Children;
                                if (propertyAtt == null || propertyAtt.Cascade == CascadeOptions.None)
                                    propInfoItem.Cascade = CascadeOptions.Delete;
                                propInfoItem.ChildParentProperty = childrenAtt.RemoteParentProperty;
                            }
                            else if ((propTypeCategory == PropertyTypeCategory.Array || propTypeCategory == PropertyTypeCategory.GenericCollection) &&
                                     propInfoItem.CollectionItemType != null &&
                                     !ValueHelper.IsScalarType(propInfoItem.CollectionItemType))
                            {
                                var childTypeProps = propInfoItem.CollectionItemType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                bool childPointsToParent = childTypeProps.Any(p =>
                                    p.PropertyType == type ||
                                    (ValueHelper.IsScalarType(p.PropertyType) && p.Name.Equals($"{type.Name}Id", StringComparison.OrdinalIgnoreCase)));

                                if (childPointsToParent)
                                {
                                    propInfoItem.ReferenceType = PropertyReferenceType.Children;
                                    if (propertyAtt == null || propertyAtt.Cascade == CascadeOptions.None)
                                        propInfoItem.Cascade = CascadeOptions.Delete;
                                }
                            }

                            var genericTypeAtt = atts.OfType<GenericTypePropertyAttribute>().FirstOrDefault();
                            if (prop.PropertyType == typeof(object) && genericTypeAtt != null)
                                propInfoItem.GenericTypeProperty = genericTypeAtt.Name;

                            // Setting reference type
                            if (propInfoItem.ReferenceType != PropertyReferenceType.Children)
                            {
                                if (propTypeCategory == PropertyTypeCategory.None ||
                                    propTypeCategory == PropertyTypeCategory.ValueTypeArray ||
                                    propTypeCategory == PropertyTypeCategory.ValueTypeCollection ||
                                    propTypeCategory == PropertyTypeCategory.Dictionary)
                                {
                                    propInfoItem.ReferenceType = PropertyReferenceType.None;
                                }
                                else if (foreignKeyAtts.Count > 0)
                                {
                                    if (prop.PropertyType.GetProperties()
                                        .Any(s => s.PropertyType == type && s.GetCustomAttribute<ForeignKeyAttribute>(true) != null))
                                        propInfoItem.ReferenceType = PropertyReferenceType.SelfForeign;
                                    else
                                        propInfoItem.ReferenceType = PropertyReferenceType.Foreign;
                                }
                                else if (parentKeyAtts.Count > 0)
                                {
                                    propInfoItem.ReferenceType = PropertyReferenceType.Parent;
                                }
                                else if (propTypeCategory == PropertyTypeCategory.Class)
                                {
                                    var targetTypeProps = prop.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                    var targetPkProp = targetTypeProps.FirstOrDefault(p => p.GetCustomAttributes(true).OfType<PrimaryKeyAttribute>().Any())
                                        ?? targetTypeProps.FirstOrDefault(p => p.Name == "Id");

                                    if (targetPkProp != null)
                                    {
                                        bool targetHasCollectionOfThis = targetTypeProps.Any(tp =>
                                        {
                                            if (tp.PropertyType.IsArray && tp.PropertyType.GetElementType() == type)
                                                return true;
                                            if (tp.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(tp.PropertyType) && tp.PropertyType.IsGenericType)
                                                return tp.PropertyType.GetGenericArguments().FirstOrDefault() == type;
                                            return false;
                                        });

                                        var localKeyProp = props.FirstOrDefault(p =>
                                            ValueHelper.IsScalarType(p.PropertyType) &&
                                            (p.Name.Equals($"{prop.Name}{targetPkProp.Name}", StringComparison.OrdinalIgnoreCase) ||
                                             p.Name.Equals($"{prop.PropertyType.Name}{targetPkProp.Name}", StringComparison.OrdinalIgnoreCase)));

                                        if (targetHasCollectionOfThis)
                                        {
                                            propInfoItem.ReferenceType = PropertyReferenceType.Parent;
                                            propInfoItem.ParentKeys = new List<ParentKeyAttribute>
                                            {
                                                new ParentKeyAttribute(localKeyProp?.Name, targetPkProp.Name)
                                            };
                                        }
                                        else if (localKeyProp != null)
                                        {
                                            propInfoItem.ReferenceType = PropertyReferenceType.Foreign;
                                            propInfoItem.ForeignKeys = new List<ForeignKeyAttribute>
                                            {
                                                new ForeignKeyAttribute(localKeyProp.Name, targetPkProp.Name)
                                            };
                                        }
                                        else
                                        {
                                            propInfoItem.ReferenceType = PropertyReferenceType.Reference;
                                        }
                                    }
                                    else
                                    {
                                        propInfoItem.ReferenceType = PropertyReferenceType.Complex;
                                        if (propertyAtt == null || propertyAtt.Cascade == CascadeOptions.None)
                                            propInfoItem.Cascade = CascadeOptions.Delete;
                                    }
                                }
                                else
                                {
                                    propInfoItem.ReferenceType = PropertyReferenceType.Reference;
                                }
                            }

                            typeProps.Add(propInfoItem);

                            if (prop.PropertyType != type && prop.PropertyType != typeof(object))
                            {
                                if (propTypeCategory == PropertyTypeCategory.Array)
                                    innerTypes.Add(prop.PropertyType.GetElementType());
                                else if (propTypeCategory == PropertyTypeCategory.GenericCollection)
                                    innerTypes.Add(prop.PropertyType.GetGenericArguments().FirstOrDefault());
                                else if (propTypeCategory == PropertyTypeCategory.Class)
                                    innerTypes.Add(prop.PropertyType);
                            }
                        }

                        // Convention: If no [PrimaryKey] attribute is specified, use property named "Id" as PrimaryKey + AutoNumber
                        if (!typeProps.Any(s => s.IsPrimaryKey))
                        {
                            var primaryKeyProperty = typeProps.FirstOrDefault(s => s.PropertyName == "Id");
                            if (primaryKeyProperty != null)
                            {
                                primaryKeyProperty.IsPrimaryKey = true;
                                primaryKeyProperty.IsIndexed = true;
                                if (ValueHelper.IsNumeric(primaryKeyProperty.PropertyType) || primaryKeyProperty.PropertyType == typeof(Guid))
                                {
                                    primaryKeyProperty.IsAutonumber = true;
                                }
                            }
                        }

                        // Mark scalar properties that serve as local ForeignKey or ParentKey columns
                        var relKeyNames = typeProps
                            .SelectMany(p => (p.ForeignKeys?.Select(fk => fk.LocalProperty) ?? Enumerable.Empty<string>())
                                .Concat(p.ParentKeys?.Select(pk => pk.LocalProperty) ?? Enumerable.Empty<string>()))
                            .Where(n => !string.IsNullOrEmpty(n))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        foreach (var tp in typeProps)
                        {
                            if (relKeyNames.Contains(tp.PropertyName))
                                tp.IsRelationshipKey = true;
                        }

                        var activeProps = typeProps.Where(s => !s.IsReadOnly && !s.IsNotMapped).ToList();
                        properties[fullName] = activeProps;
                        metadataCache[fullName] = BuildTypeMetadata(type, activeProps);
                    }
                }

                CheckReservedKeywords(type);

                foreach (var innerType in innerTypes)
                {
                    if (innerType != null && innerType != typeof(object))
                        LoadType(innerType);
                }
            }
        }

        private static ScalarPropertyKind ClassifyScalarKind(Type propType)
        {
            if (propType == null) return ScalarPropertyKind.None;
            if (propType == typeof(string)) return ScalarPropertyKind.String;
            if (propType == typeof(int)) return ScalarPropertyKind.Int32;
            if (propType == typeof(long)) return ScalarPropertyKind.Int64;
            if (propType == typeof(double)) return ScalarPropertyKind.Double;
            if (propType == typeof(decimal)) return ScalarPropertyKind.Decimal;
            if (propType == typeof(bool)) return ScalarPropertyKind.Boolean;
            if (propType == typeof(Guid)) return ScalarPropertyKind.Guid;
            if (propType == typeof(DateTime)) return ScalarPropertyKind.DateTime;
            return ValueHelper.IsScalarType(propType) ? ScalarPropertyKind.OtherScalar : ScalarPropertyKind.None;
        }

        private static void PopulateTypedAccessors(Type declaringType, PropertyInfo prop, PropertyInfoItem item)
        {
            if (prop == null || declaringType.IsValueType || declaringType.ContainsGenericParameters)
                return;

            switch (item.ScalarKind)
            {
                case ScalarPropertyKind.String:
                    item.StringGetter = CompileTypedGetter<string>(declaringType, prop);
                    item.StringSetter = CompileTypedSetter<string>(declaringType, prop);
                    break;
                case ScalarPropertyKind.Int32:
                    item.IntGetter = CompileTypedGetter<int>(declaringType, prop);
                    item.IntSetter = CompileTypedSetter<int>(declaringType, prop);
                    break;
                case ScalarPropertyKind.Int64:
                    item.LongGetter = CompileTypedGetter<long>(declaringType, prop);
                    item.LongSetter = CompileTypedSetter<long>(declaringType, prop);
                    break;
                case ScalarPropertyKind.Double:
                    item.DoubleGetter = CompileTypedGetter<double>(declaringType, prop);
                    item.DoubleSetter = CompileTypedSetter<double>(declaringType, prop);
                    break;
                case ScalarPropertyKind.Decimal:
                    item.DecimalGetter = CompileTypedGetter<decimal>(declaringType, prop);
                    item.DecimalSetter = CompileTypedSetter<decimal>(declaringType, prop);
                    break;
                case ScalarPropertyKind.Boolean:
                    item.BoolGetter = CompileTypedGetter<bool>(declaringType, prop);
                    item.BoolSetter = CompileTypedSetter<bool>(declaringType, prop);
                    break;
                case ScalarPropertyKind.Guid:
                    item.GuidGetter = CompileTypedGetter<Guid>(declaringType, prop);
                    item.GuidSetter = CompileTypedSetter<Guid>(declaringType, prop);
                    break;
            }
        }

        private static Func<object, TProp> CompileTypedGetter<TProp>(Type declaringType, PropertyInfo prop)
        {
            if (!prop.CanRead) return null;
            try
            {
                var instanceParam = Expression.Parameter(typeof(object), "instance");
                var typedInstance = Expression.Convert(instanceParam, declaringType);
                var propAccess = Expression.Property(typedInstance, prop);
                return Expression.Lambda<Func<object, TProp>>(propAccess, instanceParam).Compile();
            }
            catch
            {
                return null;
            }
        }

        private static Action<object, TProp> CompileTypedSetter<TProp>(Type declaringType, PropertyInfo prop)
        {
            if (!prop.CanWrite || prop.SetMethod == null) return null;
            try
            {
                var instanceParam = Expression.Parameter(typeof(object), "instance");
                var valueParam = Expression.Parameter(typeof(TProp), "value");
                var typedInstance = Expression.Convert(instanceParam, declaringType);
                var assign = Expression.Assign(Expression.Property(typedInstance, prop), valueParam);
                return Expression.Lambda<Action<object, TProp>>(assign, instanceParam, valueParam).Compile();
            }
            catch
            {
                return null;
            }
        }

        private static Func<object, object> CompileGetter(Type declaringType, PropertyInfo prop)
        {
            if (prop == null || !prop.CanRead || declaringType.IsValueType || declaringType.ContainsGenericParameters)
                return null;

            try
            {
                var instanceParam = Expression.Parameter(typeof(object), "instance");
                var typedInstance = Expression.Convert(instanceParam, declaringType);
                var propAccess = Expression.Property(typedInstance, prop);
                var boxedResult = Expression.Convert(propAccess, typeof(object));
                return Expression.Lambda<Func<object, object>>(boxedResult, instanceParam).Compile();
            }
            catch
            {
                return null;
            }
        }

        private static Action<object, object> CompileSetter(Type declaringType, PropertyInfo prop)
        {
            if (prop == null || !prop.CanWrite || prop.SetMethod == null || declaringType.IsValueType || declaringType.ContainsGenericParameters)
                return null;

            try
            {
                var instanceParam = Expression.Parameter(typeof(object), "instance");
                var valueParam = Expression.Parameter(typeof(object), "value");
                var typedInstance = Expression.Convert(instanceParam, declaringType);
                var typedValue = Expression.Convert(valueParam, prop.PropertyType);
                var assign = Expression.Assign(Expression.Property(typedInstance, prop), typedValue);
                return Expression.Lambda<Action<object, object>>(assign, instanceParam, valueParam).Compile();
            }
            catch
            {
                return null;
            }
        }

        internal TypeMetadata GetMetadata(Type type)
        {
            if (type == null)
                return null;

            string key = type.FullName;
            if (!string.IsNullOrEmpty(key) && metadataCache.TryGetValue(key, out var meta))
                return meta;

            LoadType(type);
            return !string.IsNullOrEmpty(key) && metadataCache.TryGetValue(key, out meta) ? meta : null;
        }

        private static TypeMetadata BuildTypeMetadata(Type type, List<PropertyInfoItem> activeProps)
        {
            var allArr = activeProps.ToArray();
            var writeArr = activeProps
                .Where(s => !s.IsNotMapped)
                .OrderByDescending(s => s.PropertyType != null && ValueHelper.IsScalarType(s.PropertyType))
                .ToArray();
            var readArr = activeProps
                .Where(s => !s.IsNotMapped && !s.IsReadOnly)
                .OrderBy(s => s.TypeCategory)
                .ToArray();

            var readMap = new Dictionary<string, PropertyInfoItem>(readArr.Length, StringComparer.Ordinal);
            foreach (var p in readArr)
            {
                readMap[p.PropertyName] = p;
            }

            var pkArr = activeProps
                .Where(s => s.IsPrimaryKey)
                .OrderBy(s => s.PropertyName)
                .ToArray();
            var uniqueArr = activeProps.Where(s => s.IsUnique).ToArray();
            var indexedArr = activeProps.Where(s => s.IsIndexed || s.IsPrimaryKey || s.IsUnique).ToArray();
            var reqArr = activeProps.Where(s => s.IsRequired && !s.IsAutonumber).ToArray();
            var autoArr = activeProps.Where(s => s.IsAutonumber).ToArray();

            bool isStandalone = activeProps.All(p =>
                p.IsNotMapped ||
                p.TypeCategory == PropertyTypeCategory.None ||
                p.TypeCategory == PropertyTypeCategory.ValueTypeArray ||
                p.TypeCategory == PropertyTypeCategory.ValueTypeCollection ||
                p.TypeCategory == PropertyTypeCategory.Dictionary);

            bool hasAttrs = readArr.Any(p => p.ValuePosition == ValuePosition.Attribute);
            bool hasDefaults = readArr.Any(p => p.DefaultValue != null && !p.DefaultValue.Equals(ValueHelper.DefaultOf(p.PropertyType)));
            bool hasCascadeOrParent = activeProps.Any(s =>
                s.ReferenceType == PropertyReferenceType.Parent ||
                (s.ReferenceType != PropertyReferenceType.SelfForeign &&
                 s.Cascade.HasFlag(CascadeOptions.Delete) &&
                 s.TypeCategory != PropertyTypeCategory.None &&
                 s.TypeCategory != PropertyTypeCategory.Dictionary));

            bool validPkTypes = true;
            foreach (var primaryProp in pkArr)
            {
                Type pt = Nullable.GetUnderlyingType(primaryProp.PropertyType) ?? primaryProp.PropertyType;
                if ((pt.IsClass && pt != typeof(string)) || pt.IsArray || pt == typeof(DateTime))
                {
                    validPkTypes = false;
                    break;
                }
            }

            bool isPureScalarStandalone =
                isStandalone &&
                !hasAttrs &&
                !hasDefaults &&
                writeArr.All(p =>
                    p.TypeCategory == PropertyTypeCategory.None &&
                    p.ScalarKind != ScalarPropertyKind.None &&
                    p.ValuePosition != ValuePosition.Attribute &&
                    p.Encryption == CryptoMethod.None &&
                    !p.IsMarkup &&
                    !p.IsGenericType);

            return new TypeMetadata
            {
                Type = type,
                InstanceFactory = ValueHelper.GetFactory(type),
                AllProperties = allArr,
                WriteProperties = writeArr,
                ReadProperties = readArr,
                ReadPropertyMap = readMap,
                PrimaryKeyProperties = pkArr,
                UniqueProperties = uniqueArr,
                IndexedProperties = indexedArr,
                RequiredNonAutoProperties = reqArr,
                AutonumberProperties = autoArr,
                IsStandalone = isStandalone,
                IsPureScalarStandalone = isPureScalarStandalone,
                HasAttributeProperties = hasAttrs,
                HasDefaultValueProperties = hasDefaults,
                HasCascadeDeleteOrParentProperties = hasCascadeOrParent,
                HasValidPrimaryKeyTypes = validPkTypes,
                Utf8EntityOpenTag = System.Text.Encoding.UTF8.GetBytes($"<{type.Name}>"),
                Utf8EntityCloseTag = System.Text.Encoding.UTF8.GetBytes($"</{type.Name}>")
            };
        }

        internal bool HasPrimaryKey(Type type)
        {
            if (type == null) return false;
            var meta = GetMetadata(type);
            return meta != null && meta.PrimaryKeyProperties.Length > 0;
        }

        internal IReadOnlyList<PropertyInfoItem> Properties(string key)
        {
            if (string.IsNullOrEmpty(key))
                return Array.Empty<PropertyInfoItem>();

            if (properties.TryGetValue(key, out var list) && list != null)
                return list;

            return Array.Empty<PropertyInfoItem>();
        }

        internal Type RegisteredType(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (registeredTypes.TryGetValue(key, out var t)) return t;

            if (properties.TryGetValue(key, out var list) && list != null)
                return list.FirstOrDefault()?.Type;

            // Search loaded assemblies as fallback
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = asm.GetType(key, throwOnError: false);
                if (found != null)
                {
                    LoadType(found);
                    return found;
                }
            }

            return null;
        }

        internal Type RegisteredTypeByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            foreach (var kvp in registeredTypes)
            {
                if (kvp.Value.Name.Equals(name, StringComparison.Ordinal) ||
                    kvp.Key.EndsWith("." + name, StringComparison.Ordinal))
                {
                    return kvp.Value;
                }
            }

            foreach (var kvp in properties)
            {
                string[] parts = kvp.Key.Split('.');
                if (parts.Length > 0 && parts.Last().Equals(name, StringComparison.Ordinal))
                {
                    var fp = kvp.Value.FirstOrDefault();
                    if (fp != null) return fp.Type;
                }
            }

            return null;
        }

        internal PropertyTypeCategory GetPropertyTypeCategory(Type type)
        {
            if (type == null || ValueHelper.IsScalarType(type))
                return PropertyTypeCategory.None;

            if (type.IsArray && type.GetArrayRank() == 1)
            {
                Type childType = type.GetElementType();
                return ValueHelper.IsScalarType(childType)
                    ? PropertyTypeCategory.ValueTypeArray
                    : PropertyTypeCategory.Array;
            }

            if (typeof(IDictionary).IsAssignableFrom(type) ||
                (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
            {
                return PropertyTypeCategory.Dictionary;
            }

            if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type) && type.IsGenericType)
            {
                Type childType = type.GetGenericArguments().FirstOrDefault();
                if (childType != null)
                {
                    return ValueHelper.IsScalarType(childType)
                        ? PropertyTypeCategory.ValueTypeCollection
                        : PropertyTypeCategory.GenericCollection;
                }
            }

            if (type.IsClass)
                return PropertyTypeCategory.Class;

            return PropertyTypeCategory.None;
        }

        internal void CheckReservedKeywords(Type type)
        {
            var primaryProps = Properties(type.FullName)
                .Where(s => s.IsPrimaryKey && IsReservedKeyword(s.PropertyName));
            if (primaryProps.Any())
                throw new ReservedKeyWordException();
        }

        internal bool IsReservedKeyword(string keyword) => keywords.Contains(keyword);
    }
}
