using System;
using System.Collections.Generic;
using System.Linq;
using Xod.Helpers;
using Xod.Services;

namespace Xod.Engines.Xml
{
    public partial class XmlEngine
    {
        private void ValidatePrimaryKeyTypes(Type type)
        {
            var meta = propertyService.GetMetadata(type);
            if (meta != null)
            {
                if (!meta.HasValidPrimaryKeyTypes)
                    exceptionService.Throw(new PrimaryKeyDataTypeException());
                return;
            }

            var primaryProps = propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey);
            foreach (var primaryProp in primaryProps)
            {
                Type pt = Nullable.GetUnderlyingType(primaryProp.PropertyType) ?? primaryProp.PropertyType;
                if ((pt.IsClass && pt != typeof(string)) ||
                    pt.IsArray ||
                    pt == typeof(DateTime))
                {
                    exceptionService.Throw(new PrimaryKeyDataTypeException());
                }
            }
        }

        private void ValidateRequiredProperties(Type type, object item)
        {
            var meta = propertyService.GetMetadata(type);
            var requiredProps = meta != null
                ? meta.RequiredNonAutoProperties
                : propertyService.Properties(type.FullName).Where(s => s.IsRequired && !s.IsAutonumber).ToArray();
            for (int i = 0; i < requiredProps.Length; i++)
            {
                var prop = requiredProps[i];
                if (prop.ScalarKind == ScalarPropertyKind.String)
                {
                    if (string.IsNullOrWhiteSpace(prop.StringGetter(item)))
                        exceptionService.Throw(new RequiredPropertyException(prop.PropertyName));
                    continue;
                }
                object value = prop.GetValue(item);
                if (IsMissingRequiredValue(value))
                {
                    exceptionService.Throw(new RequiredPropertyException(prop.PropertyName));
                }
            }
        }

        private static bool IsMissingRequiredValue(object value)
        {
            return value == null
                || (value is string s && string.IsNullOrWhiteSpace(s))
                || (value is Guid g && g == Guid.Empty)
                || (value is DateTime dt && dt == default)
                || (value is DateOnly d && d == default);
        }

