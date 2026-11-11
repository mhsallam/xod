using System;
using System.Globalization;
using System.Numerics;
using Xod.Helpers;

namespace Xod.Services
{
    /// <summary>
    /// Index keys, version 2 (PLAN-046 section 8.3). The one place that turns a property value into the
    /// string an index stores, and that says how a value of another type maps onto a property's type.
    ///
    /// Two rules make every consumer (the query planner, unique and primary-key checks, lookups by key)
    /// right by construction:
    /// <list type="bullet">
    /// <item>A key is exact: two values get the same key exactly when C# says they are equal
    /// (<c>12.5m == 12.50m</c>, <c>0.0 == -0.0</c>; DateTime compares ticks and ignores Kind).</item>
    /// <item>For an ordered kind, ordinal order of keys is the order C# compares values in.</item>
    /// </list>
    /// A value that equals nothing (NaN) has no key and is not indexed.
    /// </summary>
    internal static class IndexKeys
    {
        internal enum Kind
        {
            /// <summary>Equality only: text, bool, Guid and anything else formatted as text.</summary>
            Text,
            /// <summary>Every integral type, enums by value, char by code point, TimeSpan by ticks:
            /// <c>v + 2^63</c> as 20 digits.</summary>
            Integer,
            /// <summary>decimal: sign, exponent and significant digits (see <see cref="EncodeDecimal"/>).</summary>
            Decimal,
            /// <summary>float and double: the IEEE bits made order-preserving, as 20 digits.</summary>
            Float,
            /// <summary>DateTime (ticks), DateTimeOffset (UTC ticks), DateOnly (day number), TimeOnly (ticks).</summary>
            Temporal,
        }

        private const ulong SignBit = 0x8000_0000_0000_0000UL;

        internal static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

        internal static Kind KindOf(Type propertyType)
        {
            Type t = Underlying(propertyType);
            if (t.IsEnum || t == typeof(char) || t == typeof(TimeSpan) || IsIntegral(t)) return Kind.Integer;
            if (t == typeof(decimal)) return Kind.Decimal;
            if (t == typeof(double) || t == typeof(float)) return Kind.Float;
            if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(TimeOnly))
                return Kind.Temporal;
            return Kind.Text;
        }

        /// <summary>Whether ranges (&lt;, &lt;=, &gt;, &gt;=) can be answered from the keys.</summary>
        internal static bool IsOrdered(Type propertyType) => KindOf(propertyType) != Kind.Text;

        /// <summary>
        /// The key of <paramref name="value"/> in an index on a property of <paramref name="propertyType"/>,
        /// or null when the value has no key (null, NaN). The value must be of the property's type, or
        /// exactly convertible to it.
        /// </summary>
        internal static string Encode(Type propertyType, object value)
        {
            if (value == null) return null;
            if (value is string s) return s;

            Type t = Underlying(propertyType);
            // A text-kind key is the value's text; only a numeric or temporal key depends on the type.
            if (value.GetType() != t && KindOf(t) != Kind.Text)
            {
                if (!TryConvertExact(value, t, out value))
                    throw new ArgumentException(
                        $"An index key for a {t.Name} property can't be made from the {value.GetType().Name} value '{value}'.");
            }

            switch (value)
            {
                case int i: return EncodeInt64(i);
                case long l: return EncodeInt64(l);
                case short sh: return EncodeInt64(sh);
                case sbyte sb: return EncodeInt64(sb);
                case byte b: return EncodeInt64(b);
                case ushort us: return EncodeInt64(us);
                case uint ui: return EncodeInt64(ui);
                case ulong ul: return EncodeUInt64(ul);
                case char c: return EncodeInt64(c);
                case TimeSpan ts: return EncodeInt64(ts.Ticks);
                case decimal m: return EncodeDecimal(m);
                case double d: return EncodeDouble(d);
                case float f: return EncodeDouble(f);
                case DateTime dt: return dt.Ticks.ToString("D20", CultureInfo.InvariantCulture);
                case DateTimeOffset dto: return dto.UtcTicks.ToString("D20", CultureInfo.InvariantCulture);
                case DateOnly don: return don.DayNumber.ToString("D10", CultureInfo.InvariantCulture);
                case TimeOnly to: return to.Ticks.ToString("D20", CultureInfo.InvariantCulture);
            }

            if (value is Enum)
            {
                Type u = Enum.GetUnderlyingType(value.GetType());
                return u == typeof(ulong)
                    ? EncodeUInt64(Convert.ToUInt64(value, CultureInfo.InvariantCulture))
                    : EncodeInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }

            return ValueHelper.FormatScalar(value);
        }

