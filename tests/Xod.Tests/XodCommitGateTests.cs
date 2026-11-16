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
