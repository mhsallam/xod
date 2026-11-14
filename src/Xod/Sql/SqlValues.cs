using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Xod.Helpers;

namespace Xod.Sql
{
    /// <summary>
    /// What SQL does with values (PLAN-045 section 8): comparison (text ordinal, numbers across types, instants),
    /// arithmetic (INTEGER overflow an error, not a wrap: XQ007; division by zero an error for INTEGER and DECIMAL:
    /// XQ010), casts, LIKE, and the text form of a value. A value is always the .NET value of its SQL type
    /// (SqlTypes.FromClr), and NULL is null.
    /// </summary>
    internal static class SqlValues
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- comparison ----

        /// <summary>True when values of these two types can be compared (a NULL compares with anything).</summary>
        public static bool Comparable(SqlType a, SqlType b)
        {
            if (a == SqlType.Null || b == SqlType.Null) return true;
            if (a == SqlType.Map || a == SqlType.Array || b == SqlType.Map || b == SqlType.Array) return false;
            if (SqlTypes.IsNumeric(a) && SqlTypes.IsNumeric(b)) return true;
            if ((a == SqlType.Date && b == SqlType.Timestamp) || (a == SqlType.Timestamp && b == SqlType.Date)) return true;
            return a == b;
        }

        /// <summary>The order of two values that aren't NULL, of comparable types.</summary>
        public static int Compare(object a, object b)
        {
            switch (a)
            {
                case long la when b is long lb: return la.CompareTo(lb);
                case string sa when b is string sb: return Math.Sign(string.CompareOrdinal(sa, sb));
                case decimal ma when b is decimal mb: return ma.CompareTo(mb);
                case double da when b is double db: return da.CompareTo(db);
            }
            if (IsNumber(a) && IsNumber(b))
            {
                if (a is double || b is double) return ToDouble(a).CompareTo(ToDouble(b));
                return ToDecimal(a).CompareTo(ToDecimal(b));
            }
            switch (a)
            {
                case bool ba when b is bool bb: return ba.CompareTo(bb);
                case Guid ga when b is Guid gb: return Math.Sign(string.CompareOrdinal(ga.ToString("D"), gb.ToString("D")));
                case DateTime ta when b is DateTime tb: return ta.CompareTo(tb);
                case DateTime ta when b is DateOnly db2: return ta.CompareTo(db2.ToDateTime(TimeOnly.MinValue));
                case DateOnly da2 when b is DateTime tb: return da2.ToDateTime(TimeOnly.MinValue).CompareTo(tb);
                case DateTimeOffset oa when b is DateTimeOffset ob: return oa.CompareTo(ob);
                case DateOnly da3 when b is DateOnly db3: return da3.CompareTo(db3);
                case TimeOnly ta2 when b is TimeOnly tb2: return ta2.CompareTo(tb2);
                case TimeSpan sa2 when b is TimeSpan sb2: return sa2.CompareTo(sb2);
            }
            throw new XodSqlException("XQ005", $"a {Describe(a)} can't be compared with a {Describe(b)}");
        }

        /// <summary>SQL equality of two values: NULL when either is NULL.</summary>
        public static bool? Equal(object a, object b) => a == null || b == null ? null : Compare(a, b) == 0;

        /// <summary>A comparison operator on two values: NULL when either is NULL.</summary>
        public static bool? Compare(string op, object a, object b)
        {
            if (a == null || b == null) return null;
            int c = Compare(a, b);
            return op switch
            {
                "=" => c == 0,
                "<>" => c != 0,
                "<" => c < 0,
                "<=" => c <= 0,
                ">" => c > 0,
                ">=" => c >= 0,
                _ => throw new InvalidOperationException(op),
            };
        }

        /// <summary>A key for grouping, DISTINCT and hash joins: equal exactly when SQL calls the values equal (numbers
        /// of different types and decimals of different scale included). NULL is its own key.</summary>
        public static object KeyOf(object v) => v switch
        {
            null => null,
            // decimal's Equals and GetHashCode go by value, so 2, 2.0 and 2.00 are one key.
            long l => (decimal)l,
            decimal m => m,
            double d => d == 0 ? 0d : d,
            DateTimeOffset dto => dto.UtcDateTime.Ticks,
            DateTime dt => dt.Ticks,
            Guid or string or bool or DateOnly or TimeOnly or TimeSpan => v,
            _ => new ListKey(v),
        };

