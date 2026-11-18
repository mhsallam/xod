using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-189 and ISS-181: what a parent's update writes into the keyed rows it names. It descended into a keyed list
    /// only when the parent's own row changed, so an edit to a child alone wasn't written (ISS-189); and it took a
    /// changed single reference as an edit of the row it pointed at, rewriting that row as the new object, which threw
    /// when the new object was another row (ISS-181). Now each relation is written by what it declares: a parent owns its
    /// children, so its update writes each child's values; a relation that declares <c>Cascade = Update</c> writes them too; any other reference,
    /// single or a list, only links (or repoints) to its rows and never writes their values, so a stale copy held by the
    /// parent can't overwrite a newer one.
    /// </summary>
    public class XodRelatedRowsUpdateTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-related-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class UrLine
        {
            public int Id { get; set; }
            public int UrOrderId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(UrOrderId))] public UrOrder Order { get; set; }
        }

        public class UrOrder
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Children] public List<UrLine> Lines { get; set; }
        }

        public class UrAuthor
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class UrBook
        {
            public int Id { get; set; }
            public string Title { get; set; }
            public int AuthorId { get; set; }
            [ForeignKey(nameof(AuthorId))] public UrAuthor Author { get; set; }
        }

        public class UrShelf
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public UrAuthor Fav { get; set; }
            public List<UrAuthor> Authors { get; set; }
        }

        public class UrDesk
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Property(Cascade = CascadeOptions.Update)] public UrAuthor Owner { get; set; }
            [Property(Cascade = CascadeOptions.All)] public List<UrAuthor> Crew { get; set; }
        }

        private string NewPath(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ur.xod");
        }

        private static XodContext Open(string path, bool indexing) =>
            new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing });

        private static string[] Names(XodContext db) =>
            db.Select<UrAuthor>().Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_update_writes_an_edit_to_a_child_alone(bool indexing)
        {
            string path = NewPath("child", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new UrOrder { Name = "o", Lines = new() { new UrLine { Text = "l1" }, new UrLine { Text = "l2" } } });

                var order = db.Find<UrOrder>(o => o.Name == "o");
                order.Lines.Single(l => l.Text == "l1").Text = "l1 edited";
                Assert.True(db.Update(order));
                Assert.Equal(new[] { "l1 edited", "l2" }, db.Select<UrLine>().Select(l => l.Text).OrderBy(t => t, StringComparer.Ordinal).ToArray());
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(new[] { "l1 edited", "l2" },
                    db.Find<UrOrder>(o => o.Name == "o").Lines.Select(l => l.Text).OrderBy(t => t, StringComparer.Ordinal).ToArray());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_update_points_a_reference_at_another_row(bool indexing)
        {
            string path = NewPath("repoint", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new UrAuthor { Name = "a" });
                db.Insert(new UrAuthor { Name = "b" });
                var a = db.Find<UrAuthor>(x => x.Name == "a");
                var b = db.Find<UrAuthor>(x => x.Name == "b");
                db.Insert(new UrBook { Title = "t", AuthorId = a.Id });
                db.Insert(new UrShelf { Name = "s", Fav = a });

                var book = db.Find<UrBook>(x => x.Title == "t");
                book.Author = b;
                book.AuthorId = b.Id;
                Assert.True(db.Update(book));

                var shelf = db.Find<UrShelf>(x => x.Name == "s");
                shelf.Fav = b;
                Assert.True(db.Update(shelf));

                // To a new object: it gets a row, and the old one stays.
                shelf = db.Find<UrShelf>(x => x.Name == "s");
                shelf.Fav = new UrAuthor { Name = "c" };
                Assert.True(db.Update(shelf));

                Assert.Equal(new[] { "a", "b", "c" }, Names(db));
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal("b", db.Find<UrBook>(x => x.Title == "t").Author.Name);
                Assert.Equal("c", db.Find<UrShelf>(x => x.Name == "s").Fav.Name);
                Assert.Equal(new[] { "a", "b", "c" }, Names(db));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_reference_s_stale_copy_does_not_overwrite_its_row(bool indexing)
        {
            string path = NewPath("stale", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new UrAuthor { Name = "a" });
                var a = db.Find<UrAuthor>(x => x.Name == "a");
                db.Insert(new UrBook { Title = "t", AuthorId = a.Id });
                db.Insert(new UrShelf { Name = "s", Fav = a, Authors = new() { a } });

                var book = db.Find<UrBook>(x => x.Title == "t");
                var shelf = db.Find<UrShelf>(x => x.Name == "s");

                var fresh = db.Find<UrAuthor>(x => x.Name == "a");
                fresh.Name = "renamed";
                Assert.True(db.Update(fresh));

                book.Title = "t2";
                Assert.True(db.Update(book));
                shelf.Name = "s2";
                shelf.Authors.Add(new UrAuthor { Name = "n" });
                Assert.True(db.Update(shelf));

                Assert.Equal(new[] { "n", "renamed" }, Names(db));
            }

            using (var db = Open(path, indexing))
                Assert.Equal(new[] { "n", "renamed" }, Names(db));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_relation_that_cascades_updates_writes_its_rows(bool indexing)
        {
            string path = NewPath("cascade", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new UrDesk { Name = "d", Owner = new UrAuthor { Name = "o" }, Crew = new() { new UrAuthor { Name = "c1" } } });

                var desk = db.Find<UrDesk>(x => x.Name == "d");
                desk.Owner.Name = "o edited";
                desk.Crew[0].Name = "c1 edited";
                Assert.True(db.Update(desk));
            }

            using (var db = Open(path, indexing))
            {
                var desk = db.Find<UrDesk>(x => x.Name == "d");
                Assert.Equal("o edited", desk.Owner.Name);
                Assert.Equal("c1 edited", desk.Crew.Single().Name);
                Assert.Equal(new[] { "c1 edited", "o edited" }, Names(db));
            }
        }

        public class UrNode
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Property(Cascade = CascadeOptions.Update)] public UrNode Next { get; set; }
        }

        // Two rows that cascade updates to each other: the update writes both and stops at the second turn.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_cycle_of_cascading_updates_ends(bool indexing)
        {
            string path = NewPath("cycle", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new UrNode { Name = "a" });
                db.Insert(new UrNode { Name = "b" });
                var a = db.Find<UrNode>(x => x.Name == "a");
                var b = db.Find<UrNode>(x => x.Name == "b");
                a.Next = b;
                b.Next = a;
                Assert.True(db.Update(a));
                Assert.True(db.Update(b));

                a = db.Find<UrNode>(x => x.Name == "a");
                a.Name = "a2";
                a.Next.Name = "b2";
                Assert.True(db.Update(a));
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(new[] { "a2", "b2" }, db.Select<UrNode>().Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                Assert.Equal("b2", db.Find<UrNode>(x => x.Name == "a2").Next.Name);
                Assert.Equal("a2", db.Find<UrNode>(x => x.Name == "b2").Next.Name);
            }
        }

        public class UrLeaf
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class UrArm
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Property(Cascade = CascadeOptions.Update)] public UrLeaf Leaf { get; set; }
        }

        public class UrRoot
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Property(Cascade = CascadeOptions.Update)] public UrArm Left { get; set; }
            [Property(Cascade = CascadeOptions.Update)] public UrArm Right { get; set; }
            [Property(Cascade = CascadeOptions.Update)] public List<UrLeaf> Leaves { get; set; }
        }

        // One row reached by two paths, or as two copies in one list: written once, from the first copy reached. The
        // later copy was read before the edit, and writing it too undid the edit.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_row_reached_twice_is_written_once(bool indexing)
        {
            string path = NewPath("diamond", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new UrLeaf { Name = "leaf" });
                var leaf = db.Find<UrLeaf>(x => x.Name == "leaf");
                db.Insert(new UrArm { Name = "left", Leaf = leaf });
                db.Insert(new UrArm { Name = "right", Leaf = leaf });
                var left = db.Find<UrArm>(x => x.Name == "left");
                var right = db.Find<UrArm>(x => x.Name == "right");
                db.Insert(new UrRoot { Name = "root", Left = left, Right = right, Leaves = new() { leaf, leaf } });

                // A read shares one object per row; copies held from elsewhere, read before the edit, are not.
                var root = db.Find<UrRoot>(x => x.Name == "root");
                root.Left.Leaf.Name = "edited";
                root.Right.Leaf = new UrLeaf { Id = leaf.Id, Name = "leaf" };
                root.Leaves = new() { root.Left.Leaf, new UrLeaf { Id = leaf.Id, Name = "leaf" } };

                var updates = new List<string>();
                db.AfterAction += (_, e) => { if (e.Action == DatabaseActions.Update && e.Type == typeof(UrLeaf)) updates.Add(((UrLeaf)e.Item).Name); };
                Assert.True(db.Update(root));
                Assert.Equal(new[] { "edited" }, updates.ToArray());
                Assert.Equal("edited", db.Find<UrLeaf>(x => x.Id == leaf.Id).Name);
            }

            using (var db = Open(path, indexing))
                Assert.Equal(new[] { "edited" }, db.Select<UrLeaf>().Select(l => l.Name).ToArray());
        }
    }
}
