using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-214: a map property whose stored value isn't a map. XOD 2.0.x took an <c>IDictionary&lt;K, V&gt;</c> property
    /// for a list of its first type argument and stored each pair as text (<c>collType="generic"</c>); since TASK-106 the
    /// property is a map, and the readers sent that value to the list reader, which set a list on the map property
    /// (<c>ArgumentException</c>). A read now fails naming the row and the property, and SQL reads it as NULL with a
    /// warning, as it reads any value that isn't its column's type.
    /// </summary>
    public class XodMapStoredAsListTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-maplist-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        // The standalone row reader (no relations) and the general one (a relation keeps the type off it).
        public class MlFlat
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public IDictionary<string, int> Counts { get; set; }
        }

        public class MlTag
        {
            public int Id { get; set; }
            public string Text { get; set; }
        }

        public class MlLinked
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public IDictionary<string, int> Counts { get; set; }
            public MlTag Tag { get; set; }
        }

        private string NewPath(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ml.xod");
        }

        // Rewrites the stored map of every row as 2.0.x stored an IDictionary<string, int> property: a list of the
        // key type, each pair as its text.
        private static void StoreAsList(string dbPath)
        {
            int changed = 0;
            foreach (var page in Directory.GetFiles(Path.GetDirectoryName(dbPath), "*.xpag"))
            {
                var doc = XDocument.Load(page);
                var maps = doc.Descendants("Counts").Where(e => (string)e.Attribute("collType") == "dictionary").ToList();
                foreach (var map in maps)
                {
                    var pairs = map.Elements("Entry").Select(e => new XElement("String", $"[{(string)e.Attribute("key")}, {e.Value}]"));
                    map.ReplaceWith(new XElement("Counts", new XAttribute("dataType", "System.String"), new XAttribute("collType", "generic"), pairs));
                    changed++;
                }
                if (maps.Count > 0)
                    doc.Save(page);
            }
            Assert.True(changed > 0);
        }

        [Fact]
        public void The_standalone_reader_names_the_row_and_property()
        {
            string path = NewPath("flat");
            using (var db = new XodContext(path))
                db.Insert(new MlFlat { Name = "a", Counts = new Dictionary<string, int> { ["x"] = 1 } });
            StoreAsList(path);

            using var db2 = new XodContext(path);
            var ex = Assert.Throws<DatabaseFileException>(() => db2.Select<MlFlat>().ToList());
            Assert.Contains("Counts", ex.Message);
            Assert.Contains(nameof(MlFlat), ex.Message);
        }

        [Fact]
        public void The_general_reader_names_the_row_and_property()
        {
            string path = NewPath("linked");
            using (var db = new XodContext(path))
                db.Insert(new MlLinked { Name = "a", Counts = new Dictionary<string, int> { ["x"] = 1 }, Tag = new MlTag { Text = "t" } });
            StoreAsList(path);

            using var db2 = new XodContext(path);
            var ex = Assert.Throws<DatabaseFileException>(() => db2.Select<MlLinked>().ToList());
            Assert.Contains("Counts", ex.Message);
            Assert.Contains(nameof(MlLinked), ex.Message);
        }

        // The way out the message names, for a type with relations too, whose update reads the stored row.
        [Fact]
        public void The_row_can_be_deleted_and_inserted_again()
        {
            string path = NewPath("way-out");
            using (var db = new XodContext(path))
                db.Insert(new MlLinked { Name = "a", Counts = new Dictionary<string, int> { ["x"] = 1 }, Tag = new MlTag { Text = "t" } });
            StoreAsList(path);

            using (var db = new XodContext(path))
            {
                var ex = Assert.Throws<DatabaseFileException>(() => db.Select<MlLinked>().ToList());
                Assert.Contains("Delete the row and insert it again", ex.Message);
                Assert.True(db.Delete(new MlLinked { Id = 1 }));
                db.Insert(new MlLinked { Name = "a", Counts = new Dictionary<string, int> { ["x"] = 2 }, Tag = new MlTag { Text = "t" } });
            }

            using (var db = new XodContext(path))
                Assert.Equal(2, db.Select<MlLinked>().Single().Counts["x"]);
        }

        // A stored value with no collection mark isn't a map either: both readers refuse it, as SQL reads it as NULL.
        [Fact]
        public void A_value_with_no_mark_is_not_read_as_a_map()
        {
            string path = NewPath("unmarked");
            using (var db = new XodContext(path))
                db.Insert(new MlFlat { Name = "a", Counts = new Dictionary<string, int> { ["x"] = 1 } });
            int changed = 0;
            foreach (var page in Directory.GetFiles(Path.GetDirectoryName(path), "*.xpag"))
            {
                var doc = XDocument.Load(page);
                foreach (var map in doc.Descendants("Counts").ToList())
                {
                    map.ReplaceWith(new XElement("Counts", "x=1"));
                    changed++;
                }
                doc.Save(page);
            }
            Assert.True(changed > 0);

            using var db2 = new XodContext(path);
            Assert.Throws<DatabaseFileException>(() => db2.Select<MlFlat>().ToList());
        }

        [Fact]
        public void A_map_stored_as_a_map_still_reads()
        {
            string path = NewPath("guard");
            using (var db = new XodContext(path))
                db.Insert(new MlFlat { Name = "a", Counts = new Dictionary<string, int> { ["x"] = 1 } });
            using var db2 = new XodContext(path);
            Assert.Equal(1, db2.Select<MlFlat>().Single().Counts["x"]);
        }

        [Theory]
        [InlineData(SqlSchemaSource.Classes)]
        [InlineData(SqlSchemaSource.Recorded)]
        public void Sql_reads_it_as_null_with_a_warning(SqlSchemaSource source)
        {
            string path = NewPath("sql-" + source);
            using (var db = new XodContext(path))
                db.Insert(new MlFlat { Name = "a", Counts = new Dictionary<string, int> { ["x"] = 1 } });
            StoreAsList(path);

            using var db2 = new XodContext(path, null, new DatabaseOptions { SqlSchema = source });
            var result = db2.Sql("SELECT Name, Counts FROM MlFlat");
            Assert.Equal("a", result.Rows[0][0]);
            Assert.Null(result.Rows[0][1]);
            Assert.Contains(result.Warnings, w => w.Code == "XQ014");
        }
    }
}