        // ---- Integer: v + 2^63, 20 digits. Covers long.MinValue (all zeros) to ulong.MaxValue. ----

        internal static string EncodeInt64(long v) => FormatD20((ulong)v ^ SignBit);

        /// <summary>The value of an Integer-kind key.</summary>
        internal static decimal DecodeInteger(string key) =>
            (decimal)(BigInteger.Parse(key, CultureInfo.InvariantCulture) - SignBit);

        internal static string EncodeUInt64(ulong v)
        {
            if (v < SignBit) return FormatD20(v | SignBit);
            // v + 2^63 is at least 2^64: past ulong, still 20 digits.
            return ((UInt128)v + SignBit).ToString("D20", CultureInfo.InvariantCulture);
        }

        private static string FormatD20(ulong value) =>
            string.Create(20, value, static (span, v) =>
            {
                for (int i = 19; i >= 0; i--)
                {
                    ulong q = v / 10;
                    span[i] = (char)('0' + (int)(v - q * 10));
                    v = q;
                }
            });

        // ---- Float: IEEE bits, flipped so that unsigned order is numeric order. ----

        /// <summary>null for NaN, which equals nothing. -0.0 takes the key of 0.0. A float is widened to
        /// double first, which is exact, so float and double share keys.</summary>
        internal static string EncodeDouble(double d)
        {
            if (double.IsNaN(d)) return null;
            if (d == 0) d = 0; // -0.0 == 0.0
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(d);
            ulong ordered = (bits & SignBit) == 0 ? bits | SignBit : ~bits;
            return FormatD20(ordered);
        }

        // ---- Decimal: exact, compact, order-preserving. ----

        /// <summary>
        /// Zero is <c>1</c>. Otherwise the value is <c>0.d1d2...dn x 10^(E+1)</c> with <c>d1 != 0</c> and no
        /// trailing zeros (so 12.5 and 12.50 share a key): a positive value is <c>2</c>, then E+50 as two
        /// digits, then the digits; a negative value is <c>0</c>, then 50-E as two digits, then each digit
        /// as 9-d, then <c>~</c> (which sorts after every digit, so -1.2 &gt; -1.25 also holds in the keys).
        /// E is the power of ten of the first digit, -28 to 28 for a decimal.
        /// </summary>
        internal static string EncodeDecimal(decimal m)
        {
            if (m == 0m) return "1";
            bool negative = m < 0;
            string text = (negative ? -m : m).ToString(CultureInfo.InvariantCulture);

            int point = text.IndexOf('.');
            string digits = point < 0 ? text : text.Remove(point, 1);
            int intLength = point < 0 ? text.Length : point;

            int lead = 0;
            while (digits[lead] == '0') lead++;
            int end = digits.Length;
            while (digits[end - 1] == '0') end--;
            int exponent = intLength - lead - 1;

            var key = new System.Text.StringBuilder(end - lead + 4);
            if (!negative)
            {
                key.Append('2').Append((exponent + 50).ToString("D2", CultureInfo.InvariantCulture));
                key.Append(digits, lead, end - lead);
            }
            else
            {
                key.Append('0').Append((50 - exponent).ToString("D2", CultureInfo.InvariantCulture));
                for (int i = lead; i < end; i++) key.Append((char)('9' - (digits[i] - '0')));
                key.Append('~');
            }
            return key.ToString();
        }

        // ---- Exact conversions between the types C# compares in. ----