        private void ValidateUniqueAndPrimaryConstraints(Type type, object item, string existingCodeToIgnore)
        {
            var meta = propertyService.GetMetadata(type);
            var primaryProps = meta != null
                ? meta.PrimaryKeyProperties
                : propertyService.Properties(type.FullName).Where(s => s.IsPrimaryKey).ToArray();
            var uniqueProps = meta != null
                ? meta.UniqueProperties
                : propertyService.Properties(type.FullName).Where(s => s.IsUnique).ToArray();

            // 1. Check Primary Key (supports single or composite primary key)
            if (primaryProps.Length > 0)
            {
                if (primaryProps.Length == 1 && options.EnableIndexing)
                {
                    var pkProp = primaryProps[0];
                    if (pkProp.ScalarKind == ScalarPropertyKind.Int32)
                    {
                        int pkInt = pkProp.IntGetter(item);
                        if (pkInt != 0 && indexService.HasNormalizedKeyConflict(type, pkProp, IndexService.NormalizeInt32(pkInt), existingCodeToIgnore))
                        {
                            exceptionService.Throw(new ReservedPrimaryKeyException());
                        }
                    }
                    else if (pkProp.ScalarKind == ScalarPropertyKind.Int64)
                    {
                        long pkLong = pkProp.LongGetter(item);
                        if (pkLong != 0L && indexService.HasNormalizedKeyConflict(type, pkProp, IndexService.NormalizeInt64(pkLong), existingCodeToIgnore))
                        {
                            exceptionService.Throw(new ReservedPrimaryKeyException());
                        }
                    }
                    else if (pkProp.ScalarKind == ScalarPropertyKind.String)
                    {
                        string pkStr = pkProp.StringGetter(item);
                        if (!string.IsNullOrEmpty(pkStr) && indexService.HasNormalizedKeyConflict(type, pkProp, pkStr, existingCodeToIgnore))
                        {
                            exceptionService.Throw(new ReservedPrimaryKeyException());
                        }
                    }
                    else
                    {
                        object pkVal = pkProp.GetValue(item);
                        if (pkVal != null && !pkVal.Equals(ValueHelper.DefaultOf(pkProp.PropertyType)))
                        {
                            if (indexService.HasKeyConflict(type, pkProp, pkVal, existingCodeToIgnore))
                            {
                                exceptionService.Throw(new ReservedPrimaryKeyException());
                            }
                        }
                    }
                }
                else
                {
                    var pkValues = GetPrimaryValues(type, item);
                    if (pkValues != null && pkValues.Count == primaryProps.Length)
                    {
                        object example = PopulateItemProperties(type, pkValues);
                        var existingRow = GetRow(type, example);
                        if (existingRow?.Row != null)
                        {
                            string existingCode = $"{existingRow.Page.GetFileCode()}.{existingRow.Row.Attribute("code")?.Value}";
                            if (existingCode != existingCodeToIgnore)
                            {
                                exceptionService.Throw(new ReservedPrimaryKeyException());
                            }
                        }
                    }
                }
            }

            // 2. Check each [UniqueKey] property individually
            for (int i = 0; i < uniqueProps.Length; i++)
            {
                var uniqueProp = uniqueProps[i];
                if (options.EnableIndexing)
                {
                    if (uniqueProp.ScalarKind == ScalarPropertyKind.String && uniqueProp.Encryption == CryptoMethod.None)
                    {
                        string uStr = uniqueProp.StringGetter(item);
                        if (!string.IsNullOrEmpty(uStr) && indexService.HasNormalizedKeyConflict(type, uniqueProp, uStr, existingCodeToIgnore))
                        {
                            exceptionService.Throw(new ReservedUniqueKeyException());
                        }
                        continue;
                    }
                    if (uniqueProp.ScalarKind == ScalarPropertyKind.Int32)
                    {
                        int uInt = uniqueProp.IntGetter(item);
                        if (uInt != 0 && indexService.HasNormalizedKeyConflict(type, uniqueProp, IndexService.NormalizeInt32(uInt), existingCodeToIgnore))
                        {
                            exceptionService.Throw(new ReservedUniqueKeyException());
                        }
                        continue;
                    }
                    if (uniqueProp.ScalarKind == ScalarPropertyKind.Int64)
                    {
                        long uLong = uniqueProp.LongGetter(item);
                        if (uLong != 0L && indexService.HasNormalizedKeyConflict(type, uniqueProp, IndexService.NormalizeInt64(uLong), existingCodeToIgnore))
                        {
                            exceptionService.Throw(new ReservedUniqueKeyException());
                        }
                        continue;
                    }
                }

                object val = uniqueProp.GetValue(item);
                if (val == null || val.Equals(ValueHelper.DefaultOf(uniqueProp.PropertyType)))
                    continue;

                if (options.EnableIndexing)
                {
                    if (indexService.HasKeyConflict(type, uniqueProp, val, existingCodeToIgnore))
                    {
                        exceptionService.Throw(new ReservedUniqueKeyException());
                    }
                }
                else
                {
                    var dict = new Dictionary<string, object> { [uniqueProp.PropertyName] = val };
                    var existingRow = GetRowByExampleObject(type, PopulateItemProperties(type, dict));
                    if (existingRow?.Row != null)
                    {
                        string existingCode = $"{existingRow.Page.GetFileCode()}.{existingRow.Row.Attribute("code")?.Value}";
                        if (existingCode != existingCodeToIgnore)
                        {
                            exceptionService.Throw(new ReservedUniqueKeyException());
                        }
                    }
                }
            }
        }

        private Dictionary<string, object> GetPrimaryValues(Type type, object item)
        {
            if (item == null || type == null)
                return null;

            var meta = propertyService.GetMetadata(type);
            var primaryProps = meta != null
                ? meta.PrimaryKeyProperties
                : propertyService.Properties(type.FullName)
                    .Where(s => s.IsPrimaryKey)
                    .OrderBy(s => s.PropertyName)
                    .ToArray();

            var values = new Dictionary<string, object>(primaryProps.Length);
            for (int i = 0; i < primaryProps.Length; i++)
            {
                var primaryProp = primaryProps[i];
                if (meta == null)
                {
                    Type pt = Nullable.GetUnderlyingType(primaryProp.PropertyType) ?? primaryProp.PropertyType;
                    if ((pt.IsClass && pt != typeof(string)) ||
                        pt.IsArray ||
                        pt == typeof(DateTime))
                    {
                        exceptionService.Throw(new PrimaryKeyDataTypeException());
                    }
                }
                else if (!meta.HasValidPrimaryKeyTypes)
                {
                    exceptionService.Throw(new PrimaryKeyDataTypeException());
                }

                object value = primaryProp.GetValue(item);
                if (value != null && !value.Equals(ValueHelper.DefaultOf(primaryProp.PropertyType)))
                {
                    values.Add(primaryProp.PropertyName, value);
                }
                else
                {
                    values.Clear();
                    break;
                }
            }
            return values;
        }
    }
}
