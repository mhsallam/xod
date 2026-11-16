// ISS-043: an index (.xidx) is behind its pages from the moment a page is written until the context's indexes
// catch up (a flush, a close). After a crash in between, the open judged the index by time: stale only when a
// page was more than 50 ms newer than it (and only the first two and last two pages were checked). An index
// written just before the last batch's pages, which the crash kept from catching up, was trusted: rows on the
// pages were missing from it, so lookups missed them and unique keys weren't enforced.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Xod.Tests
{
    public class XodIndexCrashTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-index-crash-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string Seeded(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            var setup = XodCrashSafetyTests.RunWriterProcess("setup", dir);
            Assert.True(setup.ExitCode == 0, setup.Output);
            return dir;
        }

        private static void FindsEveryRow(XodContext db, int customers, int orders)
        {
            Assert.Equal(customers, db.Count<CrashCustomer>());
            Assert.Equal(orders, db.Count<CrashOrder>());
            for (int id = 1; id <= customers; id++)
                Assert.True(db.Find<CrashCustomer>(c => c.Id == id) != null, "customer " + id + " not found by its key");
            for (int id = 1; id <= orders; id++)
                Assert.True(db.Find<CrashOrder>(o => o.Id == id) != null, "order " + id + " not found by its key");
        }

        [Fact]
        public void An_index_a_crash_left_behind_its_pages_is_not_trusted_whatever_its_time()
        {
            string dir = Seeded("behind");
            // The batch's pages are written, then the process dies before its indexes are.
            var crash = XodCrashSafetyTests.RunWriterProcess("onebatch", dir, ("XOD_CRASH_AT_STEP", "batch-written"));
            Assert.DoesNotContain("COMMITTED", crash.Output);

            // The window, made certain: every index newer than every page, as when it was written just before the
            // batch's pages. Nothing in the files' times then says the index is behind.
            foreach (var index in Directory.GetFiles(dir, "*.xidx"))
                File.SetLastWriteTimeUtc(index, DateTime.UtcNow.AddMinutes(1));

            using var db = new XodContext(Path.Combine(dir, "crash.xod"));
            FindsEveryRow(db, 50, 150);
        }

        // A crash after the indexes caught up (here: in the close, after its flush) leaves an empty marker: the next
        // open deletes it and keeps the indexes.
        [Fact]
        public void An_empty_marker_a_crash_left_costs_the_indexes_nothing()
        {
            string dir = Seeded("caught-up");
            var crash = XodCrashSafetyTests.RunWriterProcess("onebatch", dir, ("XOD_CRASH_AT_STEP", "closing"));
            Assert.Contains("COMMITTED", crash.Output);
            string marker = Assert.Single(Directory.GetFiles(dir, "*.xidxdirty"));
            Assert.Equal(0, new FileInfo(marker).Length);
            var indexes = Directory.GetFiles(dir, "*.xidx").Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();

            using (var db = new XodContext(Path.Combine(dir, "crash.xod")))
                FindsEveryRow(db, 50, 150);
            Assert.Empty(Directory.GetFiles(dir, "*.xidxdirty"));
            Assert.All(indexes, i => Assert.Equal(i.Item2, File.GetLastWriteTimeUtc(i.f)));
        }

        // A context that can't take the lock (a lock file it can't read) can't drop the indexes, nor tell whether
        // the marker is a crash's or a live context's: it reads no index file, and rebuilds what it needs from the
        // pages in memory. It used to trust the stale index.
        [Fact]
        public void A_context_without_a_hold_does_not_trust_indexes_a_marker_says_may_be_behind()
        {
            if (!OperatingSystem.IsWindows())
                return; // the lock file is made unreadable with a Windows access rule

            string dir = Seeded("no-hold");
            var crash = XodCrashSafetyTests.RunWriterProcess("onebatch", dir, ("XOD_CRASH_AT_STEP", "batch-written"));
            Assert.DoesNotContain("COMMITTED", crash.Output);
            foreach (var index in Directory.GetFiles(dir, "*.xidx"))
                File.SetLastWriteTimeUtc(index, DateTime.UtcNow.AddMinutes(1));

            string lockFile = Path.Combine(dir, "crash.xod.lock");
            var info = new FileInfo(lockFile);
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
                System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(deny);
            info.SetAccessControl(acl);
            try
            {
                using var db = new XodContext(Path.Combine(dir, "crash.xod"));
                FindsEveryRow(db, 50, 150);
                Assert.ThrowsAny<XodException>(() => db.Insert(new CrashCustomer { Name = "refused", Orders = new() }));
            }
            finally
            {
                acl = info.GetAccessControl();
                acl.RemoveAccessRule(deny);
                info.SetAccessControl(acl);
            }
            Assert.Single(Directory.GetFiles(dir, "*.xidxdirty"));
            using (var db = new XodContext(Path.Combine(dir, "crash.xod")))
                FindsEveryRow(db, 50, 150);
            Assert.Empty(Directory.GetFiles(dir, "*.xidxdirty"));
        }

        // While another context has the database open, what it left may be its own, under way: an open doesn't
        // drop its indexes, as it doesn't recover its journal (PLAN-051). The next open alone does.
        [Fact]
        public void An_open_drops_no_index_while_another_context_has_the_database_open()
        {
            string dir = Seeded("shared");
            string path = Path.Combine(dir, "crash.xod");
            using (var first = new XodContext(path))
            {
                first.Insert(new CrashCustomer { Name = "first", Orders = new() });
                Assert.Single(Directory.GetFiles(dir, "*.xidxdirty"));
                var indexes = Directory.GetFiles(dir, "*.xidx").Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();

                using (var second = new XodContext(path))
                {
                    Assert.Equal(26, second.Count<CrashCustomer>());
                    Assert.All(indexes, i => Assert.Equal(i.Item2, File.GetLastWriteTimeUtc(i.f)));
                }
                Assert.Single(Directory.GetFiles(dir, "*.xidxdirty"));
                first.Insert(new CrashCustomer { Name = "first again", Orders = new() });
            }
            Assert.Empty(Directory.GetFiles(dir, "*.xidxdirty"));
            using var db = new XodContext(path);
            FindsEveryRow(db, 27, 75);
        }

        // A context that writes and closes cleanly leaves no marker, and the next open keeps its indexes. A crash
        // after a flush leaves an empty one, which an open deletes and nothing more.
        [Fact]
        public void A_clean_close_leaves_its_indexes_trusted()
        {
            string dir = Seeded("clean");
            Assert.Empty(Directory.GetFiles(dir, "*.xidxdirty"));
            using (var db = new XodContext(Path.Combine(dir, "crash.xod")))
            {
                db.Insert(new CrashCustomer { Name = "one", Orders = new() });
                Assert.All(Directory.GetFiles(dir, "*.xidxdirty"), m => Assert.Equal(1, new FileInfo(m).Length));
                db.Flush();
                // Caught up: the marker stays open, empty, until the close.
                Assert.All(Directory.GetFiles(dir, "*.xidxdirty"), m => Assert.Equal(0, new FileInfo(m).Length));
                db.Insert(new CrashCustomer { Name = "two", Orders = new() });
            }
            Assert.Empty(Directory.GetFiles(dir, "*.xidxdirty"));
            var indexes = Directory.GetFiles(dir, "*.xidx").Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();
            using (var db = new XodContext(Path.Combine(dir, "crash.xod")))
                FindsEveryRow(db, 27, 75);
            Assert.All(indexes, i => Assert.Equal(i.Item2, File.GetLastWriteTimeUtc(i.f)));
        }
        // Review of TASK-064 (F1): a writer that crashes while another context has the database open leaves a
        // marker no context holds, which an open can't act on (it isn't alone). Every context then reads no index
        // file while that marker is there: each index load asks whether a marked marker's context is still alive
        // (a live context holds its marker locked, so a probe of it is refused). It used to trust the stale index.
        [Fact]
        public void A_writer_s_crash_while_another_context_has_the_database_open_leaves_no_index_trusted()
        {
            string dir = Seeded("crash-while-open");
            string path = Path.Combine(dir, "crash.xod");
            using (var holder = new XodContext(path))
            {
                Assert.Equal(25, holder.Count<CrashCustomer>());
                var crash = XodCrashSafetyTests.RunWriterProcess("onebatch", dir, ("XOD_CRASH_AT_STEP", "batch-written"));
                Assert.DoesNotContain("COMMITTED", crash.Output);
                foreach (var index in Directory.GetFiles(dir, "*.xidx"))
                    File.SetLastWriteTimeUtc(index, DateTime.UtcNow.AddMinutes(1));

                using (var other = new XodContext(path))
                    FindsEveryRow(other, 50, 150);
                Assert.Single(Directory.GetFiles(dir, "*.xidxdirty"));
            }
            using (var alone = new XodContext(path))
                FindsEveryRow(alone, 50, 150);
            Assert.Empty(Directory.GetFiles(dir, "*.xidxdirty"));
        }

        // Review of TASK-064 (F3): a flush inside a transaction wrote its index changes while its pages waited in
        // the transaction's batch, and called the indexes caught up; a crash before the commit then left an index
        // holding the transaction's keys and not the rows' own, trusted. A batch still open keeps the mark.
        [Fact]
        public void A_flush_inside_a_transaction_keeps_the_indexes_marked_behind()
        {
            string dir = Path.Combine(tempRoot, "tx-flush");
            var setup = XodCrashSafetyTests.RunWriterProcess("members", dir);
            Assert.True(setup.ExitCode == 0, setup.Output);
            var crash = XodCrashSafetyTests.RunWriterProcess("txflush", dir);
            Assert.Contains("FLUSHED", crash.Output);
            Assert.NotEqual(0, crash.ExitCode);

            using var db = new XodContext(Path.Combine(dir, "crash.xod"));
            // Never committed: the row holds e5, and its index must say so.
            Assert.Equal("e5", db.Find<CrashMember>(m => m.Id == 5)?.Email);
            Assert.NotNull(db.Find<CrashMember>(m => m.Email == "e5"));
            Assert.Null(db.Find<CrashMember>(m => m.Email == "e5x"));
            Assert.Null(db.Find<CrashMember>(m => m.Id == 400));
            Assert.Throws<ReservedUniqueKeyException>(() => db.Insert(new CrashMember { Id = 1005, Email = "e5" }));
            db.Insert(new CrashMember { Id = 400, Email = "e400" });
        }

        // ISS-107, ISS-166: a transaction's delete that empties a page deletes it at the commit; at a crash before the
        // commit nothing is set aside and the pages are on disk (the writer counts them as it dies), and after the next
        // open the rows are all there: nothing was committed.
        // (Until ISS-166 the page was set aside at the delete, and the next open put it back.)
        [Fact]
        public void A_crash_before_a_commit_keeps_the_rows_its_deletes_took()
        {
            string dir = Path.Combine(tempRoot, "tx-delete");
            var setup = XodCrashSafetyTests.RunWriterProcess("members", dir);
            Assert.True(setup.ExitCode == 0, setup.Output);
            var crash = XodCrashSafetyTests.RunWriterProcess("txdelete", dir);
            Assert.Contains("DELETED", crash.Output);
            Assert.Matches(@"ASIDE 0 PAGES [1-9]", crash.Output);
            Assert.NotEqual(0, crash.ExitCode);

            using (var db = new XodContext(Path.Combine(dir, "crash.xod")))
            {
                Assert.Equal(50, db.Count<CrashMember>());
                Assert.NotNull(db.Find<CrashMember>(m => m.Email == "e50"));
            }
            Assert.Empty(Directory.GetFiles(dir, "*.xdel"));
        }

        // ISS-099's change writes a statement's pages when it ends. A row that adds a page saved the table's index
        // at once, before its page: a crash in between left an index holding a key whose row was never written, and
        // no marker (no page written yet), so the next open trusted it and refused the key.
        [Fact]
        public void A_crash_before_a_statement_s_pages_are_written_leaves_no_key_in_the_index()
        {
            string dir = Path.Combine(tempRoot, "first-row");
            var crash = XodCrashSafetyTests.RunWriterProcess("firstrow", dir, ("XOD_CRASH_AT_STEP", "statement-saving"));
            Assert.DoesNotContain("INSERTED", crash.Output);
            Assert.NotEqual(0, crash.ExitCode);

            using var db = new XodContext(Path.Combine(dir, "crash.xod"));
            Assert.Empty(db.QueryDelegate<CrashMember>(m => m.Email != null));
            Assert.Null(db.Find<CrashMember>(m => m.Email == "e1"));
            db.Insert(new CrashMember { Id = 1, Email = "e1" });
        }

        // Review of TASK-064 (F2): an index save that failed (its file held open elsewhere: another reader, a backup
        // tool) left its type out of those still to save, so the next flush called the indexes caught up and the
        // close deleted the mark. A type now stays to be saved until its save succeeds. The InsertRange whose rows
        // are written no longer fails for it (ISS-115): the save is the next flush's.
        [Fact]
        public void An_index_save_that_failed_is_made_by_the_next_flush()
        {
            string dir = Path.Combine(tempRoot, "index-save-failed");
            var setup = XodCrashSafetyTests.RunWriterProcess("members", dir);
            Assert.True(setup.ExitCode == 0, setup.Output);
            string path = Path.Combine(dir, "crash.xod");
            string index = Path.Combine(dir, typeof(CrashMember).FullName + ".xidx");
            using (var db = new XodContext(path))
            {
                Assert.NotNull(db.Find<CrashMember>(m => m.Email == "e1"));
                Exception failure;
                using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    failure = Record.Exception(() => db.InsertRange(Enumerable.Range(51, 10)
                        .Select(i => new CrashMember { Id = i, Email = "e" + i }).ToList()));
                }
                Assert.Null(failure);
                db.Flush();
            }
            using (var db = new XodContext(path))
            {
                for (int i = 51; i <= 60; i++)
                {
                    int id = i;
                    string email = "e" + i;
                    Assert.True(db.Find<CrashMember>(m => m.Id == id) != null, "member " + id + " not found by its key");
                    Assert.True(db.Find<CrashMember>(m => m.Email == email) != null, "member " + id + " not found by its email");
                }
                Assert.Throws<ReservedUniqueKeyException>(() => db.Insert(new CrashMember { Id = 1055, Email = "e55" }));
            }
        }

        // Review of TASK-064 (F4): an open alone that couldn't delete an index or a marker threw. An index it
        // can't drop now leaves the markers in place, and every context reads no index file while a marker no
        // context holds says the indexes may be behind; the next open alone drops them.
        [Fact]
        public void An_index_the_open_can_t_drop_is_not_trusted()
        {
            string dir = Seeded("index-held");
            string path = Path.Combine(dir, "crash.xod");
            var crash = XodCrashSafetyTests.RunWriterProcess("onebatch", dir, ("XOD_CRASH_AT_STEP", "batch-written"));
            Assert.DoesNotContain("COMMITTED", crash.Output);
            foreach (var index in Directory.GetFiles(dir, "*.xidx"))
                File.SetLastWriteTimeUtc(index, DateTime.UtcNow.AddMinutes(1));
            string marker = Assert.Single(Directory.GetFiles(dir, "*.xidxdirty"));

            var hold = new FileStream(Path.Combine(dir, typeof(CrashCustomer).FullName + ".xidx"), FileMode.Open, FileAccess.Read, FileShare.Read);
            XodContext db;
            try
            {
                db = new XodContext(path); // alone: it drops the indexes, and can't delete the one held
            }
            finally
            {
                hold.Dispose();
            }
            using (db)
                FindsEveryRow(db, 50, 150);
            if (OperatingSystem.IsWindows())
                Assert.True(File.Exists(marker), "the marker goes only with the indexes it marks");

            using (var again = new XodContext(path))
                FindsEveryRow(again, 50, 150);
            Assert.Empty(Directory.GetFiles(dir, "*.xidxdirty"));
        }

        // Review of TASK-064 (F4): a marker the open can't delete (read-only, as in a copy taken off read-only
        // media while a context had the database open) threw. An empty one costs nothing; a marked one still drops
        // the indexes, and stays.
        [Fact]
        public void A_marker_the_open_can_t_delete_doesn_t_stop_it()
        {
            string dir = Seeded("marker-read-only");
            string path = Path.Combine(dir, "crash.xod");
            string marker = path + ".deadbeef" + ".xidxdirty";
            File.WriteAllBytes(marker, Array.Empty<byte>());
            File.SetAttributes(marker, FileAttributes.ReadOnly);
            try
            {
                var indexes = Directory.GetFiles(dir, "*.xidx").Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();
                using (var db = new XodContext(path))
                    FindsEveryRow(db, 25, 75);
                Assert.All(indexes, i => Assert.Equal(i.Item2, File.GetLastWriteTimeUtc(i.f)));

                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(marker, FileAttributes.Normal);
                    File.WriteAllBytes(marker, new byte[] { 1 });
                    File.SetAttributes(marker, FileAttributes.ReadOnly);
                    using (var db = new XodContext(path))
                        FindsEveryRow(db, 25, 75);
                    Assert.True(File.Exists(marker));
                }
            }
            finally
            {
                if (File.Exists(marker))
                    File.SetAttributes(marker, FileAttributes.Normal);
            }
        }

        // A live context's marker can't be opened by another (it shares nothing while held), which is how an index
        // load tells it from one a crash left (IOService.ProbeMarker).
        [Fact]
        public void A_live_context_s_marker_reads_as_live()
        {
            string dir = Seeded("live-marker");
            var probe = typeof(XodContext).Assembly.GetType("Xod.Services.IOService")!
                .GetMethod("ProbeMarker", BindingFlags.NonPublic | BindingFlags.Static)!;
            using var db = new XodContext(Path.Combine(dir, "crash.xod"));
            db.Insert(new CrashCustomer { Name = "live", Orders = new() });
            string marker = Assert.Single(Directory.GetFiles(dir, "*.xidxdirty"));
            Assert.Equal("Live", probe.Invoke(null, new object[] { marker })!.ToString());
        }

        // A close from inside a read on its own thread can't take the write lock its flush needs to call the indexes
        // caught up: it flushes without it, lets go of the database, and leaves its marker marked (the indexes are
        // rebuilt by the next open alone), since it can't tell no write of another thread was under way.
        [Fact]
        public void A_close_from_inside_a_read_lets_go_and_leaves_the_indexes_marked()
        {
            string dir = Seeded("close-in-read");
            string path = Path.Combine(dir, "crash.xod");
            var db = new XodContext(path);
            db.Insert(new CrashCustomer { Name = "one", Orders = new() });
            _ = Record.Exception(() => db.QueryDelegate<CrashCustomer>(c =>
            {
                db.Dispose();
                return true;
            }).ToList());
            string marker = Assert.Single(Directory.GetFiles(dir, "*.xidxdirty"));
            Assert.Equal(1, new FileInfo(marker).Length);

            using (var alone = new XodContext(path, options: new DatabaseOptions { LockDatabase = true }))
                FindsEveryRow(alone, 26, 75);
            Assert.Empty(Directory.GetFiles(dir, "*.xidxdirty"));
        }
    }
}
