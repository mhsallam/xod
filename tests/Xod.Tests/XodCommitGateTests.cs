// ISS-164: writes from two contexts of one database weren't kept apart. ISS-147's check looked for another context's
// commit journal before a write but held nothing, so a commit could write its journal between the look and the write,
// and its undo then took the write back after it was acknowledged. And a commit wrote the before-images it took when it
// first changed a file, so a write another context made to that file after that was overwritten by the commit's
// success, or taken back by its undo.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class XodCommitGateTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-commit-gate-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private static string ReadUntil(Process writer, string prefix)
        {
            string line;
            while ((line = writer.StandardOutput.ReadLine()) != null && !line.StartsWith(prefix, StringComparison.Ordinal)) { }
            Assert.NotNull(line);
            return line;
        }

        // The window: another context paused past the check of its first file write (write-checked:1), before any of
        // its rename is on disk, while a commit over the same pages is cut short by a crash after writing its files. The
        // next open alone undoes the commit from its journal; the rename, acknowledged, must outlive that undo. The
        // first write, not a later one: a rename writes two pages, in an order that follows their names, which are
        // random, so a later step holds back the renamed row's page in some runs and not in others. Without the gate
        // the commit lands during the pause, and the rename is refused at its next check with half of it written,
        // which that undo takes back.
        [Theory]
        [InlineData("Direct")]
        [InlineData("Buffered")]
        public void A_write_past_its_checks_outlives_the_undo_of_a_commit_beside_it(string durability)
        {
            string dir = CrashDb.New(tempRoot, "window-" + durability);
            using var renamer = XodCrashSafetyTests.StartWriterProcess("rename-one", dir, ("XOD_PAUSE_AT_STEP", "write-checked:1"),
                ("XOD_TEST_DURABILITY", durability));
            ReadUntil(renamer, "PAUSED");

            // A commit over the same pages (the next batch of customers), cut short after writing its files if it gets
            // that far: it is refused while the paused write holds the gate.
            var commit = XodCrashSafetyTests.RunWriterProcess("txcommit", dir,
                ("XOD_CRASH_AT_STEP", "commit-written"), ("XOD_TEST_BATCH", "1"), ("XOD_TEST_TX_TIMEOUT", "500"));
            // Refused for the gate the paused write holds, not for any other reason (a crash before its journal).
            Assert.Contains("TransactionTimeoutMs", commit.Output);
            Assert.DoesNotContain("COMMITTED", commit.Output);

            renamer.StandardInput.WriteLine();
            renamer.StandardInput.Flush();
            string rest = XodCrashSafetyTests.FinishWriter(renamer);
            Assert.True(rest.Contains("UPDATED"), "renamer: " + rest + " | commit: " + commit.Output);

            using var db = new XodContext(Path.Combine(dir, "pw.xod"), "first");
            Assert.Equal("renamed", db.Find<CrashCustomer>(c => c.Id == 3).Name);
            Assert.Equal(25, db.Count<CrashCustomer>());
        }

        // A transaction changes a page; another context then writes the same page and is told it did; the commit must
        // not overwrite that write with the page as the transaction first read it. It is refused instead, and nothing
        // of it is written.
        [Fact]
        public void A_commit_over_a_file_another_context_wrote_since_is_refused()
        {
            string dir = CrashDb.New(tempRoot, "stale");
            string path = Path.Combine(dir, "pw.xod");
            using (var first = new XodContext(path, "first"))
            using (var second = new XodContext(path, "first"))
            {
                using (var tx = first.BeginTransaction())
                {
                    var mine = first.Find<CrashCustomer>(c => c.Id == 4);
                    mine.Name = "from the transaction";
                    Assert.True(first.Update(mine));

                    var theirs = second.Find<CrashCustomer>(c => c.Id == 5);
                    theirs.Name = "from the other context";
                    Assert.True(second.Update(theirs));

                    var refused = Assert.Throws<TransactionException>(() => tx.Commit());
                    Assert.Contains("another context", refused.Message, StringComparison.OrdinalIgnoreCase);
                }
            }

            using var db = new XodContext(path, "first");
            Assert.Equal("from the other context", db.Find<CrashCustomer>(c => c.Id == 5).Name);
            Assert.NotEqual("from the transaction", db.Find<CrashCustomer>(c => c.Id == 4).Name);
        }

        // The same for a file the transaction creates: another context created it first and was told its row is
        // there; the commit must not write its own file over it.
        [Fact]
        public void A_commit_creating_a_file_another_context_created_since_is_refused()
        {
            Directory.CreateDirectory(tempRoot);
            string path = Path.Combine(tempRoot, "created.xod");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var first = new XodContext(path, options: options))
            using (var second = new XodContext(path, options: options))
            {
                using (var tx = first.BeginTransaction())
                {
                    first.Insert(new GateNote { Id = 1, Text = "from the transaction" });
                    second.Insert(new GateNote { Id = 2, Text = "from the other context" });

                    var refused = Assert.Throws<TransactionException>(() => tx.Commit());
                    Assert.Contains("another context", refused.Message, StringComparison.OrdinalIgnoreCase);
                }
            }

            using var db = new XodContext(path, options: options);
            Assert.Equal(new[] { 2 }, db.Select<GateNote>().Select(n => n.Id).ToArray());
        }
    }

    public class XodCommitGateSideTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-commit-gate-side-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        // A database of notes 1..count ("one", "two", ...), closed.
        private string Notes(string name, DatabaseOptions options, int count = 3)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "notes.xod");
            using var db = new XodContext(path, options: options);
            for (int id = 1; id <= count; id++)
                db.Insert(new GateNote { Id = id, Text = "note " + id });
            return path;
        }

        // Another context's commit under way: the commit gate held exclusively, as a commit holds it.
        private static FileStream CommitUnderWay(string path) =>
            new FileStream(Path.Combine(Path.GetDirectoryName(path)!, "xod.commit.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        private static string TextOf(XodContext db, int id) => db.Find<GateNote>(n => n.Id == id)?.Text;

        // A transaction writes no data file before its commit, so its rollback has nothing on disk to take back. It
        // used to write its before-images back, and with them the pages as they were before another context's write.
        [Fact]
        public void A_rollback_leaves_another_context_s_write()
        {
            var options = new DatabaseOptions { InitialCreate = true };
            string path = Notes("rollback", options);
            using (var first = new XodContext(path, options: options))
            using (var second = new XodContext(path, options: options))
            {
                using var tx = first.BeginTransaction();
                var mine = first.Find<GateNote>(n => n.Id == 1);
                mine.Text = "from the transaction";
                Assert.True(first.Update(mine));
                var theirs = second.Find<GateNote>(n => n.Id == 2);
                theirs.Text = "from the other context";
                Assert.True(second.Update(theirs));
                tx.Rollback();
            }
            using var db = new XodContext(path, options: options);
            Assert.Equal("from the other context", TextOf(db, 2));
            Assert.Equal("note 1", TextOf(db, 1));
        }

        // A transaction's delete empties a page, which stays on disk until the commit (ISS-166); another context then
        // writes that page, and is told it did. The commit must not delete it under that write: it is refused, the
        // other write stays, and the context goes on.
        [Fact]
        public void A_commit_deleting_a_page_another_context_wrote_since_is_refused()
        {
            var options = new DatabaseOptions { InitialCreate = true, PageSize = 1 };
            string path = Notes("aside", options);
            using (var first = new XodContext(path, options: options))
            using (var second = new XodContext(path, options: options))
            {
                var theirs = second.Find<GateNote>(n => n.Id == 1);
                using (var tx = first.BeginTransaction())
                {
                    Assert.True(first.Delete(first.Find<GateNote>(n => n.Id == 1)));
                    Assert.Equal(3, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag").Length);
                    theirs.Text = "from the other context";
                    Assert.True(second.Update(theirs));

                    var refused = Assert.Throws<TransactionException>(() => tx.Commit());
                    Assert.Contains("another context", refused.Message, StringComparison.OrdinalIgnoreCase);
                }
                first.Insert(new GateNote { Id = 4, Text = "after" });
            }
            using var db = new XodContext(path, options: options);
            Assert.Equal("from the other context", TextOf(db, 1));
            Assert.Equal(4, db.Count<GateNote>());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xdel"));
        }

        // A rollback writes nothing, deletes included (ISS-166), so it doesn't wait for another context's commit: it
        // ends at once while one holds the commit gate, and the context goes on. It used to put back the pages its
        // transaction had set aside, holding the gate; refused there, the context refused writes until reopened.
        [Fact]
        public void A_rollback_doesn_t_wait_for_another_context_s_commit()
        {
            var options = new DatabaseOptions { InitialCreate = true, PageSize = 1, TransactionTimeoutMs = 300 };
            string path = Notes("putback", options);
            using (var db = new XodContext(path, options: options))
            {
                using (var tx = db.BeginTransaction())
                {
                    Assert.True(db.Delete(db.Find<GateNote>(n => n.Id == 1)));
                    using (CommitUnderWay(path))
                        tx.Rollback();
                }
                Assert.Equal("note 1", TextOf(db, 1));
                db.Insert(new GateNote { Id = 4, Text = "after" });
            }
            using (var db = new XodContext(path, options: options))
            {
                Assert.Equal("note 1", TextOf(db, 1));
                Assert.Equal(4, db.Count<GateNote>());
            }
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xdel"));
        }

        // A write waits for another context's commit, and is refused once that has held the gate for longer than
        // TransactionTimeoutMs; it goes through once the commit has ended.
        [Fact]
        public void A_write_beside_a_commit_that_doesn_t_end_is_refused_and_goes_through_after_it()
        {
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 300 };
            string path = Notes("wait", options);
            using (var db = new XodContext(path, options: options))
            {
                using (CommitUnderWay(path))
                {
                    var refused = Assert.ThrowsAny<XodException>(() => db.Insert(new GateNote { Id = 4, Text = "four" }));
                    Assert.Contains("commit is under way", refused.Message);
                }
                db.Insert(new GateNote { Id = 4, Text = "four" });
            }
            using var reopened = new XodContext(path, options: options);
            Assert.Equal(4, reopened.Count<GateNote>());
        }

        // A file the commit can't read to compare with what it first read: the commit is refused as a transaction
        // error, nothing of it written, and the context goes on. Windows only: elsewhere a file another program has
        // open can still be read.
        [Fact]
        public void A_commit_that_can_t_read_its_file_to_compare_is_refused()
        {
            if (!OperatingSystem.IsWindows())
                return;
            var options = new DatabaseOptions { InitialCreate = true };
            string path = Notes("unreadable", options);
            using (var db = new XodContext(path, options: options))
            {
                using (var tx = db.BeginTransaction())
                {
                    var note = db.Find<GateNote>(n => n.Id == 1);
                    note.Text = "from the transaction";
                    Assert.True(db.Update(note));
                    string page = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag"));
                    using (new FileStream(page, FileMode.Open, FileAccess.Read, FileShare.None))
                        Assert.Throws<TransactionException>(() => tx.Commit());
                }
                db.Insert(new GateNote { Id = 4, Text = "after" });
            }
            using var reopened = new XodContext(path, options: options);
            Assert.Equal("note 1", TextOf(reopened, 1));
            Assert.Equal(4, reopened.Count<GateNote>());
        }

        // A statement refused while another context's commit holds the gate is undone; its undo forgets a page that
        // never reached the disk, which needs no gate, and the context goes on once the commit has ended. The undo used
        // to wait for the gate too, be refused, and leave the context refusing every write until reopened.
        [Fact]
        public void A_statement_refused_at_the_gate_leaves_its_context_writing()
        {
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 300 };
            string path = Notes("refused", options);
            using (var db = new XodContext(path, options: options))
            {
                using (CommitUnderWay(path))
                    Assert.ThrowsAny<XodException>(() => db.Insert(new GateOther { Id = 1, Text = "first of its table" }));
                db.Insert(new GateOther { Id = 1, Text = "first of its table" });
                db.Insert(new GateNote { Id = 4, Text = "four" });
            }
            using var reopened = new XodContext(path, options: options);
            Assert.Equal(1, reopened.Count<GateOther>());
            Assert.Equal(4, reopened.Count<GateNote>());
        }

        // The same with a commit as it really is, holding its journal as well as the gate, begun after the statement's
        // check at its start (a trigger takes both, standing for that commit): the undo's delete of the page that never
        // reached the disk must not look for the journal either, which would refuse it and fault the context.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_statement_refused_beside_a_commit_begun_during_it_leaves_its_context_writing(bool before)
        {
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 300 };
            string path = Notes("refused-journal-" + before, options);
            string dir = Path.GetDirectoryName(path)!;
            using (var db = new XodContext(path, options: options))
            {
                FileStream gate = null, journal = null;
                EventHandler<TriggerEventArgs> commitBegins = (sender, e) =>
                {
                    if (e.Type != typeof(GateOther) || gate != null)
                        return;
                    gate = CommitUnderWay(path);
                    journal = new FileStream(Path.Combine(dir, "transaction.xwal"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                };
                if (before) db.BeforeAction += commitBegins; else db.AfterAction += commitBegins;
                try
                {
                    Assert.ThrowsAny<XodException>(() => db.Insert(new GateOther { Id = 1, Text = "first of its table" }));
                    Assert.NotNull(gate);
                }
                finally
                {
                    journal?.Dispose();
                    gate?.Dispose();
                    File.Delete(Path.Combine(dir, "transaction.xwal"));
                    if (before) db.BeforeAction -= commitBegins; else db.AfterAction -= commitBegins;
                }
                db.Insert(new GateNote { Id = 4, Text = "four" });
            }
            using var reopened = new XodContext(path, options: options);
            Assert.Equal(4, reopened.Count<GateNote>());
        }

        // ISS-166: a transaction's deletes take effect at its commit, so the files it deletes stay on disk, whole, for
        // every other context until then. A context that never read a table dropped inside a transaction writes into
        // that table, not a new one at its fixed name; rolled back, nothing is lost. It used to see the table gone
        // (its files renamed aside), make a new one, and the old rows were dropped by the rollback or the next open.
        [Fact]
        public void A_table_dropped_in_a_rolled_back_transaction_stays_whole_for_another_context()
        {
            var options = new DatabaseOptions { InitialCreate = true };
            string path = Notes("drop-rollback", options);
            using (var first = new XodContext(path, options: options))
            using (var second = new XodContext(path, options: options))
            {
                using (var tx = first.BeginTransaction())
                {
                    Assert.True(first.Drop<GateNote>());
                    second.Insert(new GateNote { Id = 99, Text = "from the other context" });
                    tx.Rollback();
                }
                first.Insert(new GateNote { Id = 4, Text = "after" });
            }
            using var db = new XodContext(path, options: options);
            Assert.Equal(new[] { 1, 2, 3, 4, 99 }, db.Select<GateNote>().Select(n => n.Id).OrderBy(i => i).ToArray());
        }

        // The same, committed: the other context wrote the table file the commit would delete, so the commit is
        // refused, and every row stays.
        [Fact]
        public void A_drop_committed_over_another_context_s_write_is_refused_and_every_row_stays()
        {
            var options = new DatabaseOptions { InitialCreate = true };
            string path = Notes("drop-commit", options);
            using (var first = new XodContext(path, options: options))
            using (var second = new XodContext(path, options: options))
            {
                using (var tx = first.BeginTransaction())
                {
                    Assert.True(first.Drop<GateNote>());
                    second.Insert(new GateNote { Id = 99, Text = "from the other context" });
                    var refused = Assert.Throws<TransactionException>(() => tx.Commit());
                    Assert.Contains("another context", refused.Message, StringComparison.OrdinalIgnoreCase);
                }
                first.Insert(new GateNote { Id = 4, Text = "after" });
            }
            using var db = new XodContext(path, options: options);
            Assert.Equal(new[] { 1, 2, 3, 4, 99 }, db.Select<GateNote>().Select(n => n.Id).OrderBy(i => i).ToArray());
        }

        // ISS-166 as filed: a transaction deletes a table's last rows (a table with no autonumber mark goes with its
        // last page), another context writes the table, and the transaction is rolled back. Nothing is lost.
        [Fact]
        public void A_table_emptied_in_a_rolled_back_transaction_stays_whole_for_another_context()
        {
            var options = new DatabaseOptions { InitialCreate = true };
            string path = Notes("empty-rollback", options);
            using (var first = new XodContext(path, options: options))
            using (var second = new XodContext(path, options: options))
            {
                using (var tx = first.BeginTransaction())
                {
                    foreach (var note in first.Select<GateNote>().ToList())
                        Assert.True(first.Delete(note));
                    Assert.Equal(0, first.Count<GateNote>());
                    second.Insert(new GateNote { Id = 99, Text = "from the other context" });
                    tx.Rollback();
                }
            }
            using var db = new XodContext(path, options: options);
            Assert.Equal(new[] { 1, 2, 3, 99 }, db.Select<GateNote>().Select(n => n.Id).OrderBy(i => i).ToArray());
        }

        // A transaction changes no data or index file before its commit: deletes included (a row's, a table's), the
        // folder's files are as they were until then. (Before, its deletes renamed the files aside at once: ISS-107's
        // set-aside.) The lock files and the marker of indexes behind (ISS-043) aren't compared.
        [Fact]
        public void A_transaction_changes_no_file_before_its_commit()
        {
            var options = new DatabaseOptions { InitialCreate = true, PageSize = 1 };
            string path = Notes("untouched", options);
            string dir = Path.GetDirectoryName(path)!;
            using var db = new XodContext(path, options: options);
            db.Count<GateNote>();
            db.Insert(new GateOther { Id = 1, Text = "a table to drop" });
            db.Flush();
            Dictionary<string, byte[]> Data() => Directory.GetFiles(dir)
                .Where(f => !f.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".xidxdirty", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(Path.GetFileName, File.ReadAllBytes);
            var before = Data();
            Assert.Contains(typeof(GateOther).FullName + ".xidx", before.Keys);
            using (var tx = db.BeginTransaction())
            {
                Assert.True(db.Drop<GateOther>());
                Assert.True(db.Delete(db.Find<GateNote>(n => n.Id == 1)));
                var note = db.Find<GateNote>(n => n.Id == 2);
                note.Text = "changed";
                Assert.True(db.Update(note));
                db.Insert(new GateNote { Id = 4, Text = "four" });
                var during = Data();
                Assert.Equal(before.Keys.OrderBy(k => k), during.Keys.OrderBy(k => k));
                foreach (var kv in before)
                    Assert.True(kv.Value.AsSpan().SequenceEqual(during[kv.Key]), kv.Key + " changed before the commit");
                tx.Commit();
            }
            Assert.Null(db.Find<GateNote>(n => n.Id == 1));
            Assert.Equal("changed", db.Find<GateNote>(n => n.Id == 2).Text);
            Assert.Equal(3, db.Count<GateNote>());
            Assert.Equal(0, db.Count<GateOther>());
            Assert.Empty(Directory.GetFiles(dir, typeof(GateOther).FullName + ".*"));
        }

        // The transaction's own view: a table it dropped is gone for it, its index too (a key the old rows held is
        // free), and it can make the table again; committed, only the new rows and their page are left. Dropped and
        // rolled back, every row is back.
        [Fact]
        public void A_transaction_sees_its_own_drop_and_can_make_the_table_again()
        {
            var options = new DatabaseOptions { InitialCreate = true, PageSize = 1 };
            string path = Notes("own-view", options);
            using (var db = new XodContext(path, options: options))
            {
                using (var tx = db.BeginTransaction())
                {
                    Assert.True(db.Drop<GateNote>());
                    Assert.Equal(0, db.Count<GateNote>());
                    Assert.Null(db.Find<GateNote>(n => n.Id == 1));
                    tx.Rollback();
                }
                Assert.Equal(3, db.Count<GateNote>());
                using (var tx = db.BeginTransaction())
                {
                    Assert.True(db.Drop<GateNote>());
                    db.Insert(new GateNote { Id = 1, Text = "anew" });
                    Assert.Equal(1, db.Count<GateNote>());
                    tx.Commit();
                }
            }
            string dir = Path.GetDirectoryName(path)!;
            Assert.Single(Directory.GetFiles(dir, "*.xpag"));
            using var reopened = new XodContext(path, options: options);
            Assert.Equal(new[] { "anew" }, reopened.Select<GateNote>().Select(n => n.Text).ToArray());
            Assert.Empty(Directory.GetFiles(dir, "*.xdel"));
        }

        // What a transaction lists of the database leaves out a table it dropped, though its file is on disk until the
        // commit: SQL's tables, RegisteredTypes and GetStatistics show what they show once the drop is committed. With
        // the schema from the table files, SQL knows no table of that name.
        [Theory]
        [InlineData(SqlSchemaSource.Classes)]
        [InlineData(SqlSchemaSource.Recorded)]
        public void A_transaction_s_lists_leave_out_a_table_it_dropped(SqlSchemaSource schema)
        {
            var options = new DatabaseOptions { InitialCreate = true, PageSize = 1, SqlSchema = schema };
            string path = Notes("lists-" + schema, options);
            static string View(XodContext db)
            {
                string sql;
                try
                {
                    sql = "rows " + db.Sql("SELECT Id FROM GateNote").Rows.Count();
                }
                catch (Exception e)
                {
                    sql = e.GetType().Name + ": " + e.Message;
                }
                var stats = db.GetStatistics();
                return string.Join(", ", sql, "registered " + db.RegisteredTypes().Contains(typeof(GateNote)),
                    "tables " + stats.TableCount, "pages " + stats.TotalPageCount);
            }
            string during, committed;
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new GateOther { Id = 1, Text = "another table" });
                using (var tx = db.BeginTransaction())
                {
                    Assert.True(db.Drop<GateNote>());
                    during = View(db);
                    tx.Rollback();
                }
                Assert.Contains("rows 3", View(db));
                Assert.True(db.Drop<GateNote>());
                committed = View(db);
            }
            Assert.Equal(committed, during);
        }

        // A commit that changed no file writes nothing, and doesn't wait for other contexts' writes or commits.
        [Fact]
        public void A_commit_that_changed_nothing_does_not_wait_for_the_gate()
        {
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 300 };
            string path = Notes("nothing", options);
            using var db = new XodContext(path, options: options);
            using (CommitUnderWay(path))
            {
                using var tx = db.BeginTransaction();
                Assert.Equal(3, db.Count<GateNote>());
                tx.Commit();
            }
        }
    }

    public class GateOther
    {
        [Xod.PrimaryKey]
        public int Id { get; set; }
        public string Text { get; set; }
    }

    public class GateNote
    {
        [Xod.PrimaryKey]
        public int Id { get; set; }
        public string Text { get; set; }
    }
}
