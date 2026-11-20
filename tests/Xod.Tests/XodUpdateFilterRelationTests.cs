using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-207: an update filter took or left a relation and its key columns one by one. Written alone, the key moved
    /// while the stored relation stayed (the parent element and the old parent's list, or the reference to the old
    /// row): the child was listed by, and deleted with, a parent its key no longer named. The other way round, the
    /// relation moved and the key stayed, and the next plain update moved it back. A filter now takes or leaves a
    /// relation and its keys together.
    /// </summary>
    public class XodUpdateFilterRelationTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-ufr-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class UfTag
        {
            [PrimaryKey] public int Code { get; set; }
            public int UfBoxId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(UfBoxId))] public UfBox Box { get; set; }
        }

        public class UfBox
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
            [Children] public List<UfTag> Tags { get; set; }
        }

        public class UfAuthor
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
        }

        public class UfBook
        {
            [PrimaryKey] public int Id { get; set; }
            public string Title { get; set; }
            public int AuthorId { get; set; }
            [ForeignKey(nameof(AuthorId))] public UfAuthor Author { get; set; }
        }

        private XodContext Open(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return new XodContext(Path.Combine(dir, "uf.xod"), null, new DatabaseOptions { EnableIndexing = indexing });
        }

        private static void Seed(XodContext db)
        {
            db.Insert(new UfBox { Id = 1, Name = "one", Tags = new() { new UfTag { Code = 1, Text = "t1" } } });
            db.Insert(new UfBox { Id = 2, Name = "two" });
        }

        private static string[] Tags(XodContext db, int box) =>
            (db.Find<UfBox>(b => b.Id == box).Tags ?? new List<UfTag>()).Select(t => t.Text).ToArray();

        // The key alone, targeted: the relation goes with it.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_parent_key_targeted_alone_moves_the_child(bool indexing)
        {
            using var db = Open("target-key", indexing);
            Seed(db);
            var tag = db.Find<UfTag>(t => t.Code == 1);
            tag.UfBoxId = 2;
            Assert.True(db.Update(tag, new UpdateFilter { Behavior = UpdateFilterBehavior.Target, Properties = new[] { nameof(UfTag.UfBoxId) } }));

            Assert.Empty(Tags(db, 1));
            Assert.Equal(new[] { "t1" }, Tags(db, 2));
            Assert.Equal(2, db.Find<UfTag>(t => t.Code == 1).UfBoxId);
            db.Delete(db.Find<UfBox>(b => b.Id == 1));
            Assert.NotNull(db.Find<UfTag>(t => t.Code == 1));
        }

        // The relation or its key skipped: both stay as stored.
        [Theory]
        [InlineData(true, nameof(UfTag.Box))]
        [InlineData(false, nameof(UfTag.Box))]
        [InlineData(true, nameof(UfTag.UfBoxId))]
        [InlineData(false, nameof(UfTag.UfBoxId))]
        public void A_parent_relation_or_its_key_skipped_leaves_both(bool indexing, string skipped)
        {
            using var db = Open("skip-" + skipped, indexing);
            Seed(db);
            var tag = db.Find<UfTag>(t => t.Code == 1);
            tag.UfBoxId = 2;
            tag.Box = db.Find<UfBox>(b => b.Id == 2);
            tag.Text = "t1b";
            Assert.True(db.Update(tag, new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = new[] { skipped } }));

            var stored = db.Find<UfTag>(t => t.Code == 1);
            Assert.Equal(1, stored.UfBoxId);
            Assert.Equal("t1b", stored.Text);
            Assert.Equal(new[] { "t1b" }, Tags(db, 1));
            Assert.Empty(Tags(db, 2));

            // A plain update afterwards changes nothing about the parent.
            stored.Text = "t1c";
            Assert.True(db.Update(stored));
            Assert.Equal(new[] { "t1c" }, Tags(db, 1));
            Assert.Empty(Tags(db, 2));
        }

        // The relation alone, targeted: its key goes with it.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_parent_relation_targeted_alone_writes_its_key(bool indexing)
        {
            using var db = Open("target-relation", indexing);
            Seed(db);
            var tag = db.Find<UfTag>(t => t.Code == 1);
            tag.UfBoxId = 2;
            tag.Box = db.Find<UfBox>(b => b.Id == 2);
            Assert.True(db.Update(tag, new UpdateFilter { Behavior = UpdateFilterBehavior.Target, Properties = new[] { nameof(UfTag.Box) } }));

            Assert.Equal(2, db.Find<UfTag>(t => t.Code == 1).UfBoxId);
            Assert.Empty(Tags(db, 1));
            Assert.Equal(new[] { "t1" }, Tags(db, 2));
        }

        // A [ForeignKey] reference skipped: its key stays as stored too.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_foreign_reference_skipped_leaves_its_key(bool indexing)
        {
            using var db = Open("skip-foreign", indexing);
            db.Insert(new UfAuthor { Id = 1, Name = "a1" });
            db.Insert(new UfAuthor { Id = 2, Name = "a2" });
            db.Insert(new UfBook { Id = 1, Title = "b", AuthorId = 1 });

            var book = db.Find<UfBook>(b => b.Id == 1);
            book.AuthorId = 2;
            book.Author = null;
            book.Title = "b2";
            Assert.True(db.Update(book, new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = new[] { nameof(UfBook.Author) } }));

            var stored = db.Find<UfBook>(b => b.Id == 1);
            Assert.Equal("b2", stored.Title);
            Assert.Equal(1, stored.AuthorId);
            Assert.Equal("a1", stored.Author?.Name);
        }

        // A [ForeignKey] reference and its key: targeting the key alone moves the reference.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_foreign_key_targeted_alone_moves_the_reference(bool indexing)
        {
            using var db = Open("foreign", indexing);
            db.Insert(new UfAuthor { Id = 1, Name = "a1" });
            db.Insert(new UfAuthor { Id = 2, Name = "a2" });
            db.Insert(new UfBook { Id = 1, Title = "b", AuthorId = 1 });

            var book = db.Find<UfBook>(b => b.Id == 1);
            book.AuthorId = 2;
            book.Author = null;
            Assert.True(db.Update(book, new UpdateFilter { Behavior = UpdateFilterBehavior.Target, Properties = new[] { nameof(UfBook.AuthorId) } }));

            var stored = db.Find<UfBook>(b => b.Id == 1);
            Assert.Equal(2, stored.AuthorId);
            Assert.Equal("a2", stored.Author?.Name);
        }
    }
}
