using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-135: a child inserted on its own is added to its parent's list. The parent's entry for it was found by the
    /// child's key values, so a child type without a key matched the first entry, which was repointed to the new child:
    /// the parent listed the new child in place of the first, though both rows existed. An entry is now found by the
    /// row's code; a key takes over an entry only for a row of a keyed type, and only an entry that names no row (one
    /// an earlier version left behind). A keyed child whose key is taken left the live child's entry repointed to it,
    /// and in a transaction or buffered mode the undo of its refused insert took that entry out.
    /// </summary>
    public class XodChildEntryTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-entry-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class EcNote
        {
            public int EcBoxId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(EcBoxId))] public EcBox Box { get; set; }
        }

        public class EcTag
        {
            [PrimaryKey] public int Code { get; set; }
            public int EcBoxId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(EcBoxId))] public EcBox Box { get; set; }
        }

        public class EcBox
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Children] public List<EcNote> Notes { get; set; }
            [Children] public List<EcTag> Tags { get; set; }
        }

        private string NewPath(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ec.xod");
        }

        private static XodContext Open(string path, bool indexing, DurabilityMode durability = DurabilityMode.Immediate) =>
            new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing, Durability = durability });

        private static string[] Notes(XodContext db) =>
            db.Find<EcBox>(b => b.Name == "b").Notes.Select(n => n.Text).OrderBy(t => t, StringComparer.Ordinal).ToArray();

        private static string[] Tags(XodContext db) =>
            db.Find<EcBox>(b => b.Name == "b").Tags.Select(t => t.Code + ":" + t.Text).OrderBy(t => t, StringComparer.Ordinal).ToArray();

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_child_without_a_key_inserted_on_its_own_joins_its_parent_s_list(bool indexing)
        {
            string path = NewPath("keyless", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new EcBox { Name = "b", Notes = new() { new EcNote { Text = "n1" } } });
                var box = db.Find<EcBox>(b => b.Name == "b");
                db.Insert(new EcNote { Box = box, Text = "n2" });
                db.Insert(new EcNote { Box = box, Text = "n3" });
                Assert.Equal(new[] { "n1", "n2", "n3" }, Notes(db));
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(new[] { "n1", "n2", "n3" }, Notes(db));
                Assert.Equal(3, db.Count<EcNote>());
            }
        }

        // A keyed child refused for its key, in a transaction and in buffered mode, where the statement's own undo takes
        // its insert back: the live child keeps its entry.
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        [InlineData(false, true)]
        public void A_keyed_child_refused_for_its_key_leaves_the_listed_child(bool indexing, bool buffered)
        {
            string path = NewPath("taken" + (buffered ? "-buffered" : "-tx"), indexing);
            var durability = buffered ? DurabilityMode.Buffered : DurabilityMode.Immediate;
            using (var db = Open(path, indexing, durability))
            {
                db.Insert(new EcBox { Name = "b", Tags = new() { new EcTag { Code = 1, Text = "one" } } });
                db.Flush();
                var box = db.Find<EcBox>(b => b.Name == "b");

                if (buffered)
                {
                    Assert.Throws<ReservedPrimaryKeyException>(() => db.Insert(new EcTag { Code = 1, Box = box, Text = "again" }));
                }
                else
                {
                    using var tx = db.BeginTransaction();
                    Assert.Throws<ReservedPrimaryKeyException>(() => db.Insert(new EcTag { Code = 1, Box = box, Text = "again" }));
                    db.Insert(new EcTag { Code = 2, Box = box, Text = "two" });
                    tx.Commit();
                }

                string[] expected = buffered ? new[] { "1:one" } : new[] { "1:one", "2:two" };
                Assert.Equal(expected, Tags(db));
                db.Flush();
            }

            using (var db = Open(path, indexing))
                Assert.Equal(buffered ? new[] { "1:one" } : new[] { "1:one", "2:two" }, Tags(db));
        }

        // A list's entry keeps the key its child had when it was listed, and an update of the child alone doesn't write
        // it: a key is the key check's to decide, never the entry's. With the entry asked, a child given a key another
        // child had been changed from was taken as a key already used, and left out of its parent's list.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_key_given_up_by_an_update_can_be_listed_again(bool indexing)
        {
            string path = NewPath("rekey", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new EcBox { Name = "b", Tags = new() { new EcTag { Code = 1, Text = "one" } } });
                var box = db.Find<EcBox>(b => b.Name == "b");
                var one = db.Find<EcTag>(t => t.Code == 1);
                Assert.True(db.Update(one, new EcTag { Code = 5, EcBoxId = one.EcBoxId, Text = "five" }));
                db.Insert(new EcTag { Code = 1, Box = box, Text = "new one" });
                Assert.Equal(new[] { "1:new one", "5:five" }, Tags(db));
            }

            using (var db = Open(path, indexing))
                Assert.Equal(new[] { "1:new one", "5:five" }, Tags(db));
        }
    }
}
