// An autonumber value is handed out once (ISS-052). The next value is above every value the table has
// held: rows still there, and rows deleted since, whose highest value the table records when they go.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Xod.Tests
{
    public class NumberedItem
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class NumberedLong
    {
        public long Id { get; set; }
        public string Name { get; set; }
    }

    // An autonumber that no index covers: its highest value is read from the rows.
    public class SequencedNote
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true)] public int Seq { get; set; }
    }

    public class XodAutonumberTests
    {
        public static IEnumerable<object[]> Encodings() => new[]
        {
            new object[] { StorageFormat.Xml, null, true },
            new object[] { StorageFormat.Json, null, true },
            new object[] { StorageFormat.Binary, null, true },
            new object[] { StorageFormat.Xml, "pw", true },
            new object[] { StorageFormat.Xml, null, false }, // no index: the other delete path
        };

        private static string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-autonumber", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static XodContext Open(string path, string password, StorageFormat format, bool indexing) =>
            new XodContext(path, password, new DatabaseOptions { StorageFormat = format, EnableIndexing = indexing });

        [Theory]
        [MemberData(nameof(Encodings))]
        public void A_deleted_highest_id_is_not_handed_out_again(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("highest");
            using (var db = Open(path, password, format, indexing))
            {
                for (int i = 0; i < 5; i++) db.Insert(new NumberedItem { Name = "n" + i });
                Assert.True(db.Delete(db.Find<NumberedItem>(x => x.Id == 5)));
            }
            using (var db = Open(path, password, format, indexing))
                Assert.Equal(6, db.Insert(new NumberedItem { Name = "after" }).Id);
        }

        [Theory]
        [MemberData(nameof(Encodings))]
        public void Emptying_a_table_does_not_restart_its_numbering(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("empty");
            using (var db = Open(path, password, format, indexing))
            {
                for (int i = 0; i < 3; i++) db.Insert(new NumberedLong { Name = "n" + i });
                foreach (var row in db.Select<NumberedLong>().ToList()) Assert.True(db.Delete(row));
                Assert.Equal(0, db.Count<NumberedLong>());
            }
            using (var db = Open(path, password, format, indexing))
            {
                Assert.Equal(0, db.Count<NumberedLong>());
                Assert.Equal(4L, db.Insert(new NumberedLong { Name = "after" }).Id);
            }
        }

        // The last row isn't the highest when a lower Id was given explicitly after a higher one.
        [Theory]
        [MemberData(nameof(Encodings))]
        public void The_next_id_is_above_the_highest_row_not_the_last(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("explicit");
            using (var db = Open(path, password, format, indexing))
            {
                db.Insert(new NumberedItem { Id = 100, Name = "high" });
                db.Insert(new NumberedItem { Id = 7, Name = "low" });
            }
            using (var db = Open(path, password, format, indexing))
                Assert.Equal(101, db.Insert(new NumberedItem { Name = "next" }).Id);
        }

        // Deleting a row in another context than the one that numbered it: the mark covers the row.
        [Fact]
        public void A_delete_in_a_later_context_still_records_the_highest_value()
        {
            string path = NewDatabase("later");
            using (var db = new XodContext(path))
                for (int i = 0; i < 4; i++) db.Insert(new NumberedItem { Name = "n" + i });
            using (var db = new XodContext(path))
                Assert.True(db.Delete(db.Find<NumberedItem>(x => x.Id == 4)));
            using (var db = new XodContext(path))
                Assert.Equal(5, db.Insert(new NumberedItem { Name = "after" }).Id);
        }

        [Fact]
        public void An_autonumber_without_an_index_continues_after_its_highest_row_is_deleted()
        {
            string path = NewDatabase("unindexed");
            using (var db = new XodContext(path))
            {
                for (int i = 0; i < 3; i++) db.Insert(new SequencedNote { Key = "k" + i });
                Assert.Equal(new[] { 1, 2, 3 }, db.Select<SequencedNote>().Select(n => n.Seq).OrderBy(s => s));
                Assert.True(db.Delete(db.Find<SequencedNote>(n => n.Key == "k2")));
            }
            using (var db = new XodContext(path))
                Assert.Equal(4, db.Insert(new SequencedNote { Key = "k9" }).Seq);
        }
    }
}
