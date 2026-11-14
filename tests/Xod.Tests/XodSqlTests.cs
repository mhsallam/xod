// XOD SQL through db.Sql (PLAN-060): what the conformance set (conformance/cases) doesn't cover - parameters,
// Sql<T>, SELECT *, EXPLAIN, indexes never changing an answer, the errors and warnings of section 3, and the
// schema read from the manifest (SqlSchemaSource.Recorded) agreeing with the one from the class.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Xod.Tests
{
    public enum SqTier { Basic = 0, Gold = 1 }

    public class SqProduct
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Property(Indexed = true)] public string Category { get; set; }
        public decimal Price { get; set; }
        public double? Weight { get; set; }
        public SqTier Tier { get; set; }
        public List<string> Tags { get; set; }
        public Dictionary<string, int> Stock { get; set; }
    }

    public class SqProductView
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public decimal Price { get; set; }
        public double? Weight { get; set; }
        public SqTier Tier { get; set; }
        public List<string> Tags { get; set; }
        public Dictionary<string, int> Stock { get; set; }
        public string NotSelected { get; set; } = "untouched";
    }

    public class SqSecret
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Crypto(CryptoMethod.AES256)] public string ApiKey { get; set; }
    }

    public class XodSqlTests : IDisposable
    {
        private readonly List<string> dirs = new();

        public void Dispose()
        {
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-sql", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            dirs.Add(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static readonly string[] Categories = { "a", "b", "c" };

        private static IEnumerable<SqProduct> Products(int n) => Enumerable.Range(1, n).Select(i => new SqProduct
        {
            Name = "p" + i.ToString("D3"),
            Category = Categories[(i * 7) % 3],
            Price = i * 1.25m,
            Weight = i % 4 == 0 ? null : i / 2.0,
            Tier = i % 2 == 0 ? SqTier.Gold : SqTier.Basic,
            Tags = i % 5 == 0 ? null : new List<string> { "t" + i, "all" },
            Stock = new Dictionary<string, int> { ["x"] = i, ["y"] = -i },
        });

        private string Seeded(string name, int n = 12, StorageFormat format = StorageFormat.Xml)
        {
            string path = NewDatabase(name);
            using var db = new XodContext(path, null, new DatabaseOptions { StorageFormat = format });
            db.InsertRange(Products(n).ToList());
            return path;
        }

        private static XodSqlException Refused(Action query) => Assert.Throws<XodSqlException>(query);

        // ---- Sql<T> ----

        [Fact]
        public void Sql_of_T_puts_each_column_in_the_property_of_its_name()
        {
            string path = Seeded("as-t", 5);
            using var db = new XodContext(path);
            var rows = db.Sql<SqProductView>("SELECT * FROM SqProduct ORDER BY Id");

            Assert.Equal(5, rows.Count);
            var first = rows[0];
            Assert.Equal(1, first.Id);
            Assert.Equal("p001", first.Name);
            Assert.Equal(1.25m, first.Price);
            Assert.Equal(0.5, first.Weight);
            Assert.Equal(SqTier.Basic, first.Tier);
            Assert.Equal(new[] { "t1", "all" }, first.Tags);
            Assert.Equal(new Dictionary<string, int> { ["x"] = 1, ["y"] = -1 }, first.Stock);
            Assert.Equal("untouched", first.NotSelected);
            Assert.Null(rows[3].Weight);
            Assert.Null(rows[4].Tags);
            Assert.Equal(SqTier.Gold, rows[1].Tier);
        }

        [Fact]
        public void Sql_of_T_matches_names_in_any_case_and_refuses_a_null_its_property_cannot_hold()
        {
            string path = Seeded("as-t-null", 4);
            using var db = new XodContext(path);
            var named = db.Sql<SqProductView>("SELECT Id AS ID, Name AS name FROM SqProduct WHERE Id = 2");
            Assert.Equal(2, named.Single().Id);
            Assert.Equal("p002", named.Single().Name);

            // Weight is NULL for Id 4; Price is a decimal, which can't hold it.
            var e = Refused(() => db.Sql<SqProductView>("SELECT Weight AS Price FROM SqProduct WHERE Id = 4"));
            Assert.Equal("XQ005", e.Code);
        }

        // ---- parameters ----

        [Fact]
        public void Parameters_are_given_by_position_or_by_name()
        {
            string path = Seeded("params");
            using var db = new XodContext(path);
            var byPosition = db.Sql("SELECT Id FROM SqProduct WHERE Category = ? AND Price > ? ORDER BY Id", "a", 5m);
            var byName = db.Sql("SELECT Id FROM SqProduct WHERE Category = @cat AND Price > @min ORDER BY Id",
                new Dictionary<string, object> { ["cat"] = "a", ["@min"] = 5m });
            var expected = Products(12).Select((p, i) => (Id: (long)(i + 1), p)).Where(x => x.p.Category == "a" && x.p.Price > 5m).Select(x => x.Id).ToList();

            Assert.NotEmpty(expected);
            Assert.Equal(expected, byPosition.Rows.Select(r => (long)r[0]));
            Assert.Equal(expected, byName.Rows.Select(r => (long)r[0]));

            // A parameter holds a value of its own type: an enum is its name, as stored.
            var gold = db.Sql("SELECT COUNT(*) FROM SqProduct WHERE Tier = ?", SqTier.Gold);
            Assert.Equal(6L, gold.Rows[0][0]);
        }

        [Fact]
        public void A_parameter_without_a_value_or_a_value_without_a_parameter_is_XQ012()
        {
            string path = Seeded("params-missing", 2);
            using var db = new XodContext(path);
            Assert.Equal("XQ012", Refused(() => db.Sql("SELECT Id FROM SqProduct WHERE Id = ?")).Code);
            Assert.Equal("XQ012", Refused(() => db.Sql("SELECT Id FROM SqProduct WHERE Id = ?", 1, 2)).Code);
            Assert.Equal("XQ012", Refused(() => db.Sql("SELECT Id FROM SqProduct WHERE Id = @id", new Dictionary<string, object>())).Code);
            Assert.Equal("XQ012", Refused(() => db.Sql("SELECT Id FROM SqProduct WHERE Id = ?", new object())).Code);
        }

        // ---- SELECT *, EXPLAIN ----

        [Fact]
        public void Select_star_gives_the_class_columns_in_property_order()
        {
            string path = Seeded("star", 1);
            using var db = new XodContext(path);
            var r = db.Sql("SELECT * FROM SqProduct");
            Assert.Equal(
                new[] { "Id INTEGER", "Name TEXT", "Category TEXT", "Price DECIMAL", "Weight DOUBLE", "Tier TEXT", "Tags ARRAY", "Stock MAP" },
                r.Columns.Select(c => c.ToString()));
        }

        [Fact]
        public void Explain_says_which_index_a_table_is_read_by()
        {
            string path = Seeded("explain");
            using var db = new XodContext(path);
            var indexed = db.Sql("EXPLAIN SELECT Id FROM SqProduct WHERE Category = 'a'");
            Assert.Equal("plan TEXT", indexed.Columns.Single().ToString());
            Assert.Contains("index on Category =", (string)indexed.Rows[0][0]);

            var scanned = db.Sql("EXPLAIN SELECT Id FROM SqProduct WHERE Name = 'p001'");
            Assert.EndsWith(": scan", (string)scanned.Rows[0][0]);
        }

        [Theory]
        [InlineData("SELECT Id, Name FROM SqProduct WHERE Category = 'b'")]
        [InlineData("SELECT Id FROM SqProduct WHERE Category IN ('a', 'c') AND Price > 3")]
        [InlineData("SELECT Id FROM SqProduct WHERE Id >= 4 AND Id < 9")]
        [InlineData("SELECT Id FROM SqProduct WHERE Id = 3 OR Category = 'a'")]
        [InlineData("SELECT Id FROM SqProduct WHERE Category = NULL")]
        [InlineData("SELECT Category, COUNT(*) FROM SqProduct WHERE Id > 2 GROUP BY Category")]
        public void An_index_never_changes_an_answer_or_its_stored_order(string sql)
        {
            string path = Seeded("index-same-" + Math.Abs(sql.GetHashCode()), 40);
            List<string> Answer(bool indexing)
            {
                using var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing });
                return db.Sql(sql).Rows.Select(r => string.Join("|", r.Select(v => v?.ToString() ?? "NULL"))).ToList();
            }
            // No ORDER BY: both answers are in stored order (D5), the index's rows included.
            Assert.Equal(Answer(false), Answer(true));
        }

        // ---- errors and warnings (PLAN-060 section 3) ----

        // A subquery computed once per group may use the grouped query's columns only as its own grouped ones: an
        // ungrouped one would take the value of whichever row came first in the group.
        [Theory]
        [InlineData("SELECT Category, (SELECT COUNT(*) FROM SqProduct q WHERE q.Id < p.Id) AS n FROM SqProduct p GROUP BY Category")]
        [InlineData("SELECT Category FROM SqProduct p GROUP BY Category HAVING (SELECT COUNT(*) FROM SqProduct q WHERE q.Id < p.Id) > 0")]
        [InlineData("SELECT Category FROM SqProduct p GROUP BY Category HAVING EXISTS (SELECT 1 FROM SqProduct q WHERE q.Price > p.Price)")]
        [InlineData("SELECT Category FROM SqProduct p GROUP BY Category ORDER BY (SELECT MAX(q.Id) FROM SqProduct q WHERE q.Price > p.Price)")]
        [InlineData("SELECT Category, (SELECT COUNT(*) FROM SqProduct q WHERE q.Id IN (SELECT r.Id FROM SqProduct r WHERE r.Id < p.Id)) AS n FROM SqProduct p GROUP BY Category")]
        public void An_ungrouped_column_a_subquery_of_a_grouped_query_uses_is_XQ006(string sql)
        {
            string path = Seeded("group-sub");
            using var db = new XodContext(path);
            Assert.Equal("XQ006", Refused(() => db.Sql(sql)).Code);
        }

        [Fact]
        public void A_subquery_of_a_grouped_query_may_use_its_grouped_columns_and_WHERE_any()
        {
            string path = Seeded("group-sub-ok");
            using var db = new XodContext(path);
            var products = Products(12).ToList();

            var r = db.Sql("SELECT Category, (SELECT COUNT(*) FROM SqProduct q WHERE q.Category = p.Category) AS n " +
                           "FROM SqProduct p GROUP BY Category ORDER BY Category");
            Assert.Equal(products.GroupBy(p => p.Category).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}"),
                r.Rows.Select(x => $"{x[0]}:{x[1]}"));

            var w = db.Sql("SELECT Category, COUNT(*) AS n FROM SqProduct p " +
                           "WHERE EXISTS (SELECT 1 FROM SqProduct q WHERE q.Id = p.Id AND q.Price > 5) GROUP BY Category ORDER BY Category");
            Assert.Equal(products.Where(p => p.Price > 5m).GroupBy(p => p.Category).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}"),
                w.Rows.Select(x => $"{x[0]}:{x[1]}"));
        }

        // Edges the review of TASK-072 found: each gave a wrong value or a .NET exception instead of a code.
        public static IEnumerable<object[]> EdgeValues() => new[]
        {
            new object[] { "SELECT SUBSTRING(Name, 2, 9223372036854775807) FROM SqProduct WHERE Id = 1", "001" },
            new object[] { "SELECT ROUND(CAST(Price AS DOUBLE), -400) FROM SqProduct WHERE Id = 1", 0.0 },
            new object[] { "SELECT 'a\U0001F600b' LIKE 'a_b' FROM SqProduct WHERE Id = 1", true },
            new object[] { "SELECT 'a\U0001F600b' LIKE 'a__b' FROM SqProduct WHERE Id = 1", false },
        };

        [Theory]
        [MemberData(nameof(EdgeValues))]
        public void Edge_values_are_what_the_standard_says(string sql, object expected)
        {
            string path = Seeded("edges", 1);
            using var db = new XodContext(path);
            Assert.Equal(expected, db.Sql(sql).Rows.Single()[0]);
        }

        [Theory]
        [InlineData("SELECT INTERVAL '01:00:00' * (CAST(0 AS DOUBLE) / 0) FROM SqProduct", "XQ007")]
        [InlineData("SELECT ٣ FROM SqProduct", "XQ001")]
        public void Edge_errors_have_their_code(string sql, string code)
        {
            string path = Seeded("edge-errors", 1);
            using var db = new XodContext(path);
            Assert.Equal(code, Refused(() => db.Sql(sql)).Code);
        }

        [Fact]
        public void Division_by_zero_is_XQ010_for_exact_numbers_and_IEEE_for_DOUBLE()
        {
            string path = Seeded("div0", 1);
            using var db = new XodContext(path);
            Assert.Equal("XQ010", Refused(() => db.Sql("SELECT Id / 0 FROM SqProduct")).Code);
            Assert.Equal("XQ010", Refused(() => db.Sql("SELECT Price / 0 FROM SqProduct")).Code);
            Assert.Equal(double.PositiveInfinity, db.Sql("SELECT Weight / 0 FROM SqProduct").Rows[0][0]);
        }

        [Fact]
        public void A_subquery_used_as_a_value_that_gives_two_rows_is_XQ011()
        {
            string path = Seeded("xq011", 3);
            using var db = new XodContext(path);
            Assert.Equal("XQ011", Refused(() => db.Sql("SELECT (SELECT Id FROM SqProduct) FROM SqProduct")).Code);
            Assert.Null(db.Sql("SELECT (SELECT Id FROM SqProduct WHERE Id = 99) FROM SqProduct WHERE Id = 1").Rows[0][0]);
        }

        [Theory]
        [InlineData("INSERT INTO SqProduct (Name) VALUES ('x')")]
        [InlineData("UPDATE SqProduct SET Name = 'x'")]
        [InlineData("DELETE FROM SqProduct")]
        [InlineData("SELECT Id FROM SqProduct UNION SELECT Id FROM SqProduct")]
        public void Writes_and_version_2_syntax_are_XQ001_saying_so(string sql)
        {
            string path = Seeded("writes", 1);
            using var db = new XodContext(path);
            var e = Refused(() => db.Sql(sql));
            Assert.Equal("XQ001", e.Code);
            Assert.Contains("version", e.Message);
            Assert.Single(db.Select<SqProduct>());
        }

        [Fact]
        public void An_encrypted_column_opens_with_the_caller_key_and_is_NULL_with_XQ008_without_it()
        {
            string path = NewDatabase("crypto");
            const string appKey = "sql-application-key";
            using (var db = new XodContext(path, null, new DatabaseOptions { PropertyEncryptionKey = appKey }))
                db.Insert(new SqSecret { Name = "billing", ApiKey = "sk-1" });

            using (var db = new XodContext(path, null, new DatabaseOptions { PropertyEncryptionKey = appKey }))
            {
                var r = db.Sql("SELECT ApiKey FROM SqSecret");
                Assert.Equal("sk-1", r.Rows[0][0]);
                Assert.Empty(r.Warnings);
            }
            using (var db = new XodContext(path))
            {
                var r = db.Sql("SELECT Name, ApiKey FROM SqSecret");
                Assert.Equal("billing", r.Rows[0][0]);
                Assert.Null(r.Rows[0][1]);
                Assert.Equal("XQ008", r.Warnings.Single().Code);
            }
        }

        [Fact]
        public void Current_date_and_time_are_the_machine_clock_fixed_for_the_query()
        {
            string path = Seeded("now", 3);
            using var db = new XodContext(path);
            var before = DateTimeOffset.Now;
            var r = db.Sql("SELECT CURRENT_TIMESTAMP, CURRENT_DATE FROM SqProduct");
            var after = DateTimeOffset.Now;
            var stamps = r.Rows.Select(x => (DateTimeOffset)x[0]).Distinct().ToList();
            Assert.Single(stamps);
            Assert.InRange(stamps[0], before, after);
            Assert.Equal(DateOnly.FromDateTime(stamps[0].DateTime), (DateOnly)r.Rows[0][1]);
        }

        // ---- the schema from the manifest (SqlSchemaSource.Recorded) ----

        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        [InlineData(StorageFormat.Binary)]
        public void The_manifest_gives_the_same_answers_as_the_class(StorageFormat format)
        {
            string path = Seeded("recorded-" + format, 12, format);
            string[] queries =
            {
                "SELECT * FROM SqProduct ORDER BY Id",
                "SELECT Tier, COUNT(*) AS n, SUM(Price) AS total, AVG(Weight) AS w FROM SqProduct GROUP BY Tier ORDER BY Tier",
                "SELECT Id, Stock['x'] AS x, CARDINALITY(Tags) AS n FROM SqProduct WHERE 'all' = ANY(Tags) ORDER BY Id",
            };
            foreach (string sql in queries)
            {
                SqlResult fromClass, fromManifest;
                using (var db = new XodContext(path, null, new DatabaseOptions { StorageFormat = format }))
                    fromClass = db.Sql(sql);
                using (var db = new XodContext(path, null, new DatabaseOptions { StorageFormat = format, SqlSchema = SqlSchemaSource.Recorded }))
                    fromManifest = db.Sql(sql);

                Assert.Equal(fromClass.Columns.Select(c => c.ToString()), fromManifest.Columns.Select(c => c.ToString()));
                Assert.Equal(Text(fromClass), Text(fromManifest));
                Assert.Empty(fromManifest.Warnings);
            }
        }

        private static List<string> Text(SqlResult r) =>
            r.Rows.Select(row => string.Join("|", row.Select(Show))).ToList();

        private static string Show(object v) => v switch
        {
            null => "NULL",
            IReadOnlyDictionary<string, object> map => "{" + string.Join(",", map.Select(kv => kv.Key + "=" + Show(kv.Value))) + "}",
            System.Collections.IEnumerable items when v is not string => "[" + string.Join(",", items.Cast<object>().Select(Show)) + "]",
            _ => v.GetType().Name + ":" + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        };

        private static string TableFile(string dbPath, Type type) => Path.Combine(Path.GetDirectoryName(dbPath), type.FullName + ".xtab");

        private static string PageFile(string dbPath, Type type)
        {
            var table = XDocument.Load(TableFile(dbPath, type));
            return Path.Combine(Path.GetDirectoryName(dbPath), table.Root.Element("Pages").Elements("Page").First().Attribute("file").Value);
        }

        private static void Edit(string file, Action<XDocument> change)
        {
            var doc = XDocument.Load(file);
            change(doc);
            doc.Save(file);
        }

        [Fact]
        public void A_table_that_records_no_schema_reads_every_column_as_text_with_XQ013()
        {
            string path = Seeded("no-manifest", 2);
            Edit(TableFile(path, typeof(SqProduct)), d => d.Root.Element("Schema").Remove());

            using var db = new XodContext(path, null, new DatabaseOptions { SqlSchema = SqlSchemaSource.Recorded });
            var r = db.Sql("SELECT Id, Price FROM SqProduct ORDER BY Id");
            Assert.Equal(new[] { "Id TEXT", "Price TEXT" }, r.Columns.Select(c => c.ToString()));
            Assert.Equal("1", r.Rows[0][0]);
            Assert.Equal("1.25", r.Rows[0][1]);
            Assert.Equal("XQ013", r.Warnings.Single().Code);
        }

        [Fact]
        public void A_value_that_does_not_parse_as_its_column_type_is_NULL_with_XQ014()
        {
            string path = Seeded("unparsed", 2);
            Edit(PageFile(path, typeof(SqProduct)), d => d.Descendants("Price").First().Value = "not-a-number");

            foreach (var source in new[] { SqlSchemaSource.Classes, SqlSchemaSource.Recorded })
            {
                using var db = new XodContext(path, null, new DatabaseOptions { SqlSchema = source });
                var r = db.Sql("SELECT Id, Price FROM SqProduct ORDER BY Id");
                Assert.Null(r.Rows[0][1]);
                Assert.Equal(2.50m, r.Rows[1][1]);
                Assert.Equal("XQ014", r.Warnings.Single().Code);
            }
        }

        // The writers never mark a value of a Dictionary<string, int> null, but a file another writer made may; the
        // library reads it as 0, and the manifest (valueNullable, PLAN-046 section 10.2) says so to a reader without
        // the class. A null value of a Dictionary<string, string> stays NULL either way (BlankCollections, conformance).
        [Fact]
        public void A_null_map_value_reads_as_the_library_reads_it_through_the_class_and_the_manifest()
        {
            string path = Seeded("map-nil", 1);
            Edit(PageFile(path, typeof(SqProduct)), d =>
            {
                var entry = d.Descendants("Stock").First().Elements("Entry").First(e => (string)e.Attribute("key") == "x");
                entry.Value = "";
                entry.SetAttributeValue("nil", "true");
            });

            using (var db = new XodContext(path))
                Assert.Equal(0, db.Select<SqProduct>().Single().Stock["x"]);
            foreach (var source in new[] { SqlSchemaSource.Classes, SqlSchemaSource.Recorded })
            {
                using var db = new XodContext(path, null, new DatabaseOptions { SqlSchema = source });
                var r = db.Sql("SELECT Stock['x'] AS x, Stock['y'] AS y FROM SqProduct");
                Assert.Equal(new object[] { 0L, -1L }, r.Rows.Single());
            }
        }

        [Fact]
        public void A_column_rows_hold_that_the_manifest_does_not_list_reads_as_text_with_XQ013()
        {
            string path = Seeded("unlisted", 2);
            Edit(PageFile(path, typeof(SqProduct)), d => d.Descendants("SqProduct").First().Add(new XElement("Legacy", "old")));

            using var db = new XodContext(path, null, new DatabaseOptions { SqlSchema = SqlSchemaSource.Recorded });
            var r = db.Sql("SELECT Id, legacy FROM SqProduct ORDER BY Id");
            Assert.Equal(new[] { "Id INTEGER", "Legacy TEXT" }, r.Columns.Select(c => c.ToString()));
            Assert.Equal("old", r.Rows[0][1]);
            Assert.Null(r.Rows[1][1]);
            Assert.Equal("XQ013", r.Warnings.Single().Code);
            Assert.Equal("XQ004", Refused(() => db.Sql("SELECT Nope FROM SqProduct")).Code);
        }
    }
}
