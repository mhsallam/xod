using System;
using System.Collections.Generic;
using System.Linq;
using Xod.Helpers;

namespace Xod.Sql
{
    // The scalar functions of version 1 (PLAN-045 section 5, PLAN-060 section 3): each checks its arguments' types
    // when the query is bound, and NULL in gives NULL out unless it says otherwise.
    internal sealed partial class SqlBinder
    {
        private static readonly HashSet<string> DateParts = new() { "YEAR", "QUARTER", "MONTH", "DAY", "DOW", "DOY" };
        private static readonly HashSet<string> ClockParts = new() { "HOUR", "MINUTE", "SECOND" };
        private static readonly HashSet<string> TruncUnits = new() { "year", "quarter", "month", "week", "day", "hour", "minute", "second" };

        private static BExpr ScalarFunction(string name, List<BExpr> args)
        {
            switch (name)
            {
                case "LOWER":
                    Arity(name, args, 1);
                    return F(name, SqlType.Text, Args(name, args, SqlType.Text), (_, a) => ((string)a[0]).ToLowerInvariant());
                case "UPPER":
                    Arity(name, args, 1);
                    return F(name, SqlType.Text, Args(name, args, SqlType.Text), (_, a) => ((string)a[0]).ToUpperInvariant());
                case "LENGTH":
                case "CHAR_LENGTH":
                case "CHARACTER_LENGTH":
                    Arity(name, args, 1);
                    return F(name, SqlType.Integer, Args(name, args, SqlType.Text), (_, a) => SqlValues.Length((string)a[0]));
                case "TRIM":
                    // The parser puts the side first: TRIM(side, s [, characters]).
                    Arity(name, args, 2, 3);
                    return F(name, SqlType.Text, Args(name, args, SqlType.Text),
                        (_, a) => SqlValues.Trim((string)a[1], (string)a[0], a.Length > 2 ? (string)a[2] : null));
                case "SUBSTRING":
                    Arity(name, args, 2, 3);
                    return F(name, SqlType.Text, Args(name, args, SqlType.Text, SqlType.Integer, SqlType.Integer),
                        (_, a) => SqlValues.Substring((string)a[0], (long)a[1], a.Length > 2 ? (long)a[2] : null));
                case "REPLACE":
                    Arity(name, args, 3);
                    return F(name, SqlType.Text, Args(name, args, SqlType.Text, SqlType.Text, SqlType.Text),
                        (_, a) => ((string)a[1]).Length == 0 ? a[0] : ((string)a[0]).Replace((string)a[1], (string)a[2], StringComparison.Ordinal));
                case "POSITION":
                    Arity(name, args, 2);
                    return F(name, SqlType.Integer, Args(name, args, SqlType.Text, SqlType.Text), (_, a) => SqlValues.Position((string)a[0], (string)a[1]));
                case "ABS":
                {
                    Arity(name, args, 1);
                    var x = NumberOrInterval(name, args[0]);
                    return F(name, x.Type, new List<BExpr> { x }, (_, a) => Abs(a[0]));
                }
                case "ROUND":
                {
                    Arity(name, args, 1, 2);
                    var list = new List<BExpr> { Number(name, args[0]) };
                    if (args.Count > 1) list.Add(Expect(args[1], SqlType.Integer, name));
                    return F(name, list[0].Type, list, (_, a) => Round(a[0], a.Length > 1 ? (long)a[1] : 0));
                }
                case "FLOOR":
                {
                    Arity(name, args, 1);
                    var x = Number(name, args[0]);
                    return F(name, x.Type, new List<BExpr> { x }, (_, a) => a[0] switch { decimal m => Math.Floor(m), double d => Math.Floor(d), var v => v });
                }
                case "CEIL":
                case "CEILING":
                {
                    Arity(name, args, 1);
                    var x = Number(name, args[0]);
                    return F(name, x.Type, new List<BExpr> { x }, (_, a) => a[0] switch { decimal m => Math.Ceiling(m), double d => Math.Ceiling(d), var v => v });
                }
                case "EXTRACT":
                    return Extract(args);
                case "DATE_TRUNC":
                    return DateTrunc(args);
                case "CURRENT_DATE":
                    Arity(name, args, 0);
                    return F(name, SqlType.Date, args, (env, _) => DateOnly.FromDateTime(env.Exec.Now.DateTime));
                case "CURRENT_TIMESTAMP":
                    Arity(name, args, 0);
                    return F(name, SqlType.TimestampTz, args, (env, _) => env.Exec.Now);
                case "COALESCE":
                {
                    if (args.Count == 0) throw new XodSqlException("XQ001", "COALESCE takes at least one argument");
                    var type = CommonType(args, "COALESCE");
                    var list = args.Select(a => Coerce(a, type)).ToList();
                    return new BCoalesce { Args = list, Type = type, Elem = list.FirstOrDefault(x => x.Type == type)?.Elem ?? SqlType.Null };
                }
                case "NULLIF":
                {
                    Arity(name, args, 2);
                    var (a0, a1) = CoercePair(args[0], args[1]);
                    if (!SqlValues.Comparable(a0.Type, a1.Type))
                        throw new XodSqlException("XQ005", $"a {Name(a0.Type)} can't be compared with a {Name(a1.Type)}");
                    return F(name, a0.Type, new List<BExpr> { a0, a1 },
                        (_, a) => a[0] == null ? null : a[1] == null || SqlValues.Compare(a[0], a[1]) != 0 ? a[0] : null, takesNulls: true);
                }
                case "VERIFY_HASH":
                {
                    Arity(name, args, 2);
                    var list = Args(name, args, SqlType.Text, SqlType.Text);
                    string method = (list[0] as BColumn)?.Info?.Crypto;
                    if (method == "aes256")
                        throw new XodSqlException("XQ005", $"{((BColumn)list[0]).Name} is encrypted, not hashed: VERIFY_HASH doesn't apply");
                    return F(name, SqlType.Boolean, list, (_, a) => VerifyHash((string)a[0], (string)a[1], method));
                }
                case "CARDINALITY":
                {
                    Arity(name, args, 1);
                    if (args[0].Type != SqlType.Array && args[0].Type != SqlType.Null)
                        throw new XodSqlException("XQ005", $"CARDINALITY takes an ARRAY, not a {Name(args[0].Type)}");
                    return F(name, SqlType.Integer, args, (_, a) => (long)((IReadOnlyList<object>)a[0]).Count);
                }
                case "$ISTRUE":
                case "$ISFALSE":
                    return new BIsTruth { Operand = Condition(args[0], "IS TRUE"), Value = name == "$ISTRUE", Type = SqlType.Boolean };
            }
            throw new XodSqlException("XQ001", $"no function named {name}");
        }

