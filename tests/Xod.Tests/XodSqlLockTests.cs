// SQL holds the read locks of every table a query reads until its last row (PLAN-060 section 2.2). A writer waiting
// for a table's write lock keeps new readers out of that table (ReaderWriterLockSlim prefers writers), so two
// queries taking the same tables in opposite orders, each behind a waiting writer, would wait for each other
// forever. The test drives that interleaving step by step on the engine's own table locks, each step gated on the
// lock state the last one left; the only clock is the bound that turns a deadlock into a failure.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Xod.Tests
{
    public class SqLockA
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class SqLockB
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class XodSqlLockTests : IDisposable
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
        private readonly List<string> dirs = new();

        public void Dispose()
        {
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-sql-locks", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            dirs.Add(dir);
            return Path.Combine(dir, "app.xod");
        }

        /// <summary>The engine's read/write lock for a table, the one its writers and SQL take.</summary>
        private static ReaderWriterLockSlim TableLock(XodContext db, Type type)
        {
            object engine = db.Engine;
            var get = engine.GetType().GetMethod("GetTableLock", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null)!;
            return (ReaderWriterLockSlim)get.Invoke(engine, new object[] { type.FullName })!;
        }

        private static void Until(Func<bool> condition, string what) =>
            Assert.True(SpinWait.SpinUntil(condition, Bound), "never reached: " + what);

        [Fact]
        public async Task Queries_taking_tables_in_opposite_orders_behind_waiting_writers_both_finish()
        {
            var db = new XodContext(NewDatabase("opposite"));
            bool finished = false;
            try
            {
                db.Insert(new SqLockA { Name = "a1" });
                db.Insert(new SqLockB { Name = "b1" });
                var lockA = TableLock(db, typeof(SqLockA));
                var lockB = TableLock(db, typeof(SqLockB));

                // Another query, as the locks see it: it holds A and will take B next.
                var holdingA = new ManualResetEventSlim();
                var takeB = new ManualResetEventSlim();
                var other = new Thread(() =>
                {
                    lockA.EnterReadLock();
                    holdingA.Set();
                    takeB.Wait();
                    lockB.EnterReadLock();
                    lockB.ExitReadLock();
                    lockA.ExitReadLock();
                }) { IsBackground = true };
                other.Start();
                holdingA.Wait();

                var writeA = Task.Run(() => db.Insert(new SqLockA { Name = "a2" }));
                Until(() => lockA.WaitingWriteCount == 1, "a writer waiting for A");

                // This query reads B first, then A: it waits for A behind the writer.
                var query = Task.Run(() => db.Sql("SELECT b.Name AS b, a.Name AS a FROM SqLockB b CROSS JOIN SqLockA a"));
                Until(() => lockA.WaitingReadCount == 1, "the query waiting for A");

                // A writer of B: it waits while the query holds B.
                var writeB = Task.Run(() => db.Insert(new SqLockB { Name = "b2" }));
                Until(() => writeB.IsCompleted || lockB.WaitingWriteCount == 1, "the B writer done or waiting");

                // The other query takes B: behind a waiting B writer, that closes the circle.
                takeB.Set();
                finished = SpinWait.SpinUntil(() => writeA.IsCompleted && query.IsCompleted && writeB.IsCompleted, Bound) && other.Join(Bound);
                Assert.True(finished, "the queries and writers deadlocked");

                var rows = (await query).Rows;
                Assert.NotEmpty(rows);
                Assert.All(rows, r => Assert.True(r[0] is string && r[1] is string));
                AllFree(lockA, lockB);
            }
            finally
            {
                // A deadlocked context can't be closed: closing waits for the reads and writes under way.
                if (finished)
                    db.Dispose();
            }
        }

        // SQL run from inside a read of B (a query's predicate) holds B before the query starts. Behind a waiting
        // writer of A, held by another query that waits for B behind a waiting writer of B, waiting for A would close
        // the same circle: the query is refused instead, and every lock is given back.
        [Fact]
        public void Sql_inside_a_read_that_would_wait_out_of_order_is_refused_and_nothing_waits_forever()
        {
            var db = new XodContext(NewDatabase("nested"));
            bool finished = false;
            try
            {
                db.Insert(new SqLockA { Name = "a1" });
                db.Insert(new SqLockB { Name = "b1" });
                var lockA = TableLock(db, typeof(SqLockA));
                var lockB = TableLock(db, typeof(SqLockB));

                var insideB = new ManualResetEventSlim();
                var runSql = new ManualResetEventSlim();
                var nested = Task.Run(() => db.QueryDelegate<SqLockB>(b =>
                {
                    insideB.Set();
                    runSql.Wait();
                    return db.Sql("SELECT Name FROM SqLockA").Rows.Count > 0;
                }).ToList());
                Assert.True(insideB.Wait(Bound), "the read of B never ran its predicate");

                // Another query, as the locks see it: it holds A and will take B next.
                var holdingA = new ManualResetEventSlim();
                var takeB = new ManualResetEventSlim();
                var other = new Thread(() =>
                {
                    lockA.EnterReadLock();
                    holdingA.Set();
                    takeB.Wait();
                    lockB.EnterReadLock();
                    lockB.ExitReadLock();
                    lockA.ExitReadLock();
                }) { IsBackground = true };
                other.Start();
                holdingA.Wait();

                var writeB = Task.Run(() => db.Insert(new SqLockB { Name = "b2" }));
                Until(() => lockB.WaitingWriteCount == 1, "a writer waiting for B");
                var writeA = Task.Run(() => db.Insert(new SqLockA { Name = "a2" }));
                Until(() => lockA.WaitingWriteCount == 1, "a writer waiting for A");
                takeB.Set();
                Until(() => lockB.WaitingReadCount == 1, "the other query waiting for B");

                runSql.Set();
                finished = SpinWait.SpinUntil(() => nested.IsCompleted && writeA.IsCompleted && writeB.IsCompleted, Bound)
                    && other.Join(Bound);
                Assert.True(finished, "the reads and writers deadlocked");

                Assert.True(writeA.IsCompletedSuccessfully && writeB.IsCompletedSuccessfully);
                var refused = Assert.IsAssignableFrom<XodException>(nested.Exception?.GetBaseException());
                Assert.Contains(typeof(SqLockA).FullName!, refused.Message);
                AllFree(lockA, lockB);
            }
            finally
            {
                if (finished)
                    db.Dispose();
            }
        }

        // A table's file name and its class's full name may differ in case (a case-insensitive file system):
        // they name one table, and one lock, or SQL's read lock wouldn't keep that table's writer out.
        [Fact]
        public void A_table_has_one_lock_whatever_the_case_of_its_name()
        {
            using var db = new XodContext(NewDatabase("case"));
            object engine = db.Engine;
            var get = engine.GetType().GetMethod("GetTableLock", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null)!;
            object upper = get.Invoke(engine, new object[] { typeof(SqLockA).FullName });
            object lower = get.Invoke(engine, new object[] { typeof(SqLockA).FullName!.ToLowerInvariant() });
            Assert.Same(upper, lower);
        }

        private static void AllFree(params ReaderWriterLockSlim[] locks) =>
            Assert.All(locks, l => Assert.True(l.CurrentReadCount == 0 && !l.IsWriteLockHeld && l.WaitingReadCount == 0 && l.WaitingWriteCount == 0));
    }
}
