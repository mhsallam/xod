using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-210, closed as designed: a relation's stored row code that finds no row is a state XOD's own operations
    /// make, and the reads that leave it out give the right answer. A table dropped (<c>Drop&lt;T&gt;</c>) deletes its
    /// rows without touching the rows that name them; a row deleted leaves the references to it (only a child is taken
    /// out of its parent's list, and not a child with no parent property: ISS-095); and XOD 2.0 left any child's entry
    /// in its parent's list after the child's delete. So the list reader leaves such an entry out, a reference with a <c>[ForeignKey]</c> is found again by its
    /// key, and one without reads as null. These pin that: a read that made such a code an error would fail them.
    /// </summary>
    public class XodDanglingRelationTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-dangling-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class DrTag
        {
            [PrimaryKey] public int Code { get; set; }
            public int DrBoxId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(DrBoxId))] public DrBox Box { get; set; }
        }

        public class DrBox
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
            [Children] public List<DrTag> Tags { get; set; }
        }

        public class DrAuthor
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
        }

        public class DrBook
        {
            [PrimaryKey] public int Id { get; set; }
            public int AuthorId { get; set; }
            [ForeignKey(nameof(AuthorId))] public DrAuthor Author { get; set; }
        }

        public class DrShelf
        {
            [PrimaryKey] public int Id { get; set; }
            public List<DrAuthor> Favourites { get; set; }
        }

        public class DrNote
        {
            [PrimaryKey] public int Id { get; set; }
            public object Ref { get; set; }
        }

        private XodContext Open(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return new XodContext(Path.Combine(dir, "dr.xod"));
        }

        // The children's table dropped: the parent's list still names them, and reads as empty; children inserted
        // again are listed.
        [Fact]
        public void A_parent_whose_children_s_table_was_dropped_reads_an_empty_list()
        {
            using var db = Open("drop-children");
            db.Insert(new DrBox { Id = 1, Name = "b", Tags = new() { new DrTag { Code = 1, Text = "t1" } } });
            Assert.True(db.Drop<DrTag>());

            Assert.Empty(db.Find<DrBox>(b => b.Id == 1).Tags ?? new List<DrTag>());
            Assert.Single(db.Select<DrBox>());

            db.Insert(new DrTag { Code = 2, DrBoxId = 1, Text = "t2" });
            Assert.Equal(new[] { "t2" }, db.Find<DrBox>(b => b.Id == 1).Tags.Select(t => t.Text));
        }

        // A [ForeignKey] reference whose row was deleted reads as null, and as the row inserted again with its key.
        [Fact]
        public void A_foreign_reference_whose_row_was_deleted_is_found_again_by_its_key()
        {
            using var db = Open("foreign");
            db.Insert(new DrAuthor { Id = 1, Name = "a" });
            db.Insert(new DrBook { Id = 1, AuthorId = 1 });

            db.Delete(new DrAuthor { Id = 1 });
            Assert.Null(db.Find<DrBook>(b => b.Id == 1).Author);

            db.Insert(new DrAuthor { Id = 1, Name = "a again" });
            Assert.Equal("a again", db.Find<DrBook>(b => b.Id == 1).Author?.Name);
        }

        // A reference of type object whose row is gone, deleted or its table dropped, reads as null: it threw that
        // the type isn't registered, and the row holding it couldn't be read (the review of TASK-104).
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_object_reference_whose_row_is_gone_reads_as_null(bool drop)
        {
            using var db = Open("object-" + (drop ? "drop" : "delete"));
            db.Insert(new DrAuthor { Id = 1, Name = "a" });
            db.Insert(new DrNote { Id = 1, Ref = db.Find<DrAuthor>(a => a.Id == 1) });
            Assert.Equal("a", (db.Find<DrNote>(n => n.Id == 1).Ref as DrAuthor)?.Name);

            if (drop)
                Assert.True(db.Drop<DrAuthor>());
            else
                db.Delete(new DrAuthor { Id = 1 });

            Assert.Null(db.Find<DrNote>(n => n.Id == 1).Ref);
        }

        // A plain list of rows with a key (not children): one of them deleted, the list reads without it.
        [Fact]
        public void A_list_whose_row_was_deleted_reads_without_it()
        {
            using var db = Open("list");
            db.Insert(new DrAuthor { Id = 1, Name = "a1" });
            db.Insert(new DrAuthor { Id = 2, Name = "a2" });
            db.Insert(new DrShelf { Id = 1, Favourites = new() { db.Find<DrAuthor>(a => a.Id == 1), db.Find<DrAuthor>(a => a.Id == 2) } });

            db.Delete(new DrAuthor { Id = 1 });

            Assert.Equal(new[] { "a2" }, db.Find<DrShelf>(s => s.Id == 1).Favourites.Select(a => a.Name));
        }
    }
}