        private static BFunc F(string name, SqlType type, List<BExpr> args, Func<Env, object[], object> impl, bool takesNulls = false) =>
            new() { Name = name, Args = args, Impl = impl, TakesNulls = takesNulls, Type = type };

        private static void Arity(string name, List<BExpr> args, int min, int max = -1)
        {
            if (max < 0) max = min;
            if (args.Count < min || args.Count > max)
            {
                string count = min == max ? $"{min}" : $"{min} or {max}";
                throw new XodSqlException("XQ001", $"{name} takes {count} argument{(max == 1 ? "" : "s")}, not {args.Count}");
            }
        }

        /// <summary>The arguments checked against their types, in order (the last type repeats).</summary>
        private static List<BExpr> Args(string name, List<BExpr> args, params SqlType[] types)
        {
            var result = new List<BExpr>(args.Count);
            for (int i = 0; i < args.Count; i++)
                result.Add(Expect(args[i], types[Math.Min(i, types.Length - 1)], name));
            return result;
        }

        private static BExpr Number(string name, BExpr e)
        {
            if (e.Type != SqlType.Null && !SqlTypes.IsNumeric(e.Type))
                throw new XodSqlException("XQ005", $"{name} takes a number, not a {Name(e.Type)}");
            return e;
        }

        private static BExpr NumberOrInterval(string name, BExpr e)
        {
            if (e.Type != SqlType.Null && !SqlTypes.IsNumeric(e.Type) && e.Type != SqlType.Interval)
                throw new XodSqlException("XQ005", $"{name} takes a number or an INTERVAL, not a {Name(e.Type)}");
            return e;
        }

