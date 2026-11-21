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

        // ISS-213: a map class with no parameterless constructor is made from a filled Dictionary<K, V>: through its
        // constructor taking one (ReadOnlyDictionary), or its type's factory (the immutable and frozen dictionaries).
        public class MiFixed
        {
            public int Id { get; set; }
            public System.Collections.ObjectModel.ReadOnlyDictionary<string, int> ReadOnly { get; set; }
            public System.Collections.Immutable.ImmutableDictionary<string, int> Immutable { get; set; }
            public System.Collections.Immutable.ImmutableSortedDictionary<string, int> Sorted { get; set; }
            public System.Collections.Immutable.IImmutableDictionary<int, string> ByInterface { get; set; }
            public System.Collections.Frozen.FrozenDictionary<string, int> Frozen { get; set; }
        }

        private static MiFixed Filled() => new MiFixed
        {
            ReadOnly = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }),
            Immutable = System.Collections.Immutable.ImmutableDictionary.CreateRange(new Dictionary<string, int> { ["c"] = 3 }),
            Sorted = System.Collections.Immutable.ImmutableSortedDictionary.CreateRange(new Dictionary<string, int> { ["z"] = 26, ["y"] = 25 }),
            ByInterface = System.Collections.Immutable.ImmutableDictionary.CreateRange(new Dictionary<int, string> { [7] = "seven" }),
            Frozen = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<string, int> { ["f"] = 6 }),
        };

        private static void AssertFilled(MiFixed row)
        {
            Assert.Equal(new[] { "a=1", "b=2" }, row.ReadOnly.Select(kv => kv.Key + "=" + kv.Value).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal(3, row.Immutable["c"]);
            Assert.Equal(new[] { "y", "z" }, row.Sorted.Keys.ToArray());
            Assert.Equal("seven", row.ByInterface[7]);
            Assert.Equal(6, row.Frozen["f"]);
        }

        [Fact]
        public void Maps_with_no_parameterless_constructor_read_back_as_written()
        {
            string path = NewPath("fixed");
            using (var db = new XodContext(path))
            {
                db.Insert(Filled());
                db.Insert(new MiFixed());
            }

            using (var db = new XodContext(path))
            {
                AssertFilled(db.Find<MiFixed>(r => r.Id == 1));
                var empty = db.Find<MiFixed>(r => r.Id == 2);
                Assert.Null(empty.ReadOnly);
                Assert.Null(empty.Frozen);
            }
        }

        // SQL puts such a column back into such a property, by the same maker.
        [Fact]
        public void Sql_puts_them_into_a_class()
        {
            string path = NewPath("fixed-sql");
            using (var db = new XodContext(path))
                db.Insert(Filled());

            using (var db = new XodContext(path))
                AssertFilled(db.Sql<MiFixed>("SELECT ReadOnly, Immutable, Sorted, ByInterface, Frozen FROM MiFixed").Single());
        }

        // ISS-216: a sorted map with string keys is made with the ordinal comparer, the one under which any keys that were
        // distinct when written stay distinct. Made with the default comparer, which follows the culture, two keys the
        // culture finds equal ("ab" and "a" + soft hyphen + "b") written under an ordinal comparer were one key: a
        // SortedDictionary or a SortedList kept one of the two, and an ImmutableSortedDictionary threw at every read.
        public class MiSortedKeys
        {
            public int Id { get; set; }
            public SortedDictionary<string, int> Sorted { get; set; }
            public SortedList<string, int> Listed { get; set; }
            public System.Collections.Immutable.ImmutableSortedDictionary<string, int> Immutable { get; set; }
        }

        [Fact]
        public void A_sorted_map_keeps_keys_its_culture_finds_equal()
        {
            const string soft = "a\u00ADb";
            var pairs = new[] { new KeyValuePair<string, int>("ab", 1), new KeyValuePair<string, int>(soft, 2) };
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                Assert.Equal(0, string.Compare("ab", soft, StringComparison.CurrentCulture)); // the culture finds them equal
                string path = NewPath("sorted-keys");
                using (var db = new XodContext(path))
                {
                    var sorted = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    var listed = new SortedList<string, int>(StringComparer.Ordinal);
                    foreach (var p in pairs) { sorted[p.Key] = p.Value; listed[p.Key] = p.Value; }
                    db.Insert(new MiSortedKeys
                    {
                        Sorted = sorted,
                        Listed = listed,
                        Immutable = System.Collections.Immutable.ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, pairs),
                    });
                }

                using (var db = new XodContext(path))
                {
                    var row = db.Find<MiSortedKeys>(r => r.Id == 1);
                    foreach (IDictionary<string, int> map in new IDictionary<string, int>[] { row.Sorted, row.Listed })
                    {
                        Assert.Equal(2, map.Count);
                        Assert.Equal(1, map["ab"]);
                        Assert.Equal(2, map[soft]);
                    }
                    Assert.Equal(2, row.Immutable.Count);
                    Assert.Equal(2, row.Immutable[soft]);
                }
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
        }

        // A map class of the user's keeps the comparer its own constructor sets, though it also has one taking a comparer:
        // only .NET's sorted maps are made with the ordinal one.
        public sealed class Caseless : SortedDictionary<string, int>
        {
            public Caseless() : base(StringComparer.OrdinalIgnoreCase) { }
            public Caseless(IComparer<string> comparer) : base(comparer) { }
        }

        public class MiCaseless
        {
            public int Id { get; set; }
            public Caseless Map { get; set; }
        }

        [Fact]
        public void A_map_class_keeps_the_comparer_its_constructor_sets()
        {
            string path = NewPath("caseless");
            using (var db = new XodContext(path))
                db.Insert(new MiCaseless { Map = new Caseless { ["Key"] = 1 } });
            using (var db = new XodContext(path))
                Assert.True(db.Find<MiCaseless>(r => r.Id == 1).Map.ContainsKey("KEY"));
        }

        // The order a sorted map with string keys reads back in is ordinal, whatever the culture.
        [Fact]
        public void A_sorted_map_reads_back_in_ordinal_order()
        {
            string path = NewPath("sorted-order");
            using (var db = new XodContext(path))
                db.Insert(new MiSortedKeys { Sorted = new SortedDictionary<string, int> { ["apple"] = 1, ["Banana"] = 2, ["cherry"] = 3 } });
            using (var db = new XodContext(path))
                Assert.Equal(new[] { "Banana", "apple", "cherry" }, db.Find<MiSortedKeys>(r => r.Id == 1).Sorted.Keys.ToArray());
        }

        // A map class neither made by a parameterless constructor, nor from a Dictionary<K, V>, is still refused when its
        // type loads, as any collection a read can't give back is (ISS-184), saying which maps can be.
        public sealed class OnlyFromPairs : Dictionary<string, int>
        {
            public OnlyFromPairs(int seed) { this["seed"] = seed; }
        }

        public class MiUnmade
        {
            public int Id { get; set; }
            public OnlyFromPairs Pairs { get; set; }
        }

        [Fact]
        public void A_map_class_a_read_cannot_make_is_refused_when_its_type_loads()
        {
            using var db = new XodContext(NewPath("refused"));
            var e = Assert.ThrowsAny<XodException>(() => db.Insert(new MiUnmade()));
            Assert.Contains("a map is a Dictionary<K, V>", e.Message);
        }

        // A constructor whose one parameter only accepts a dictionary (an object: a configuration, say) doesn't take the
        // entries, so it makes no map.
        public sealed class ConfiguredMap : Dictionary<string, int>
        {
            public ConfiguredMap(object configuration) { }
        }

        public class MiConfigured
        {
            public int Id { get; set; }
            public ConfiguredMap Pairs { get; set; }
        }

        [Fact]
        public void A_map_class_whose_constructor_takes_no_entries_is_refused()
        {
            using var db = new XodContext(NewPath("configured"));
            Assert.ThrowsAny<XodException>(() => db.Insert(new MiConfigured()));
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
