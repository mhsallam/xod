using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-128: a property declared as <c>IDictionary&lt;K, V&gt;</c> or <c>IReadOnlyDictionary&lt;K, V&gt;</c> is a map.
    /// The map test took only a type that is the non-generic <c>IDictionary</c> or a <c>Dictionary&lt;,&gt;</c>, so either
    /// interface counted as a list of its first type argument: each pair was stored as text, and a read threw. Its key
    /// and value types are the ones its interface names, a value is written through that interface (a dictionary that
    /// isn't an <c>IDictionary</c> too), and a read makes a <c>Dictionary&lt;K, V&gt;</c> for an interface.
    /// </summary>
    public class XodMapInterfaceTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-mapif-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class MiRow
        {
            public int Id { get; set; }
            public IDictionary<string, int> Counts { get; set; }
            public IReadOnlyDictionary<int, string> Names { get; set; }
        }

        // A dictionary that is only an IReadOnlyDictionary<K, V>, not the non-generic IDictionary.
        public sealed class PairsOnly : IReadOnlyDictionary<int, string>
        {
            private readonly Dictionary<int, string> inner;
            public PairsOnly(Dictionary<int, string> inner) { this.inner = inner; }
            public string this[int key] => inner[key];
            public IEnumerable<int> Keys => inner.Keys;
            public IEnumerable<string> Values => inner.Values;
            public int Count => inner.Count;
            public bool ContainsKey(int key) => inner.ContainsKey(key);
            public bool TryGetValue(int key, out string value) => inner.TryGetValue(key, out value);
            public IEnumerator<KeyValuePair<int, string>> GetEnumerator() => inner.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => inner.GetEnumerator();
        }

        private string NewPath(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "mi.xod");
        }

        [Fact]
        public void Maps_declared_by_their_interfaces_read_back_as_written()
        {
            string path = NewPath("interfaces");
            using (var db = new XodContext(path))
                db.Insert(new MiRow
                {
                    Counts = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 },
                    Names = new Dictionary<int, string> { [7] = "seven", [8] = null },
                });

            using (var db = new XodContext(path))
            {
                var row = db.Find<MiRow>(r => r.Id == 1);
                Assert.Equal(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }, row.Counts);
                Assert.Equal(2, row.Names.Count);
                Assert.Equal("seven", row.Names[7]);
                Assert.Null(row.Names[8]);
            }
        }

        [Fact]
        public void A_dictionary_that_is_only_its_generic_interface_is_written_as_a_map()
        {
            string path = NewPath("pairs-only");
            using (var db = new XodContext(path))
                db.Insert(new MiRow { Names = new PairsOnly(new Dictionary<int, string> { [1] = "one", [2] = "two" }) });

            using (var db = new XodContext(path))
            {
                var row = db.Find<MiRow>(r => r.Id == 1);
                Assert.Equal("one", row.Names[1]);
                Assert.Equal("two", row.Names[2]);
                Assert.Null(row.Counts);
            }
        }

        public class MiReadOnly
        {
            public int Id { get; set; }
            public System.Collections.ObjectModel.ReadOnlyDictionary<string, int> Fixed { get; set; }
        }

        // A map class a read can't make (no parameterless constructor, and no Dictionary<K, V> fits it) is refused when
        // its type loads, as any collection a read can't give back is (ISS-184), saying which maps can be.
        [Fact]
        public void A_map_class_a_read_cannot_make_is_refused_when_its_type_loads()
        {
            using var db = new XodContext(NewPath("refused"));
            var e = Assert.ThrowsAny<XodException>(() => db.Insert(new MiReadOnly()));
            Assert.Contains("a map is a Dictionary<K, V>", e.Message);
        }

        // SQL reads the column as a map, by the class and by the schema recorded.
        [Fact]
        public void Sql_reads_an_interface_map_with_its_types()
        {
            string path = NewPath("sql");
            using (var db = new XodContext(path))
                db.Insert(new MiRow { Names = new Dictionary<int, string> { [7] = "seven" } });

            foreach (var source in new[] { SqlSchemaSource.Classes, SqlSchemaSource.Recorded })
            {
                using var db = new XodContext(path, null, new DatabaseOptions { SqlSchema = source });
                var result = db.Sql("SELECT Names FROM MiRow");
                var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(result.Rows[0][0]);
                Assert.Equal("seven", map["7"]);
            }
        }
    }
}
