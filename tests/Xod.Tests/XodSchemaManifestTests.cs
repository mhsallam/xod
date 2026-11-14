// The schema manifest (PLAN-046 section 10, PLAN-060 section 2.4): every write records, in the table files it may
// write, the schema of the class whose rows they hold, as a change to the table file like any other. A read never
// writes it. Observed from outside: the XML table file itself, and SQL reading the tables by their manifest
// (SqlSchemaSource.Recorded), which falls back to text with XQ013 where there is none.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Xod.Tests
{
    public class SmLine
    {
        public int Id { get; set; }
        public int SmOrderId { get; set; }
        public int Quantity { get; set; }
        [ParentKey(nameof(SmOrderId))] public SmOrder SmOrder { get; set; }
    }

    public class SmAddress
    {
        public string City { get; set; }
    }

    public class SmOrder
    {
        public int Id { get; set; }
        public decimal Total { get; set; }
        public SmAddress ShipTo { get; set; }
        [Children] public List<SmLine> Lines { get; set; }
    }

    public class XodSchemaManifestTests : IDisposable
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
            string dir = Path.Combine(Path.GetTempPath(), "xod-manifest", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            dirs.Add(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static SmOrder Order(int lines) => new SmOrder
        {
            Total = 12.50m,
            ShipTo = new SmAddress { City = "Amman" },
            Lines = Enumerable.Range(1, lines).Select(i => new SmLine { Quantity = i }).ToList(),
        };

        private static string TableFile(string dbPath, Type type) => Path.Combine(Path.GetDirectoryName(dbPath), type.FullName + ".xtab");

        private static XElement StoredSchema(string dbPath, Type type) => XDocument.Load(TableFile(dbPath, type)).Root.Element("Schema");

        /// <summary>A table file as 2.0.1 left it: no manifest.</summary>
        private static void Strip(string dbPath, Type type)
        {
            string file = TableFile(dbPath, type);
            var doc = XDocument.Load(file);
            doc.Root.Element("Schema")?.Remove();
            doc.Save(file);
        }

        /// <summary>The column types SQL reads a table with from its manifest alone, and its warnings.</summary>
        private static (string Columns, string Warnings) Recorded(string dbPath, string table, StorageFormat format = StorageFormat.Xml, string password = null)
        {
            using var db = new XodContext(dbPath, password, new DatabaseOptions { StorageFormat = format, SqlSchema = SqlSchemaSource.Recorded });
            var r = db.Sql($"SELECT * FROM {table}");
            return (string.Join(", ", r.Columns), string.Join(", ", r.Warnings.Select(w => w.Code)));
        }

        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        [InlineData(StorageFormat.Binary)]
        public void The_first_write_records_the_schema_of_each_table_it_creates(StorageFormat format)
        {
            string path = NewDatabase("first-" + format);
            using (var db = new XodContext(path, null, new DatabaseOptions { StorageFormat = format }))
                db.Insert(Order(2));

            Assert.Equal(("Id INTEGER, Total DECIMAL", ""), Recorded(path, "SmOrder", format));
            Assert.Equal(("Id INTEGER, SmOrderId INTEGER, Quantity INTEGER", ""), Recorded(path, "SmLine", format));
            Assert.Equal(("City TEXT", ""), Recorded(path, "SmAddress", format));
        }

        [Fact]
        public void The_xml_manifest_says_what_PLAN_046_lists()
        {
            string path = NewDatabase("contents");
            using (var db = new XodContext(path))
                db.Insert(Order(1));

            var schema = StoredSchema(path, typeof(SmOrder));
            Assert.Equal("1", schema.Attribute("version").Value);
            Assert.Equal(typeof(SmOrder).FullName, schema.Attribute("type").Value);
            Assert.Equal("SmOrder", schema.Attribute("element").Value);
            Assert.NotNull(schema.Attribute("writer"));
            var id = schema.Elements("Column").First(c => c.Attribute("name").Value == "Id");
            Assert.Equal("int32", id.Attribute("type").Value);
            Assert.Equal("1", id.Attribute("key").Value);
            Assert.Equal("true", id.Attribute("autonumber").Value);
            var lines = schema.Elements("Relation").Single(r => r.Attribute("name").Value == "Lines");
            Assert.Equal("children", lines.Attribute("kind").Value);
            Assert.Equal(typeof(SmLine).FullName, lines.Attribute("table").Value);
            Assert.Equal("Id", lines.Attribute("localKey").Value);
            Assert.Equal("SmOrderId", lines.Attribute("remoteKey").Value);
            Assert.Equal("complex", schema.Elements("Relation").Single(r => r.Attribute("name").Value == "ShipTo").Attribute("kind").Value);
        }

        [Fact]
        public void A_read_never_writes_it()
        {
            string path = NewDatabase("read-only");
            using (var db = new XodContext(path))
                db.Insert(Order(2));
            Strip(path, typeof(SmOrder));
            byte[] before = File.ReadAllBytes(TableFile(path, typeof(SmOrder)));

            using (var db = new XodContext(path))
            {
                Assert.Single(db.Select<SmOrder>());
                Assert.Single(db.Query<SmOrder>(o => o.Total > 1m));
                Assert.Equal(1, db.Count<SmOrder>());
                Assert.Single(db.Sql("SELECT Id FROM SmOrder").Rows);
            }

            Assert.Equal(before, File.ReadAllBytes(TableFile(path, typeof(SmOrder))));
            Assert.Null(StoredSchema(path, typeof(SmOrder)));
        }

        [Fact]
        public void Drop_writes_none_and_leaves_the_tables_it_reaches_as_they_were()
        {
            string path = NewDatabase("drop");
            using (var db = new XodContext(path))
                db.Insert(Order(2));
            Strip(path, typeof(SmLine));
            byte[] lines = File.ReadAllBytes(TableFile(path, typeof(SmLine)));

            using (var db = new XodContext(path))
            {
                Assert.True(db.Drop<SmOrder>());
                Assert.False(db.Drop<SmOrder>());
            }

            Assert.False(File.Exists(TableFile(path, typeof(SmOrder))));
            Assert.Equal(lines, File.ReadAllBytes(TableFile(path, typeof(SmLine))));
        }

        [Theory]
        [InlineData("update")]
        [InlineData("delete")]
        [InlineData("insert-other")]
        public void Any_write_records_it_in_a_table_written_before_it_existed(string write)
        {
            string path = NewDatabase("older-" + write);
            using (var db = new XodContext(path))
            {
                db.Insert(Order(2));
                db.Insert(Order(1));
            }
            Strip(path, typeof(SmOrder));
            Strip(path, typeof(SmLine));
            Assert.Equal("XQ013", Recorded(path, "SmLine").Warnings);

            using (var db = new XodContext(path))
            {
                var first = db.Select<SmOrder>().First();
                switch (write)
                {
                    case "update":
                        first.Total = 99m;
                        db.Update(first);
                        break;
                    case "delete":
                        db.Delete(first);
                        break;
                    default:
                        db.Insert(Order(0)); // a row of SmOrder only: SmLine's table is one it may write
                        break;
                }
            }

            Assert.Equal(("Id INTEGER, Total DECIMAL", ""), Recorded(path, "SmOrder"));
            Assert.Equal(("Id INTEGER, SmOrderId INTEGER, Quantity INTEGER", ""), Recorded(path, "SmLine"));
        }

        [Fact]
        public void A_manifest_that_says_otherwise_is_replaced_and_one_that_says_the_same_is_kept()
        {
            string path = NewDatabase("replace");
            using (var db = new XodContext(path))
                db.Insert(Order(1));

            string file = TableFile(path, typeof(SmOrder));
            var doc = XDocument.Load(file);
            doc.Root.Element("Schema").Elements("Column").First(c => c.Attribute("name").Value == "Total").SetAttributeValue("type", "text");
            doc.Save(file);
            Assert.Equal("Id INTEGER, Total TEXT", Recorded(path, "SmOrder").Columns);

            using (var db = new XodContext(path))
                db.Insert(Order(0));
            Assert.Equal("Id INTEGER, Total DECIMAL", Recorded(path, "SmOrder").Columns);

            // Only the writer differs: the manifest says the same, and stays as it is.
            doc = XDocument.Load(file);
            doc.Root.Element("Schema").SetAttributeValue("writer", "0.0.1");
            doc.Save(file);
            using (var db = new XodContext(path))
                db.Insert(Order(0));
            Assert.Equal("0.0.1", StoredSchema(path, typeof(SmOrder)).Attribute("writer").Value);
        }

        [Fact]
        public void A_rollback_takes_it_away_with_the_rows_and_the_next_write_records_it()
        {
            string path = NewDatabase("rollback");
            using (var db = new XodContext(path))
                db.Insert(Order(1));
            Strip(path, typeof(SmOrder));

            using (var db = new XodContext(path))
            {
                using (db.BeginTransaction())
                    db.Insert(Order(1)); // not committed: rolled back
                Assert.Equal(1, db.Count<SmOrder>());
                db.Flush();
                Assert.Null(StoredSchema(path, typeof(SmOrder)));

                // The same context's next write records it again.
                db.Insert(Order(0));
            }
            Assert.NotNull(StoredSchema(path, typeof(SmOrder)));
            Assert.Equal(("Id INTEGER, Total DECIMAL", ""), Recorded(path, "SmOrder"));
        }

        [Fact]
        public void The_migrator_and_a_password_change_keep_it()
        {
            string path = NewDatabase("migrate");
            using (var db = new XodContext(path))
                db.Insert(Order(2));

            string target = Path.Combine(Path.GetDirectoryName(path), "json");
            XodMigrator.ConvertDatabase(Path.GetDirectoryName(path), target, StorageFormat.Json);
            Assert.Equal(("Id INTEGER, Total DECIMAL", ""), Recorded(Path.Combine(target, "app.xod"), "SmOrder", StorageFormat.Json));

            using (var db = new XodContext(path))
                db.Secure("pw-manifest");
            Assert.Equal(("Id INTEGER, Total DECIMAL", ""), Recorded(path, "SmOrder", StorageFormat.Xml, "pw-manifest"));
        }
    }
}
