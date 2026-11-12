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

    // A child inserted on its own whose own children are refused (review of TASK-063).
    public class UndoGParent
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Children] public List<UndoGChild> Children { get; set; }
    }

    public class UndoGChild
    {
        public int Id { get; set; }
        public int UndoGParentId { get; set; }
        public string Label { get; set; }
        [ParentKey(nameof(UndoGParentId))] public UndoGParent Parent { get; set; }
        [Children] public List<UndoGGrand> Grands { get; set; }
    }

    public class UndoGGrand
    {
        public int Id { get; set; }
        public int UndoGChildId { get; set; }
        [Required] public string Label { get; set; }
    }

    // A child with a parent property and no key property for it: the parent is found from the row.
    public class NavParent
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Children] public List<NavChild> Kids { get; set; }
    }

    public class NavChild
    {
        public int Id { get; set; }
        public string Label { get; set; }
        public NavParent Parent { get; set; }
    }

    // The same with a natural key: a stale entry with the key hid a later child that had it.
    public class NavKeyParent
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Children] public List<NavKeyChild> Kids { get; set; }
    }

    public class NavKeyChild
    {
        [PrimaryKey] public string Code { get; set; }
        public string Label { get; set; }
        public NavKeyParent Parent { get; set; }
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

        // The same with buffered durability: the close of a context that writes nothing more writes nothing, so it
        // doesn't throw, and it lets go of everything (review of TASK-063: the close threw from the buffer's flush
        // and left the rest of the context undisposed). What was still buffered is dropped, as at a crash.
        [Fact]
        public void A_buffered_context_whose_undo_failed_closes_without_writing()
        {
            if (!OperatingSystem.IsWindows())
                return; // a file held open blocks its deletion on Windows only

            string path = NewDatabase("undo-fails-buffered");
            string dir = Path.GetDirectoryName(path)!;
            using (var seed = new XodContext(path))
                seed.Insert(new UndoTag { Name = "seed" });

            var db = new XodContext(path, null, new DatabaseOptions { Durability = DurabilityMode.Buffered, LockDatabase = true });
            FileStream held = null;
            db.AfterAction += (_, e) =>
            {
                if (e.Item is UndoTag t && t.Name == "refused")
                {
                    db.Delete(db.Find<UndoTag>(x => x.Name == "seed"));
                    string table = File.ReadAllText(Path.Combine(dir, typeof(UndoTag).FullName + ".xtab"));
                    string page = Regex.Match(table, "file=\"([^\"]+)\"").Groups[1].Value;
                    held = new FileStream(Path.Combine(dir, page), FileMode.Open, FileAccess.Read, FileShare.None);
                    throw new InvalidOperationException("trigger refused it");
                }
            };
            try
            {
                Assert.ThrowsAny<XodException>(() => db.Insert(new UndoTag { Name = "refused" }));
            }
            finally
            {
                held?.Dispose();
            }
            db.Dispose();

            // Let go of: an exclusive open succeeds.
            using (var after = new XodContext(path, null, new DatabaseOptions { LockDatabase = true }))
            {
                Assert.Equal(after.Select<UndoTag>().Count(), after.Query<UndoTag>(t => t.Id == 1).Count());
                after.Insert(new UndoTag { Name = "after reopening" });
            }
        }

        // ---- review of TASK-063: what the undo takes back ----

        // A child inserted on its own is added to its parent's list while its row is built, before its page is
        // written: a refused grandchild, or a parent key that disagrees, failed it after the parent was saved, and the
        // parent kept listing a child that didn't exist.
        [Fact]
        public void A_child_insert_that_fails_after_its_parent_listed_it_leaves_the_parent_without_it()
        {
            string path = NewDatabase("listed-then-failed");
            using (var db = new XodContext(path))
            {
                var parent = db.Insert(new UndoGParent { Name = "p" });
                Assert.ThrowsAny<Exception>(() => db.Insert(new UndoGChild
                {
                    UndoGParentId = parent.Id,
                    Label = "c",
                    Grands = new() { new UndoGGrand { Label = "g1" }, new UndoGGrand() },
                }));
                Assert.Empty(db.Select<UndoGChild>());
                Assert.Empty(db.Select<UndoGGrand>());

                var linked = db.Insert(new LinkParent { Name = "p" });
                Assert.ThrowsAny<Exception>(() => db.Insert(new LinkChild { LinkParentId = 999, Parent = linked, Label = "x" }));
                Assert.Empty(db.Select<LinkChild>());
            }
            Assert.DoesNotContain("<UndoGChild", PageText<UndoGParent>(path));
            Assert.DoesNotContain("<LinkChild", PageText<LinkParent>(path));
        }

        // A row whose own page write failed was already in the cached page, and wasn't noted for the undo: the next
        // write of that table saved it, a child whose parent didn't exist.
        [Fact]
        public void A_row_whose_page_write_fails_is_taken_back()
        {
            if (!OperatingSystem.IsWindows())
                return; // a file held open blocks writing it on Windows only

            string path = NewDatabase("page-write-fails");
            string dir = Path.GetDirectoryName(path)!;
            using (var db = new XodContext(path))
            {
                db.Insert(new RefusedParent { Name = "first", Children = new() { new RefusedChild { Label = "c0" } } });
                string table = File.ReadAllText(Path.Combine(dir, typeof(RefusedChild).FullName + ".xtab"));
                string page = Regex.Match(table, "file=\"([^\"]+)\"").Groups[1].Value;
                using (new FileStream(Path.Combine(dir, page), FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.ThrowsAny<Exception>(() => db.Insert(new RefusedParent { Name = "bad", Children = new() { new RefusedChild { Label = "orphan" } } }));
                // Its undo couldn't write the page either: the context writes nothing more (ISS-091), and the row
                // stays out of the disk.
                Assert.Contains("Reopen", Assert.ThrowsAny<XodException>(() =>
                    db.Insert(new RefusedParent { Name = "refused", Children = new() { new RefusedChild { Label = "x" } } })).Message);
            }
            using (var db = new XodContext(path))
                db.Insert(new RefusedParent { Name = "later", Children = new() { new RefusedChild { Label = "c2" } } });
            using (var db = new XodContext(path))
            {
                var parents = db.Select<RefusedParent>().Select(p => p.Id).ToHashSet();
                var children = db.Select<RefusedChild>().ToList();
                Assert.Equal(new[] { "c0", "c2" }, children.Select(c => c.Label).OrderBy(l => l));
                Assert.All(children, c => Assert.Contains(c.RefusedParentId, parents));
            }
        }

        // A child with a parent property but no key property for it: its delete, and an undo of its insert, left it
        // in the parent's list (the unlink looked for the parent by a key property only). The row names its parent.
        [Fact]
        public void A_child_without_a_parent_key_property_leaves_its_parents_list()
        {
            string path = NewDatabase("nav-only");
            using (var db = new XodContext(path))
            {
                var parent = db.Insert(new NavParent { Name = "p" });
                var deleted = db.Insert(new NavChild { Label = "deleted", Parent = parent });
                Assert.True(db.Delete(deleted));
                db.AfterAction += (_, e) =>
                {
                    if (e.Item is NavChild k && k.Label == "refused")
                        throw new InvalidOperationException("trigger refused it");
                };
                Assert.Throws<InvalidOperationException>(() => db.Insert(new NavChild { Label = "refused", Parent = parent }));
                Assert.Empty(db.Select<NavChild>());
            }
            Assert.DoesNotContain("<NavChild", PageText<NavParent>(path));

            // The same delete through a scan of the pages (no index): the one unlink.
            string scanned = NewDatabase("nav-only-scan");
            var noIndex = new DatabaseOptions { EnableIndexing = false };
            using (var db = new XodContext(scanned, null, noIndex))
            {
                var parent = db.Insert(new NavParent { Name = "p" });
                Assert.True(db.Delete(db.Insert(new NavChild { Label = "deleted", Parent = parent })));
            }
            Assert.DoesNotContain("<NavChild", PageText<NavParent>(scanned));
        }

        // A child deleted and inserted again with the same key is in its parent's list.
        [Fact]
        public void A_child_inserted_again_with_its_key_is_listed()
        {
            string path = NewDatabase("same-key");
            using (var db = new XodContext(path))
            {
                var parent = db.Insert(new NavKeyParent { Name = "p" });
                Assert.True(db.Delete(db.Insert(new NavKeyChild { Code = "k", Label = "first", Parent = parent })));
                db.Insert(new NavKeyChild { Code = "k", Label = "second", Parent = parent });
            }
            using (var db = new XodContext(path))
                Assert.Equal(new[] { "second" }, (db.Select<NavKeyParent>().Single().Kids ?? new()).Select(k => k.Label));
        }

        // An entry a delete left behind (2.0 and earlier left them) with a child's key names that child once it is
        // inserted: the entry hid it from its parent.
        [Fact]
        public void A_stale_entry_with_a_child_s_key_names_the_child_inserted_with_it()
        {
            string path = NewDatabase("stale-entry");
            using (var db = new XodContext(path))
                db.Insert(new NavKeyParent { Name = "p" });
            string dir = Path.GetDirectoryName(path)!;
            string table = File.ReadAllText(Path.Combine(dir, typeof(NavKeyParent).FullName + ".xtab"));
            string pageFile = Path.Combine(dir, Regex.Match(table, "file=\"([^\"]+)\"").Groups[1].Value);
            string stale = "<Kids dataType=\"" + typeof(NavKeyChild).FullName + "\" collType=\"generic\" refType=\"children\">" +
                "<NavKeyChild Code=\"k\">0deadbeef.0deadbeef</NavKeyChild></Kids></NavKeyParent>";
            File.WriteAllText(pageFile, File.ReadAllText(pageFile).Replace("</NavKeyParent>", stale));
            foreach (var index in Directory.GetFiles(dir, "*.xidx"))
                File.Delete(index);

            using (var db = new XodContext(path))
                db.Insert(new NavKeyChild { Code = "k", Label = "new", Parent = db.Select<NavKeyParent>().Single() });
            using (var db = new XodContext(path))
                Assert.Equal(new[] { "new" }, (db.Select<NavKeyParent>().Single().Kids ?? new()).Select(k => k.Label));
            Assert.DoesNotContain("0deadbeef", PageText<NavKeyParent>(path));
        }

        // ISS-095 (named, not fixed): a child with no parent property names no parent in its row, so its own delete
        // leaves its entry in the parent's list. The parent reads the children that are there.
        [Fact]
        public void A_child_with_no_parent_property_deleted_on_its_own_is_not_read_as_its_parent_s()
        {
            string path = NewDatabase("no-parent-property");
            using (var db = new XodContext(path))
            {
                db.Insert(new RefusedParent { Name = "p", Children = new() { new RefusedChild { Label = "a" }, new RefusedChild { Label = "b" } } });
                Assert.True(db.Delete(db.Select<RefusedChild>().Single(c => c.Label == "b")));
            }
            using (var db = new XodContext(path))
                Assert.Equal(new[] { "a" }, db.Select<RefusedParent>().Single().Children.Select(c => c.Label));
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
