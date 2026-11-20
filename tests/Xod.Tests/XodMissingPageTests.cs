using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Xod;
using Xod.Services;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-196: a page file that a table names and that isn't there read as a page with no rows, and an index entry
    /// whose row isn't on its page read as no row: a query returned fewer rows than <c>Count</c> said, with no error.
    /// Reads skipped them because writes could leave them: a statement wrote its table before its new page, deleted a
    /// page its delete emptied before writing the table that stopped naming it, and a drop deleted its pages before its
    /// table, so a crash between two writes left a table naming a page that wasn't there. Now a page reaches the disk
    /// before any table names it and leaves after, and a table naming a page that isn't there, or an index naming a
    /// row that isn't on its page, is a <see cref="DatabaseFileException"/>.
    /// </summary>
    [Collection(InProcessHookCollection.Name)]
    public class XodMissingPageTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-mpage-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class MpRow
        {
            [PrimaryKey] public int Id { get; set; }
            public string Text { get; set; }
        }

        public class MpCounter
        {
            [PrimaryKey, Property(AutoNumber = true)] public int Id { get; set; }
            public string Text { get; set; }
        }

        public class MpParent
        {
            [PrimaryKey] public int Id { get; set; }
            [Children] public List<MpChild> Children { get; set; }
        }

        public class MpChild
        {
            [PrimaryKey] public int Id { get; set; }
            public int MpParentId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(MpParentId))] public MpParent Parent { get; set; }
        }

        public class MpReqParent
        {
            [PrimaryKey] public int Id { get; set; }
            [Children] public List<MpReqChild> Children { get; set; }
        }

        public class MpReqChild
        {
            [PrimaryKey] public int Id { get; set; }
            public int MpReqParentId { get; set; }
            [Required] public string Label { get; set; }
            [ParentKey(nameof(MpReqParentId))] public MpReqParent Parent { get; set; }
        }

        private static readonly AsyncLocal<string> Watched = new AsyncLocal<string>();

        // Every table file in the folder names only page files that are there. Read without blocking a writer's move.
        private static List<string> TablesNamingMissingPages(string dir)
        {
            var missing = new List<string>();
            foreach (string table in Directory.GetFiles(dir, "*.xtab"))
            {
                XDocument doc;
                using (var fs = new FileStream(table, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    doc = XDocument.Load(fs);
                foreach (var page in doc.Descendants("Page"))
                {
                    string file = page.Attribute("file")?.Value;
                    if (!string.IsNullOrEmpty(file) && !File.Exists(Path.Combine(dir, file)))
                        missing.Add(Path.GetFileName(table) + " -> " + file);
                }
            }
            return missing;
        }

        // Runs the writes with the disk checked before each file write and each delete (so after every earlier one),
        // then once at the end: no moment where a crash would leave a table naming a missing page.
        private static void EveryStepKeepsPagesNamed(string dir, Action writes)
        {
            var seen = new List<string>();
            int steps = 0;
            Action<string> hook = step =>
            {
                if (Watched.Value != dir || !(step.StartsWith("file-write:") || step.StartsWith("file-deleting:")))
                    return;
                Interlocked.Increment(ref steps);
                foreach (string m in TablesNamingMissingPages(dir))
                    lock (seen) seen.Add(step + ": " + m);
            };
            Watched.Value = dir;
            FaultInjection.InProcess += hook;
            try
            {
                writes();
            }
            finally
            {
                FaultInjection.InProcess -= hook;
                Watched.Value = null;
            }
            seen.AddRange(TablesNamingMissingPages(dir).Select(m => "end: " + m));
            Assert.True(steps > 0, "no write step was seen");
            Assert.Empty(seen);
        }

        private static IEnumerable<MpRow> Rows(int from, int count) =>
            Enumerable.Range(from, count).Select(i => new MpRow { Id = i, Text = new string('x', 200) + i });

        private string Folder(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static DatabaseOptions Options(bool buffered = false, bool indexing = true) => new DatabaseOptions
        {
            PageSize = 4096,
            EnableIndexing = indexing,
            Durability = buffered ? DurabilityMode.Buffered : DurabilityMode.Immediate,
        };

        // Direct mode, one statement each: a table's first row, rows filling new pages, a delete that empties a page
        // while others stay, the last row's delete, a drop.
        [Fact]
        public void Direct_writes_never_leave_a_table_naming_a_missing_page()
        {
            string dir = Folder("direct");
            using var db = new XodContext(Path.Combine(dir, "mp.xod"), null, Options());
            EveryStepKeepsPagesNamed(dir, () => db.Insert(new MpRow { Id = 1, Text = "first" }));
            EveryStepKeepsPagesNamed(dir, () => { foreach (var row in Rows(2, 60)) db.Insert(row); });
            Assert.True(Directory.GetFiles(dir, "*.xpag").Length >= 3);

            // The rows of the first page: the last of them empties it, and it goes.
            EveryStepKeepsPagesNamed(dir, () => { foreach (var row in db.Select<MpRow>().Where(r => r.Id <= 15).ToList()) db.Delete(row); });
            EveryStepKeepsPagesNamed(dir, () => db.DeleteWhere<MpRow>(r => r.Id > 0));
            EveryStepKeepsPagesNamed(dir, () => { foreach (var row in Rows(100, 40)) db.Insert(row); });
            EveryStepKeepsPagesNamed(dir, () => db.Drop<MpRow>());
            Assert.Empty(Directory.GetFiles(dir, "*.xpag"));
        }

        // A batch (InsertRange, DeleteWhere) and a cascade (a parent with its children).
        [Fact]
        public void Batches_and_cascades_never_leave_a_table_naming_a_missing_page()
        {
            string dir = Folder("batch");
            using var db = new XodContext(Path.Combine(dir, "mp.xod"), null, Options());
            EveryStepKeepsPagesNamed(dir, () => db.InsertRange(Rows(1, 80).ToList()));
            EveryStepKeepsPagesNamed(dir, () => db.DeleteWhere<MpRow>(r => r.Id <= 40));
            EveryStepKeepsPagesNamed(dir, () => db.Insert(new MpParent
            {
                Id = 1,
                Children = Enumerable.Range(1, 40).Select(i => new MpChild { Id = i, Text = new string('c', 200) }).ToList(),
            }));
            EveryStepKeepsPagesNamed(dir, () => db.Delete(db.Find<MpParent>(p => p.Id == 1)));
            EveryStepKeepsPagesNamed(dir, () => db.DropAll());
        }

        // Buffered: the writes wait for the flush, which writes them in the same order.
        [Fact]
        public void A_buffered_flush_never_leaves_a_table_naming_a_missing_page()
        {
            string dir = Folder("buffered");
            using var db = new XodContext(Path.Combine(dir, "mp.xod"), null, Options(buffered: true));
            EveryStepKeepsPagesNamed(dir, () => { foreach (var row in Rows(1, 60)) db.Insert(row); db.Flush(); });
            EveryStepKeepsPagesNamed(dir, () => { db.DeleteWhere<MpRow>(r => r.Id <= 30); db.Flush(); });
            EveryStepKeepsPagesNamed(dir, () => { db.Drop<MpRow>(); db.Flush(); });
            Assert.Empty(Directory.GetFiles(dir, "*.xpag"));
        }

        // A transaction's commit writes in the same order (its journal also undoes it).
        [Fact]
        public void A_commit_never_leaves_a_table_naming_a_missing_page()
        {
            string dir = Folder("commit");
            using var db = new XodContext(Path.Combine(dir, "mp.xod"), null, Options());
            EveryStepKeepsPagesNamed(dir, () =>
            {
                using var tx = db.BeginTransaction();
                foreach (var row in Rows(1, 60)) db.Insert(row);
                tx.Commit();
            });
            EveryStepKeepsPagesNamed(dir, () =>
            {
                using var tx = db.BeginTransaction();
                db.DeleteWhere<MpRow>(r => r.Id <= 30);
                tx.Commit();
            });
        }

        // The deletes of the files the writes reach, as each comes (the step before it).
        private static List<string> DeletesDuring(string dir, Action writes)
        {
            var deletes = new List<string>();
            Action<string> hook = step =>
            {
                if (Watched.Value == dir && step.StartsWith("file-deleting:"))
                    lock (deletes) deletes.Add(step.Substring("file-deleting:".Length));
            };
            Watched.Value = dir;
            FaultInjection.InProcess += hook;
            try
            {
                writes();
            }
            finally
            {
                FaultInjection.InProcess -= hook;
                Watched.Value = null;
            }
            return deletes;
        }

        // A table dropped and made again before its delete is written (a file of the same name made again: in one
        // transaction, or in the buffer) is written, never deleted after: its delete is of the file it replaces.
        [Fact]
        public void A_table_dropped_and_made_again_is_written_not_deleted()
        {
            string table = typeof(MpRow).FullName + ".xtab";
            foreach (bool buffered in new[] { false, true })
            {
                string dir = Folder(buffered ? "remade-buffered" : "remade-direct");
                string path = Path.Combine(dir, "mp.xod");
                using (var db = new XodContext(path, null, Options(buffered)))
                {
                    db.Insert(new MpRow { Id = 1, Text = "old" });
                    db.Flush();
                    var deletes = DeletesDuring(dir, () =>
                    {
                        using var tx = db.BeginTransaction();
                        db.Drop<MpRow>();
                        db.Insert(new MpRow { Id = 2, Text = "new" });
                        tx.Commit();
                    });
                    Assert.DoesNotContain(table, deletes);
                    Assert.Contains(deletes, d => d.EndsWith(".xpag")); // the dropped table's page

                    if (buffered)
                    {
                        deletes = DeletesDuring(dir, () =>
                        {
                            db.Drop<MpRow>();
                            db.Insert(new MpRow { Id = 3, Text = "newer" });
                            db.Flush();
                        });
                        Assert.DoesNotContain(table, deletes);
                    }
                }
                using (var db = new XodContext(path, null, Options(buffered)))
                    Assert.Equal(new[] { buffered ? 3 : 2 }, db.Select<MpRow>().Select(r => r.Id).ToArray());
            }
        }

        // A table dropped, then an insert into it that fails once it has made the table again in memory (a child
        // refused): the table made again was never written, so the drop deletes the old table with its pages. It was
        // kept as "made again" while its pages went, and the reopened table named deleted pages (review of ISS-196).
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_drop_followed_by_a_failed_insert_deletes_the_table_with_its_pages(bool inTransaction)
        {
            string dir = Folder("drop-failed-insert-" + inTransaction);
            string path = Path.Combine(dir, "mp.xod");
            using (var db = new XodContext(path, null, Options(buffered: !inTransaction)))
            {
                db.Insert(new MpReqParent { Id = 1, Children = new() { new MpReqChild { Id = 1, Label = "one" } } });
                db.Flush();
                EveryStepKeepsPagesNamed(dir, () =>
                {
                    Xod.Infra.IXodTransaction tx = inTransaction ? db.BeginTransaction() : null;
                    db.Drop<MpReqParent>();
                    Assert.ThrowsAny<XodException>(() =>
                        db.Insert(new MpReqParent { Id = 2, Children = new() { new MpReqChild { Id = 2, Label = null } } }));
                    if (tx != null)
                    {
                        tx.Commit();
                        tx.Dispose();
                    }
                    else
                    {
                        db.Flush();
                    }
                });
            }
            using (var db = new XodContext(path, null, Options()))
                Assert.Empty(db.Select<MpReqParent>().ToList());
        }

        // A table dropped in the buffer, then made again and written by a batch (InsertRange) before the buffer's flush:
        // the flush's delete is of the file the batch replaced, and the new table stays.
        [Fact]
        public void A_table_dropped_in_the_buffer_and_written_again_by_a_batch_stays()
        {
            string dir = Folder("buffered-drop-batch");
            string path = Path.Combine(dir, "mp.xod");
            using (var db = new XodContext(path, null, Options(buffered: true)))
            {
                db.Insert(new MpRow { Id = 1, Text = "old" });
                db.Flush();
                db.Drop<MpRow>();
                db.InsertRange(Rows(10, 3).ToList());
                db.ClearCache();
                db.Flush();
            }
            using (var db = new XodContext(path, null, Options()))
                Assert.Equal(new[] { 10, 11, 12 }, db.Select<MpRow>().Select(r => r.Id).OrderBy(i => i).ToArray());
        }

        // A statement that fails after emptying a page leaves the page, its rows and the table as they were.
        [Fact]
        public void A_failed_statement_that_emptied_a_page_leaves_it()
        {
            string dir = Folder("failed");
            string path = Path.Combine(dir, "mp.xod");
            using (var db = new XodContext(path, null, Options()))
            {
                db.InsertRange(Rows(1, 60).ToList());
                int pages = Directory.GetFiles(dir, "*.xpag").Length;
                db.AfterAction += (s, e) => { if (e.Action == DatabaseActions.Delete) throw new InvalidOperationException("refused"); };
                Assert.Throws<InvalidOperationException>(() => db.DeleteWhere<MpRow>(r => r.Id <= 30));
                Assert.Equal(pages, Directory.GetFiles(dir, "*.xpag").Length);
                Assert.Equal(60, db.Select<MpRow>().Count());
            }
            using (var db = new XodContext(path, null, Options()))
                Assert.Equal(60, db.Select<MpRow>().Count());
        }

        // ---- A table naming a page that isn't there: every read throws, naming it.

        private (string Path, string Page) DatabaseWithAMissingPage(string name, bool indexing)
        {
            string dir = Folder(name);
            string path = Path.Combine(dir, "mp.xod");
            using (var db = new XodContext(path, null, Options(indexing: indexing)))
                db.InsertRange(Rows(1, 60).ToList());
            var pages = Directory.GetFiles(dir, "*.xpag");
            Assert.True(pages.Length >= 3);
            // A page that holds rows (the table's first).
            var first = XDocument.Load(Directory.GetFiles(dir, "*MpRow.xtab").Single()).Descendants("Page").First().Attribute("file").Value;
            File.Delete(Path.Combine(dir, first));
            return (path, first);
        }

        private static void ThrowsNaming(string page, Action read)
        {
            var e = Assert.Throws<DatabaseFileException>(read);
            Assert.Contains(page, e.Message);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_missing_page_is_an_error_for_every_read(bool indexing)
        {
            var (path, page) = DatabaseWithAMissingPage("missing-" + indexing, indexing);
            using var db = new XodContext(path, null, Options(indexing: indexing));
            ThrowsNaming(page, () => db.Select<MpRow>().ToList());
            ThrowsNaming(page, () => db.Query<MpRow>(r => r.Text.EndsWith("7")).ToList());
            ThrowsNaming(page, () => db.Query(new MpRow { Text = new string('x', 200) + "9" }).ToList());
            ThrowsNaming(page, () => db.GetStatistics());
            // By key with an index: the row is found by the index, and its page is there.
            if (!indexing)
            {
                ThrowsNaming(page, () => db.Find<MpRow>(r => r.Id == 2));
                ThrowsNaming(page, () => db.Update(new MpRow { Id = 59, Text = "t" }, new MpRow { Id = 59, Text = "u" }));
            }
        }

        [Fact]
        public void A_missing_page_is_an_error_for_count_and_the_next_number()
        {
            string dir = Folder("count");
            string path = Path.Combine(dir, "mp.xod");
            using (var db = new XodContext(path, null, Options(indexing: false)))
                db.Insert(new MpCounter { Text = "one" });
            string page = Directory.GetFiles(dir, "*.xpag").Single();
            File.Delete(page);
            using var reopened = new XodContext(path, null, Options(indexing: false));
            ThrowsNaming(Path.GetFileName(page), () => reopened.Count<MpCounter>());
            ThrowsNaming(Path.GetFileName(page), () => reopened.Insert(new MpCounter { Text = "two" }));
        }

        // The index is built from the pages: one missing is an error, not an index without its rows.
        [Fact]
        public void A_missing_page_is_an_error_for_building_the_index()
        {
            var (path, page) = DatabaseWithAMissingPage("rebuild", indexing: true);
            foreach (string index in Directory.GetFiles(Path.GetDirectoryName(path), "*.xidx"))
                File.Delete(index);
            using var db = new XodContext(path, null, Options(indexing: true));
            ThrowsNaming(page, () => db.Find<MpRow>(r => r.Id == 2));
        }

        // ---- An index naming a row that isn't on its page: the read throws; it no longer drops the row.

        [Fact]
        public void An_index_entry_without_its_row_is_an_error()
        {
            string dir = Folder("stale");
            string path = Path.Combine(dir, "mp.xod");
            using (var db = new XodContext(path, null, Options()))
            {
                db.InsertRange(Rows(1, 10).ToList());
                Assert.NotNull(db.Find<MpRow>(r => r.Id == 5));
            }

            // Row 5 taken off its page behind the index's back.
            string pageFile = Directory.GetFiles(dir, "*.xpag").Single();
            var page = XDocument.Load(pageFile);
            page.Descendants("Row").Single(r => r.Descendants("Id").Any(i => i.Value == "5") || r.Descendants().Any(d => d.Attribute("Id")?.Value == "5")).Remove();
            page.Save(pageFile);

            using (var reopened = new XodContext(path, null, Options()))
            {
                Assert.Throws<DatabaseFileException>(() => reopened.Find<MpRow>(r => r.Id == 5));
                Assert.Throws<DatabaseFileException>(() => reopened.Query<MpRow>(r => r.Id >= 1).ToList());
                Assert.Throws<DatabaseFileException>(() => reopened.Query(new MpRow { Id = 5 }).ToList());
                Assert.Throws<DatabaseFileException>(() => reopened.Update(new MpRow { Id = 5, Text = "u" }));
                Assert.Throws<DatabaseFileException>(() => reopened.Delete(new MpRow { Id = 5 }));
            }

            // The way out the message gives: the index file deleted, it is built again from the pages.
            foreach (string index in Directory.GetFiles(dir, "*.xidx"))
                File.Delete(index);
            using var rebuilt = new XodContext(path, null, Options());
            Assert.Null(rebuilt.Find<MpRow>(r => r.Id == 5));
            Assert.Equal(9, rebuilt.Query<MpRow>(r => r.Id >= 1).Count());
        }
    }
}