        private static object Abs(object v)
        {
            switch (v)
            {
                case long l:
                    if (l == long.MinValue) throw SqlValues.Overflow();
                    return Math.Abs(l);
                case decimal m:
                    return Math.Abs(m);
                case double d:
                    return Math.Abs(d);
                case TimeSpan s:
                    if (s == TimeSpan.MinValue) throw SqlValues.Overflow();
                    return s.Duration();
            }
            return v;
        }

        /// <summary>ROUND(x, n): half away from zero (G1); a negative <c>n</c> rounds to tens, hundreds...</summary>
        private static object Round(object v, long n)
        {
            try
            {
                switch (v)
                {
                    case decimal m:
                        if (n >= 0) return Math.Round(m, (int)Math.Min(n, 28), MidpointRounding.AwayFromZero);
                        if (n < -28) return 0m;
                        decimal f = Pow10(-n);
                        return Math.Round(m / f, 0, MidpointRounding.AwayFromZero) * f;
                    case double d:
                        if (n > 15) return d;
                        if (n >= 0) return Math.Round(d, (int)n, MidpointRounding.AwayFromZero);
                        double g = Math.Pow(10, -n);
                        // Beyond 10^308 every finite double rounds to 0 (Infinity * 0 would be NaN).
                        if (double.IsInfinity(g)) return double.IsFinite(d) ? 0.0 : d;
                        return Math.Round(d / g, 0, MidpointRounding.AwayFromZero) * g;
                    case long l:
                        if (n >= 0) return l;
                        if (n < -18) return 0L;
                        decimal h = Pow10(-n);
                        return checked((long)(Math.Round(l / h, 0, MidpointRounding.AwayFromZero) * h));
                }
            }
            catch (OverflowException)
            {
                throw SqlValues.Overflow();
            }
            return v;
        }

        private static decimal Pow10(long k)
        {
            decimal f = 1m;
            for (long i = 0; i < k; i++) f *= 10m;
            return f;
        }

        private static BExpr Extract(List<BExpr> args)
        {
            string part = (string)((BConst)args[0]).Value;
            var x = args[1];
            bool ok = x.Type switch
            {
                SqlType.Timestamp or SqlType.TimestampTz or SqlType.Date or SqlType.Null => DateParts.Contains(part) || ClockParts.Contains(part),
                SqlType.Time => ClockParts.Contains(part),
                SqlType.Interval => part == "DAY" || ClockParts.Contains(part),
                _ => throw new XodSqlException("XQ005", $"EXTRACT takes a date, a time or an INTERVAL, not a {Name(x.Type)}"),
            };
            if (!ok)
                throw new XodSqlException("XQ005", $"EXTRACT has no {part} of a {Name(x.Type)} (it has YEAR, QUARTER, MONTH, DAY, HOUR, MINUTE, SECOND, DOW and DOY)");
            // SECOND keeps its fraction, as the standard; the other parts are whole numbers.
            var type = part == "SECOND" ? SqlType.Decimal : SqlType.Integer;
            return F("EXTRACT", type, new List<BExpr> { args[0], x }, (_, a) => ExtractPart(part, a[1]));
        }

        private static object ExtractPart(string part, object v)
        {
            switch (v)
            {
                case TimeSpan s:
                    return part switch
                    {
                        "DAY" => (long)s.Days,
                        "HOUR" => (long)s.Hours,
                        "MINUTE" => (long)s.Minutes,
                        _ => (decimal)(s.Ticks % TimeSpan.TicksPerMinute) / TimeSpan.TicksPerSecond,
                    };
                case TimeOnly t:
                    return ClockPart(part, t.Hour, t.Minute, t.Ticks);
                case DateOnly d:
                    return DatePart(part, d.ToDateTime(TimeOnly.MinValue));
                case DateTimeOffset o:
                    // Its own clock: there is no session time zone (PLAN-060 section 3).
                    return DatePart(part, o.DateTime);
                case DateTime t:
                    return DatePart(part, t);
            }
            throw new XodSqlException("XQ005", $"EXTRACT doesn't apply to a {SqlValues.Describe(v)}");
        }

