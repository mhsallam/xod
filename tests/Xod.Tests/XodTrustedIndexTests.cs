// The review of TASK-066 (PLAN-054 section 8): ways a context still called its indexes caught up, or a reader still
// trusted an index, while the index file didn't hold what the pages do. Each test ends where the defect showed: after
// a reopen, a row on the pages that its key doesn't find, a key with no row that is refused, or rows lost.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Xod.Tests
{
    public class TrustMember
    {
        [PrimaryKey] public int Id { get; set; }
        [UniqueKey] public string Email { get; set; }
    }

    public class TrustParent
    {
        [PrimaryKey] public int Id { get; set; }
        [UniqueKey] public string Code { get; set; }
        [Children] public List<TrustKid> Kids { get; set; }
    }

    public class TrustKid
    {
        [PrimaryKey] public int Id { get; set; }
        public int TrustParentId { get; set; }
        [ParentKey(nameof(TrustParentId))] public TrustParent Parent { get; set; }
        public string Name { get; set; }
    }

    public class TrustSecret
    {
        public int Id { get; set; }
        [Crypto(CryptoMethod.AES256)] public string Text { get; set; }
    }

    // A row whose Name getter blocks once, for the row "block": it is read inside the insert's locks.
    public class TrustGate
    {
        internal static readonly ManualResetEventSlim Entered = new(false);
        internal static readonly ManualResetEventSlim Go = new(false);
        internal static volatile bool Armed;
        private string name;
        [PrimaryKey] public string Code { get; set; }
        public string Name
        {
            get
            {
                if (Armed && Code == "block")
                {
                    Armed = false;
                    Entered.Set();
                    Go.Wait(10000);
                }
                return name;
            }
            set => name = value;
        }
    }

    public class XodTrustedIndexTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-trusted-index-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string NewDatabase(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "data.xod");
        }

        private sealed class Undo : IDisposable
        {
            internal Action Action;
            public void Dispose() => Action();
        }

        // A page another program holds, so a write to it fails: on Windows a reader that shares reading only (as
        // XOD's own loads do, File.ReadAllBytes); on Unix, where an open file can be written and replaced, the file
        // made read-only, the nearest failure a non-root process meets there.
        private static IDisposable HoldPage(string page)
        {
            if (OperatingSystem.IsWindows())
                return new FileStream(page, FileMode.Open, FileAccess.Read, FileShare.Read);
            return MakeReadOnly(page);
        }

        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        private static IDisposable MakeReadOnly(string page)
        {
            var mode = File.GetUnixFileMode(page);
            File.SetUnixFileMode(page, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            return new Undo { Action = () => File.SetUnixFileMode(page, mode) };
        }

        // The write to a page HoldPage holds failed: always on Windows. On Unix only a write made in place fails on
        // a read-only page; a whole page written beside it and renamed over it (JSON storage) replaces it, so there
        // the test checks only that the index and the pages agree.
        private static void AssertWriteFailed(Exception failure)
        {
            if (OperatingSystem.IsWindows())
                Assert.NotNull(failure);
        }

        private static string PageOf(string path) => Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag"));

        // The page a table lists first.
        private static string PageOf(string path, Type type)
        {
            string dir = Path.GetDirectoryName(path)!;
            string table = File.ReadAllText(Path.Combine(dir, type.FullName + ".xtab"));
            return Path.Combine(dir, Regex.Match(table, "([0-9A-Za-z]+\\.xpag)").Groups[1].Value);
        }

        // Opened again: every member on the pages (read by a scan) is found by its key and its key is refused again,
        // and no key is found or refused for a member the pages don't hold.
        private static void IndexAgreesWithPages(string path, DatabaseOptions options, params string[] emails)
        {
            using var db = new XodContext(path, options: options);
            var onPages = db.QueryDelegate<TrustMember>(m => m.Email != null).Select(m => m.Email).ToHashSet();
            int id = 9000;
            foreach (string email in emails)
            {
                string e = email;
                if (onPages.Contains(e))
                {
                    Assert.True(db.Find<TrustMember>(m => m.Email == e) != null, e + " is on the pages, not found by its key");
                    Assert.Throws<ReservedUniqueKeyException>(() => db.Insert(new TrustMember { Id = ++id, Email = e }));
                }
                else
                {
                    Assert.True(db.Find<TrustMember>(m => m.Email == e) == null, e + " isn't on the pages, found by its key");
                    db.Insert(new TrustMember { Id = ++id, Email = e });
                }
            }
        }

        // ISS-099: with no batch and no buffer (the default), a page write that failed left its change in the cache
        // and in no set of writes still to make; the next write carried the row to the page, and the close called
        // the indexes caught up without it.
        [Theory]
        [InlineData(StorageFormat.Xml, true)]
        [InlineData(StorageFormat.Xml, false)]
        [InlineData(StorageFormat.Json, true)]
        [InlineData(StorageFormat.Json, false)]
        public void A_direct_page_write_that_failed_isn_t_forgotten(StorageFormat format, bool writeAgain)
        {
            string path = NewDatabase("direct-" + format + "-" + writeAgain);
            var options = new DatabaseOptions { InitialCreate = true, StorageFormat = format };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new TrustMember { Id = 1, Email = "a" });
                db.Flush();
                Exception failure;
                using (HoldPage(PageOf(path)))
                    failure = Record.Exception(() => db.Insert(new TrustMember { Id = 2, Email = "b" }));
                AssertWriteFailed(failure);
                if (writeAgain)
                    db.Insert(new TrustMember { Id = 3, Email = "c" });
            }
            IndexAgreesWithPages(path, options, "a", "b", "c");
        }

        // ISS-099, an update: its page write fails too while another program holds the page; the row's new key and
        // its page must still agree after a close and a reopen.
        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        public void A_direct_update_whose_page_write_failed_isn_t_forgotten(StorageFormat format)
        {
            string path = NewDatabase("direct-update-" + format);
            var options = new DatabaseOptions { InitialCreate = true, StorageFormat = format };
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(new[] { "a", "b" }.Select((e, i) => new TrustMember { Id = i + 1, Email = e }).ToList());
                db.Flush();
                var member = db.Find<TrustMember>(m => m.Email == "b");
                member.Email = "c";
                Exception failure;
                using (HoldPage(PageOf(path)))
                    failure = Record.Exception(() => db.Update(member));
                AssertWriteFailed(failure);
            }
            IndexAgreesWithPages(path, options, "a", "b", "c");
        }

        // ISS-100: a flush inside a transaction wrote the index files with the transaction's keys; a commit refused
        // before its journal (another context's journal there) put nothing back, and the next flush called them
        // caught up: the key of a row that was never written was refused after reopening.
        [Fact]
        public void A_commit_refused_before_its_journal_leaves_no_keys_behind()
        {
            string path = NewDatabase("refused-keys");
            string journal = Path.Combine(Path.GetDirectoryName(path)!, "transaction.xwal");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new TrustMember { Id = 1, Email = "a" });
                db.Flush();
                var tx = db.BeginTransaction();
                db.Insert(new TrustMember { Id = 2, Email = "b" });
                db.Flush();
                File.WriteAllText(journal, "another commit's journal");
                Assert.Throws<TransactionException>(() => tx.Commit());
                File.Delete(journal);
                tx.Dispose();
                db.Flush();
            }
            IndexAgreesWithPages(path, options, "a", "b");
        }

        // A delete inside a transaction that empties a page deleted the page file at once, outside the transaction's
        // batch; a commit refused before its journal put nothing back, so the rows were lost though nothing was
        // committed.
        [Fact]
        public void A_commit_refused_before_its_journal_keeps_the_rows_it_deleted()
        {
            string path = NewDatabase("refused-delete");
            string journal = Path.Combine(Path.GetDirectoryName(path)!, "transaction.xwal");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(new[] { "a", "b", "c" }.Select((e, i) => new TrustMember { Id = i + 1, Email = e }).ToList());
                var tx = db.BeginTransaction();
                foreach (var m in db.Select<TrustMember>().ToList())
                    db.Delete(m);
                File.WriteAllText(journal, "another commit's journal");
                Assert.Throws<TransactionException>(() => tx.Commit());
                File.Delete(journal);
                tx.Dispose();
            }
            using (var db = new XodContext(path, options: options))
                Assert.Equal(new[] { "a", "b", "c" }, db.Select<TrustMember>().Select(m => m.Email).OrderBy(e => e).ToArray());
            IndexAgreesWithPages(path, options, "a", "b", "c");
        }

        // A committed delete takes its emptied pages away, and leaves nothing set aside; a rolled-back one puts them back.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_transaction_s_deletes_leave_nothing_aside(bool commit)
        {
            string path = NewDatabase("deletes-" + commit);
            string dir = Path.GetDirectoryName(path)!;
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(new[] { "a", "b", "c" }.Select((e, i) => new TrustMember { Id = i + 1, Email = e }).ToList());
                using var tx = db.BeginTransaction();
                foreach (var m in db.Select<TrustMember>().ToList())
                    db.Delete(m);
                Assert.Empty(db.Select<TrustMember>());
                if (commit)
                    tx.Commit();
                else
                    tx.Rollback();
                Assert.Equal(commit ? 0 : 3, db.Select<TrustMember>().Count());
            }
            Assert.Empty(Directory.GetFiles(dir, "*.xdel"));
            using (var db = new XodContext(path, options: options))
                Assert.Equal(commit ? 0 : 3, db.Select<TrustMember>().Count());
            IndexAgreesWithPages(path, options, "a", "b", "c");
        }

        // A transaction's delete that empties a page sets the page aside (.xdel). Held by another program, it can't be
        // deleted at the commit, which then fails and is undone (the journal puts the page back). The open after the
        // context's close finds a file set aside and no marker (the close caught the indexes up), and must still clear
        // it. Windows only: elsewhere a file held open can be deleted, and the commit passes.
        [Fact]
        public void A_set_aside_file_a_commit_couldn_t_delete_is_cleared_by_the_next_open()
        {
            if (!OperatingSystem.IsWindows())
                return;
            string path = NewDatabase("aside-held-commit");
            string dir = Path.GetDirectoryName(path)!;
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(new[] { "a", "b", "c" }.Select((e, i) => new TrustMember { Id = i + 1, Email = e }).ToList());
                using var tx = db.BeginTransaction();
                foreach (var m in db.Select<TrustMember>().ToList())
                    db.Delete(m);
                string aside = Assert.Single(Directory.GetFiles(dir, "*.xpag.xdel"));
                using (new FileStream(aside, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.ThrowsAny<Exception>(() => tx.Commit());
            }
            Assert.Empty(Directory.GetFiles(dir, "*.xidxdirty"));
            using (var db = new XodContext(path, options: options))
                Assert.Equal(3, db.Select<TrustMember>().Count());
            Assert.Empty(Directory.GetFiles(dir, "*.xdel"));
            IndexAgreesWithPages(path, options, "a", "b", "c");
        }

        // A rollback that can't put back a page its transaction set aside (another program holds it) leaves the index
        // files naming rows this context can't read: the context refuses writes, and the next open alone puts the page
        // back. Windows only, as above.
        [Fact]
        public void A_rollback_that_can_t_put_back_a_page_refuses_writes()
        {
            if (!OperatingSystem.IsWindows())
                return;
            string path = NewDatabase("aside-held-rollback");
            string dir = Path.GetDirectoryName(path)!;
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(new[] { "a", "b", "c" }.Select((e, i) => new TrustMember { Id = i + 1, Email = e }).ToList());
                var tx = db.BeginTransaction();
                foreach (var m in db.Select<TrustMember>().ToList())
                    db.Delete(m);
                string aside = Assert.Single(Directory.GetFiles(dir, "*.xpag.xdel"));
                using (new FileStream(aside, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.ThrowsAny<Exception>(() => tx.Rollback());
                tx.Dispose();
                Assert.Throws<TransactionException>(() => db.Insert(new TrustMember { Id = 4, Email = "d" }));
            }
            using (var db = new XodContext(path, options: options))
                Assert.Equal(3, db.Select<TrustMember>().Count());
            Assert.Empty(Directory.GetFiles(dir, "*.xdel"));
            IndexAgreesWithPages(path, options, "a", "b", "c", "d");
        }

        // ISS-101: a close from a trigger in the middle of a statement flushed the indexes (holding the parent's new
        // key) and deleted the marker, then failed, so the parent's page was never saved: after reopening, the index
        // said "new" and the page "old", and a second "old" was accepted.
        [Fact]
        public void A_close_from_a_trigger_doesn_t_split_the_index_from_the_pages()
        {
            string path = NewDatabase("trigger-close");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
                db.Insert(new TrustParent { Id = 1, Code = "old", Kids = new List<TrustKid> { new TrustKid { Id = 10, Name = "k" } } });

            var context = new XodContext(path, options: options);
            var parent = context.Find<TrustParent>(p => p.Id == 1);
            context.AfterAction += (_, e) =>
            {
                if (e.Type == typeof(TrustKid))
                    context.Dispose();
            };
            parent.Code = "new";
            parent.Kids[0].Name = "k2";
            // The close is refused before it does anything (the update fails with it), and the context stays open
            // and writes: it used to close under the statement.
            Exception failure = Record.Exception(() => context.Update(parent));
            Assert.IsType<InvalidOperationException>(failure);
            context.Insert(new TrustMember { Id = 1, Email = "after" });
            context.Dispose();

            using var again = new XodContext(path, options: options);
            string code = Assert.Single(again.QueryDelegate<TrustParent>(p => p.Code != null)).Code;
            Assert.NotNull(again.Find<TrustParent>(p => p.Code == code));
            string other = code == "old" ? "new" : "old";
            Assert.Null(again.Find<TrustParent>(p => p.Code == other));
            Assert.Throws<ReservedUniqueKeyException>(() => again.Insert(new TrustParent { Id = 2, Code = code, Kids = new List<TrustKid>() }));
        }

        // ISS-099: a statement that fails part way (here its trigger throws) can leave a change in a cached file that
        // no save made a write to make: the index then held the parent's new key while its page on disk kept the old
        // one. A change to a cached file is a write to make from the moment it is made (IOService.Watch). The update
        // itself stays half done (ISS-089): this checks only that the index and the pages agree.
        [Fact]
        public void A_statement_a_trigger_fails_leaves_the_index_agreeing_with_the_pages()
        {
            string path = NewDatabase("trigger-throws");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
                db.Insert(new TrustParent { Id = 1, Code = "old", Kids = new List<TrustKid> { new TrustKid { Id = 10, Name = "k" } } });

            using (var context = new XodContext(path, options: options))
            {
                var parent = context.Find<TrustParent>(p => p.Id == 1);
                context.AfterAction += (_, e) =>
                {
                    if (e.Type == typeof(TrustKid))
                        throw new InvalidOperationException("refused by the trigger");
                };
                parent.Code = "new";
                parent.Kids[0].Name = "k2";
                Assert.NotNull(Record.Exception(() => context.Update(parent)));
            }

            using var again = new XodContext(path, options: options);
            string code = Assert.Single(again.QueryDelegate<TrustParent>(p => p.Code != null)).Code;
            Assert.NotNull(again.Find<TrustParent>(p => p.Code == code));
            Assert.Null(again.Find<TrustParent>(p => p.Code == (code == "old" ? "new" : "old")));
            Assert.Throws<ReservedUniqueKeyException>(() => again.Insert(new TrustParent { Id = 2, Code = code, Kids = new List<TrustKid>() }));
        }

        // ISS-102: a marker left by a crash (one byte: the indexes behind) that another program holds read as a live
        // context's, so even an open alone kept the index files, and every load trusted one that missed a row.
        [Fact]
        public void A_crash_marker_another_program_holds_doesn_t_keep_a_stale_index_trusted()
        {
            string path = NewDatabase("held-marker");
            string dir = Path.GetDirectoryName(path)!;
            var options = new DatabaseOptions { InitialCreate = true };
            string index = Path.Combine(dir, typeof(TrustMember).FullName + ".xidx");
            using (var db = new XodContext(path, options: options))
                db.InsertRange(new[] { "a", "b" }.Select((e, i) => new TrustMember { Id = i + 1, Email = e }).ToList());
            byte[] before = File.ReadAllBytes(index);
            using (var db = new XodContext(path, options: options))
                db.Insert(new TrustMember { Id = 3, Email = "c" });

            // As a crash leaves it: the index without the last row (its time after the pages', so the guess by time
            // doesn't catch it), and a marker that says so.
            File.WriteAllBytes(index, before);
            File.SetLastWriteTimeUtc(index, DateTime.UtcNow.AddMinutes(1));
            string marker = path + ".deadbeef" + ".xidxdirty";
            File.WriteAllBytes(marker, new byte[] { 1 });
            using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None))
                IndexAgreesWithPages(path, options, "a", "b", "c");
        }

        // ISS-101: a flush from a trigger comes in the middle of a statement, with the parent's new key in the index
        // and its page not yet saved: it must leave the marker saying the indexes may be behind (a crash then leaves
        // them for the next open alone to drop). It used to call them caught up.
        [Fact]
        public void A_flush_from_a_trigger_leaves_the_indexes_marked_behind()
        {
            string path = NewDatabase("trigger-flush");
            string dir = Path.GetDirectoryName(path)!;
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
                db.Insert(new TrustParent { Id = 1, Code = "old", Kids = new List<TrustKid> { new TrustKid { Id = 10, Name = "k" } } });

            using var context = new XodContext(path, options: options);
            var parent = context.Find<TrustParent>(p => p.Id == 1);
            long markerInTrigger = -1;
            context.AfterAction += (_, e) =>
            {
                if (e.Type != typeof(TrustKid) || markerInTrigger >= 0)
                    return;
                context.Flush();
                // Its length from the folder: the context holds it sharing nothing.
                markerInTrigger = new FileInfo(Assert.Single(Directory.GetFiles(dir, "*.xidxdirty"))).Length;
            };
            parent.Code = "new";
            parent.Kids[0].Name = "k2";
            context.Update(parent);
            Assert.Equal(1, markerInTrigger);
        }

        // ISS-100: an index rebuilt for a query is a cache, saved now or not at all. Inside a transaction it was held
        // for the commit, and an index file another program holds then failed a commit that only read the table.
        // (On Unix a file held open can be replaced, so the commit passes there either way.) With a password the index
        // is saved through the file service, which a transaction's batch would hold for the commit (review of TASK-068).
        [Theory]
        [InlineData(null)]
        [InlineData("pw")]
        public void A_query_in_a_transaction_doesn_t_fail_its_commit_on_a_held_index_file(string password)
        {
            string path = NewDatabase("tx-query-" + (password ?? "plain"));
            string dir = Path.GetDirectoryName(path)!;
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, password, options))
                db.InsertRange(new[] { "a", "b" }.Select((e, i) => new TrustMember { Id = i + 1, Email = e }).ToList());
            string index = Path.Combine(dir, typeof(TrustMember).FullName + ".xidx");
            using var context = new XodContext(path, password, options);
            using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                using var tx = context.BeginTransaction();
                Assert.NotNull(context.Find<TrustMember>(m => m.Email == "b")); // the index can't be loaded: rebuilt
                context.Insert(new TrustParent { Id = 1, Code = "p", Kids = new List<TrustKid>() });
                tx.Commit();
            }
            Assert.NotNull(context.Find<TrustParent>(p => p.Code == "p"));
        }

        // Review of TASK-068: in a transaction, a page joined the batch before its before-image was taken; when that
        // capture failed (another program held the page), the page stayed in the batch with nothing to put back, and
        // no later change tried again, so a commit undone after writing it kept the transaction's row. Windows only:
        // elsewhere a file held open can be read.
        [Fact]
        public void A_commit_undone_keeps_no_row_of_a_page_whose_first_capture_failed()
        {
            if (!OperatingSystem.IsWindows())
                return;
            string path = NewDatabase("capture-failed");
            string dir = Path.GetDirectoryName(path)!;
            var options = new DatabaseOptions { InitialCreate = true, StorageFormat = StorageFormat.Json };
            Exception insertD, commit;
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(new[] { "a", "b" }.Select((e, i) => new TrustMember { Id = i + 1, Email = e }).ToList());
                db.Insert(new TrustParent { Id = 1, Code = "x", Kids = new List<TrustKid>() });
                db.Flush();
                var tx = db.BeginTransaction();
                using (new FileStream(PageOf(path, typeof(TrustMember)), FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.NotNull(Record.Exception(() => db.Insert(new TrustMember { Id = 3, Email = "c" })));
                insertD = Record.Exception(() => db.Insert(new TrustMember { Id = 4, Email = "d" }));
                Record.Exception(() => db.Delete(db.Find<TrustParent>(p => p.Id == 1)));
                string[] aside = Directory.GetFiles(dir, "*.xpag.xdel");
                if (aside.Length == 1)
                {
                    // The commit fails after its files are written (a file it set aside can't be deleted): undone.
                    using (new FileStream(aside[0], FileMode.Open, FileAccess.Read, FileShare.None))
                        commit = Record.Exception(() => tx.Commit());
                }
                else
                {
                    commit = Record.Exception(() => tx.Commit());
                }
                tx.Dispose();
            }
            using (var db = new XodContext(path, options: options))
            {
                var emails = db.QueryDelegate<TrustMember>(m => m.Email != null).Select(m => m.Email).OrderBy(e => e).ToArray();
                // d only if its insert and the commit both succeeded (the context may refuse writes after the failed
                // capture); never after a commit that failed.
                Assert.Equal(insertD == null && commit == null ? new[] { "a", "b", "d" } : new[] { "a", "b" }, emails);
            }
        }

        // Review of TASK-068: with a password, an index a read rebuilt (it couldn't load the file another program
        // holds) was queued to be written at the next statement's end, and every later write, to any table, and the
        // close then failed on the held file. It is written now or not at all, as a plain one is. Windows only.
        // ISS-109: with buffered writes it waited for the next flush, and failed it.
        [Theory]
        [InlineData(DurabilityMode.Immediate)]
        [InlineData(DurabilityMode.Buffered)]
        public void A_read_that_rebuilt_an_encrypted_index_doesn_t_fail_later_writes(DurabilityMode durability)
        {
            if (!OperatingSystem.IsWindows())
                return;
            string path = NewDatabase("encrypted-rebuilt-" + durability);
            var options = new DatabaseOptions { InitialCreate = true, Durability = durability };
            using (var db = new XodContext(path, "pw", options))
            {
                db.Insert(new TrustMember { Id = 1, Email = "a" });
                db.Insert(new TrustParent { Id = 1, Code = "x", Kids = new List<TrustKid>() });
            }
            string index = Path.Combine(Path.GetDirectoryName(path)!, typeof(TrustMember).FullName + ".xidx");
            Assert.True(File.Exists(index));
            using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var db = new XodContext(path, "pw", options);
                Assert.NotNull(db.Find<TrustMember>(m => m.Email == "a"));
                db.Insert(new TrustParent { Id = 2, Code = "y", Kids = new List<TrustKid>() });
                db.Flush();
                db.Dispose();
            }
            using (var again = new XodContext(path, "pw", options))
                Assert.Equal(2, again.Select<TrustParent>().Count());
        }

        // ISS-103, review of TASK-068: a close from inside a read takes no write lock, so a standalone table's write
        // on another thread, under way before the close, went on after it: it landed in a database another context
        // then held alone, and left a marker no context closed. It now fails, and nothing of it is written.
        [Fact]
        public void A_write_under_way_when_a_close_comes_from_inside_a_read_doesn_t_land_after_it()
        {
            string path = NewDatabase("close-in-read-race");
            string dir = Path.GetDirectoryName(path)!;
            var db = new XodContext(path, options: new DatabaseOptions { InitialCreate = true });
            db.Insert(new TrustGate { Code = "seed", Name = "s" });
            db.Insert(new TrustParent { Id = 1, Code = "x", Kids = new List<TrustKid>() });
            db.Flush();

            TrustGate.Entered.Reset();
            TrustGate.Go.Reset();
            TrustGate.Armed = true;
            Exception writerFailure = null;
            var writer = Task.Run(() => writerFailure = Record.Exception(() => db.Insert(new TrustGate { Code = "block", Name = "late" })));
            Assert.True(TrustGate.Entered.Wait(10000));
            db.QueryDelegate<TrustParent>(p => { db.Dispose(); return true; }).ToList();

            using var alone = new XodContext(path, options: new DatabaseOptions { LockDatabase = true });
            int markers = Directory.GetFiles(dir, "*.xidxdirty").Length;
            TrustGate.Go.Set();
            Assert.True(writer.Wait(10000));
            Assert.IsType<ObjectDisposedException>(writerFailure);
            Assert.Equal(markers, Directory.GetFiles(dir, "*.xidxdirty").Length);
            Assert.Equal(new[] { "seed" }, alone.QueryDelegate<TrustGate>(g => g.Code != null).Select(g => g.Code).ToArray());
        }

        // Review of TASK-068: a new database's file was queued to be written at the first statement's end, so it
        // wasn't on disk after the open, and a second context's close wrote its own copy over the AES256 key another
        // context had stored meanwhile: the value no longer read.
        [Fact]
        public void A_new_database_s_file_is_on_disk_after_the_open()
        {
            string path = NewDatabase("new-file");
            using var first = new XodContext(path, options: new DatabaseOptions { InitialCreate = true });
            Assert.True(File.Exists(path));
            new XodContext(path, options: new DatabaseOptions { InitialCreate = false }).Dispose();
        }

        [Fact]
        public void An_AES256_key_one_context_stored_survives_another_context_s_close()
        {
            string path = NewDatabase("aes-key");
            var options = new DatabaseOptions { InitialCreate = true, PropertyEncryptionKey = "app-secret" };
            var first = new XodContext(path, options: options);
            var second = new XodContext(path, options: options);
            first.Insert(new TrustSecret { Text = "s1" });
            first.Dispose();
            second.Dispose();
            using var again = new XodContext(path, options: options);
            Assert.Equal("s1", again.Select<TrustSecret>().Single().Text);
        }

        // ISS-103: a context's entry points refuse once it is closed, rather than write after its hold and marker
        // are let go.
        [Fact]
        public void A_closed_context_refuses_writes()
        {
            string path = NewDatabase("closed");
            var db = new XodContext(path, options: new DatabaseOptions { InitialCreate = true });
            db.Insert(new TrustMember { Id = 1, Email = "a" });
            db.Dispose();
            Assert.Throws<ObjectDisposedException>(() => db.Insert(new TrustMember { Id = 2, Email = "b" }));
            Assert.Throws<ObjectDisposedException>(() => db.Flush());
        }
    }
}
