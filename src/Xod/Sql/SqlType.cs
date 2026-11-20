using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Xod.Sql
{
    /// <summary>The SQL types of PLAN-045 section 4. <see cref="Null"/> is the type of a bare <c>NULL</c>, which
    /// takes the type of what it meets.</summary>
    internal enum SqlType : byte
    {
        Null = 0,
        Integer,
        Double,
        Decimal,
        Text,
        Boolean,
        Uuid,
        Timestamp,
        TimestampTz,
        Date,
        Time,
        Interval,
        Map,
        Array,
    }

    internal static class SqlTypes
    {
        public static string Name(SqlType t) => t switch
        {
            SqlType.Integer => "INTEGER",
            SqlType.Double => "DOUBLE",
            SqlType.Decimal => "DECIMAL",
            SqlType.Text => "TEXT",
            SqlType.Boolean => "BOOLEAN",
            SqlType.Uuid => "UUID",
            SqlType.Timestamp => "TIMESTAMP",
            SqlType.TimestampTz => "TIMESTAMPTZ",
            SqlType.Date => "DATE",
            SqlType.Time => "TIME",
            SqlType.Interval => "INTERVAL",
            SqlType.Map => "MAP",
            SqlType.Array => "ARRAY",
            _ => "NULL",
        };

        public static bool IsNumeric(SqlType t) => t == SqlType.Integer || t == SqlType.Double || t == SqlType.Decimal;

        /// <summary>The type two numbers combine to: DOUBLE if either is, else DECIMAL if either is, else INTEGER.</summary>
        public static SqlType Numeric(SqlType a, SqlType b) =>
            a == SqlType.Double || b == SqlType.Double ? SqlType.Double
            : a == SqlType.Decimal || b == SqlType.Decimal ? SqlType.Decimal
            : SqlType.Integer;

        /// <summary>The type a cast names (several spellings per type, as BI tools write them).</summary>
        public static bool TryParseName(string name, out SqlType type)
        {
            switch (name.ToUpperInvariant())
            {
                case "INTEGER": case "INT": case "BIGINT": case "SMALLINT": case "TINYINT": case "LONG":
                    type = SqlType.Integer; return true;
                case "DOUBLE": case "FLOAT": case "REAL": case "DOUBLE PRECISION":
                    type = SqlType.Double; return true;
                case "DECIMAL": case "NUMERIC": case "MONEY":
                    type = SqlType.Decimal; return true;
                case "TEXT": case "VARCHAR": case "CHAR": case "STRING": case "NVARCHAR": case "CHARACTER": case "CHARACTER VARYING":
                    type = SqlType.Text; return true;
                case "BOOLEAN": case "BOOL": case "BIT":
                    type = SqlType.Boolean; return true;
                case "UUID": case "UNIQUEIDENTIFIER": case "GUID":
                    type = SqlType.Uuid; return true;
                case "TIMESTAMP": case "DATETIME":
                    type = SqlType.Timestamp; return true;
                case "TIMESTAMPTZ": case "DATETIMEOFFSET":
                    type = SqlType.TimestampTz; return true;
                case "DATE":
                    type = SqlType.Date; return true;
                case "TIME":
                    type = SqlType.Time; return true;
                case "INTERVAL":
                    type = SqlType.Interval; return true;
                default:
                    type = SqlType.Null; return false;
            }
        }

        // ---- the manifest's types (PLAN-046 section 10.2) ----

        /// <summary>The manifest type of a scalar .NET type: finer than the SQL types, as writes need the C# range.
        /// Every scalar XOD stores (ValueHelper.IsScalarType) has one; a property of any other value type is refused when
        /// its type loads (ISS-142). The last line, <c>text</c>, is for a column a table written before that may hold.</summary>
        public static string StoredTypeOf(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t.IsEnum) return "enum";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(sbyte)) return "int8";
            if (t == typeof(byte)) return "uint8";
            if (t == typeof(short)) return "int16";
            if (t == typeof(ushort)) return "uint16";
            if (t == typeof(int)) return "int32";
            if (t == typeof(uint)) return "uint32";
            if (t == typeof(long)) return "int64";
            if (t == typeof(ulong)) return "uint64";
            if (t == typeof(float)) return "float32";
            if (t == typeof(double)) return "float64";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(char)) return "char";
            if (t == typeof(string)) return "text";
            if (t == typeof(Guid)) return "uuid";
            if (t == typeof(DateTime)) return "timestamp";
            if (t == typeof(DateTimeOffset)) return "timestamptz";
            if (t == typeof(DateOnly)) return "date";
            if (t == typeof(TimeOnly)) return "time";
            if (t == typeof(TimeSpan)) return "interval";
            return "text";
        }

        /// <summary>The .NET type a stored scalar of this manifest type is parsed as (ValueHelper.ParseScalar), or
        /// null for <c>enum</c>, which a reader without the class reads by the members the manifest lists.</summary>
        public static Type ClrTypeOf(string stored) => stored switch
        {
            "bool" => typeof(bool),
            "int8" => typeof(sbyte),
            "uint8" => typeof(byte),
            "int16" => typeof(short),
            "uint16" => typeof(ushort),
            "int32" => typeof(int),
            "uint32" => typeof(uint),
            "int64" => typeof(long),
            "uint64" => typeof(ulong),
            "float32" => typeof(float),
            "float64" => typeof(double),
            "decimal" => typeof(decimal),
            "char" => typeof(char),
            "text" => typeof(string),
            "uuid" => typeof(Guid),
            "timestamp" => typeof(DateTime),
            "timestamptz" => typeof(DateTimeOffset),
            "date" => typeof(DateOnly),
            "time" => typeof(TimeOnly),
            "interval" => typeof(TimeSpan),
            _ => null,
        };

        /// <summary>The SQL type of a manifest type (PLAN-045 section 4).</summary>
        public static SqlType OfStored(string stored) => stored switch
        {
            "bool" => SqlType.Boolean,
            "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" or "int64" => SqlType.Integer,
            "uint64" => SqlType.Decimal,
            "float32" or "float64" => SqlType.Double,
            "decimal" => SqlType.Decimal,
            "char" or "text" or "enum" => SqlType.Text,
            "uuid" => SqlType.Uuid,
            "timestamp" => SqlType.Timestamp,
            "timestamptz" => SqlType.TimestampTz,
            "date" => SqlType.Date,
            "time" => SqlType.Time,
            "interval" => SqlType.Interval,
            "map" => SqlType.Map,
            "list" => SqlType.Array,
            _ => SqlType.Text,
        };

        // ---- values ----

        /// <summary>
        /// A .NET value as the SQL value of its type: whole numbers as <c>long</c> (a <c>ulong</c> as <c>decimal</c>,
        /// since its column is DECIMAL), a <c>float</c> widened to <c>double</c> as C# holds it (G2), a <c>char</c>
        /// and an enum as their text (an enum's name, as stored), a map as a read-only dictionary of text keys, a
        /// list as a read-only list.
        /// </summary>
        public static object FromClr(object v)
        {
            switch (v)
            {
                case null: return null;
                case string s: return s;
                case long l: return l;
                case int i: return (long)i;
                case short sh: return (long)sh;
                case byte b: return (long)b;
                case sbyte sb: return (long)sb;
                case ushort us: return (long)us;
                case uint ui: return (long)ui;
                case ulong ul: return (decimal)ul;
                case float f: return (double)f;
                case double d: return d;
                case decimal m: return m;
                case bool bo: return bo;
                case char c: return c.ToString();
                case Guid g: return g;
                case DateTime dt: return dt;
                case DateTimeOffset dto: return dto;
                case DateOnly don: return don;
                case TimeOnly ton: return ton;
                case TimeSpan ts: return ts;
                case Enum e: return e.ToString();
                case System.Collections.IEnumerable map when Helpers.ValueHelper.IsMapType(map.GetType()):
                {
                    var result = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var kv in Helpers.ValueHelper.MapEntries(map))
                        result[Helpers.ValueHelper.FormatScalar(kv.Key) ?? ""] = FromClr(kv.Value);
                    return new SqlMap(result);
                }
                case System.Collections.IEnumerable items:
                    return items.Cast<object>().Select(FromClr).ToList().AsReadOnly();
                default:
                    return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The SQL type of a parameter's .NET value.</summary>
        public static bool TryTypeOfValue(object v, out SqlType type)
        {
            type = v switch
            {
                null => SqlType.Null,
                string or char or Enum => SqlType.Text,
                long or int or short or byte or sbyte or ushort or uint => SqlType.Integer,
                ulong ul => ul <= long.MaxValue ? SqlType.Integer : SqlType.Decimal,
                float or double => SqlType.Double,
                decimal => SqlType.Decimal,
                bool => SqlType.Boolean,
                Guid => SqlType.Uuid,
                DateTime => SqlType.Timestamp,
                DateTimeOffset => SqlType.TimestampTz,
                DateOnly => SqlType.Date,
                TimeOnly => SqlType.Time,
                TimeSpan => SqlType.Interval,
                System.Collections.IEnumerable when Helpers.ValueHelper.IsMapType(v.GetType()) => SqlType.Map,
                System.Collections.IEnumerable => SqlType.Array,
                _ => (SqlType)255,
            };
            return (byte)type != 255;
        }

        /// <summary>A parameter's value as its SQL value (<see cref="TryTypeOfValue"/> gave its type).</summary>
        public static object ParameterValue(object v) => v is ulong ul && ul <= long.MaxValue ? (long)ul : FromClr(v);

        /// <summary>
        /// A SQL value as a .NET type, for <c>Sql&lt;T&gt;</c>: the inverse of <see cref="FromClr"/>, exact or not at
        /// all. A number goes to any numeric type that holds it exactly (an INTEGER to an <c>int</c> in range, a
        /// DECIMAL to a <c>long</c> only when whole); text to a string, a <c>char</c> of one character, or an
        /// enum by its member's name; a MAP to a dictionary and an ARRAY to a list or an array, item by item. False
        /// when it can't, or when the type can't hold a NULL.
        /// </summary>
        public static bool TryToClr(object v, Type target, out object result)
        {
            result = null;
            Type u = Nullable.GetUnderlyingType(target) ?? target;
            if (v == null)
                return !target.IsValueType || u != target;
            bool collection = v is SqlMap || v is IReadOnlyList<object>;
            if (u == typeof(object) || (!collection && u.IsInstanceOfType(v)))
            {
                result = v;
                return true;
            }
            try
            {
                switch (v)
                {
                    case long or decimal or double when IsNumberType(u):
                        return TryNumber(v, u, out result);
                    case string s when u == typeof(char):
                        if (s.Length != 1) return false;
                        result = s[0];
                        return true;
                    case string s when u.IsEnum:
                        if (!Enum.TryParse(u, s, false, out object member)) return false;
                        result = member;
                        return true;
                    case SqlMap map:
                        return TryMap(map, u, out result);
                    case IReadOnlyList<object> list:
                        return TryList(list, u, out result);
                }
            }
            catch (OverflowException)
            {
            }
            return false;
        }

        private static bool IsNumberType(Type t) =>
            t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(sbyte) || t == typeof(ulong) ||
            t == typeof(uint) || t == typeof(ushort) || t == typeof(byte) || t == typeof(decimal) || t == typeof(double) || t == typeof(float);

        private static bool TryNumber(object v, Type u, out object result)
        {
            result = null;
            if (u == typeof(double) || u == typeof(float))
            {
                double d = v is double x ? x : v is long l ? l : (double)(decimal)v;
                if (u == typeof(float))
                {
                    // Exactly: a float column read by SQL is the float widened (G2), which narrows back to it.
                    float f = (float)d;
                    if ((double)f != d && !double.IsNaN(d)) return false;
                    result = f;
                    return true;
                }
                result = d;
                return true;
            }
            decimal m;
            if (v is double dv)
            {
                if (double.IsNaN(dv) || double.IsInfinity(dv)) return false;
                m = (decimal)dv;
                if ((double)m != dv) return false;
            }
            else
            {
                m = v is long l2 ? l2 : (decimal)v;
            }
            if (u == typeof(decimal))
            {
                result = m;
                return true;
            }
            if (m != decimal.Truncate(m)) return false;
            result = Convert.ChangeType(m, u, CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryMap(SqlMap map, Type u, out object result)
        {
            result = null;
            Type[] types = Helpers.ValueHelper.MapArguments(u);
            if (types == null) return false;
            Type keyType = types[0], valueType = types[1];
            // The type a read of the row makes too (ValueHelper.MapToRead); none where it can't be made (XQ005).
            Type made = Helpers.ValueHelper.MapToRead(u);
            if (made == null) return false;
            var dict = (System.Collections.IDictionary)Activator.CreateInstance(made);
            foreach (var kv in map)
            {
                object key = Helpers.ValueHelper.ParseScalar(keyType, kv.Key);
                if (key == null || !TryToClr(kv.Value, valueType, out object value)) return false;
                dict[key] = value;
            }
            result = dict;
            return true;
        }

        private static bool TryList(IReadOnlyList<object> list, Type u, out object result)
        {
            result = null;
            Type item = u.IsArray ? u.GetElementType()
                : u.IsGenericType && u.GetGenericArguments().Length == 1 ? u.GetGenericArguments()[0] : null;
            if (item == null) return false;
            var items = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(item));
            foreach (var x in list)
            {
                if (!TryToClr(x, item, out object converted)) return false;
                items.Add(converted);
            }
            if (u.IsArray)
            {
                var array = Array.CreateInstance(item, items.Count);
                items.CopyTo(array, 0);
                result = array;
                return true;
            }
            if (!u.IsAssignableFrom(items.GetType())) return false;
            result = items;
            return true;
        }
    }

    /// <summary>A MAP value: text keys in the order the row stores them.</summary>
    internal sealed class SqlMap : System.Collections.ObjectModel.ReadOnlyDictionary<string, object>
    {
        public SqlMap(IDictionary<string, object> entries) : base(entries) { }
    }
}
