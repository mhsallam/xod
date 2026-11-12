// ISS-062: outside a transaction, a statement that failed part-way left the rows it had already created: a
// parent whose second child was refused left its first child in the child table, with a parent key that
// matched no row. A statement that fails now removes the rows it created.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Xod.Tests
{
    // A child that refers, with cascade delete, to rows that were there before the statement.
    public class UndoTag
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class UndoChild
    {
        public int Id { get; set; }
        public int UndoParentId { get; set; }
        [Required] public string Label { get; set; }
        [Property(Cascade = CascadeOptions.Delete)] public List<UndoTag> Tags { get; set; }
    }

    public class UndoParent
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Children] public List<UndoChild> Children { get; set; }
    }

    // A child with a parent navigation property: its insert adds it to the existing parent's list.
    public class LinkParent
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Children] public List<LinkChild> Children { get; set; }
    }

    public class LinkChild
    {
        public int Id { get; set; }
        public int LinkParentId { get; set; }
        public string Label { get; set; }
        [ParentKey(nameof(LinkParentId))] public LinkParent Parent { get; set; }
    }

    public class XodStatementUndoTests
    {
        private static string PageText<T>(string dbPath)
        {
            string dir = Path.GetDirectoryName(dbPath)!;
            string table = File.ReadAllText(Path.Combine(dir, typeof(T).FullName + ".xtab"));
            return string.Concat(Regex.Matches(table, "file=\"([^\"]+)\"")
                .Select(m => File.ReadAllText(Path.Combine(dir, m.Groups[1].Value))));
        }

        // ISS-088: the undo of a child inserted on its own left it in the list of the parent the insert had added
        // it to: the unlink compared the parent's entries ("page.row") with the bare row code, and matched none.
        [Fact]
        public void A_failed_insert_of_a_child_leaves_its_parent_without_it()
        {
            string path = NewDatabase("child-link");
            using (var db = new XodContext(path))
            {
                var parent = db.Insert(new LinkParent { Name = "p" });
                db.AfterAction += (_, e) =>
                {
                    if (e.Item is LinkChild c && c.Label == "refused")
                        throw new InvalidOperationException("trigger refused it");
                };
                Assert.Throws<InvalidOperationException>(() => db.Insert(new LinkChild { LinkParentId = parent.Id, Label = "refused" }));
                Assert.Empty(db.Select<LinkChild>());
            }
            Assert.DoesNotContain("<LinkChild", PageText<LinkParent>(path));
        }

        // ISS-088 in an ordinary delete: one found through the primary-key index went through the same unlink,
        // so the parent went on listing the deleted child.
        [Fact]
        public void Deleting_a_child_takes_it_out_of_its_parents_list()
        {
            string path = NewDatabase("delete-link");
            using (var db = new XodContext(path))
            {
                var parent = db.Insert(new LinkParent { Name = "p" });
                var kept = db.Insert(new LinkChild { LinkParentId = parent.Id, Label = "kept" });
                var deleted = db.Insert(new LinkChild { LinkParentId = parent.Id, Label = "deleted" });
                Assert.Equal(2, Regex.Matches(PageText<LinkParent>(path), "<LinkChild").Count);
                Assert.True(db.Delete(deleted));
            }
            Assert.Single(Regex.Matches(PageText<LinkParent>(path), "<LinkChild"));
            using (var db = new XodContext(path))
                Assert.Equal(new[] { "kept" }, db.Select<LinkParent>().Single().Children.Select(c => c.Label));
        }

        // A number a failed statement took isn't handed out again, after a reopen either: the undo keeps the
        // counters and records them, as a rollback does (ISS-083).
        [Fact]
        public void A_number_a_failed_statement_took_is_not_handed_out_again()
        {
            string path = NewDatabase("numbers");
            using (var db = new XodContext(path))
                Assert.ThrowsAny<XodException>(() => db.Insert(ParentWithRefusedSecondChild("refused")));
            using (var db = new XodContext(path))
                Assert.Equal(2, db.Insert(new RefusedParent { Name = "next", Children = new() }).Id);
        }

        // ISS-091: an undo that failed part-way left the index without the row while the page still held it (the
        // index entry went before the page delete failed, and the undo saved the index anyway): a lookup missed a
        // row a scan found. The index now agrees with the pages, and the caller gets both failures.
        [Fact]
        public void An_undo_that_fails_leaves_the_index_agreeing_with_the_pages()
        {
            if (!OperatingSystem.IsWindows())
                return; // a file held open blocks its deletion on Windows only

            string path = NewDatabase("undo-fails");
            string dir = Path.GetDirectoryName(path)!;
            FileStream held = null;
            using (var db = new XodContext(path))
            {
                db.AfterAction += (_, e) =>
                {
                    if (e.Item is UndoTag t && t.Name == "refused")
                    {
                        string table = File.ReadAllText(Path.Combine(dir, typeof(UndoTag).FullName + ".xtab"));
                        string page = Regex.Match(table, "file=\"([^\"]+)\"").Groups[1].Value;
                        held = new FileStream(Path.Combine(dir, page), FileMode.Open, FileAccess.Read, FileShare.None);
                        throw new InvalidOperationException("trigger refused it");
                    }
                };
                try
                {
                    var failure = Assert.ThrowsAny<XodException>(() => db.Insert(new UndoTag { Name = "refused" }));
                    var both = Assert.IsType<AggregateException>(failure.InnerException);
                    Assert.IsType<InvalidOperationException>(both.InnerExceptions[0]);
                }
                finally
                {
                    held?.Dispose();
                }
                Assert.Equal(db.Select<UndoTag>().Count(), db.Query<UndoTag>(t => t.Id == 1).Count());
                // What the context holds may not be what the disk holds: it writes nothing more.
                Assert.Contains("Reopen", Assert.ThrowsAny<XodException>(() => db.Insert(new UndoTag { Name = "after" })).Message);
            }
            using (var db = new XodContext(path))
            {
                Assert.Equal(db.Select<UndoTag>().Count(), db.Query<UndoTag>(t => t.Id == 1).Count());
                db.Insert(new UndoTag { Name = "after reopening" });
            }
        }

        public static IEnumerable<object[]> Formats() => XodTextRoundTripTests.Formats();

        private static string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-statement", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static RefusedParent ParentWithRefusedSecondChild(string name) => new RefusedParent
        {
            Name = name,
            Children = new() { new RefusedChild { Label = name + "-first" }, new RefusedChild() },
        };

        [Theory]
        [MemberData(nameof(Formats))]
        public void An_insert_whose_second_child_is_refused_leaves_no_child(StorageFormat format, string password)
        {
            string path = NewDatabase("insert-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            using (var db = new XodContext(path, password, options))
            {
                Assert.ThrowsAny<XodException>(() => db.Insert(ParentWithRefusedSecondChild("refused")));
                Assert.Empty(db.Select<RefusedChild>());
                Assert.Empty(db.Select<RefusedParent>());

                db.Insert(new RefusedParent { Name = "kept", Children = new() { new RefusedChild { Label = "c" } } });
            }

            using (var db = new XodContext(path, password, options))
            {
                Assert.Equal(new[] { "c" }, db.Select<RefusedChild>().Select(c => c.Label));
                var parent = db.Select<RefusedParent>().Single();
                Assert.Equal("kept", parent.Name);
                Assert.Equal(new[] { "c" }, parent.Children.Select(c => c.Label));
            }
        }

        // Each item of a range is one statement: the items before the refused one stay, as before; the refused
        // item leaves nothing.
        [Fact]
        public void A_range_keeps_the_items_before_a_refused_one_and_nothing_of_it()
        {
            string path = NewDatabase("range");
            using var db = new XodContext(path);
            var good = new RefusedParent { Name = "good", Children = new() { new RefusedChild { Label = "g" } } };
            Assert.ThrowsAny<XodException>(() => db.InsertRange(new[] { good, ParentWithRefusedSecondChild("bad") }));
            Assert.Equal(new[] { "good" }, db.Select<RefusedParent>().Select(p => p.Name));
            Assert.Equal(new[] { "g" }, db.Select<RefusedChild>().Select(c => c.Label));
        }

        // An update that adds children, the second refused, leaves no new child and the row as it was.
        [Fact]
        public void An_update_whose_second_new_child_is_refused_leaves_no_new_child()
        {
            string path = NewDatabase("update");
            using var db = new XodContext(path);
            db.Insert(new RefusedParent { Name = "p", Children = new() { new RefusedChild { Label = "old" } } });
            var current = db.Select<RefusedParent>().Single();

            var changed = new RefusedParent
            {
                Id = current.Id,
                Name = "renamed",
                Children = new() { new RefusedChild { Label = "new" }, new RefusedChild() },
            };
            Assert.ThrowsAny<XodException>(() => db.Update(current, changed));

            Assert.Equal(new[] { "old" }, db.Select<RefusedChild>().Select(c => c.Label));
            var after = db.Select<RefusedParent>().Single();
            Assert.Equal("p", after.Name);
        }

        // The undo removes only what the statement created: a removed child's cascade would also delete the
        // existing rows it refers to.
        [Fact]
        public void The_undo_leaves_rows_the_statement_only_referred_to()
        {
            string path = NewDatabase("cascade");
            using var db = new XodContext(path);
            var tag = db.Insert(new UndoTag { Name = "existing" });
            var parent = new UndoParent
            {
                Name = "refused",
                Children = new() { new UndoChild { Label = "first", Tags = new() { tag } }, new UndoChild() },
            };
            Assert.ThrowsAny<XodException>(() => db.Insert(parent));
            Assert.Empty(db.Select<UndoChild>());
            Assert.Equal(new[] { "existing" }, db.Select<UndoTag>().Select(t => t.Name));
        }

        // A trigger that throws after the row is written fails the statement too: the row and its children go.
        [Fact]
        public void An_after_trigger_that_throws_leaves_nothing_of_the_insert()
        {
            string path = NewDatabase("trigger");
            using var db = new XodContext(path);
            db.AfterAction += (_, e) =>
            {
                if (e.Item is RefusedParent p && p.Name == "refused")
                    throw new InvalidOperationException("trigger refused it");
            };
            Assert.Throws<InvalidOperationException>(() => db.Insert(new RefusedParent { Name = "refused", Children = new() { new RefusedChild { Label = "c" } } }));
            Assert.Empty(db.Select<RefusedParent>());
            Assert.Empty(db.Select<RefusedChild>());

            db.Insert(new RefusedParent { Name = "kept", Children = new() { new RefusedChild { Label = "k" } } });
            Assert.Equal(new[] { "k" }, db.Select<RefusedChild>().Select(c => c.Label));
        }

        // Inside a transaction the same: a failed statement that the caller catches leaves nothing for the
        // commit to keep.
        [Fact]
        public void A_failed_statement_inside_a_transaction_leaves_nothing_for_the_commit()
        {
            string path = NewDatabase("transaction");
            using var db = new XodContext(path);
            using (var tx = db.BeginTransaction())
            {
                Assert.ThrowsAny<XodException>(() => db.Insert(ParentWithRefusedSecondChild("refused")));
                db.Insert(new RefusedParent { Name = "kept", Children = new() { new RefusedChild { Label = "c" } } });
                tx.Commit();
            }
            Assert.Equal(new[] { "c" }, db.Select<RefusedChild>().Select(c => c.Label));
            Assert.Equal(new[] { "kept" }, db.Select<RefusedParent>().Select(p => p.Name));
        }
    }
}
