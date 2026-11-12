// ISS-086: a write to a standalone table took the engine-wide read lock and then the table's write lock; when the
// second failed (a write from inside a read of that table on the same thread, which can't be upgraded), the first
// was never given back. The context then refused transactions, other threads' writes waited for ever, and closing
// it threw.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Xod.Tests
{
    public class LockedNote
    {
        public int Id { get; set; }
        public string Text { get; set; }
    }

    public class LockedHolder
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Children] public List<LockedPart> Parts { get; set; }
    }

    public class LockedPart
    {
        public int Id { get; set; }
        public int LockedHolderId { get; set; }
    }

    public class XodLockSafetyTests
    {
        private static string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-locks", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.xod");
        }

        [Fact]
        public async Task A_write_refused_inside_a_read_of_its_table_leaves_no_lock_held()
        {
            string path = NewDatabase("upgrade");
            using var db = new XodContext(path);
            db.Insert(new LockedNote { Text = "a" });

            var refused = Assert.ThrowsAny<XodException>(() =>
                db.QueryDelegate<LockedNote>(n => db.Update(new LockedNote { Id = n.Id, Text = "b" })).ToList());
            Assert.Contains("inside a read", refused.Message);

            using (var tx = db.BeginTransaction())
            {
                db.Insert(new LockedNote { Text = "c" });
                tx.Commit();
            }
            // A table with relations: its write takes the engine-wide write lock, which a lock left held blocks.
            await Task.Run(() => db.Insert(new LockedHolder { Name = "h", Parts = new() { new LockedPart() } }))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { "a", "c" }, db.Select<LockedNote>().OrderBy(n => n.Id).Select(n => n.Text));
        }

        // A transaction begun inside a read takes the transaction gate, then fails to take the write lock: the gate
        // was never given back, so every later transaction waited out its timeout and was refused.
        [Fact]
        public void A_transaction_refused_inside_a_read_leaves_the_next_one_free()
        {
            string path = NewDatabase("transaction-gate");
            using var db = new XodContext(path, null, new DatabaseOptions { TransactionTimeoutMs = 2000 });
            db.Insert(new LockedNote { Text = "a" });

            Assert.ThrowsAny<XodException>(() => db.QueryDelegate<LockedNote>(n =>
            {
                using var inner = db.BeginTransaction();
                return true;
            }).ToList());

            using (var tx = db.BeginTransaction())
            {
                db.Insert(new LockedNote { Text = "b" });
                tx.Commit();
            }
            Assert.Equal(2, db.Count<LockedNote>());
        }

        // The same for a table with relations: its write takes the engine-wide write lock, which a read of it on
        // the same thread can't upgrade to either. Refused the same way, and nothing is left held.
        [Fact]
        public async Task A_write_refused_inside_a_read_of_a_table_with_relations_leaves_no_lock_held()
        {
            string path = NewDatabase("upgrade-relations");
            using var db = new XodContext(path);
            db.Insert(new LockedHolder { Name = "h", Parts = new() { new LockedPart() } });

            var refused = Assert.ThrowsAny<XodException>(() =>
                db.QueryDelegate<LockedHolder>(h => db.Update(new LockedHolder { Id = h.Id, Name = "renamed" })).ToList());
            Assert.Contains("inside a read", refused.Message);

            await Task.Run(() => db.Insert(new LockedHolder { Name = "second", Parts = new() }))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { "h", "second" }, db.Select<LockedHolder>().OrderBy(h => h.Id).Select(h => h.Name));
        }

        // Review of TASK-063: a flush, a cache drop, a drop, statistics and a commit took the engine-wide write lock
        // directly; from inside a read on the same thread they threw a raw LockRecursionException. Refused now with
        // the reason, and nothing is left held.
        [Fact]
        public async Task Maintenance_inside_a_read_is_refused_with_its_reason()
        {
            string path = NewDatabase("maintenance");
            using var db = new XodContext(path, null, new DatabaseOptions { TransactionTimeoutMs = 2000 });
            db.Insert(new LockedNote { Text = "a" });

            var calls = new (string Name, Action Call)[]
            {
                ("Flush", () => db.Flush()),
                ("ClearCache", () => db.ClearCache()),
                ("Drop", () => db.Drop<LockedPart>()),
                ("GetStatistics", () => db.GetStatistics()),
            };
            foreach (var (name, call) in calls)
            {
                var refused = Assert.ThrowsAny<XodException>(() => db.QueryDelegate<LockedNote>(n => { call(); return true; }).ToList());
                Assert.True(refused.Message.Contains("inside a read"), name + ": " + refused.Message);
            }

            var tx = db.BeginTransaction();
            db.Insert(new LockedNote { Text = "b" });
            Assert.Contains("inside a read", Assert.ThrowsAny<XodException>(() =>
                db.QueryDelegate<LockedNote>(n => { tx.Commit(); return true; }).ToList()).Message);
            tx.Commit();
            tx.Dispose();

            await Task.Run(() =>
            {
                using var other = db.BeginTransaction();
                db.Insert(new LockedNote { Text = "c" });
                other.Commit();
            }).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { "a", "b", "c" }, db.Select<LockedNote>().OrderBy(n => n.Id).Select(n => n.Text));
        }

        // An exclusive hold is claimed only once a second open of the lock file is refused: where the file system
        // gave no lock (an NFS client and a lock file opened for reading), the open is let go and refused as if the
        // database were held (review of TASK-063). Driven directly: Windows and local Unix file systems always lock.
        [Fact]
        public void An_exclusive_hold_the_file_system_did_not_lock_is_refused()
        {
            string path = NewDatabase("unproven");
            string lockFile = path + ".lock";
            var prove = typeof(XodContext).Assembly.GetType("Xod.Services.IOService")!
                .GetMethod("ProveExclusive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

            var unlocked = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => prove.Invoke(null, new object[] { unlocked, lockFile, path }));
            Assert.IsType<DatabaseLockedException>(failure.InnerException);
            Assert.False(unlocked.CanRead); // let go

            using var locked = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);
            prove.Invoke(null, new object[] { locked, lockFile, path });
            Assert.True(locked.CanRead);
        }

        // A read-only medium and no lock file: the open is for reading only, not "locked by another process" (review
        // of TASK-063: EROFS is an IOException, which was taken for another's lock). Needs a folder mounted read-only:
        // XOD_RO_RW (writable) and XOD_RO_RO (the same folder, read-only), as in the Linux container run; does nothing
        // without them.
        [Fact]
        public void A_database_on_read_only_media_without_a_lock_file_opens_for_reading()
        {
            string rw = Environment.GetEnvironmentVariable("XOD_RO_RW");
            string ro = Environment.GetEnvironmentVariable("XOD_RO_RO");
            if (rw == null || ro == null)
                return;
            using (var db = new XodContext(Path.Combine(rw, "app.xod")))
                db.Insert(new LockedNote { Text = "a" });
            File.Delete(Path.Combine(rw, "app.xod.lock"));

            using (var db = new XodContext(Path.Combine(ro, "app.xod")))
            {
                Assert.Equal(1, db.Count<LockedNote>());
                Assert.Contains("lock file", Assert.ThrowsAny<XodException>(() => db.Insert(new LockedNote { Text = "b" })).Message);
            }
        }
    }
}
