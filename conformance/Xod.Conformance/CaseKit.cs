// The parts every case uses (PLAN-047): the case record, the two sources a case runs against, the
// encoding of result values as JSON, and the SQL semantics that C# doesn't have built in.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xod.Conformance
{
    /// <summary>
    /// One conformance case. <see cref="Rows"/> is the case's meaning in C#: run against the oracle it
    /// produces the expected answer, run against XOD it produces XOD's answer. The SQL text must mean the
    /// same thing; a reviewer checks the two side by side.
    /// </summary>
    public sealed record Case(string Id, string Sql, string Columns, bool Ordered, Func<ISource, IEnumerable<object[]>> Rows)
    {
        public string Group { get; init; }
        /// <summary>An open issue that makes XOD answer wrongly; the runner expects the failure and fails when it stops.</summary>
        public string Known { get; init; }
        /// <summary>"password": only the password fixture. "no-password": only the others.</summary>
        public string Requires { get; init; }
        /// <summary>The expected error code (PLAN-045 section 9); such a case has no rows and no C# meaning.</summary>
        public string Error { get; init; }
        /// <summary>False when the C# library can't give the SQL answer (the expected rows still come from the oracle).</summary>
        public bool RunOnXod { get; init; } = true;
        public string Note { get; init; }

        public IReadOnlyList<(string Name, string Type)> ColumnList =>
            string.IsNullOrEmpty(Columns)
                ? Array.Empty<(string, string)>()
                : Columns.Split(',').Select(c => c.Trim().Split(' ')).Select(p => (p[0], p[1])).ToArray();

        public bool AppliesTo(string fixture) => Requires switch
        {
            null => true,
            "password" => fixture == "password",
            "no-password" => fixture != "password",
            _ => throw new InvalidOperationException($"{Id}: unknown requirement {Requires}")
        };
    }

    /// <summary>Where a case reads its rows from.</summary>
    public interface ISource
    {
        /// <summary>Every row of a table, in stored order.</summary>
        IEnumerable<T> All<T>() where T : class;
        /// <summary>The rows matching a predicate. On XOD this is <c>Query&lt;T&gt;</c>, so XOD's own
        /// filtering and indexes answer it.</summary>
        IEnumerable<T> Where<T>(Expression<Func<T, bool>> predicate) where T : class;
    }

    public sealed class OracleSource : ISource
    {
        private readonly Dataset data = Dataset.Build();
        public IEnumerable<T> All<T>() where T : class => data.All<T>().ToList();
        public IEnumerable<T> Where<T>(Expression<Func<T, bool>> predicate) where T : class => data.All<T>().Where(predicate.Compile()).ToList();
    }

    public sealed class XodSource : ISource
    {
        private readonly XodContext db;
        public XodSource(XodContext db) => this.db = db;
        public IEnumerable<T> All<T>() where T : class => db.Select<T>().ToList();
        public IEnumerable<T> Where<T>(Expression<Func<T, bool>> predicate) where T : class => db.Query(predicate).ToList();
    }

    public static class Values
    {
        private const long MaxExactInJson = 1L << 53;

        public static readonly JsonSerializerOptions Json = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>A result value as JSON, by its SQL type (PLAN-047 section 4). Throws when the C# value
        /// doesn't fit the declared type, which catches a case written wrongly.</summary>
        public static JsonNode Encode(object v, string type)
        {
            if (v == null) return null;
            switch (type)
            {
                case "INTEGER":
                    long x = v switch
                    {
                        sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(v, CultureInfo.InvariantCulture),
                        ulong u when u <= long.MaxValue => (long)u,
                        _ => throw Mismatch(v, type)
                    };
                    return x >= -MaxExactInJson && x <= MaxExactInJson ? JsonValue.Create(x) : JsonValue.Create(x.ToString(CultureInfo.InvariantCulture));
                case "DOUBLE":
                    double d = v switch { float f => f, double dd => dd, _ => throw Mismatch(v, type) };
                    if (double.IsNaN(d) || double.IsInfinity(d)) return JsonValue.Create(d.ToString(CultureInfo.InvariantCulture));
                    return JsonValue.Create(d);
                case "DECIMAL":
                    return v switch
                    {
                        decimal m => JsonValue.Create(m.ToString(CultureInfo.InvariantCulture)),
                        ulong u => JsonValue.Create(u.ToString(CultureInfo.InvariantCulture)),
                        _ => throw Mismatch(v, type)
                    };
                case "TEXT":
                    return v switch
                    {
                        string s => JsonValue.Create(s),
                        char c => JsonValue.Create(c.ToString()),
                        Enum e => JsonValue.Create(e.ToString()),
                        _ => throw Mismatch(v, type)
                    };
                case "BOOLEAN":
                    return v is bool b ? JsonValue.Create(b) : throw Mismatch(v, type);
                case "UUID":
                    return v is Guid g ? JsonValue.Create(g.ToString("D")) : throw Mismatch(v, type);
                case "TIMESTAMP":
                    if (v is not DateTime dt || dt.Kind == DateTimeKind.Local) throw Mismatch(v, type);
                    return JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture));
                case "TIMESTAMPTZ":
                    return v is DateTimeOffset dto ? JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)) : throw Mismatch(v, type);
                case "DATE":
                    return v is DateOnly day ? JsonValue.Create(day.ToString("O", CultureInfo.InvariantCulture)) : throw Mismatch(v, type);
                case "TIME":
                    return v is TimeOnly t ? JsonValue.Create(t.ToString("O", CultureInfo.InvariantCulture)) : throw Mismatch(v, type);
                case "INTERVAL":
                    return v is TimeSpan ts ? JsonValue.Create(ts.ToString("c", CultureInfo.InvariantCulture)) : throw Mismatch(v, type);
                case "MAP":
                    if (v is not IDictionary<string, string> map) throw Mismatch(v, type);
                    var o = new JsonObject();
                    foreach (var kv in map.OrderBy(k => k.Key, StringComparer.Ordinal)) o[kv.Key] = kv.Value;
                    return o;
                case "ARRAY":
                    if (v is string || v is not IEnumerable items) throw Mismatch(v, type);
                    return new JsonArray(items.Cast<object>().Select(i => Encode(i, "TEXT")).ToArray());
                default:
                    throw new ArgumentException("unknown SQL type " + type);
            }
        }

        private static Exception Mismatch(object v, string type) =>
            new InvalidCastException($"a {v.GetType().Name} value ({v}) is not {type}");

        public static List<string> EncodeRows(Case c, IEnumerable<object[]> rows)
        {
            var cols = c.ColumnList;
            var result = new List<string>();
            foreach (var row in rows)
            {
                if (row.Length != cols.Count) throw new InvalidOperationException($"{c.Id}: row has {row.Length} values for {cols.Count} columns");
                var arr = new JsonArray(row.Select((v, i) => Encode(v, cols[i].Type)).ToArray());
                result.Add(arr.ToJsonString(Json));
            }
            return result;
        }

        /// <summary>Unordered answers compare as sorted lists of rows.</summary>
        public static List<string> Canonical(Case c, List<string> rows) =>
            c.Ordered ? rows : rows.OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    /// <summary>SQL meaning that C# doesn't provide (PLAN-045 section 8).</summary>
    public static class Sql
    {
        public static object[] R(params object[] values) => values;

        /// <summary>LIKE: % any run, _ one character, optional escape; case-sensitive, or not for ILIKE.
        /// NULL when either side is NULL.</summary>
        public static bool? Like(string value, string pattern, char? escape = null, bool ignoreCase = false)
        {
            if (value == null || pattern == null) return null;
            var tokens = new List<(char Kind, char Ch)>(); // '%', '_', or 'c' for a literal
            for (int i = 0; i < pattern.Length; i++)
            {
                char ch = pattern[i];
                if (escape.HasValue && ch == escape.Value && i + 1 < pattern.Length) tokens.Add(('c', pattern[++i]));
                else if (ch == '%') tokens.Add(('%', ch));
                else if (ch == '_') tokens.Add(('_', ch));
                else tokens.Add(('c', ch));
            }
            bool Eq(char a, char b) => ignoreCase ? char.ToLowerInvariant(a) == char.ToLowerInvariant(b) : a == b;
            bool Match(int vi, int ti)
            {
                if (ti == tokens.Count) return vi == value.Length;
                var (kind, ch) = tokens[ti];
                if (kind == '%')
                {
                    for (int k = vi; k <= value.Length; k++) if (Match(k, ti + 1)) return true;
                    return false;
                }
                if (vi == value.Length) return false;
                return (kind == '_' || Eq(value[vi], ch)) && Match(vi + 1, ti + 1);
            }
            return Match(0, 0);
        }

        /// <summary>Ordinal text order with NULLs last (the ascending default).</summary>
        public static IOrderedEnumerable<T> AscNullsLast<T, TKey>(this IEnumerable<T> src, Func<T, TKey> key, IComparer<TKey> cmp = null) =>
            src.OrderBy(x => key(x) == null).ThenBy(key, cmp ?? Comparer<TKey>.Default);

        public static IOrderedEnumerable<T> ThenAscNullsLast<T, TKey>(this IOrderedEnumerable<T> src, Func<T, TKey> key, IComparer<TKey> cmp = null) =>
            src.ThenBy(x => key(x) == null).ThenBy(key, cmp ?? Comparer<TKey>.Default);

        /// <summary>Descending with NULLs first (the descending default).</summary>
        public static IOrderedEnumerable<T> DescNullsFirst<T, TKey>(this IEnumerable<T> src, Func<T, TKey> key, IComparer<TKey> cmp = null) =>
            src.OrderBy(x => key(x) != null).ThenByDescending(key, cmp ?? Comparer<TKey>.Default);

        public static readonly StringComparer Ordinal = StringComparer.Ordinal;

        /// <summary>SUM over values that may be NULL: NULL when every value is NULL (or there are none).</summary>
        public static decimal? SumOrNull(IEnumerable<decimal?> values)
        {
            var present = values.Where(v => v.HasValue).ToList();
            return present.Count == 0 ? null : present.Sum();
        }

        public static long? SumOrNull(IEnumerable<int?> values)
        {
            var present = values.Where(v => v.HasValue).ToList();
            return present.Count == 0 ? null : present.Sum(v => (long)v.Value);
        }

        /// <summary>ROUND(x, n): half away from zero.</summary>
        public static decimal Round(decimal x, int n) => Math.Round(x, n, MidpointRounding.AwayFromZero);
    }

    public static class Repo
    {
        public static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "XodPlusPlus.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("XodPlusPlus.sln not found above " + AppContext.BaseDirectory);
        }

        public static string Conformance => Path.Combine(Root(), "conformance");
        public static readonly string[] Fixtures = { "xml", "json", "binary", "password" };

        public static StorageFormat FormatOf(string fixture) => fixture switch
        {
            "json" => StorageFormat.Json,
            "binary" => StorageFormat.Binary,
            _ => StorageFormat.Xml
        };

        public static string PasswordOf(string fixture) => fixture == "password" ? Dataset.Password : null;

        /// <summary>
        /// Deletes what the context that wrote a fixture held, the database file's lock file and the folder's
        /// <c>xod.lock</c>: they are the open contexts', not the database's content (PLAN-046 section 9), and a
        /// fixture is committed without them. Called once the context is closed.
        /// </summary>
        public static void DropLockFiles(string folder)
        {
            foreach (string f in Directory.GetFiles(folder, "*.lock")) File.Delete(f);
        }
    }
}
