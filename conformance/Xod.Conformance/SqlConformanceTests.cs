// Runs the conformance set through XOD SQL (PLAN-060): each case's `sql` goes to db.Sql and must give the
// case's columns, types and rows, or its error code. Two ways a table gets its column types:
//  - from the program's classes, over the committed fixtures (written by 2.0.1, with no manifest);
//  - from the manifest only (SqlSchemaSource.Recorded, as `xod query` reads), over the same dataset written by
//    the library in the tree into a scratch folder, so the manifest it writes is judged by the same answers.
// Neither opens a no-password fixture with the application key: SQL reads an encrypted column only with a
// secret the caller gave, so crypto-002's NULL holds in both.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace Xod.Conformance
{
    public class SqlConformanceTests
    {
        private readonly ITestOutputHelper output;
        public SqlConformanceTests(ITestOutputHelper output) => this.output = output;

        [Theory]
        [InlineData("xml")]
        [InlineData("json")]
        [InlineData("binary")]
        [InlineData("password")]
        public void Sql_gives_the_expected_answers_with_the_classes(string fixture)
        {
            string dir = ConformanceTests.CopyFixture(fixture);
            try
            {
                Run(fixture, dir, SqlSchemaSource.Classes);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        [Theory]
        [InlineData("xml")]
        [InlineData("json")]
        [InlineData("binary")]
        [InlineData("password")]
        public void Sql_gives_the_expected_answers_from_the_manifest_the_library_writes(string fixture)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-conformance", "fresh-" + fixture + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                Dataset.Write(dir, Repo.FormatOf(fixture), Repo.PasswordOf(fixture));
                Repo.DropLockFiles(dir);
                Run(fixture, dir, SqlSchemaSource.Recorded);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>
        /// The two decoders against the reader (PLAN-060 section 4, item 4), over the stored-values fixture's forms:
        /// an empty value, a nil one, and a map's or list's null and empty items. The library writes every value a
        /// read wouldn't give back by itself, so row 2 of each table then loses the elements a row written before
        /// its property existed lacks: the read gives what a new instance holds ([DefaultValue] has no effect,
        /// ISS-118, and Count pins that SQL agrees whichever way it is settled), which the manifest records as
        /// <c>default</c>. Every row SQL gives, put back in its class, must be the
        /// object Select&lt;T&gt; reads, whether SQL typed the table from the class or from the manifest alone.
        /// </summary>
        [Theory]
        [InlineData(SqlSchemaSource.Classes)]
        [InlineData(SqlSchemaSource.Recorded)]
        public void Sql_reads_each_stored_form_as_the_reader_does(SqlSchemaSource schema)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-conformance", "fresh-stored-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                StoredValuesFixture.Write(dir);
                LeaveOut(dir, "Blank", "2", "Label", "Area", "Token", "When");
                LeaveOut(dir, "BlankDocument", "2", "Label", "Count");
                LeaveOut(dir, "BlankCollections", "2", "Names", "Words");
                using var db = new XodContext(Path.Combine(dir, "app.xod"), null, new DatabaseOptions { SqlSchema = schema });
                var failures = new List<string>();
                Compare<Model.Format.Blank>(db, b => b.Id, failures);
                Compare<Model.Format.BlankDocument>(db, b => b.Id, failures);
                Compare<Model.Format.BlankCollections>(db, b => b.Id, failures);
                Assert.True(failures.Count == 0, string.Join("\n", failures));
            }
            finally
            {
                TryDelete(dir);
            }
        }

        // Removes the named elements from the row of <entity> whose Id is <id>, in whichever page holds it (XML pages).
        private static void LeaveOut(string dir, string entity, string id, params string[] names)
        {
            int removed = 0;
            foreach (string page in Directory.GetFiles(dir, "*.xpag"))
            {
                var doc = System.Xml.Linq.XDocument.Load(page);
                var row = doc.Descendants(entity).FirstOrDefault(e => (string)e.Element("Id") == id);
                if (row == null) continue;
                foreach (string name in names)
                {
                    var e = row.Element(name);
                    Assert.True(e != null, $"{entity} {id} has no {name} to leave out");
                    e.Remove();
                    removed++;
                }
                doc.Save(page, System.Xml.Linq.SaveOptions.DisableFormatting);
            }
            Assert.Equal(names.Length, removed);
        }

        private static void Compare<T>(XodContext db, Func<T, int> id, List<string> failures) where T : new()
        {
            var sql = db.Sql($"SELECT * FROM {typeof(T).Name} ORDER BY Id");
            if (sql.Warnings.Count > 0)
                failures.Add($"{typeof(T).Name}: warnings {string.Join(", ", sql.Warnings.Select(w => w.Code + " " + w.Message))}");
            var got = db.Sql<T>($"SELECT * FROM {typeof(T).Name} ORDER BY Id").Select(ShowRow).ToList();
            var want = db.Select<T>().OrderBy(id).Select(ShowRow).ToList();
            for (int i = 0; i < Math.Max(got.Count, want.Count); i++)
            {
                string g = i < got.Count ? got[i] : "(none)", w = i < want.Count ? want[i] : "(none)";
                if (g != w) failures.Add($"{typeof(T).Name} row {i}: SQL gives {g}\n    the reader gives {w}");
            }
        }

        // An object's stored properties as text: a map's keys in ordinal order, a timestamp with its kind.
        private static string ShowRow<T>(T item) =>
            string.Join(", ", typeof(T).GetProperties().Select(p => p.Name + "=" + Show(p.GetValue(item))));

        private static string Show(object v) => v switch
        {
            null => "NULL",
            string s => "'" + s + "'",
            DateTime t => t.ToString("O", CultureInfo.InvariantCulture) + "/" + t.Kind,
            IDictionary map => "{" + string.Join(",", map.Keys.Cast<object>().Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => k + ":" + Show(map[k]))) + "}",
            IEnumerable items => "[" + string.Join(",", items.Cast<object>().Select(Show)) + "]",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };

        private void Run(string fixture, string dir, SqlSchemaSource schema)
        {
            var expected = CaseFiles.ExpectedRows();
            var failures = new List<string>();
            int passed = 0, skipped = 0;
            using (var db = new XodContext(Path.Combine(dir, "app.xod"), Repo.PasswordOf(fixture),
                       new DatabaseOptions { StorageFormat = Repo.FormatOf(fixture), SqlSchema = schema }))
            {
                foreach (var c in Cases.All)
                {
                    if (!c.AppliesTo(fixture)) { skipped++; continue; }
                    string problem = c.Error != null ? CheckError(db, c) : CheckRows(db, c, expected[c.Id]);
                    if (problem == null) passed++;
                    else failures.Add($"{c.Id}: {problem}\n    {c.Sql}");
                }
            }
            output.WriteLine($"{fixture} ({schema}): {passed} passed, {failures.Count} failed, {skipped} not for this fixture");
            Assert.True(failures.Count == 0, $"{failures.Count} cases failed:\n" + string.Join("\n", failures));
        }

        private static string CheckError(XodContext db, Case c)
        {
            try
            {
                db.Sql(c.Sql);
                return $"no error, expected {c.Error}";
            }
            catch (XodSqlException e)
            {
                return e.Code == c.Error ? null : $"error {e.Code} ({e.Message}), expected {c.Error}";
            }
            catch (Exception e)
            {
                return $"threw {e.GetType().Name}: {e.Message}, expected {c.Error}";
            }
        }

        private static string CheckRows(XodContext db, Case c, List<string> expectedRows)
        {
            SqlResult result;
            try
            {
                result = db.Sql(c.Sql);
            }
            catch (Exception e)
            {
                return $"threw {e.GetType().Name}: {e.Message}";
            }

            string want = string.Join(", ", c.ColumnList.Select(col => col.Name + " " + col.Type));
            string got = string.Join(", ", result.Columns.Select(col => col.Name + " " + col.Type));
            if (want != got)
                return $"columns {got}, expected {want}";

            List<string> rows;
            try
            {
                rows = result.Rows.Select(r => new JsonArray(r.Select((v, i) => Encode(v, result.Columns[i].Type)).ToArray()).ToJsonString(Values.Json)).ToList();
            }
            catch (Exception e)
            {
                return $"a value isn't of its column's type: {e.Message}";
            }

            var w = Values.Canonical(c, expectedRows);
            var g = Values.Canonical(c, rows);
            if (w.SequenceEqual(g))
                return null;
            int i = 0;
            while (i < w.Count && i < g.Count && w[i] == g[i]) i++;
            return $"{g.Count} rows, expected {w.Count}; first difference at row {i}: expected {(i < w.Count ? w[i] : "(none)")}, got {(i < g.Count ? g[i] : "(none)")}";
        }

        /// <summary>A SQL value as the case files encode it (PLAN-047 section 4.2). The scalar types go through the
        /// same encoder as the expected answers; a MAP or ARRAY's values are encoded by what they are.</summary>
        internal static JsonNode Encode(object v, string type)
        {
            if (v == null) return null;
            switch (type)
            {
                case "MAP":
                    if (v is not IEnumerable<KeyValuePair<string, object>> map) throw new InvalidCastException($"a {v.GetType().Name} is not MAP");
                    var o = new JsonObject();
                    foreach (var kv in map.OrderBy(k => k.Key, StringComparer.Ordinal)) o[kv.Key] = Encode(kv.Value, TypeOf(kv.Value));
                    return o;
                case "ARRAY":
                    if (v is string || v is not IEnumerable items) throw new InvalidCastException($"a {v.GetType().Name} is not ARRAY");
                    return new JsonArray(items.Cast<object>().Select(x => Encode(x, TypeOf(x))).ToArray());
                default:
                    // SQL values have one .NET type per SQL type; the case encoder also takes the model's types, so a
                    // value of the wrong .NET type is refused here first.
                    if (!Is(v, type)) throw new InvalidCastException($"a {v.GetType().Name} value ({v}) is not {type}");
                    return Values.Encode(v, type);
            }
        }

        private static bool Is(object v, string type) => type switch
        {
            "INTEGER" => v is long,
            "DOUBLE" => v is double,
            "DECIMAL" => v is decimal,
            "TEXT" => v is string,
            "BOOLEAN" => v is bool,
            "UUID" => v is Guid,
            "TIMESTAMP" => v is DateTime,
            "TIMESTAMPTZ" => v is DateTimeOffset,
            "DATE" => v is DateOnly,
            "TIME" => v is TimeOnly,
            "INTERVAL" => v is TimeSpan,
            _ => false
        };

        private static string TypeOf(object v) => v switch
        {
            null => "TEXT",
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
            IEnumerable<KeyValuePair<string, object>> => "MAP",
            IEnumerable => "ARRAY",
            _ => throw new InvalidCastException("not a SQL value: " + v.GetType().Name)
        };

        internal static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* a temp copy; the OS cleans it */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
