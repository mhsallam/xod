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
    }
}
