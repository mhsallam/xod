// ISS-060: the writers left some values out of the row (an empty string, Guid.Empty, a default date or
// interval, a type default under a different [DefaultValue], null), and a read then gave the property what it
// has without the row (its DefaultValue, or what a new instance holds): "" read back as null, 0 as the
// DefaultValue, Guid.Empty as a new instance's Guid. A value is now left out only when a read gives it back.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Xunit;

namespace Xod.Tests
{
    // The document writer's path: an attribute and a [DefaultValue] keep it off the fast row writer.
    public class StoredValues
    {
        public int Id { get; set; }
        public string Plain { get; set; }
        public string Status { get; set; } = "new";
        [Property(Position = ValuePosition.Attribute)] public string Tag { get; set; }
        [DefaultValue(5)] public int Count { get; set; }
        public Guid Token { get; set; } = Guid.NewGuid();
        public DateTime When { get; set; } = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public TimeSpan? Span { get; set; }
    }

    // Plain scalars only: the fast row writer and the fast row reader.
    public class FastStoredValues
    {
        public int Id { get; set; }
        public string Plain { get; set; }
        [Property(Indexed = true)] public string Code { get; set; }
        public string Status { get; set; } = "new";
        public Guid Token { get; set; } = Guid.NewGuid();
        public DateTime When { get; set; } = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public TimeSpan? Span { get; set; }
    }

    public class IndexedStoredValues
    {
        public int Id { get; set; }
        [Property(Indexed = true)] public string Code { get; set; }
        [Property(Indexed = true)] public string Area { get; set; } = "north";
    }

    // Relationship keys a new instance holds as "" (review of TASK-063): the write-back fills them after the
    // scalar pass has marked them null.
    public class KeyedAuthor
    {
        [PrimaryKey] public string Code { get; set; }
        public string Name { get; set; }
    }

