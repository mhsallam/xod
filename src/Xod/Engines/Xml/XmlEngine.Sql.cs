using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using Xod.Helpers;
using Xod.Services;
using Xod.Sql;

namespace Xod.Engines.Xml
{
    // XOD SQL (PLAN-045, PLAN-060): the engine's side, the tables and their rows. The language lives in Xod.Sql.
    public partial class XmlEngine
    {
        internal SqlResult Sql(string sql, object[] positional, IReadOnlyDictionary<string, object> named)
        {
            if (sql == null)
                throw new ArgumentNullException(nameof(sql));
            var stmt = SqlParser.Parse(sql, out int questionMarks);
            positional ??= Array.Empty<object>();
            if (positional.Length > questionMarks)
                throw new XodSqlException("XQ012", $"{positional.Length} value{(positional.Length == 1 ? " was" : "s were")} given for {questionMarks} ? parameter{(questionMarks == 1 ? "" : "s")}");

            // Writes with relations take the engine's write lock, which this read lock keeps out; a standalone table's
            // write takes the engine's read lock and its table's write lock, which the query's table read locks keep
            // out (SqlSource). Both are held from the first table read to the last row computed.
            EnterRead();
            try
            {
                var now = DateTimeOffset.Now;
                // The tables to take before binding, in order: those an earlier attempt found it needed (SqlSource.Lock).
                var tables = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    var source = new SqlSource(this);
                    try
                    {
                        source.LockInOrder(tables);
                        return Run(stmt, source, now, positional, named);
                    }
                    catch (SqlSource.TakeInOrder again)
                    {
                        // Each attempt starts again with at least one more table, of the database's finitely many.
                        tables.UnionWith(again.Tables);
                    }
                    finally
                    {
                        source.Release();
                    }
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        private static SqlResult Run(SelectStmt stmt, SqlSource source, DateTimeOffset now, object[] positional, IReadOnlyDictionary<string, object> named)
        {
            var exec = new Exec { Db = source, Now = now };
            source.Exec = exec;
            var bound = new SqlBinder(source, exec, positional, named).Bind(stmt);
            var env = new Env { Exec = exec };
            if (stmt.Explain)
            {
                var lines = new List<string>();
                bound.Explain(env, lines, "");
                return new SqlResult(new[] { new SqlColumn("plan", "TEXT") }, lines.Select(l => new object[] { l }).ToList(), exec.Warnings);
            }
            var rows = bound.Run(env);
            var columns = bound.Output
                .Select(o => new SqlColumn(o.Name, SqlTypes.Name(o.Type == SqlType.Null ? SqlType.Text : o.Type)))
                .ToList();
            return new SqlResult(columns, rows, exec.Warnings);
        }

        /// <summary>
        /// The database as one query reads it (ISqlDatabase): its tables (a table file each, or a class this context
        /// knows), each one's schema (from its class, or the manifest its file records, or every column as text), and
        /// its rows decoded column by column. Each table's read lock is taken the first time the query touches it and
        /// held to the end, in one order for every query (by full name, ordinal ignoring case; see <see cref="Lock"/>).
        /// </summary>
        private sealed class SqlSource : ISqlDatabase
        {
            private static readonly MethodInfo PlanMethod = typeof(IndexService).GetMethod(nameof(IndexService.TryPlanExpression), BindingFlags.Instance | BindingFlags.NonPublic);

            private readonly XmlEngine engine;
            private readonly bool fromClasses;
            private readonly Dictionary<string, TableSchema> schemas = new(StringComparer.Ordinal);
            private readonly Dictionary<string, XFile> files = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<(string Name, bool Attribute)>> storedNames = new(StringComparer.Ordinal);
            private readonly List<ReaderWriterLockSlim> held = new();
            private readonly HashSet<string> locked = new(StringComparer.OrdinalIgnoreCase);
            // The tables this thread held before the query started (SQL run inside a read's predicate, or while an
            // InsertRange reads its items): the query can't give them back.
            private readonly HashSet<string> outside = new(StringComparer.OrdinalIgnoreCase);
            private readonly string outsideLast;
            private string last; // the table that sorts last among those held, the outside ones included
            private IReadOnlyList<string> catalog;

            public SqlSource(XmlEngine engine)
            {
                this.engine = engine;
                fromClasses = engine.options.SqlSchema == SqlSchemaSource.Classes;
                foreach (var (table, _) in engine.heldTableLocks.Value.Values)
                {
                    outside.Add(table);
                    if (outsideLast == null || Order(table, outsideLast) > 0)
                        outsideLast = table;
                }
                last = outsideLast;
            }

            private static int Order(string a, string b) => StringComparer.OrdinalIgnoreCase.Compare(a, b);

            public Exec Exec { get; set; }

            public void Release()
            {
                for (int i = held.Count - 1; i >= 0; i--)
                {
                    engine.LettingGo(held[i]);
                    held[i].ExitReadLock();
                }
                held.Clear();
                locked.Clear();
                last = outsideLast;
            }

            /// <summary>Takes these tables' read locks, in ascending order, before the query is bound.</summary>
            public void LockInOrder(IEnumerable<string> fullNames)
            {
                foreach (var fullName in fullNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    Lock(fullName);
            }

            /// <summary>The query needs a table that sorts before one it holds, and can't have it now: it gives every
            /// table back and starts again, taking these first, in order.</summary>
            public sealed class TakeInOrder : Exception
            {
                public TakeInOrder(IReadOnlyList<string> tables) : base("SQL takes its tables' locks again, in order") => Tables = tables;
                public IReadOnlyList<string> Tables { get; }
            }

            // ---- tables ----

            private IReadOnlyList<string> Catalog() =>
                catalog ??= engine.ioService.TableFileNames().Select(n => n.Substring(0, n.Length - ".xtab".Length)).ToList();

            public TableSchema FindTable(string name)
            {
                var names = Catalog();
                var exact = names.Where(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count > 1)
                    throw new XodSqlException("XQ003", $"{name} names {string.Join(" and ", exact)}: their names differ only by case");
                if (exact.Count == 1)
                    return Schema(exact[0]);

                var byShortName = names.Where(n => string.Equals(SchemaManifest.ShortNameOf(n), name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byShortName.Count > 1)
                    throw new XodSqlException("XQ003", $"{name} is ambiguous: it names {string.Join(" and ", byShortName)}; write the full name in quotes");
                if (byShortName.Count == 1)
                    return Schema(byShortName[0]);

                // A class this context has used, with no table file yet: an empty table.
                if (fromClasses)
                {
                    var type = engine.propertyService.RegisteredTypeByName(SchemaManifest.ShortNameOf(name));
                    if (type != null && (string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return Schema(type.FullName);
                }
                throw new XodSqlException("XQ002", $"no table named {name}");
            }

            public TableSchema TableByFullName(string fullName)
            {
                if (schemas.TryGetValue(fullName, out var known))
                    return known;
                if (Catalog().Contains(fullName, StringComparer.Ordinal) || (fromClasses && ClassOf(fullName) != null))
                    return Schema(fullName);
                return null;
            }

            /// <summary>The class a table's rows were written by, when this program has it and SQL is to use it.</summary>
            private Type ClassOf(string fullName)
            {
                if (!fromClasses) return null;
                var type = engine.propertyService.RegisteredType(fullName);
                return type != null && type.IsClass && !type.IsAbstract && type.Assembly != typeof(XmlEngine).Assembly ? type : null;
            }

            private XFile File(string fullName)
            {
                if (!files.TryGetValue(fullName, out var file))
                {
                    Lock(fullName);
                    file = engine.ioService.OpenFileOrCreate(fullName + ".xtab", ClassOf(fullName), openOnly: true);
                    files[fullName] = file;
                }
                return file;
            }

            /// <summary>
            /// A table's read lock, held to the end of the query. A writer waiting for a table's write lock keeps new
            /// readers out of it (ReaderWriterLockSlim prefers writers), so two queries that each hold a table and wait
            /// for the other's, behind waiting writers, would wait forever. So a query waits for a lock only when the
            /// table sorts after every table it holds: one order for every query, and a writer holds at most one table
            /// lock (a write to a table with relations takes the engine's write lock, which the query's read lock keeps
            /// out). A table that sorts earlier is taken only if it is free now; otherwise <see cref="TakeInOrder"/>
            /// starts the query again with its tables taken in order.
            /// The tables this thread held before the query count as held: one of them is entered again without waiting
            /// (a thread in a lock passes the writers waiting for it), and the query waits only above them all. A table
            /// below them that isn't free now can't be waited for, and they can't be given back to start again, so the
            /// query is refused, as a write inside a read is (EnterWrite).
            /// </summary>
            private void Lock(string fullName)
            {
                if (locked.Contains(fullName)) return;
                var tableLock = engine.GetTableLock(fullName);
                if (outside.Contains(fullName))
                {
                    tableLock.EnterReadLock();
                }
                else if (last == null || Order(fullName, last) > 0)
                {
                    tableLock.EnterReadLock();
                    last = fullName;
                }
                else if (!tableLock.TryEnterReadLock(0))
                {
                    if (outside.Count == 0)
                        throw new TakeInOrder(locked.Append(fullName).ToList());
                    throw new XodException($"A SQL query inside a read or write of {string.Join(", ", outside)} on the same " +
                        $"thread (a query's predicate, for example) needs {fullName}, which another thread is writing or waiting " +
                        "to write: waiting for it there could wait forever. Run the query after that read or write has returned.");
                }
                engine.Holding(tableLock, fullName);
                locked.Add(fullName);
                held.Add(tableLock);
            }

            private TableSchema Schema(string fullName)
            {
                if (schemas.TryGetValue(fullName, out var schema))
                    return schema;
                var file = File(fullName);
                var type = ClassOf(fullName);
                if (type != null)
                {
                    // The class's manifest, built by the code that writes it, so both sources give one schema.
                    engine.propertyService.LoadType(type);
                    schema = SchemaManifest.Read(SchemaManifest.Build(type, engine.propertyService), fullName, type, engine.propertyService.Properties(type.FullName));
                }
                else
                {
                    var stored = file?.Root()?.Element(SchemaManifest.Element);
                    schema = stored == null ? null : SchemaManifest.Read(stored, fullName);
                    if (schema == null)
                    {
                        schema = SchemaManifest.Untyped(fullName, Entities(file));
                        Exec.Warn("XQ013", stored == null
                            ? $"{schema.ShortName} records no schema (written before 2.0.2, or never written by the library since): every column is read as text"
                            : $"{schema.ShortName} records a schema of a later version: every column is read as text");
                    }
                }
                schemas[fullName] = schema;
                return schema;
            }

            private IEnumerable<XElement> Entities(XFile file) =>
                file == null ? Enumerable.Empty<XElement>() : engine.StoredRows(file).Select(r => r.Row.Elements().FirstOrDefault()).Where(e => e != null);

            public ColumnSchema UnlistedColumn(TableSchema table, string name)
            {
                if (!storedNames.TryGetValue(table.FullName, out var names))
                {
                    names = new List<(string, bool)>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var entity in Entities(File(table.FullName)))
                    {
                        foreach (var a in entity.Attributes())
                            if (seen.Add(a.Name.LocalName)) names.Add((a.Name.LocalName, true));
                        foreach (var e in entity.Elements())
                            if (e.Attribute("refType") == null && e.Attribute("collType") == null && seen.Add(e.Name.LocalName))
                                names.Add((e.Name.LocalName, false));
                    }
                    storedNames[table.FullName] = names;
                }
                var hits = names.Where(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
                    && table.Relation(n.Name) == null && table.Column(n.Name) == null).ToList();
                if (hits.Select(h => h.Name).Distinct(StringComparer.Ordinal).Count() > 1)
                    throw new XodSqlException("XQ003", $"{table.ShortName}'s rows hold {string.Join(" and ", hits.Select(h => h.Name))}: names that differ only by case");
                if (hits.Count == 0)
                    return null;
                return new ColumnSchema { Name = hits[0].Name, StoredType = "text", IsAttribute = hits.All(h => h.Attribute) };
            }

            // ---- rows ----

            public IReadOnlyList<SqlRow> Rows(TableSchema table, IReadOnlyCollection<string> onlyCodes)
            {
                var file = File(table.FullName);
                var rows = new List<SqlRow>();
                if (file == null)
                    return rows;

                HashSet<string> codes = null, pages = null;
                if (onlyCodes != null)
                {
                    if (onlyCodes.Count == 0)
                        return rows;
                    codes = new HashSet<string>(onlyCodes, StringComparer.Ordinal);
                    pages = new HashSet<string>(codes.Select(c => c.Substring(0, Math.Max(0, c.IndexOf('.')))), StringComparer.Ordinal);
                }

                var decoders = table.Columns.Select(c => Decoder(table, c)).ToArray();
                foreach (var (page, row) in engine.StoredRows(file, wantPage: pages == null ? null : pages.Contains))
                {
                    string code = page + "." + row.Attribute("code")?.Value;
                    if (codes != null && !codes.Contains(code))
                        continue;
                    var entity = row.Elements().FirstOrDefault();
                    if (entity == null)
                        continue;
                    var values = new object[decoders.Length];
                    for (int i = 0; i < decoders.Length; i++)
                        values[i] = decoders[i](entity);
                    rows.Add(new SqlRow { Code = code, Values = values, Links = Links(table, entity) });
                }
                return rows;
            }

            /// <summary>The item codes a row holds for each relation (its to-one element's text, a collection's items').</summary>
            private static string[][] Links(TableSchema table, XElement entity)
            {
                if (table.Relations.Count == 0)
                    return null;
                var links = new string[table.Relations.Count][];
                for (int i = 0; i < links.Length; i++)
                {
                    var rel = table.Relations[i];
                    var e = entity.Element(rel.Name);
                    if (e == null || StoredValue.IsNil(e))
                        continue;
                    links[i] = rel.IsCollection
                        ? e.Elements().Select(x => x.Value).Where(v => v.Length > 0).ToArray()
                        : e.HasElements || e.Value.Length == 0 ? null : new[] { e.Value };
                }
                return links;
            }

            private Func<XElement, object> Decoder(TableSchema table, ColumnSchema c)
            {
                if (c.Property != null)
                    return entity => FromClass(table, c, entity);
                if (!table.Typed)
                    return entity => StoredText(c, entity);
                return entity => FromManifest(table, c, entity);
            }

            /// <summary>A column read as the library reads its property (StoredScalar and the collection readers),
            /// then given its SQL value. An encrypted one opens only with a secret the caller gave (XQ008).</summary>
            private object FromClass(TableSchema table, ColumnSchema c, XElement entity)
            {
                var prop = c.Property;
                try
                {
                    switch (c.StoredType)
                    {
                        case "map":
                        {
                            var e = entity.Element(prop.PropertyName);
                            object map = e == null ? StoredValue.ReadBackOfMissing(prop) : StoredValue.IsNil(e) ? null : ReadStoredMap(prop, e);
                            return SqlTypes.FromClr(map);
                        }
                        case "list":
                        {
                            var e = entity.Element(prop.PropertyName);
                            object list = e == null ? StoredValue.ReadBackOfMissing(prop) : StoredValue.IsNil(e) ? null : ReadStoredScalarItems(prop, StoredItemType(prop), e);
                            return SqlTypes.FromClr(list);
                        }
                    }
                    if (prop.Encryption == CryptoMethod.AES256 && prop.PropertyType == typeof(string))
                    {
                        string text = RawText(c, entity, out bool nil);
                        if (nil) return null;
                        if (string.IsNullOrEmpty(text)) return StoredValue.ReadBackOfMissing(prop);
                        return Decrypt(table, c, text);
                    }
                    return SqlTypes.FromClr(engine.StoredScalar(prop, entity));
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
                {
                    return Unparsed(table, c);
                }
            }

            /// <summary>A column read by its manifest (PLAN-046 section 10, PLAN-060 section 2.3): nil is NULL; an empty
            /// text is <c>''</c> for a plain text column and otherwise counts as left out; a value left out reads as the
            /// manifest's <c>default</c>, or NULL for a nullable or text column, or its type's default.</summary>
            private object FromManifest(TableSchema table, ColumnSchema c, XElement entity)
            {
                switch (c.StoredType)
                {
                    case "map":
                    {
                        var e = entity.Element(c.Name);
                        if (e == null) return c.Default == "" ? new SqlMap(new Dictionary<string, object>()) : null;
                        if (StoredValue.IsNil(e)) return null;
                        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
                        foreach (var entry in e.Elements("Entry"))
                        {
                            string key = entry.Attribute("key")?.Value;
                            if (key == null) continue;
                            string text = StoredValue.ItemText(entry);
                            // A null where the value type has none reads as its default, as the library reads it.
                            entries[key] = text == null
                                ? (c.ValueNullable ? null : TypeDefault(c.ValueType, c.Members))
                                : Parse(table, c, c.ValueType, text);
                        }
                        return new SqlMap(entries);
                    }
                    case "list":
                    {
                        var e = entity.Element(c.Name);
                        if (e == null) return c.Default == "" ? new List<object>().AsReadOnly() : null;
                        if (StoredValue.IsNil(e)) return null;
                        var items = new List<object>();
                        foreach (var item in e.Elements())
                        {
                            string text = StoredValue.ItemText(item);
                            items.Add(text == null
                                ? (c.ItemNullable ? null : TypeDefault(c.ItemType, c.Members))
                                : Parse(table, c, c.ItemType, text));
                        }
                        return items.AsReadOnly();
                    }
                }

                string value = RawText(c, entity, out bool nil);
                if (nil) return null;
                bool plainText = c.StoredType == "text" && c.Crypto == null;
                if (value == null || (value.Length == 0 && !plainText))
                {
                    if (c.Default != null) return Parse(table, c, c.StoredType, c.Default);
                    return c.Nullable ? null : TypeDefault(c.StoredType, c.Members);
                }
                if (c.Crypto == "aes256")
                    return Decrypt(table, c, value);
                return Parse(table, c, c.StoredType, value);
            }

            /// <summary>A column of a table that records no schema, or that its schema doesn't list: its stored text.</summary>
            private static object StoredText(ColumnSchema c, XElement entity)
            {
                string text = RawText(c, entity, out bool nil);
                return nil ? null : text;
            }

            private static string RawText(ColumnSchema c, XElement entity, out bool nil)
            {
                nil = false;
                if (c.IsAttribute)
                    return entity.Attribute(c.Name)?.Value;
                var e = entity.Element(c.Name);
                if (e == null)
                    return null;
                nil = StoredValue.IsNil(e);
                return nil ? null : e.Value;
            }

            private object Parse(TableSchema table, ColumnSchema c, string storedType, string text)
            {
                Type clr = SqlTypes.ClrTypeOf(storedType);
                if (clr == null)
                    return text; // text, an enum's name, as stored
                try
                {
                    return SqlTypes.FromClr(ValueHelper.ParseScalar(clr, text));
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
                {
                    return Unparsed(table, c);
                }
            }

            /// <summary>The value of a type a reader gives one its row leaves out: an enum's member numbered 0.</summary>
            private static object TypeDefault(string storedType, List<KeyValuePair<string, string>> members)
            {
                if (storedType == "enum")
                    return members?.FirstOrDefault(m => m.Value == "0").Key ?? "0";
                Type clr = SqlTypes.ClrTypeOf(storedType);
                return clr == null || !clr.IsValueType ? null : SqlTypes.FromClr(Activator.CreateInstance(clr));
            }

            private object Unparsed(TableSchema table, ColumnSchema c)
            {
                Exec.Warn("XQ014", $"{table.ShortName}.{c.Name} holds values that aren't {SqlTypes.Name(c.Type)}: they read as NULL");
                return null;
            }

            private object Decrypt(TableSchema table, ColumnSchema c, string stored)
            {
                if (engine.propertyKeys.TryDecryptWithSecret(stored, out string plain))
                    return plain;
                Exec.Warn("XQ008", $"{table.ShortName}.{c.Name} is encrypted and the database wasn't opened with its key: it reads as NULL");
                return null;
            }

            // ---- indexes ----

            /// <summary>The item codes the index planner Query&lt;T&gt; uses gives for these conditions, put as a typed
            /// expression on the class: only those whose values convert exactly to the property's type, and never a
            /// range on text, an enum (C# orders an enum by its number, SQL by its name) or a float.</summary>
            public IReadOnlyCollection<string> IndexCandidates(TableSchema table, IReadOnlyList<IndexCondition> conditions)
            {
                if (!engine.options.EnableIndexing || table.ClrType == null)
                    return null;
                var type = table.ClrType;
                var x = Expression.Parameter(type, "x");
                Expression body = null;
                foreach (var c in conditions)
                {
                    var values = c.Values.Where(v => v != null).ToList();
                    if (values.Count == 0)
                        return Array.Empty<string>(); // compared with NULL: no row
                    if (c.Op != "IN" && values.Count < c.Values.Count)
                        return Array.Empty<string>();
                    var term = Term(x, c.Column.Property, c.Op, values);
                    if (term != null)
                        body = body == null ? term : Expression.AndAlso(body, term);
                }
                if (body == null)
                    return null;

                engine.EnsureIndexHydrated(type);
                var lambda = Expression.Lambda(typeof(Func<,>).MakeGenericType(type, typeof(bool)), body, x);
                var args = new object[] { lambda, null, null };
                bool planned = (bool)PlanMethod.MakeGenericMethod(type).Invoke(engine.indexService, args);
                return planned ? (IReadOnlyCollection<string>)args[1] : null;
            }

            private static Expression Term(ParameterExpression x, PropertyInfoItem prop, string op, List<object> values)
            {
                if (prop == null)
                    return null;
                Type pt = prop.PropertyType;
                Type u = Nullable.GetUnderlyingType(pt) ?? pt;
                bool ordered = IsInteger(u) || u == typeof(decimal);
                var member = Expression.Property(x, prop.PropertyName);
                if (op == "IN")
                {
                    Expression any = null;
                    foreach (var v in values)
                    {
                        object k = Exact(u, v);
                        if (k == null) return null;
                        var eq = Expression.Equal(member, Expression.Constant(k, pt));
                        any = any == null ? eq : Expression.OrElse(any, eq);
                    }
                    return any;
                }
                if (op != "=" && !ordered)
                    return null;
                object key = Exact(u, values[0]);
                if (key == null)
                    return null;
                var constant = Expression.Constant(key, pt);
                return op switch
                {
                    "=" => Expression.Equal(member, constant),
                    "<" => Expression.LessThan(member, constant),
                    "<=" => Expression.LessThanOrEqual(member, constant),
                    ">" => Expression.GreaterThan(member, constant),
                    ">=" => Expression.GreaterThanOrEqual(member, constant),
                    _ => null,
                };
            }

            private static bool IsInteger(Type t) =>
                t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
                t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

            /// <summary>A SQL value as the property's type when it converts exactly, or null.</summary>
            private static object Exact(Type u, object v)
            {
                try
                {
                    if (IsInteger(u))
                    {
                        decimal d;
                        if (v is long l) d = l;
                        else if (v is decimal m && m == decimal.Truncate(m)) d = m;
                        else return null;
                        return Convert.ChangeType(d, u, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    if (u == typeof(decimal))
                        return v switch { long l => (decimal)l, decimal m => m, _ => null };
                    if (u == typeof(string))
                        return v as string;
                    if (u == typeof(char))
                        return v is string s && s.Length == 1 ? s[0] : null;
                    if (u == typeof(bool))
                        return v as bool?;
                    if (u == typeof(Guid))
                        return v as Guid?;
                    if (u.IsEnum)
                        return v is string name && Enum.TryParse(u, name, false, out object member) && member.ToString() == name ? member : null;
                }
                catch (OverflowException)
                {
                }
                return null;
            }
        }
    }
}
