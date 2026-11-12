// ISS-043: an index (.xidx) is behind its pages from the moment a page is written until the context's indexes
// catch up (a flush, a close). After a crash in between, the open judged the index by time: stale only when a
// page was more than 50 ms newer than it (and only the first two and last two pages were checked). An index
// written just before the last batch's pages, which the crash kept from catching up, was trusted: rows on the
// pages were missing from it, so lookups missed them and unique keys weren't enforced.
using System;
using System.IO;
using System.Linq;
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
    }
}
