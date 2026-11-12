// ISS-060: the writers left some values out of the row (an empty string, Guid.Empty, a default date or
// interval, a type default under a different [DefaultValue], null), and a read then gave the property what it
// has without the row (its DefaultValue, or what a new instance holds): "" read back as null, 0 as the
// DefaultValue, Guid.Empty as a new instance's Guid. A value is now left out only when a read gives it back.
using System;
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
    }
}