        internal static bool IsIntegral(Type t) =>
            t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) ||
            t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);

        // Integral range, with char as 0..65535 and an enum as its underlying type.
        private static bool TryIntegralRange(Type t, out BigInteger min, out BigInteger max)
        {
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            if (t == typeof(char)) t = typeof(ushort);
            (min, max) = t switch
            {
                _ when t == typeof(sbyte) => (sbyte.MinValue, sbyte.MaxValue),
                _ when t == typeof(byte) => (byte.MinValue, byte.MaxValue),
                _ when t == typeof(short) => (short.MinValue, short.MaxValue),
                _ when t == typeof(ushort) => (ushort.MinValue, ushort.MaxValue),
                _ when t == typeof(int) => (int.MinValue, int.MaxValue),
                _ when t == typeof(uint) => (uint.MinValue, uint.MaxValue),
                _ when t == typeof(long) => (long.MinValue, long.MaxValue),
                _ when t == typeof(ulong) => ((BigInteger)ulong.MinValue, (BigInteger)ulong.MaxValue),
                _ => (BigInteger.One, BigInteger.Zero),
            };
            return min <= max;
        }

        /// <summary>
        /// True when every value of <paramref name="from"/> converts to <paramref name="to"/> without
        /// changing its value. Such a conversion also keeps order, so a comparison made in
        /// <paramref name="to"/> means the same as one made in <paramref name="from"/>.
        /// int to double is exact; long to double is not (2^53 + 1 rounds), and neither is a conversion
        /// to a narrower type.
        /// </summary>
        internal static bool IsExactWidening(Type from, Type to)
        {
            from = Underlying(from);
            to = Underlying(to);
            if (from == to) return true;

            if (TryIntegralRange(from, out var fMin, out var fMax))
            {
                if (to.IsEnum || to == typeof(char)) return false; // never an implicit widening target
                if (TryIntegralRange(to, out var tMin, out var tMax)) return tMin <= fMin && fMax <= tMax;
                if (to == typeof(decimal)) return true;
                if (to == typeof(double)) return BitsNeeded(fMin, fMax) <= 53;
                if (to == typeof(float)) return BitsNeeded(fMin, fMax) <= 24;
                return false;
            }
            return from == typeof(float) && to == typeof(double);
        }

        private static int BitsNeeded(BigInteger min, BigInteger max) =>
            (int)Math.Max(BigInteger.Abs(min).GetBitLength(), BigInteger.Abs(max).GetBitLength());

        /// <summary>
        /// Converts <paramref name="value"/> to <paramref name="target"/> when it has exactly that value
        /// there; otherwise false. NaN converts between float and double. Never rounds, truncates or
        /// wraps, so where it succeeds every C# conversion gives the same result.
        /// </summary>
        internal static bool TryConvertExact(object value, Type target, out object result)
        {
            result = null;
            if (value == null) return false;
            target = Underlying(target);
            Type source = value.GetType();
            if (source == target) { result = value; return true; }

            if (target.IsEnum)
            {
                if (!TryConvertExact(value, Enum.GetUnderlyingType(target), out var raw)) return false;
                result = Enum.ToObject(target, raw);
                return true;
            }
            if (target == typeof(char))
            {
                if (!TryConvertExact(value, typeof(ushort), out var raw)) return false;
                result = (char)(ushort)raw;
                return true;
            }

            // The source as a number: an enum by its value, a char by its code point.
            if (value is Enum)
                value = Convert.ChangeType(value, Enum.GetUnderlyingType(source), CultureInfo.InvariantCulture);
            else if (value is char ch)
                value = (int)ch;
            source = value.GetType();

            bool targetIntegral = TryIntegralRange(target, out var min, out var max);

            if (IsIntegral(source) || source == typeof(decimal))
            {
                decimal m = Convert.ToDecimal(value, CultureInfo.InvariantCulture); // exact for both
                if (targetIntegral)
                {
                    if (decimal.Truncate(m) != m) return false;
                    var whole = new BigInteger(m);
                    if (whole < min || whole > max) return false;
                    result = Convert.ChangeType(m, target, CultureInfo.InvariantCulture);
                    return true;
                }
                if (target == typeof(decimal)) { result = m; return true; }
                if ((target == typeof(double) || target == typeof(float)) && IsIntegral(source))
                {
                    var whole = new BigInteger(m);
                    double d = (double)whole;
                    if (target == typeof(float)) d = (float)d;
                    if (double.IsInfinity(d) || new BigInteger(d) != whole) return false;
                    result = target == typeof(float) ? (object)(float)d : d;
                    return true;
                }
                return false; // decimal to a binary float is not exact in general; C# never does it implicitly
            }

            if (source == typeof(double) || source == typeof(float))
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture); // float widens exactly
                if (target == typeof(double)) { result = d; return true; }
                if (target == typeof(float))
                {
                    float f = (float)d;
                    if (!double.IsNaN(d) && (double)f != d) return false;
                    result = f;
                    return true;
                }
                if (targetIntegral)
                {
                    if (double.IsNaN(d) || double.IsInfinity(d) || Math.Floor(d) != d) return false;
                    var whole = new BigInteger(d);
                    if (whole < min || whole > max) return false;
                    result = Convert.ChangeType(whole.ToString(CultureInfo.InvariantCulture), target, CultureInfo.InvariantCulture);
                    return true;
                }
                return false;
            }

            return false;
        }
    }
}
