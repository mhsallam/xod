// ISS-125 (and ISS-089, ISS-090): a write statement outside a transaction is all or nothing. One whose file write fails
// leaves the database, and what the context shows, as they were before it: before, an InsertRange reported failed was
// written by the next flush (and its retry refused, or stored twice), an Update reported failed kept its change, and a
// Delete reported failed lost its row. A write fails here through FaultInjection.InProcess at the step file-write, for
// the file named, so it fails the same way on every file system (a page another program holds fails a write on
// Windows only).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xod.Services;
using Xunit;

namespace Xod.Tests
{
    // The tests that set FaultInjection.InProcess, one process-wide slot, run one at a time.
    [CollectionDefinition(Name)]
    public class InProcessHookCollection
    {
        public const string Name = "FaultInjection.InProcess";
    }

    [Collection(InProcessHookCollection.Name)]
    public class XodStatementAtomicTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-statement-atomic-" + Guid.NewGuid().ToString("N")[..8]);
        private static readonly AsyncLocal<bool> failing = new AsyncLocal<bool>();

        public void Dispose()
        {
            FaultInjection.InProcess = null;
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string NewPath(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "atomic.xod");
        }

        // Runs the statement with the first write of a file whose name ends with suffix failed, as a held file fails it.
        private static Exception FailingOn(string suffix, Action statement)
        {
            int failed = 0;
            FaultInjection.InProcess = step =>
            {
                if (failing.Value && step.StartsWith("file-write:", StringComparison.Ordinal) &&
                    step.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref failed, 1) == 0)
                    throw new IOException("Injected failure: " + step);
            };
            failing.Value = true;
            try
            {
                return Record.Exception(statement);
            }
            finally
            {
                failing.Value = false;
                FaultInjection.InProcess = null;
            }
        }

        private static string[] Notes(XodContext db) =>
            db.Select<AtomicNote>().OrderBy(n => n.Id).Select(n => n.Id + "=" + n.Text).ToArray();

        private static string[] Parents(XodContext db) =>
            db.Select<AtomicParent>().OrderBy(p => p.Id)
                .Select(p => p.Id + ":" + string.Join(",", (p.Children ?? new List<AtomicChild>()).OrderBy(c => c.Id).Select(c => c.Id)))
                .ToArray();

        private static AtomicNote Note(int id) => new AtomicNote { Id = id, Text = "note " + id };

        // The issue's InsertRange: reported failed, it leaves no row, in the context, after a flush, after a reopen; and
        // its retry is accepted.
        [Theory]
        [InlineData(".xpag")]
        [InlineData(".xtab")]
        public void An_insert_range_whose_write_fails_leaves_nothing(string failedFile)
        {
            string path = NewPath("range" + failedFile);
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(Note(1));
                var failure = FailingOn(failedFile, () => db.InsertRange(new List<AtomicNote> { Note(2), Note(3), Note(4) }));
                Assert.IsType<IOException>(failure);
                Assert.Equal(new[] { "1=note 1" }, Notes(db));
                db.Flush();
            }
            using (var db = new XodContext(path, options: options))
            {
                Assert.Equal(new[] { "1=note 1" }, Notes(db));
                db.InsertRange(new List<AtomicNote> { Note(2), Note(3), Note(4) });
                Assert.Equal(new[] { "1=note 1", "2=note 2", "3=note 3", "4=note 4" }, Notes(db));
            }
        }

        // An Update reported failed keeps nothing of its change; in an encrypted database too, whose files are put back
        // as their bytes on disk.
        [Theory]
        [InlineData(null)]
        [InlineData("pw")]
        public void An_update_whose_write_fails_changes_nothing(string password)
        {
            string path = NewPath("update-" + (password ?? "plain"));
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, password, options))
            {
                db.Insert(Note(1));
                var failure = FailingOn(".xpag", () => db.Update(new AtomicNote { Id = 1, Text = "changed" }));
                Assert.IsType<IOException>(failure);
                Assert.Equal(new[] { "1=note 1" }, Notes(db));
                db.Flush();
            }
            using (var reopened = new XodContext(path, password, options))
                Assert.Equal(new[] { "1=note 1" }, Notes(reopened));
        }

        // A statement that wrote its page (its trigger flushed) and then failed puts the page back as the statement before
        // it left it: the bytes it notes are the file's as last written, not as first read, and as they are on disk
        // (encrypted, under a password). A page that is a document (JSON) is noted from those bytes; a raw page (XML) from
        // its cached bytes. Without a statement before, the page is put back as it was read.
        [Theory]
        [InlineData(StorageFormat.Xml, null, true)]
        [InlineData(StorageFormat.Xml, "pw", true)]
        [InlineData(StorageFormat.Json, null, true)]
        [InlineData(StorageFormat.Json, "pw", true)]
        [InlineData(StorageFormat.Xml, "pw", false)]
        [InlineData(StorageFormat.Json, "pw", false)]
        public void A_page_written_then_put_back_keeps_the_statement_before(StorageFormat format, string password, bool writeBefore)
        {
            string path = NewPath("put-back-" + format + "-" + (password ?? "plain") + "-" + writeBefore);
            string first = writeBefore ? "1=one" : "1=note 1";
            var options = new DatabaseOptions { InitialCreate = true, StorageFormat = format };
            using (var db = new XodContext(path, password, options))
                db.InsertRange(new List<AtomicNote> { Note(1), Note(2) });
            using (var db = new XodContext(path, password, options))
            {
                if (writeBefore)
                    db.Update(new AtomicNote { Id = 1, Text = "one" });
                db.AfterAction += (_, e) =>
                {
                    if (e.Item is AtomicNote n && n.Id == 2)
                    {
                        db.Flush();
                        throw new InvalidOperationException("refused by the trigger");
                    }
                };
                Assert.Throws<InvalidOperationException>(() => db.Update(new AtomicNote { Id = 2, Text = "two" }));
                Assert.Equal(new[] { first, "2=note 2" }, Notes(db));
            }
            using (var reopened = new XodContext(path, password, options))
                Assert.Equal(new[] { first, "2=note 2" }, Notes(reopened));
        }

        // A Delete reported failed leaves its row.
        [Fact]
        public void A_delete_whose_write_fails_deletes_nothing()
        {
            string path = NewPath("delete");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(new List<AtomicNote> { Note(1), Note(2) });
                var failure = FailingOn(".xpag", () => db.Delete(new AtomicNote { Id = 1 }));
                Assert.IsType<IOException>(failure);
                Assert.Equal(new[] { "1=note 1", "2=note 2" }, Notes(db));
                db.Flush();
            }
            using (var reopened = new XodContext(path, options: options))
                Assert.Equal(new[] { "1=note 1", "2=note 2" }, Notes(reopened));
        }

        // A relational InsertRange: parents and children, none kept.
        [Theory]
        [InlineData(".xpag", null)]
        [InlineData(".xtab", null)]
        [InlineData(".xpag", "pw")]
        [InlineData(".xtab", "pw")]
        public void A_relational_insert_range_whose_write_fails_leaves_nothing(string failedFile, string password)
        {
            string path = NewPath("relational-range" + failedFile + (password ?? "plain"));
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, password, options))
            {
                db.Insert(Parent(1, 10));
                var failure = FailingOn(failedFile, () => db.InsertRange(new List<AtomicParent> { Parent(2, 20, 21), Parent(3, 30) }));
                Assert.IsType<IOException>(failure);
                Assert.Equal(new[] { "1:10" }, Parents(db));
                db.Flush();
            }
            using (var reopened = new XodContext(path, password, options))
            {
                Assert.Equal(new[] { "1:10" }, Parents(reopened));
                Assert.Equal(new[] { 10 }, reopened.Select<AtomicChild>().Select(c => c.Id).ToArray());
            }
        }

        // ISS-089: an update of a parent that drops a child, reported failed, keeps the child and the parent as they were.
        [Fact]
        public void A_relational_update_whose_write_fails_changes_nothing()
        {
            string path = NewPath("relational-update");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(Parent(1, 10, 11));
                var changed = Parent(1, 10);
                changed.Name = "changed";
                var failure = FailingOn(".xpag", () => db.Update(changed));
                Assert.IsType<IOException>(failure);
                Assert.Equal(new[] { "1:10,11" }, Parents(db));
                Assert.Equal("parent 1", db.Select<AtomicParent>().Single().Name);
                db.Flush();
            }
            using (var reopened = new XodContext(path, options: options))
            {
                Assert.Equal(new[] { "1:10,11" }, Parents(reopened));
                Assert.Equal(new[] { 10, 11 }, reopened.Select<AtomicChild>().OrderBy(c => c.Id).Select(c => c.Id).ToArray());
            }
        }

        // ISS-131: a delete refused part way had dropped its row's index keys and left them dropped: a second row with
        // the same unique key was accepted, and both kept. Here its trigger refuses it once the row is deleted, which
        // left the row deleted (a delete had no undo). The row and its keys are as they were.
        [Fact]
        public void A_delete_refused_part_way_leaves_its_row_and_its_unique_key()
        {
            string path = NewPath("delete-refused");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new AtomicKeyed { Id = 1, Code = "k", Kids = new List<AtomicKeyedKid> { new AtomicKeyedKid { Id = 10 } } });
                db.AfterAction += (_, e) =>
                {
                    if (e.Item is AtomicKeyed && e.Action == DatabaseActions.Delete)
                        throw new InvalidOperationException("refused by the trigger");
                };
                Assert.Throws<InvalidOperationException>(() => db.Delete(new AtomicKeyed { Id = 1 }));
                Assert.Single(db.Query<AtomicKeyed>(k => k.Code == "k"));
                Assert.Equal(new[] { 10 }, db.Select<AtomicKeyedKid>().Select(k => k.Id).ToArray());
                Assert.ThrowsAny<XodException>(() => db.Insert(new AtomicKeyed { Id = 2, Code = "k", Kids = new List<AtomicKeyedKid>() }));
                db.Flush();
            }
            using (var reopened = new XodContext(path, options: options))
            {
                Assert.Equal(new[] { 1 }, reopened.Select<AtomicKeyed>().Select(k => k.Id).ToArray());
                Assert.Equal(new[] { 10 }, reopened.Select<AtomicKeyedKid>().Select(k => k.Id).ToArray());
                Assert.ThrowsAny<XodException>(() => reopened.Insert(new AtomicKeyed { Id = 2, Code = "k", Kids = new List<AtomicKeyedKid>() }));
            }
        }

        // A delete of a table's last row deletes its page and table files; refused after (its trigger), it gets them
        // back, and the row with them.
        [Fact]
        public void A_delete_of_the_last_row_refused_after_keeps_the_table()
        {
            string path = NewPath("delete-page");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(Note(1));
                db.Flush();
                db.AfterAction += (_, e) =>
                {
                    if (e.Item is AtomicNote && e.Action == DatabaseActions.Delete)
                        throw new InvalidOperationException("refused by the trigger");
                };
                Assert.Throws<InvalidOperationException>(() => db.Delete(new AtomicNote { Id = 1 }));
                Assert.Equal(new[] { "1=note 1" }, Notes(db));
                db.Flush();
            }
            using (var reopened = new XodContext(path, options: options))
                Assert.Equal(new[] { "1=note 1" }, Notes(reopened));
        }

        // An index built from the pages in the middle of a statement (its trigger's query) holds the statement's
        // changes: a statement that fails drops it with the indexes it changed, or a lookup misses the row it put back.
        [Fact]
        public void An_index_built_inside_a_failed_statement_is_dropped_with_it()
        {
            string path = NewPath("built-inside");
            string dir = Path.GetDirectoryName(path)!;
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new AtomicTag { Name = "a", N = 1 });
                db.Insert(new AtomicTag { Name = "b", N = 2 });
            }
            foreach (string index in Directory.GetFiles(dir, "*AtomicTag.xidx"))
                File.Delete(index);
            using (var db = new XodContext(path, options: options))
            {
                int seen = -1;
                db.AfterAction += (_, e) =>
                {
                    if (e.Item is AtomicTag && e.Action == DatabaseActions.Delete)
                    {
                        seen = db.Query<AtomicTag>(t => t.Name == "a").Count();
                        throw new InvalidOperationException("refused by the trigger");
                    }
                };
                // A keyless type: deleted by a scan of its pages, which builds no index.
                Assert.Throws<InvalidOperationException>(() => db.Delete(new AtomicTag { Name = "a", N = 1 }));
                Assert.Equal(0, seen);
                Assert.Single(db.Query<AtomicTag>(t => t.Name == "a"));
            }
        }

        // An insert into a type whose index file is gone changes no index; its trigger's query then builds the index from
        // the pages, the new row included. The statement fails: its row isn't found, and its key is free. (The index built
        // is dropped through its save as well as its build; the tests above catch a build or save not noted.)
        [Fact]
        public void An_insert_failed_after_its_trigger_built_the_index_leaves_its_key_free()
        {
            string path = NewPath("built-after-insert");
            string dir = Path.GetDirectoryName(path)!;
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new AtomicTag { Name = "a", N = 1 });
                db.Insert(new AtomicTag { Name = "b", N = 2 });
            }
            foreach (string index in Directory.GetFiles(dir, "*AtomicTag.xidx"))
                File.Delete(index);
            using (var db = new XodContext(path, options: options))
            {
                int seen = -1;
                bool refuse = true;
                db.AfterAction += (_, e) =>
                {
                    if (refuse && e.Item is AtomicTag && e.Action == DatabaseActions.Insert)
                    {
                        seen = db.Query<AtomicTag>(t => t.Name == "c").Count();
                        throw new InvalidOperationException("refused by the trigger");
                    }
                };
                Assert.Throws<InvalidOperationException>(() => db.Insert(new AtomicTag { Name = "c", N = 3 }));
                Assert.Equal(1, seen);
                Assert.Empty(db.Query<AtomicTag>(t => t.Name == "c"));
                refuse = false;
                db.Insert(new AtomicTag { Name = "c", N = 4 });
                Assert.Equal(4, db.Query<AtomicTag>(t => t.Name == "c").Single().N);
            }
        }

        // An insert refused before it changed anything (its key is used) built the index from the pages, as on disk: the
        // index stays, and is saved. Dropped with the refused statement, it was built again from every page by the next
        // statement, and dropped again by each refusal.
        [Fact]
        public void An_index_built_by_an_insert_refused_before_any_change_is_kept()
        {
            string path = NewPath("refused-keeps");
            string dir = Path.GetDirectoryName(path)!;
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new AtomicTag { Name = "a", N = 1 });
                db.Insert(new AtomicTag { Name = "b", N = 2 });
            }
            foreach (string index in Directory.GetFiles(dir, "*AtomicTag.xidx"))
                File.Delete(index);
            using (var db = new XodContext(path, options: options))
            {
                Assert.ThrowsAny<Exception>(() => db.Insert(new AtomicTag { Name = "a", N = 3 }));
                Assert.NotEmpty(Directory.GetFiles(dir, "*AtomicTag.xidx"));
                Assert.Equal(1, db.Query<AtomicTag>(t => t.Name == "a").Single().N);
            }
        }

        // A password switch rewrites every file itself: inside a write that then failed, the write's undo put its files
        // back as they were before the switch, and no password opened the database. It is refused there.
        [Fact]
        public void A_password_switch_inside_a_write_is_refused()
        {
            string path = NewPath("switch-inside");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(Note(1));
                db.AfterAction += (_, e) =>
                {
                    if (e.Item is AtomicNote n && n.Id == 2)
                        db.Secure("pw");
                };
                Assert.Throws<InvalidOperationException>(() => db.Insert(Note(2)));
                Assert.Equal(new[] { 1 }, db.Select<AtomicNote>().Select(n => n.Id).ToArray());
            }
            using (var db = new XodContext(path, options: new DatabaseOptions()))
                Assert.Equal(new[] { 1 }, db.Select<AtomicNote>().Select(n => n.Id).ToArray());
        }

        private static string[] Counted(XodContext db) =>
            db.Select<AtomicCounted>().OrderBy(c => c.Id).Select(c => c.Id + "=" + c.Name).ToArray();

        // ISS-178: a drop its AfterAction refuses, once the table's files are deleted, keeps the table: its files back,
        // its rows read, by key too, after a reopen. It was a write of its own, with no savepoint, and kept its deletes.
        [Fact]
        public void A_drop_refused_after_its_deletes_keeps_the_table()
        {
            string path = NewPath("drop-refused");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(new List<AtomicCounted> { new AtomicCounted { Name = "a" }, new AtomicCounted { Name = "b" } });
                db.AfterAction += (_, e) =>
                {
                    if (e.Action == DatabaseActions.Drop)
                        throw new InvalidOperationException("refused by the trigger");
                };

                Assert.Throws<InvalidOperationException>(() => db.Drop<AtomicCounted>());

                Assert.Equal(new[] { "1=a", "2=b" }, Counted(db));
                Assert.Equal("b", db.Find<AtomicCounted>(c => c.Id == 2).Name);
            }
            using (var reopened = new XodContext(path, options: options))
            {
                Assert.Equal(new[] { "1=a", "2=b" }, Counted(reopened));
                Assert.Equal("b", reopened.Find<AtomicCounted>(c => c.Id == 2).Name);
            }
        }

        // A drop clears its table's counters; refused, it gives them back: a number a failed statement took before it
        // isn't handed out again (ISS-083), though the table's mark doesn't hold it yet.
        [Fact]
        public void A_drop_refused_keeps_the_numbers_already_taken()
        {
            string path = NewPath("drop-numbers");
            using var db = new XodContext(path, options: new DatabaseOptions { InitialCreate = true });
            db.Insert(new AtomicCounted { Name = "a" });
            db.AfterAction += (_, e) =>
            {
                if (e.Action == DatabaseActions.Drop || e.Item is AtomicCounted { Name: "refused" })
                    throw new InvalidOperationException("refused by the trigger");
            };
            Assert.Throws<InvalidOperationException>(() => db.Insert(new AtomicCounted { Name = "refused" }));

            Assert.Throws<InvalidOperationException>(() => db.Drop<AtomicCounted>());

            Assert.Equal(3, db.Insert(new AtomicCounted { Name = "c" }).Id);
            Assert.Equal(new[] { "1=a", "3=c" }, Counted(db));
        }

        // A number a failed insert takes, held only in memory until the next flush records it (ISS-083): 1 is "a", 2 the
        // refused insert's.
        private static void TakeTwo(XodContext db)
        {
            db.Insert(new AtomicCounted { Name = "a" });
            EventHandler<TriggerEventArgs> refuse = (_, e) =>
            {
                if (e.Item is AtomicCounted { Name: "refused" })
                    throw new InvalidOperationException("refused by the trigger");
            };
            db.AfterAction += refuse;
            Assert.Throws<InvalidOperationException>(() => db.Insert(new AtomicCounted { Name = "refused" }));
            db.AfterAction -= refuse;
        }

        // A drop that fails at its statement's end (writing what its trigger saved), after its body returned: the table
        // comes back, and so do its counters.
        [Fact]
        public void A_drop_failed_at_its_end_keeps_the_table_and_its_numbers()
        {
            string path = NewPath("drop-end");
            using (var db = new XodContext(path, options: new DatabaseOptions { InitialCreate = true }))
            {
                TakeTwo(db);
                db.AfterAction += (_, e) =>
                {
                    if (e.Action == DatabaseActions.Drop)
                        db.Insert(Note(9));
                };
                int failed = 0;
                FaultInjection.InProcess = step =>
                {
                    if (failing.Value && (step.StartsWith("statement-saving", StringComparison.Ordinal) ||
                        step.StartsWith("file-write:", StringComparison.Ordinal)) && Interlocked.Exchange(ref failed, 1) == 0)
                        throw new IOException("Injected failure: " + step);
                };
                failing.Value = true;
                Exception failure;
                try
                {
                    failure = Record.Exception(() => db.Drop<AtomicCounted>());
                }
                finally
                {
                    failing.Value = false;
                    FaultInjection.InProcess = null;
                }

                Assert.IsType<IOException>(failure);
                Assert.Equal(new[] { "1=a" }, Counted(db));
                Assert.Equal(3, db.Insert(new AtomicCounted { Name = "c" }).Id);
            }
            using (var reopened = new XodContext(path, options: new DatabaseOptions()))
                Assert.Equal(new[] { "1=a", "3=c" }, Counted(reopened));
        }

        // A drop inside another statement's trigger, which then fails: the outer statement's undo puts the table back,
        // and its counters.
        [Fact]
        public void A_drop_inside_a_failed_statement_keeps_the_table_and_its_numbers()
        {
            string path = NewPath("drop-nested");
            using var db = new XodContext(path, options: new DatabaseOptions { InitialCreate = true });
            TakeTwo(db);
            db.AfterAction += (_, e) =>
            {
                if (e.Item is AtomicNote { Id: 7 })
                {
                    Assert.True(db.Drop<AtomicCounted>());
                    throw new InvalidOperationException("refused by the trigger");
                }
            };

            Assert.Throws<InvalidOperationException>(() => db.Insert(Note(7)));

            Assert.Equal(new[] { "1=a" }, Counted(db));
            Assert.Equal(3, db.Insert(new AtomicCounted { Name = "c" }).Id);
        }

        // A drop that succeeds restarts its table's numbering, and a later statement that fails doesn't bring the old
        // numbers back: what a statement cleared is given back only by its own undo.
        [Fact]
        public void A_drop_that_succeeds_restarts_the_numbering_past_a_later_failure()
        {
            string path = NewPath("drop-ok");
            using var db = new XodContext(path, options: new DatabaseOptions { InitialCreate = true });
            TakeTwo(db);
            Assert.True(db.Drop<AtomicCounted>());
            Assert.Equal(1, db.Insert(new AtomicCounted { Name = "new" }).Id);
            db.AfterAction += (_, e) =>
            {
                if (e.Item is AtomicNote { Id: 8 })
                    throw new InvalidOperationException("refused by the trigger");
            };
            Assert.Throws<InvalidOperationException>(() => db.Insert(Note(8)));

            Assert.Equal(2, db.Insert(new AtomicCounted { Name = "next" }).Id);
        }

        // ISS-176: DeleteWhere is one write: a delete its trigger refuses part way leaves every match, the ones before it
        // included, in the context and after a reopen. Each match used to be a statement of its own.
        [Fact]
        public void A_delete_where_refused_part_way_leaves_every_match()
        {
            string path = NewPath("delete-where");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(new List<AtomicCounted>
                {
                    new AtomicCounted { Name = "x1" }, new AtomicCounted { Name = "keep" }, new AtomicCounted { Name = "x2" }
                });
                int deletes = 0;
                db.AfterAction += (_, e) =>
                {
                    if (e.Action == DatabaseActions.Delete && ++deletes == 2)
                        throw new InvalidOperationException("refused by the trigger");
                };

                Assert.Throws<InvalidOperationException>(() => db.DeleteWhere<AtomicCounted>(c => c.Name.StartsWith("x")));

                Assert.Equal(new[] { "1=x1", "2=keep", "3=x2" }, Counted(db));
            }
            using (var reopened = new XodContext(path, options: options))
            {
                Assert.Equal(new[] { "1=x1", "2=keep", "3=x2" }, Counted(reopened));
                Assert.Equal(2, reopened.DeleteWhere<AtomicCounted>(c => c.Name.StartsWith("x")));
                Assert.Equal(new[] { "2=keep" }, Counted(reopened));
            }
        }

        // ISS-176: DropAll is one write: a drop its trigger refuses part way leaves every table, the ones dropped before
        // it included.
        [Fact]
        public void A_drop_all_refused_part_way_leaves_every_table()
        {
            string path = NewPath("drop-all");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new AtomicCounted { Name = "a" });
                db.Insert(Note(1));
                int drops = 0;
                db.AfterAction += (_, e) =>
                {
                    if (e.Action == DatabaseActions.Drop && ++drops == 2)
                        throw new InvalidOperationException("refused by the trigger");
                };

                Assert.Throws<InvalidOperationException>(() => db.DropAll());

                Assert.Equal(new[] { "1=a" }, Counted(db));
                Assert.Equal(new[] { "1=note 1" }, Notes(db));
            }
            using (var reopened = new XodContext(path, options: options))
            {
                Assert.Equal(new[] { "1=a" }, Counted(reopened));
                Assert.Equal(new[] { "1=note 1" }, Notes(reopened));
            }
        }

        // ISS-133: an insert that couldn't read its target page (another program held it) had noted its row for the undo
        // first; the undo failed on the same page, and the context refused every write until it was reopened. The
        // insert fails, and the next one is made.
        [Fact]
        public void An_insert_that_can_t_read_its_page_leaves_the_context_writing()
        {
            if (!OperatingSystem.IsWindows())
                return; // a file held open blocks reading it on Windows only
            string path = NewPath("page-held");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
                db.Insert(Note(1));
            using (var db = new XodContext(path, options: options))
            {
                string page = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag").Single();
                Exception failure;
                using (new FileStream(page, FileMode.Open, FileAccess.Read, FileShare.None))
                    failure = Record.Exception(() => db.Insert(Note(2)));
                Assert.NotNull(failure);
                db.Insert(Note(3));
                Assert.Equal(new[] { "1=note 1", "3=note 3" }, Notes(db));
            }
            using (var reopened = new XodContext(path, options: options))
                Assert.Equal(new[] { "1=note 1", "3=note 3" }, Notes(reopened));
        }

        private static AtomicParent Parent(int id, params int[] children) => new AtomicParent
        {
            Id = id,
            Name = "parent " + id,
            Children = children.Select(c => new AtomicChild { Id = c, Name = "child " + c }).ToList(),
        };
    }

    public class AtomicCounted
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class AtomicNote
    {
        [PrimaryKey]
        public int Id { get; set; }
        public string Text { get; set; }
    }

    public class AtomicParent
    {
        [PrimaryKey]
        public int Id { get; set; }
        public string Name { get; set; }
        [Children]
        public List<AtomicChild> Children { get; set; }
    }

    public class AtomicTag
    {
        [UniqueKey]
        public string Name { get; set; }
        public int N { get; set; }
    }

    public class AtomicKeyed
    {
        [PrimaryKey]
        public int Id { get; set; }
        [UniqueKey]
        public string Code { get; set; }
        [Children]
        public List<AtomicKeyedKid> Kids { get; set; }
    }

    public class AtomicKeyedKid
    {
        [PrimaryKey]
        public int Id { get; set; }
        public int AtomicKeyedId { get; set; }
        [ParentKey(nameof(AtomicKeyedId))]
        public AtomicKeyed Parent { get; set; }
    }

    public class AtomicChild
    {
        [PrimaryKey]
        public int Id { get; set; }
        public int AtomicParentId { get; set; }
        public string Name { get; set; }
        [ParentKey(nameof(AtomicParentId))]
        public AtomicParent Parent { get; set; }
    }
}
