using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-202: an update that moves a child to another parent takes it out of its old parent's list. The old parent
    /// was taken from the object the update was given, and a child read on its own doesn't hold its parent, so the old
    /// parent was never found: both parents listed the child, and deleting the old one deleted it. The old parent is
    /// the one the stored row names.
    /// </summary>
    public class XodChildMoveTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-move-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class MvNote
        {
            public int MvBoxId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(MvBoxId))] public MvBox Box { get; set; }
        }

        public class MvTag
        {
            [PrimaryKey] public int Code { get; set; }
            public int MvBoxId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(MvBoxId))] public MvBox Box { get; set; }
        }

        public class MvBox
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
            [Children] public List<MvNote> Notes { get; set; }
            [Children] public List<MvTag> Tags { get; set; }
        }

        private string NewPath(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "mv.xod");
        }

        private static XodContext Open(string path, bool indexing) =>
            new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing });

        private static string[] Tags(XodContext db, int box) =>
            (db.Find<MvBox>(b => b.Id == box).Tags ?? new List<MvTag>()).Select(t => t.Text).OrderBy(t => t, StringComparer.Ordinal).ToArray();

        private static string[] Notes(XodContext db, int box) =>
            (db.Find<MvBox>(b => b.Id == box).Notes ?? new List<MvNote>()).Select(t => t.Text).OrderBy(t => t, StringComparer.Ordinal).ToArray();

        // A keyed child read on its own, moved by its parent object and key, or by its parent key alone.
        [Theory]
        [InlineData(true, true)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        public void A_child_moved_on_its_own_leaves_its_old_parent(bool indexing, bool byObject)
        {
            string path = NewPath("tag" + (byObject ? "-object" : "-key"), indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new MvBox { Id = 1, Name = "one", Tags = new() { new MvTag { Code = 1, Text = "t1" }, new MvTag { Code = 2, Text = "t2" } } });
                db.Insert(new MvBox { Id = 2, Name = "two" });

                var tag = db.Find<MvTag>(t => t.Code == 1);
                Assert.Null(tag.Box);
                // By object, its key set to match (a key and an object that disagree are refused,
                // ReservedChildException); or by the key alone.
                if (byObject)
                    tag.Box = db.Find<MvBox>(b => b.Id == 2);
                tag.MvBoxId = 2;
                Assert.True(db.Update(tag));

                Assert.Equal(new[] { "t2" }, Tags(db, 1));
                Assert.Equal(new[] { "t1" }, Tags(db, 2));

                db.Delete(db.Find<MvBox>(b => b.Id == 1));
                Assert.NotNull(db.Find<MvTag>(t => t.Code == 1));
                Assert.Null(db.Find<MvTag>(t => t.Code == 2));
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(new[] { "t1" }, Tags(db, 2));
                Assert.Equal(1, db.Count<MvTag>());
            }
        }

        // A child without a key, read through its parent (so it holds no parent object of its own) and moved.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_child_without_a_key_moved_on_its_own_leaves_its_old_parent(bool indexing)
        {
            string path = NewPath("note", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new MvBox { Id = 1, Name = "one", Notes = new() { new MvNote { Text = "n1" }, new MvNote { Text = "n2" } } });
                db.Insert(new MvBox { Id = 2, Name = "two" });

                var note = db.Find<MvNote>(n => n.Text == "n1");
                Assert.Null(note.Box);
                var moved = new MvNote { Text = "n1", Box = db.Find<MvBox>(b => b.Id == 2) };
                Assert.True(db.Update(note, moved));

                Assert.Equal(new[] { "n2" }, Notes(db, 1));
                Assert.Equal(new[] { "n1" }, Notes(db, 2));

                db.Delete(db.Find<MvBox>(b => b.Id == 1));
                Assert.Equal(new[] { "n1" }, db.Select<MvNote>().Select(n => n.Text).ToArray());
            }
        }

        public class MfTag
        {
            [PrimaryKey] public int Code { get; set; }
            public int MfBoxId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(MfBoxId))] public MfBox Box { get; set; }
        }

        public class MfBox
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
            [Children] public List<MfTag> Tags { get; set; }
            [Property(Cascade = CascadeOptions.Update)] public MfTag Featured { get; set; }
        }

        // A row a parent writes through a reference isn't that parent's child. The relation a row was written through
        // was a field of the parent's track, set by its children's writes and never reset, so the tag took the box
        // that features it as its parent: its key was rewritten, and it left its own box's list (review of TASK-100).
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_child_another_parent_features_keeps_its_own_parent(bool indexing)
        {
            string path = NewPath("featured", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new MfBox { Id = 1, Name = "one", Tags = new() { new MfTag { Code = 1, Text = "t1" }, new MfTag { Code = 2, Text = "t2" } } });
                db.Insert(new MfBox { Id = 2, Name = "two" });

                var two = db.Find<MfBox>(b => b.Id == 2);
                var t1 = db.Find<MfTag>(t => t.Code == 1);
                t1.Text = "t1b";
                two.Featured = t1;
                Assert.True(db.Update(two));
            }

            using (var db = Open(path, indexing))
            {
                var tag = db.Find<MfTag>(t => t.Code == 1);
                Assert.Equal(1, tag.MfBoxId);
                Assert.Equal("t1b", tag.Text);
                Assert.Equal(new[] { "t1b", "t2" }, db.Find<MfBox>(b => b.Id == 1).Tags.Select(t => t.Text).OrderBy(t => t, StringComparer.Ordinal).ToArray());
                var two = db.Find<MfBox>(b => b.Id == 2);
                Assert.Empty(two.Tags ?? new List<MfTag>());
                Assert.Equal(1, two.Featured.Code);
            }
        }

        public class MtTransfer
        {
            [PrimaryKey] public int Id { get; set; }
            public int FromId { get; set; }
            public int ToId { get; set; }
            public string Memo { get; set; }
            [ParentKey(nameof(FromId))] public MtAccount From { get; set; }
            [ParentKey(nameof(ToId))] public MtAccount To { get; set; }
        }

        public class MtAccount
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
            [Children(RemoteParentProperty = nameof(MtTransfer.From))] public List<MtTransfer> Outgoing { get; set; }
            [Children(RemoteParentProperty = nameof(MtTransfer.To))] public List<MtTransfer> Incoming { get; set; }
        }

        private static int[] Ids(List<MtTransfer> list) => (list ?? new List<MtTransfer>()).Select(t => t.Id).OrderBy(i => i).ToArray();

        // A child with two parent properties of one type, and a parent with a list for each (ISS-203): each parent
        // property gets its own parent, and the child is listed in the list that pairs with it. The second parent
        // property took the first one's parent and its key was rewritten; a child written on its own was listed in
        // the parent's first list of its type.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_child_with_two_parents_of_one_type_is_listed_by_each_in_its_own_list(bool indexing)
        {
            string path = NewPath("two-parents", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new MtAccount { Id = 1, Name = "a1" });
                db.Insert(new MtAccount { Id = 2, Name = "a2" });
                db.Insert(new MtTransfer { Id = 1, FromId = 1, ToId = 2, Memo = "alone" });
                db.Insert(new MtAccount { Id = 3, Name = "a3", Outgoing = new() { new MtTransfer { Id = 2, ToId = 1, Memo = "listed" } } });
            }

            using (var db = Open(path, indexing))
            {
                var alone = db.Find<MtTransfer>(t => t.Id == 1);
                Assert.Equal((1, 2), (alone.FromId, alone.ToId));
                var listed = db.Find<MtTransfer>(t => t.Id == 2);
                Assert.Equal((3, 1), (listed.FromId, listed.ToId));

                var a1 = db.Find<MtAccount>(a => a.Id == 1);
                var a2 = db.Find<MtAccount>(a => a.Id == 2);
                var a3 = db.Find<MtAccount>(a => a.Id == 3);
                Assert.Equal(new[] { 1 }, Ids(a1.Outgoing));
                Assert.Equal(new[] { 2 }, Ids(a1.Incoming));
                Assert.Empty(Ids(a2.Outgoing));
                Assert.Equal(new[] { 1 }, Ids(a2.Incoming));
                Assert.Equal(new[] { 2 }, Ids(a3.Outgoing));
                Assert.Empty(Ids(a3.Incoming));
            }
        }

        // A child row that names no parent (its parent element taken out of the page here, as a row written before its
        // parent existed has none): its old parent is the one its stored key finds, as for a delete.
        [Fact]
        public void A_child_whose_row_names_no_parent_leaves_the_one_its_key_finds()
        {
            string path = NewPath("unnamed", indexing: false);
            using (var db = Open(path, indexing: false))
            {
                db.Insert(new MvBox { Id = 1, Name = "one", Tags = new() { new MvTag { Code = 1, Text = "t1" } } });
                db.Insert(new MvBox { Id = 2, Name = "two" });
            }

            int removed = 0;
            foreach (string page in Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag"))
            {
                string text = File.ReadAllText(page);
                string cut = System.Text.RegularExpressions.Regex.Replace(text, "<Box refType=\"parent\"[^>]*>[^<]*</Box>", "");
                removed += (text.Length - cut.Length) > 0 ? 1 : 0;
                File.WriteAllText(page, cut);
            }
            Assert.Equal(1, removed);

            using (var db = Open(path, indexing: false))
            {
                Assert.Equal(new[] { "t1" }, Tags(db, 1));
                var tag = db.Find<MvTag>(t => t.Code == 1);
                tag.MvBoxId = 2;
                Assert.True(db.Update(tag));
                Assert.Empty(Tags(db, 1));
                Assert.Equal(new[] { "t1" }, Tags(db, 2));
            }
        }

        // The guard the old rule was right on: an update that keeps the parent keeps the child listed once.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_child_updated_in_place_stays_listed_once(bool indexing)
        {
            string path = NewPath("stay", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new MvBox { Id = 1, Name = "one", Tags = new() { new MvTag { Code = 1, Text = "t1" } } });
                var tag = db.Find<MvTag>(t => t.Code == 1);
                tag.Text = "t1b";
                Assert.True(db.Update(tag));
                Assert.Equal(new[] { "t1b" }, Tags(db, 1));
            }
        }
    }
}