        private static object DatePart(string part, DateTime t) => part switch
        {
            "YEAR" => (long)t.Year,
            "QUARTER" => (long)((t.Month - 1) / 3 + 1),
            "MONTH" => (long)t.Month,
            "DAY" => (long)t.Day,
            "DOW" => (long)(int)t.DayOfWeek,
            "DOY" => (long)t.DayOfYear,
            _ => ClockPart(part, t.Hour, t.Minute, t.TimeOfDay.Ticks),
        };

        private static object ClockPart(string part, int hour, int minute, long ticksOfDay) => part switch
        {
            "HOUR" => (long)hour,
            "MINUTE" => (long)minute,
            _ => (decimal)(ticksOfDay % TimeSpan.TicksPerMinute) / TimeSpan.TicksPerSecond,
        };

        private static BExpr DateTrunc(List<BExpr> args)
        {
            Arity("DATE_TRUNC", args, 2);
            var unit = Expect(args[0], SqlType.Text, "DATE_TRUNC");
            var x = args[1];
            if (x.Type != SqlType.Timestamp && x.Type != SqlType.TimestampTz && x.Type != SqlType.Date && x.Type != SqlType.Null)
                throw new XodSqlException("XQ005", $"DATE_TRUNC takes a TIMESTAMP, a TIMESTAMPTZ or a DATE, not a {Name(x.Type)}");
            if (unit is BConst { Value: string u })
                CheckUnit(u);
            var type = x.Type == SqlType.Null ? SqlType.Timestamp : x.Type;
            return F("DATE_TRUNC", type, new List<BExpr> { unit, x }, (_, a) => Truncate((string)a[0], a[1]));
        }

        private static string CheckUnit(string unit)
        {
            string u = unit.ToLowerInvariant();
            if (!TruncUnits.Contains(u))
                throw new XodSqlException("XQ005", $"DATE_TRUNC has no unit '{unit}' (it has year, quarter, month, week, day, hour, minute and second)");
            return u;
        }

        private static object Truncate(string unit, object v)
        {
            string u = CheckUnit(unit);
            return v switch
            {
                DateTime t => Trunc(u, t),
                DateTimeOffset o => new DateTimeOffset(Trunc(u, o.DateTime), o.Offset),
                DateOnly d => DateOnly.FromDateTime(Trunc(u, d.ToDateTime(TimeOnly.MinValue))),
                _ => throw new XodSqlException("XQ005", $"DATE_TRUNC doesn't apply to a {SqlValues.Describe(v)}"),
            };
        }

        // The Kind stays: a UTC value truncated is still UTC (G3).
        private static DateTime Trunc(string u, DateTime t) => u switch
        {
            "year" => new DateTime(t.Year, 1, 1, 0, 0, 0, t.Kind),
            "quarter" => new DateTime(t.Year, (t.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, t.Kind),
            "month" => new DateTime(t.Year, t.Month, 1, 0, 0, 0, t.Kind),
            "week" => t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7)),
            "day" => t.Date,
            "hour" => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind),
            "minute" => new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind),
            _ => new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Kind),
        };

        /// <summary>VERIFY_HASH(stored, plain): the plain text hashed by the column's method (or, for a column whose
        /// method isn't known, the one its length gives), compared with the stored hash.</summary>
        private static object VerifyHash(string stored, string plain, string method)
        {
            method ??= stored.Length switch
            {
                32 => "md5",
                40 => "sha1",
                64 => "sha256",
                128 => "sha512",
                _ => null,
            };
            string hash = method switch
            {
                "md5" => CryptoHelper.GetMD5HashData(plain),
                "sha1" => CryptoHelper.GetSHA1HashData(plain),
                "sha256" => CryptoHelper.GetSHA256HashData(plain),
                "sha512" => CryptoHelper.GetSHA512HashData(plain),
                _ => null,
            };
            return hash != null && string.Equals(hash, stored, StringComparison.OrdinalIgnoreCase);
        }
    }
}
