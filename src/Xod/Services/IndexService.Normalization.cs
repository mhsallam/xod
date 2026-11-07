using System;
using Xod.Helpers;

namespace Xod.Services
{
    internal partial class IndexService
    {
        private static readonly string[] SmallIntNormalizedCache = InitSmallIntCache();

        private static string[] InitSmallIntCache()
        {
            var arr = new string[2048];
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = FormatNormalizedInt64(i);
            }
            return arr;
        }

        internal static string NormalizeIndexValue(Type propertyType, object value)
        {
            if (value == null) return string.Empty;
            if (value is string sVal) return sVal;
            if (value is int iVal)
            {
                return NormalizeInt32(iVal);
            }
            if (value is long lVal && lVal >= -900_000_000_000_000_000L && lVal <= 900_000_000_000_000_000L)
            {
                return NormalizeInt64(lVal);
            }

            Type underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

            if (ValueHelper.IsNumeric(underlying))
            {
                decimal dec = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
                // Shift by 10^18 so negative and positive numbers sort lexicographically
                decimal shifted = dec + 1_000_000_000_000_000_000m;
                return shifted.ToString("0000000000000000000.00000000", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (underlying == typeof(DateTime))
            {
                var dt = (DateTime)value;
                return dt.ToUniversalTime().Ticks.ToString("D20", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (underlying == typeof(DateTimeOffset))
            {
                var dto = (DateTimeOffset)value;
                return dto.UtcTicks.ToString("D20", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (underlying == typeof(DateOnly))
            {
                var d = (DateOnly)value;
                return d.DayNumber.ToString("D10", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (underlying == typeof(TimeOnly))
            {
                var t = (TimeOnly)value;
                return t.Ticks.ToString("D20", System.Globalization.CultureInfo.InvariantCulture);
            }

            return ValueHelper.FormatScalar(value);
        }

        internal static string NormalizeInt32(int iVal)
        {
            if ((uint)iVal < (uint)SmallIntNormalizedCache.Length)
                return SmallIntNormalizedCache[iVal];
            return FormatNormalizedInt64(iVal);
        }

        internal static string NormalizeInt64(long lVal)
        {
            if ((ulong)lVal < (ulong)SmallIntNormalizedCache.Length)
                return SmallIntNormalizedCache[(int)lVal];
            if (lVal >= -900_000_000_000_000_000L && lVal <= 900_000_000_000_000_000L)
                return FormatNormalizedInt64(lVal);
            decimal shifted = (decimal)lVal + 1_000_000_000_000_000_000m;
            return shifted.ToString("0000000000000000000.00000000", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string FormatNormalizedInt64(long val)
        {
            long shifted = 1_000_000_000_000_000_000L + val;
            return string.Create(28, shifted, static (span, s) =>
            {
                for (int i = 18; i >= 0; i--)
                {
                    s = Math.DivRem(s, 10, out long rem);
                    span[i] = (char)('0' + rem);
                }
                ".00000000".AsSpan().CopyTo(span.Slice(19));
            });
        }

        internal static string GetNormalizedPropertyValue(PropertyInfoItem prop, object item)
        {
            if (prop == null || item == null)
                return null;

            switch (prop.ScalarKind)
            {
                case ScalarPropertyKind.Int32 when prop.IntGetter != null:
                    return NormalizeInt32(prop.IntGetter(item));
                case ScalarPropertyKind.String when prop.StringGetter != null:
                    return prop.StringGetter(item);
                case ScalarPropertyKind.Int64 when prop.LongGetter != null:
                    return NormalizeInt64(prop.LongGetter(item));
            }

            object val = prop.GetValue(item);
            return val != null ? NormalizeIndexValue(prop.PropertyType, val) : null;
        }
    }
}