        /// <summary>A key over several values (a row, or a group's values).</summary>
        public sealed class RowKey : IEquatable<RowKey>
        {
            private readonly object[] keys;
            private readonly int hash;

            public RowKey(IReadOnlyList<object> values)
            {
                keys = new object[values.Count];
                var h = new HashCode();
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i] = KeyOf(values[i]);
                    h.Add(keys[i]);
                }
                hash = h.ToHashCode();
            }

            public bool Equals(RowKey other)
            {
                if (other == null || other.keys.Length != keys.Length) return false;
                for (int i = 0; i < keys.Length; i++)
                    if (!Equals(keys[i], other.keys[i])) return false;
                return true;
            }

            public override bool Equals(object obj) => Equals(obj as RowKey);
            public override int GetHashCode() => hash;
        }

        // A MAP or ARRAY as a key (DISTINCT over them): equal when their text forms are.
        private sealed class ListKey : IEquatable<ListKey>
        {
            private readonly string text;
            public ListKey(object v) => text = TextOf(v);
            public bool Equals(ListKey other) => other != null && other.text == text;
            public override bool Equals(object obj) => Equals(obj as ListKey);
            public override int GetHashCode() => text.GetHashCode();
        }

        // ---- numbers ----

        public static bool IsNumber(object v) => v is long || v is decimal || v is double;

        public static double ToDouble(object v) => v switch
        {
            long l => l,
            decimal m => (double)m,
            double d => d,
            _ => throw new InvalidCastException(),
        };

        public static decimal ToDecimal(object v) => v switch
        {
            long l => l,
            decimal m => m,
            double d => (decimal)d,
            _ => throw new InvalidCastException(),
        };

        /// <summary>A value of one numeric type as another (INTEGER to DECIMAL or DOUBLE, DECIMAL to DOUBLE).</summary>
        public static object Widen(object v, SqlType to)
        {
            if (v == null) return null;
            return to switch
            {
                SqlType.Double => v is double ? v : ToDouble(v),
                SqlType.Decimal => v is decimal ? v : v is long l ? (decimal)l : v,
                _ => v,
            };
        }

        public static object Arithmetic(string op, object a, object b, SqlType result)
        {
            if (a == null || b == null) return null;
            try
            {
                switch (result)
                {
                    case SqlType.Integer:
                    {
                        long x = (long)a, y = (long)b;
                        return op switch
                        {
                            "+" => checked(x + y),
                            "-" => checked(x - y),
                            "*" => checked(x * y),
                            "/" => y == 0 ? throw DivisionByZero() : x == long.MinValue && y == -1 ? throw Overflow() : x / y,
                            "%" => y == 0 ? throw DivisionByZero() : y == -1 ? 0L : x % y,
                            _ => throw new InvalidOperationException(op),
                        };
                    }
                    case SqlType.Decimal:
                    {
                        decimal x = ToDecimal(a), y = ToDecimal(b);
                        return op switch
                        {
                            "+" => x + y,
                            "-" => x - y,
                            "*" => x * y,
                            "/" => y == 0 ? throw DivisionByZero() : x / y,
                            "%" => y == 0 ? throw DivisionByZero() : x % y,
                            _ => throw new InvalidOperationException(op),
                        };
                    }
                    case SqlType.Double:
                    {
                        double x = ToDouble(a), y = ToDouble(b);
                        return op switch
                        {
                            "+" => x + y,
                            "-" => x - y,
                            "*" => x * y,
                            "/" => x / y,
                            "%" => x % y,
                            _ => throw new InvalidOperationException(op),
                        };
                    }
                }
            }
            catch (OverflowException)
            {
                throw Overflow();
            }
            return TemporalArithmetic(op, a, b);
        }

        // Dates and times: a point plus or minus an interval, the interval between two points, days added to a date.
        private static object TemporalArithmetic(string op, object a, object b)
        {
            try
            {
                switch (a, b, op)
                {
                    case (DateTime t, TimeSpan s, "+"): return t + s;
                    case (DateTime t, TimeSpan s, "-"): return t - s;
                    case (TimeSpan s, DateTime t, "+"): return t + s;
                    case (DateTime t1, DateTime t2, "-"): return t1 - t2;
                    case (DateTimeOffset t, TimeSpan s, "+"): return t + s;
                    case (DateTimeOffset t, TimeSpan s, "-"): return t - s;
                    case (TimeSpan s, DateTimeOffset t, "+"): return t + s;
                    case (DateTimeOffset t1, DateTimeOffset t2, "-"): return t1 - t2;
                    case (DateOnly d, long n, "+"): return d.AddDays(checked((int)n));
                    case (long n, DateOnly d, "+"): return d.AddDays(checked((int)n));
                    case (DateOnly d, long n, "-"): return d.AddDays(checked((int)-n));
                    case (DateOnly d1, DateOnly d2, "-"): return (long)(d1.DayNumber - d2.DayNumber);
                    case (DateOnly d, TimeSpan s, "+"): return d.ToDateTime(TimeOnly.MinValue) + s;
                    case (DateOnly d, TimeSpan s, "-"): return d.ToDateTime(TimeOnly.MinValue) - s;
                    case (TimeOnly t, TimeSpan s, "+"): return t.Add(s);
                    case (TimeOnly t, TimeSpan s, "-"): return t.Add(-s);
                    case (TimeOnly t1, TimeOnly t2, "-"): return t1 - t2;
                    case (TimeSpan s1, TimeSpan s2, "+"): return s1 + s2;
                    case (TimeSpan s1, TimeSpan s2, "-"): return s1 - s2;
                    case (TimeSpan s, long n, "*"): return s * n;
                    case (long n, TimeSpan s, "*"): return s * n;
                    case (TimeSpan s, double n, "*"): return s * n;
                    case (TimeSpan s, decimal n, "*"): return s * (double)n;
                    case (TimeSpan s, long n, "/"): return n == 0 ? throw DivisionByZero() : s / n;
                    case (TimeSpan s, double n, "/"): return n == 0 ? throw DivisionByZero() : s / n;
                    case (TimeSpan s, decimal n, "/"): return n == 0 ? throw DivisionByZero() : s / (double)n;
                }
            }
            // An INTERVAL beyond TimeSpan's range, or times or divided by NaN (TimeSpan refuses it with an
            // ArgumentException): no INTERVAL holds the result.
            catch (Exception e) when (e is OverflowException || e is ArgumentException)
            {
                throw Overflow();
            }
            throw new XodSqlException("XQ005", $"'{op}' doesn't apply to a {Describe(a)} and a {Describe(b)}");
        }

        /// <summary>The type of <c>a op b</c> for arithmetic, or null when the types don't allow it (XQ005).</summary>
        public static SqlType? ArithmeticType(string op, SqlType a, SqlType b)
        {
            if (a == SqlType.Null && b == SqlType.Null) return SqlType.Null;
            if (a == SqlType.Null) a = b;
            if (b == SqlType.Null) b = a;
            if (SqlTypes.IsNumeric(a) && SqlTypes.IsNumeric(b)) return SqlTypes.Numeric(a, b);
            switch (a, b, op)
            {
                case (SqlType.Timestamp, SqlType.Interval, "+" or "-"): return SqlType.Timestamp;
                case (SqlType.Interval, SqlType.Timestamp, "+"): return SqlType.Timestamp;
                case (SqlType.Timestamp, SqlType.Timestamp, "-"): return SqlType.Interval;
                case (SqlType.TimestampTz, SqlType.Interval, "+" or "-"): return SqlType.TimestampTz;
                case (SqlType.Interval, SqlType.TimestampTz, "+"): return SqlType.TimestampTz;
                case (SqlType.TimestampTz, SqlType.TimestampTz, "-"): return SqlType.Interval;
                case (SqlType.Date, SqlType.Integer, "+" or "-"): return SqlType.Date;
                case (SqlType.Integer, SqlType.Date, "+"): return SqlType.Date;
                case (SqlType.Date, SqlType.Date, "-"): return SqlType.Integer;
                case (SqlType.Date, SqlType.Interval, "+" or "-"): return SqlType.Timestamp;
                case (SqlType.Time, SqlType.Interval, "+" or "-"): return SqlType.Time;
                case (SqlType.Time, SqlType.Time, "-"): return SqlType.Interval;
                case (SqlType.Interval, SqlType.Interval, "+" or "-"): return SqlType.Interval;
                case (SqlType.Interval, SqlType.Integer or SqlType.Double or SqlType.Decimal, "*" or "/"): return SqlType.Interval;
                case (SqlType.Integer or SqlType.Double or SqlType.Decimal, SqlType.Interval, "*"): return SqlType.Interval;
            }
            return null;
        }

        public static object Negate(object v)
        {
            try
            {
                return v switch
                {
                    null => null,
                    long l => checked(-l),
                    decimal m => -m,
                    double d => -d,
                    TimeSpan s => s.Negate(),
                    _ => throw new XodSqlException("XQ005", $"a {Describe(v)} can't be negated"),
                };
            }
            catch (OverflowException)
            {
                throw Overflow();
            }
        }

        public static XodSqlException Overflow() => new("XQ007", "a number is out of range");
        public static XodSqlException DivisionByZero() => new("XQ010", "division by zero");

        // ---- text ----

        /// <summary>The text form of a value, for CAST(x AS TEXT) and <c>||</c>: XOD's stored text form
        /// (ValueHelper.FormatScalar: invariant numbers, a decimal with its scale, round-trip dates), with booleans
        /// as <c>true</c>/<c>false</c>.</summary>
        public static string TextOf(object v) => v switch
        {
            null => null,
            string s => s,
            bool b => b ? "true" : "false",
            double d => d.ToString("R", Inv),
            Guid g => g.ToString("D"),
            SqlMap map => "{" + string.Join(", ", map.Select(kv => kv.Key + ": " + (TextOf(kv.Value) ?? "NULL"))) + "}",
            System.Collections.IEnumerable items => "[" + string.Join(", ", items.Cast<object>().Select(x => TextOf(x) ?? "NULL")) + "]",
            _ => ValueHelper.FormatScalar(v),
        };

        /// <summary>LIKE (and ILIKE): <c>%</c> any run, <c>_</c> one character, an optional escape character;
        /// case-sensitive, or by invariant lower case for ILIKE (D4). NULL when either side is NULL. Characters are
        /// Unicode code points, as LENGTH and SUBSTRING count them (PLAN-060 section 3).</summary>
        public static bool? Like(string value, string pattern, string escape, bool ignoreCase)
        {
            if (value == null || pattern == null) return null;
            int[] escapeChars = escape == null ? null : CodePoints(escape, false);
            if (escapeChars != null && escapeChars.Length != 1)
                throw new XodSqlException("XQ005", "an ESCAPE is one character");
            int? esc = escapeChars?[0];

            int[] p = CodePoints(pattern, ignoreCase);
            var kinds = new List<char>();
            var chars = new List<int>();
            for (int i = 0; i < p.Length; i++)
            {
                int ch = p[i];
                if (esc.HasValue && ch == esc.Value)
                {
                    if (i + 1 >= p.Length) throw new XodSqlException("XQ005", "a LIKE pattern ends with its escape character");
                    kinds.Add('c'); chars.Add(p[++i]);
                }
                else if (ch == '%') { kinds.Add('%'); chars.Add(ch); }
                else if (ch == '_') { kinds.Add('_'); chars.Add(ch); }
                else { kinds.Add('c'); chars.Add(ch); }
            }

            // Matching by dynamic programming over (value position, pattern position): linear in each, so a
            // pattern of many %s can't take exponential time.
            int[] v = CodePoints(value, ignoreCase);
            int n = v.Length, m = kinds.Count;
            var prev = new bool[m + 1];
            var cur = new bool[m + 1];
            prev[0] = true;
            for (int j = 1; j <= m; j++) prev[j] = prev[j - 1] && kinds[j - 1] == '%';
            for (int i = 1; i <= n; i++)
            {
                cur[0] = false;
                int vc = v[i - 1];
                for (int j = 1; j <= m; j++)
                {
                    cur[j] = kinds[j - 1] switch
                    {
                        '%' => cur[j - 1] || prev[j],
                        '_' => prev[j - 1],
                        _ => prev[j - 1] && vc == chars[j - 1],
                    };
                }
                (prev, cur) = (cur, prev);
            }
            return prev[m];
        }

        /// <summary>A text's code points (a lone surrogate counts as one), lowered (invariant) for ILIKE.</summary>
        private static int[] CodePoints(string s, bool lower)
        {
            var points = new List<int>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                bool pair = char.IsSurrogatePair(s, i);
                if (lower)
                {
                    string one = s.Substring(i, pair ? 2 : 1).ToLowerInvariant();
                    points.Add(one.Length == 2 && char.IsSurrogatePair(one, 0) ? char.ConvertToUtf32(one, 0) : one[0]);
                }
                else
                {
                    points.Add(pair ? char.ConvertToUtf32(s, i) : s[i]);
                }
                if (pair) i++;
            }
            return points.ToArray();
        }

        // ---- casts ----

        /// <summary>CAST(v AS type). Throws XQ005 when the value has no form in that type.</summary>
        public static object Cast(object v, SqlType to)
        {
            if (v == null) return null;
            try
            {
                switch (to)
                {
                    case SqlType.Text:
                        if (v is SqlMap || (v is System.Collections.IEnumerable && v is not string)) break;
                        return TextOf(v);
                    case SqlType.Integer:
                        switch (v)
                        {
                            case long: return v;
                            case decimal m: return (long)Math.Round(m, MidpointRounding.AwayFromZero);
                            case double d:
                                if (double.IsNaN(d) || double.IsInfinity(d)) throw Overflow();
                                return checked((long)Math.Round(d, MidpointRounding.AwayFromZero));
                            case bool b: return b ? 1L : 0L;
                            case string s when long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, Inv, out long l): return l;
                        }
                        break;
                    case SqlType.Decimal:
                        switch (v)
                        {
                            case long l: return (decimal)l;
                            case decimal: return v;
                            case double d: return (decimal)d;
                            case string s when decimal.TryParse(s.Trim(), NumberStyles.Float, Inv, out decimal m): return m;
                        }
                        break;
                    case SqlType.Double:
                        switch (v)
                        {
                            case long or decimal or double: return ToDouble(v);
                            case string s when double.TryParse(s.Trim(), NumberStyles.Float, Inv, out double d): return d;
                        }
                        break;
                    case SqlType.Boolean:
                        switch (v)
                        {
                            case bool: return v;
                            case long l: return l != 0;
                            case string s when SqlLiterals.TryParse(SqlType.Boolean, s, out object b): return b;
                        }
                        break;
                    case SqlType.Uuid:
                        if (v is Guid) return v;
                        if (v is string su && Guid.TryParse(su.Trim(), out Guid g)) return g;
                        break;
                    case SqlType.Timestamp:
                        switch (v)
                        {
                            case DateTime: return v;
                            case DateOnly d: return d.ToDateTime(TimeOnly.MinValue);
                            case DateTimeOffset o: return o.UtcDateTime;
                            case string s when SqlLiterals.TryParse(SqlType.Timestamp, s, out object t): return t;
                        }
                        break;
                    case SqlType.TimestampTz:
                        switch (v)
                        {
                            case DateTimeOffset: return v;
                            case DateTime t: return t.Kind == DateTimeKind.Local ? new DateTimeOffset(t) : new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc));
                            case DateOnly d: return new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                            case string s when SqlLiterals.TryParse(SqlType.TimestampTz, s, out object o): return o;
                        }
                        break;
                    case SqlType.Date:
                        switch (v)
                        {
                            case DateOnly: return v;
                            case DateTime t: return DateOnly.FromDateTime(t);
                            case DateTimeOffset o: return DateOnly.FromDateTime(o.DateTime);
                            case string s when SqlLiterals.TryParse(SqlType.Date, s, out object d): return d;
                        }
                        break;
                    case SqlType.Time:
                        switch (v)
                        {
                            case TimeOnly: return v;
                            case DateTime t: return TimeOnly.FromDateTime(t);
                            case DateTimeOffset o: return TimeOnly.FromDateTime(o.DateTime);
                            case TimeSpan s when s >= TimeSpan.Zero && s < TimeSpan.FromDays(1): return TimeOnly.FromTimeSpan(s);
                            case string s when SqlLiterals.TryParse(SqlType.Time, s, out object t): return t;
                        }
                        break;
                    case SqlType.Interval:
                        switch (v)
                        {
                            case TimeSpan: return v;
                            case TimeOnly t: return t.ToTimeSpan();
                            case string s when SqlLiterals.TryParse(SqlType.Interval, s, out object i): return i;
                        }
                        break;
                }
            }
            catch (OverflowException)
            {
                throw Overflow();
            }
            throw new XodSqlException("XQ005", $"{(v is string text ? "'" + text + "'" : "a " + Describe(v))} can't be cast to {SqlTypes.Name(to)}");
        }

        /// <summary>Whether CAST from one type to another can succeed for some value.</summary>
        public static bool CanCast(SqlType from, SqlType to)
        {
            if (from == SqlType.Null || from == to) return true;
            if (from == SqlType.Map || from == SqlType.Array || to == SqlType.Map || to == SqlType.Array) return false;
            if (to == SqlType.Text || from == SqlType.Text) return true;
            if (SqlTypes.IsNumeric(from) && SqlTypes.IsNumeric(to)) return true;
            return (from, to) switch
            {
                (SqlType.Boolean, SqlType.Integer) or (SqlType.Integer, SqlType.Boolean) => true,
                (SqlType.Date or SqlType.TimestampTz, SqlType.Timestamp) => true,
                (SqlType.Date or SqlType.Timestamp, SqlType.TimestampTz) => true,
                (SqlType.Timestamp or SqlType.TimestampTz, SqlType.Date) => true,
                (SqlType.Timestamp or SqlType.TimestampTz or SqlType.Interval, SqlType.Time) => true,
                (SqlType.Time, SqlType.Interval) => true,
                _ => false,
            };
        }

        public static string Describe(object v) => v switch
        {
            null => "NULL",
            long => "INTEGER",
            double => "DOUBLE",
            decimal => "DECIMAL",
            string => "TEXT",
            bool => "BOOLEAN",
            Guid => "UUID",
            DateTime => "TIMESTAMP",
            DateTimeOffset => "TIMESTAMPTZ",
            DateOnly => "DATE",
            TimeOnly => "TIME",
            TimeSpan => "INTERVAL",
            SqlMap => "MAP",
            _ => "ARRAY",
        };

        // ---- characters (code points: PLAN-060 section 3) ----

        public static long Length(string s)
        {
            long n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                n++;
            }
            return n;
        }

        /// <summary>The UTF-16 index where code point <paramref name="index"/> (from 0) starts, or the length.</summary>
        public static int CharIndex(string s, long index)
        {
            int i = 0;
            for (long k = 0; k < index && i < s.Length; k++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i += 2;
                else i++;
            }
            return i;
        }

        /// <summary>SUBSTRING(s, start, length) counted in characters from 1, as the standard: the characters from
        /// <c>start</c> to <c>start + length - 1</c> that exist.</summary>
        public static string Substring(string s, long start, long? length)
        {
            if (length < 0) throw new XodSqlException("XQ005", "SUBSTRING's length is negative");
            long from = Math.Max(start, 1);
            // Exclusive; past the end of any text when start + length doesn't fit in 64 bits.
            long to = length is long len && !(start > 0 && len > long.MaxValue - start) ? start + len : long.MaxValue;
            if (to <= from) return "";
            int a = CharIndex(s, from - 1);
            int b = to == long.MaxValue ? s.Length : CharIndex(s, to - 1);
            return a >= s.Length ? "" : s.Substring(a, Math.Max(0, b - a));
        }

        /// <summary>POSITION(needle IN s): where the first occurrence starts, in characters from 1; 0 when absent.</summary>
        public static long Position(string needle, string s)
        {
            int at = s.IndexOf(needle, StringComparison.Ordinal);
            return at < 0 ? 0 : Length(s.Substring(0, at)) + 1;
        }

        public static string Trim(string s, string side, string chars)
        {
            chars ??= " ";
            var set = chars.ToCharArray();
            return side switch
            {
                "LEADING" => s.TrimStart(set),
                "TRAILING" => s.TrimEnd(set),
                _ => s.Trim(set),
            };
        }
    }
}