    public class KeyedBook
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string AuthorCode { get; set; } = "";
        [ForeignKey(nameof(AuthorCode), "Code")] public KeyedAuthor Author { get; set; }
    }

    public class KeyedShelf
    {
        [PrimaryKey] public string Code { get; set; }
        [Children] public List<KeyedSlot> Slots { get; set; }
    }

    public class KeyedSlot
    {
        public int Id { get; set; }
        public string KeyedShelfCode { get; set; } = "";
        public string Label { get; set; }
        [ParentKey(nameof(KeyedShelfCode), "Code")] public KeyedShelf Shelf { get; set; }
    }

    public class KeptStoredValues
    {
        public int Id { get; set; }
        [Property(Indexed = true)] public string Code { get; set; }
        [Property(Indexed = true)] public string Area { get; set; } = "north";
        public string Name { get; set; }
    }

    // A type the reader makes without a constructor: its strings hold null without the row.
    public class UnconstructedValues
    {
        public UnconstructedValues(int id) { Id = id; }
        public int Id { get; set; }
        public string Name { get; set; }
        [Property(Indexed = true)] public string Code { get; set; }
    }

    // ISS-097: the value collections, a map and a list or array of scalars. A map's null value read back as "", a
    // list or array dropped a null item, a list of a nullable type couldn't be stored (its items were named
    // "Nullable`1"), and an empty map read back as null.
    public abstract class CollectionValues
    {
        public int Id { get; set; }
        public Dictionary<string, string> Names { get; set; }
        public Dictionary<string, int?> Counts { get; set; }
        public Dictionary<string, string> NoNames { get; set; }
        public List<string> Words { get; set; }
        public string[] Letters { get; set; }
        public List<int?> Numbers { get; set; }
        public int?[] Marks { get; set; }
    }

    // Read by the standalone row reader.
    public class StoredCollections : CollectionValues { }

    // An attribute property sends it to the general row reader.
    public class AttributedStoredCollections : CollectionValues
    {
        [Property(Position = ValuePosition.Attribute)] public string Tag { get; set; }
    }

    // Maps of other types than Dictionary<,> (review of TASK-071).
    public class OtherMaps
    {
        public int Id { get; set; }
        public SortedDictionary<string, int> Sorted { get; set; }
        public Hashtable Table { get; set; }
        public CountMap Counts { get; set; }
    }

    public class CountMap : Dictionary<string, int> { }

    public class XodStoredValueTests
    {
        public static IEnumerable<object[]> Formats() => XodTextRoundTripTests.Formats();

        private static string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-stored", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static void ReadsBackAsWritten(StoredValues v)
        {
            Assert.Equal("", v.Plain);
            Assert.Null(v.Status);
            Assert.Equal("", v.Tag);
            Assert.Equal(0, v.Count);
            Assert.Equal(Guid.Empty, v.Token);
            Assert.Equal(default, v.When);
            Assert.Equal(TimeSpan.Zero, v.Span);
        }

        private static StoredValues Written() => new StoredValues
        {
            Plain = "",
            Status = null,
            Tag = "",
            Count = 0,
            Token = Guid.Empty,
            When = default,
            Span = TimeSpan.Zero,
        };

        [Theory]
        [MemberData(nameof(Formats))]
        public void Values_a_read_would_not_give_back_are_stored(StorageFormat format, string password)
        {
            string path = NewDatabase("document-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            using (var db = new XodContext(path, password, options))
            {
                db.Insert(Written());
                ReadsBackAsWritten(db.Find<StoredValues>(v => v.Id == 1));
            }
            using (var db = new XodContext(path, password, options))
            {
                ReadsBackAsWritten(db.Find<StoredValues>(v => v.Id == 1));
                ReadsBackAsWritten(db.Select<StoredValues>().Single());
            }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void Values_a_read_would_not_give_back_are_stored_by_the_fast_writer(StorageFormat format, string password)
        {
            string path = NewDatabase("fast-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            var written = new FastStoredValues { Plain = "", Code = "", Status = null, Token = Guid.Empty, When = default, Span = TimeSpan.Zero };
            using (var db = new XodContext(path, password, options))
                db.Insert(written);
            using (var db = new XodContext(path, password, options))
            {
                foreach (var v in new[] { db.Find<FastStoredValues>(x => x.Id == 1), db.Select<FastStoredValues>().Single() })
                {
                    Assert.Equal("", v.Plain);
                    Assert.Equal("", v.Code);
                    Assert.Null(v.Status);
                    Assert.Equal(Guid.Empty, v.Token);
                    Assert.Equal(default, v.When);
                    Assert.Equal(TimeSpan.Zero, v.Span);
                }
                // The index keys what a read gives back: the empty string.
                Assert.Equal(1, db.Find<FastStoredValues>(x => x.Code == "")?.Id);
            }
        }

        // An index rebuilt from the pages keys what a read of each row gives: a marked null is no key, an empty
        // string is the empty key.
        [Fact]
        public void An_index_rebuilt_from_the_pages_keys_an_empty_string_and_not_a_marked_null()
        {
            string path = NewDatabase("rebuilt");
            using (var db = new XodContext(path))
            {
                db.Insert(new IndexedStoredValues { Code = "", Area = null });
                db.Insert(new IndexedStoredValues { Code = "c", Area = "south" });
            }
            foreach (var index in Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xidx"))
                File.Delete(index);

            using (var db = new XodContext(path))
            {
                Assert.Equal(1, db.Find<IndexedStoredValues>(x => x.Code == "")?.Id);
                Assert.Null(db.Find<IndexedStoredValues>(x => x.Area == ""));
                Assert.Null(db.Find<IndexedStoredValues>(x => x.Area == "north"));
                Assert.Null(db.Select<IndexedStoredValues>().Single(x => x.Id == 1).Area);
            }
        }

        // A key the relationship write-back fills in after the scalar pass marked it null is stored as its value,
        // in place: a foreign key, and a parent key (review of TASK-063: both read back null).
        [Theory]
        [MemberData(nameof(Formats))]
        public void A_key_filled_in_by_its_relationship_reads_back(StorageFormat format, string password)
        {
            string path = NewDatabase("writeback-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            using (var db = new XodContext(path, password, options))
            {
                var author = db.Insert(new KeyedAuthor { Code = "A1", Name = "a" });
                db.Insert(new KeyedBook { Title = "t", AuthorCode = null, Author = author });
                var shelf = db.Insert(new KeyedShelf { Code = "S1" });
                db.Insert(new KeyedSlot { Label = "s", KeyedShelfCode = null, Shelf = shelf });
            }
            using (var db = new XodContext(path, password, options))
            {
                Assert.Equal("A1", db.Select<KeyedBook>().Single().AuthorCode);
                var slot = db.Select<KeyedSlot>().Single();
                Assert.Equal("S1", slot.KeyedShelfCode);
            }
            if (format == StorageFormat.Xml && password == null)
            {
                string pages = string.Concat(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag").Select(File.ReadAllText));
                Assert.DoesNotContain("nil=", pages);
                Assert.Single(System.Text.RegularExpressions.Regex.Matches(pages, "<KeyedShelfCode"));
            }
        }

        // An update that leaves an indexed property as it was keys it as a read gives it back: a marked null and
        // a null left out are no key, an empty string is the empty key (review of TASK-063: both nulls keyed "").
        [Fact]
        public void An_update_that_leaves_a_property_out_keys_it_as_it_reads_back()
        {
            string path = NewDatabase("kept");
            using var db = new XodContext(path);
            var skip = new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = new[] { "Code", "Area" } };
            db.Insert(new KeptStoredValues { Code = null, Area = null });
            Assert.True(db.Update(db.Find<KeptStoredValues>(v => v.Id == 1), new KeptStoredValues { Id = 1, Code = "x", Area = "x", Name = "b" }, skip));
            Assert.Null(db.Find<KeptStoredValues>(v => v.Code == ""));
            Assert.Null(db.Find<KeptStoredValues>(v => v.Area == ""));
            var first = db.Select<KeptStoredValues>().Single();
            Assert.Null(first.Code);
            Assert.Null(first.Area);

            db.Insert(new KeptStoredValues { Code = "", Area = "" });
            Assert.True(db.Update(db.Find<KeptStoredValues>(v => v.Id == 2), new KeptStoredValues { Id = 2, Code = "x", Area = "x", Name = "b" }, skip));
            Assert.Equal(2, db.Find<KeptStoredValues>(v => v.Code == "")?.Id);
            Assert.Equal(2, db.Find<KeptStoredValues>(v => v.Area == "")?.Id);
        }

        // An insert that leaves the item's relations out (lazyLoad) stores its values by the same rule.
        [Fact]
        public void A_shallow_insert_stores_values_a_read_would_not_give_back()
        {
            string path = NewDatabase("shallow");
            using (var db = new XodContext(path))
                db.Insert(Written(), lazyLoad: true);
            using (var db = new XodContext(path))
                ReadsBackAsWritten(db.Select<StoredValues>().Single());
        }

        // A type without a parameterless constructor: the reader makes it without one, so a string it leaves out
        // reads null, and an empty string is stored (review of TASK-063: "" was left out and read back null).
        [Fact]
        public void A_type_made_without_a_constructor_reads_back_an_empty_string()
        {
            string path = NewDatabase("unconstructed");
            using (var db = new XodContext(path))
            {
                db.Insert(new UnconstructedValues(0) { Name = "", Code = "" });
                db.Insert(new UnconstructedValues(0) { Name = null, Code = null });
            }
            using (var db = new XodContext(path))
            {
                var rows = db.Select<UnconstructedValues>().OrderBy(v => v.Id).ToList();
                Assert.Equal("", rows[0].Name);
                Assert.Equal("", rows[0].Code);
                Assert.Null(rows[1].Name);
                Assert.Null(rows[1].Code);
                Assert.Equal(new[] { 1 }, db.Query<UnconstructedValues>(v => v.Code == "").Select(v => v.Id));
            }
        }

        // What a read gives back anyway stays out of the row, as before: the format doesn't grow for it.
        [Fact]
        public void Values_a_read_gives_back_are_still_left_out()
        {
            string path = NewDatabase("left-out");
            using (var db = new XodContext(path))
                db.Insert(new StoredValues { Plain = null, Count = 5, Span = null, Tag = null });
            string page = File.ReadAllText(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag").Single());
            Assert.DoesNotContain("<Plain", page);
            Assert.DoesNotContain("<Span", page);
            Assert.DoesNotContain("Tag=", page);
            Assert.DoesNotContain("nil=", page);
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void Value_collections_read_back_as_written(StorageFormat format, string password)
        {
            string path = NewDatabase("collections-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            using (var db = new XodContext(path, password, options))
            {
                db.Insert(WrittenCollections(new StoredCollections()));
                db.Insert(WrittenCollections(new AttributedStoredCollections { Tag = "t" }));
            }
            using (var db = new XodContext(path, password, options))
            {
                CollectionsReadBackAsWritten(db.Find<StoredCollections>(v => v.Id == 1));
                CollectionsReadBackAsWritten(db.Select<StoredCollections>().Single());
                CollectionsReadBackAsWritten(db.Find<AttributedStoredCollections>(v => v.Id == 1));
                CollectionsReadBackAsWritten(db.Select<AttributedStoredCollections>().Single());
            }
        }

        // The stored form (PLAN-046 section 7): a null entry or item is marked as a null property is, and a nullable
        // item is named by its underlying type, as the item of a list of that type is.
        [Fact]
        public void A_null_entry_or_item_is_marked_and_a_nullable_item_is_named_by_its_type()
        {
            string path = NewDatabase("collections-format");
            using (var db = new XodContext(path))
                db.Insert(new StoredCollections { Names = new() { ["none"] = null }, Words = new() { null }, Numbers = new() { 1, null } });
            string page = File.ReadAllText(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag").Single());
            Assert.Contains("<Entry key=\"none\" nil=\"true\" />", page);
            Assert.Contains("<String nil=\"true\" />", page);
            Assert.Contains("<Int32>1</Int32><Int32 nil=\"true\" />", page);
        }

        // The review of TASK-071: an update compares the row it would write with the stored one, and took a map's
        // value marked null for the same entry with no text (only the stored row's attributes were looked for in the
        // new one), and a list reordered, or with an item repeated in place of another, for the same list: the update
        // was dropped, and reported made. Each update here changes that alone.
        [Theory]
        [MemberData(nameof(Formats))]
        public void An_update_of_what_a_map_or_list_holds_is_written(StorageFormat format, string password)
        {
            string path = NewDatabase("collections-update-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            var changes = new Action<CollectionValues>[]
            {
                v => v.Names["k"] = null,
                v => v.Words = new List<string> { "x", null },
                v => v.Letters = new[] { "a", "b", "b" },
            };
            using (var db = new XodContext(path, password, options))
            {
                foreach (var _ in changes)
                {
                    db.Insert(Before(new StoredCollections()));
                    db.Insert(Before(new AttributedStoredCollections { Tag = "t" }));
                }
            }
            using (var db = new XodContext(path, password, options))
            {
                for (int i = 0; i < changes.Length; i++)
                {
                    int id = i + 1;
                    foreach (CollectionValues v in new CollectionValues[] { db.Find<StoredCollections>(x => x.Id == id), db.Find<AttributedStoredCollections>(x => x.Id == id) })
                    {
                        changes[i](v);
                        Assert.True(db.Update(v.GetType(), v));
                    }
                }
            }
            using (var db = new XodContext(path, password, options))
            {
                foreach (CollectionValues[] rows in new[]
                {
                    db.Select<StoredCollections>().OrderBy(x => x.Id).ToArray<CollectionValues>(),
                    db.Select<AttributedStoredCollections>().OrderBy(x => x.Id).ToArray<CollectionValues>(),
                })
                {
                    Assert.Null(rows[0].Names["k"]);
                    Assert.Equal(new List<string> { "x", null }, rows[1].Words);
                    Assert.Equal(new[] { "a", "b", "b" }, rows[2].Letters);
                }
            }

            static T Before<T>(T v) where T : CollectionValues
            {
                v.Names = new Dictionary<string, string> { ["k"] = "" };
                v.Words = new List<string> { null, "x" };
                v.Letters = new[] { "a", "a", "b" };
                return v;
            }
        }

        // A map property reads back as its own type, with its own key and value types (review of TASK-071: a
        // Dictionary<,> was set on every map property, so a SortedDictionary, a Hashtable or a class deriving from
        // Dictionary<,> couldn't be read once it held anything, or, since empty maps are stored, at all).
        [Theory]
        [MemberData(nameof(Formats))]
        public void Maps_of_other_types_read_back_as_their_own_type(StorageFormat format, string password)
        {
            string path = NewDatabase("other-maps-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            using (var db = new XodContext(path, password, options))
            {
                db.Insert(new OtherMaps { Sorted = new() { ["b"] = 2, ["a"] = 1 }, Table = new Hashtable { ["x"] = "1" }, Counts = new CountMap { ["n"] = 3 } });
                db.Insert(new OtherMaps { Sorted = new(), Table = new Hashtable(), Counts = new CountMap() });
            }
            using (var db = new XodContext(path, password, options))
            {
                var full = db.Find<OtherMaps>(m => m.Id == 1);
                Assert.Equal(new[] { "a", "b" }, full.Sorted.Keys);
                Assert.Equal(2, full.Sorted["b"]);
                Assert.Equal("1", full.Table["x"]);
                Assert.Equal(3, full.Counts["n"]);
                var empty = db.Find<OtherMaps>(m => m.Id == 2);
                Assert.Empty(empty.Sorted);
                Assert.Empty(empty.Table);
                Assert.Empty(empty.Counts);
            }
        }

        private static T WrittenCollections<T>(T v) where T : CollectionValues
        {
            v.Names = new Dictionary<string, string> { ["none"] = null, ["empty"] = "", ["one"] = "1" };
            v.Counts = new Dictionary<string, int?> { ["none"] = null, ["zero"] = 0 };
            v.NoNames = new Dictionary<string, string>();
            v.Words = new List<string> { "x", null, "" };
            v.Letters = new[] { null, "a" };
            v.Numbers = new List<int?> { 1, null, 0 };
            v.Marks = new int?[] { null, 2 };
            return v;
        }

        private static void CollectionsReadBackAsWritten(CollectionValues v)
        {
            Assert.Equal(new Dictionary<string, string> { ["none"] = null, ["empty"] = "", ["one"] = "1" }, v.Names);
            Assert.Equal(new Dictionary<string, int?> { ["none"] = null, ["zero"] = 0 }, v.Counts);
            Assert.NotNull(v.NoNames);
            Assert.Empty(v.NoNames);
            Assert.Equal(new List<string> { "x", null, "" }, v.Words);
            Assert.Equal(new[] { null, "a" }, v.Letters);
            Assert.Equal(new List<int?> { 1, null, 0 }, v.Numbers);
            Assert.Equal(new int?[] { null, 2 }, v.Marks);
        }
    }
}
