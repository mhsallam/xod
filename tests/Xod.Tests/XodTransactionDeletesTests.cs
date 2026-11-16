// ISS-166 (PLAN-063): a transaction changes no data file before its commit, deletes included. Its deletes are made by
// its commit, after its journal; a rollback forgets them and touches no file. Its own reads and lists treat a file it
// deleted as gone.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class XodTransactionDeletesTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-transaction-deletes-" + Guid.NewGuid().ToString("N")[..8]);

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
                db.Insert(new DeletesNote { Id = id, Text = "note " + id });
            return path;
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
            db.Count<DeletesNote>();
            db.Insert(new DeletesOther { Id = 1, Text = "a table to drop" });
            db.Flush();
            Dictionary<string, byte[]> Data() => Directory.GetFiles(dir)
                .Where(f => !f.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".xidxdirty", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(Path.GetFileName, File.ReadAllBytes);
            var before = Data();
            Assert.Contains(typeof(DeletesOther).FullName + ".xidx", before.Keys);
            using (var tx = db.BeginTransaction())
            {
                Assert.True(db.Drop<DeletesOther>());
                Assert.True(db.Delete(db.Find<DeletesNote>(n => n.Id == 1)));
                var note = db.Find<DeletesNote>(n => n.Id == 2);
                note.Text = "changed";
                Assert.True(db.Update(note));
                db.Insert(new DeletesNote { Id = 4, Text = "four" });
                var during = Data();
                Assert.Equal(before.Keys.OrderBy(k => k), during.Keys.OrderBy(k => k));
                foreach (var kv in before)
                    Assert.True(kv.Value.AsSpan().SequenceEqual(during[kv.Key]), kv.Key + " changed before the commit");
                tx.Commit();
            }
            Assert.Null(db.Find<DeletesNote>(n => n.Id == 1));
            Assert.Equal("changed", db.Find<DeletesNote>(n => n.Id == 2).Text);
            Assert.Equal(3, db.Count<DeletesNote>());
            Assert.Equal(0, db.Count<DeletesOther>());
            Assert.Empty(Directory.GetFiles(dir, typeof(DeletesOther).FullName + ".*"));
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
                    Assert.True(db.Drop<DeletesNote>());
                    Assert.Equal(0, db.Count<DeletesNote>());
                    Assert.Null(db.Find<DeletesNote>(n => n.Id == 1));
                    tx.Rollback();
                }
                Assert.Equal(3, db.Count<DeletesNote>());
                using (var tx = db.BeginTransaction())
                {
                    Assert.True(db.Drop<DeletesNote>());
                    db.Insert(new DeletesNote { Id = 1, Text = "anew" });
                    Assert.Equal(1, db.Count<DeletesNote>());
                    tx.Commit();
                }
            }
            string dir = Path.GetDirectoryName(path)!;
            Assert.Single(Directory.GetFiles(dir, "*.xpag"));
            using var reopened = new XodContext(path, options: options);
            Assert.Equal(new[] { "anew" }, reopened.Select<DeletesNote>().Select(n => n.Text).ToArray());
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
                    sql = "rows " + db.Sql("SELECT Id FROM DeletesNote").Rows.Count();
                }
                catch (Exception e)
                {
                    sql = e.GetType().Name + ": " + e.Message;
                }
                var stats = db.GetStatistics();
                return string.Join(", ", sql, "registered " + db.RegisteredTypes().Contains(typeof(DeletesNote)),
                    "tables " + stats.TableCount, "pages " + stats.TotalPageCount);
            }
            string during, committed;
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new DeletesOther { Id = 1, Text = "another table" });
                using (var tx = db.BeginTransaction())
                {
                    Assert.True(db.Drop<DeletesNote>());
                    during = View(db);
                    tx.Rollback();
                }
                Assert.Contains("rows 3", View(db));
                Assert.True(db.Drop<DeletesNote>());
                committed = View(db);
            }
            Assert.Equal(committed, during);
        }
    }

    public class DeletesOther
    {
        [Xod.PrimaryKey]
        public int Id { get; set; }
        public string Text { get; set; }
    }

    public class DeletesNote
    {
        [Xod.PrimaryKey]
        public int Id { get; set; }
        public string Text { get; set; }
    }
}
