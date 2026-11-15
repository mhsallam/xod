using System;
using Xod.Helpers;

namespace Xod.Services
{
    internal partial class IndexService
    {
        /// <summary>The value a read of this row gives the property (StoredValue.ReadBack). An indexed property is
        /// never encrypted, so its text is parsed as it stands.</summary>
        private static object ReadBackFromRow(PropertyInfoItem prop, System.Xml.Linq.XElement entityElement) =>
            StoredValue.ReadBack(prop, entityElement, text => ValueHelper.ParseScalar(prop.PropertyType, text));

        // Keys of the ints most tables hold, made once.
        private static readonly string[] SmallIntNormalizedCache = InitSmallIntCache();

        private static string[] InitSmallIntCache()
        {
            var arr = new string[2048];
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = IndexKeys.EncodeInt64(i);
            }
            return arr;
        }

        /// <summary>The index key of a value of a property of <paramref name="propertyType"/>: see
        /// <see cref="IndexKeys"/>. Null when the value has no key (null, NaN), and is not indexed.</summary>
        internal static string NormalizeIndexValue(Type propertyType, object value)
        {
            if (value is int iVal && IndexKeys.Underlying(propertyType) == typeof(int))
                return NormalizeInt32(iVal);
            return IndexKeys.Encode(propertyType, value);
        }

        /// <summary>The key of an int property's value; equal to <see cref="IndexKeys.Encode"/>.</summary>
        internal static string NormalizeInt32(int iVal)
        {
            if ((uint)iVal < (uint)SmallIntNormalizedCache.Length)
                return SmallIntNormalizedCache[iVal];
            return IndexKeys.EncodeInt64(iVal);
        }

        /// <summary>The key of a long property's value; equal to <see cref="IndexKeys.Encode"/>.</summary>
        internal static string NormalizeInt64(long lVal)
        {
            if ((ulong)lVal < (ulong)SmallIntNormalizedCache.Length)
                return SmallIntNormalizedCache[(int)lVal];
            return IndexKeys.EncodeInt64(lVal);
        }

        /// <summary>The key of the value a read of the row gives back for this property: the same from
        /// the stored row (<see cref="ReadBackFromRow"/>) as from the object written.</summary>
        internal static string GetNormalizedPropertyValue(PropertyInfoItem prop, object item)
        {
            if (prop == null || item == null)
                return null;

            // A non-zero number and a non-empty string are always stored as they are.
            switch (prop.ScalarKind)
            {
                case ScalarPropertyKind.Int32 when prop.IntGetter != null:
                    int i = prop.IntGetter(item);
                    if (i != 0) return NormalizeInt32(i);
                    break;
                case ScalarPropertyKind.String when prop.StringGetter != null:
                    string s = prop.StringGetter(item);
                    if (!string.IsNullOrEmpty(s)) return s;
                    break;
                case ScalarPropertyKind.Int64 when prop.LongGetter != null:
                    long l = prop.LongGetter(item);
                    if (l != 0L) return NormalizeInt64(l);
                    break;
            }

            // Keyed as a read of the stored row gives it back: a value the writers leave out reads back
            // as the property's value without a row (StoredValue), and the rows rebuild the same key.
            object val = StoredValue.AsReadBack(prop, prop.GetValue(item));
            return val != null ? NormalizeIndexValue(prop.PropertyType, val) : null;
        }
    }
}
