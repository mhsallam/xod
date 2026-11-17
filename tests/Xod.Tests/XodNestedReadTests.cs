// ISS-163: a read nested in another on its thread (a query's predicate reading another table, by a lambda query or by
// SQL) can't wait forever behind writers. Standalone tables' reads took their table's lock in any order, and SQL waited
// only above the tables it held: thread 1 reading A, its predicate reading B, and thread 2 reading B, its predicate
// reading A, each behind a waiting writer of the other's table, waited for each other forever (a nested SQL was refused
// instead). Every write now takes the engine-wide write lock (ISS-125) and every read only the engine-wide read lock,
// which a thread that holds it enters again without waiting. The test drives the interleaving step by step, each step
// gated on the state the last left; the only clock is the bound that turns a deadlock into a failure.
using System;
using System.Collections.Concurrent;
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

    public class XodNestedReadTests : IDisposable
    {
        private static readonly TimeSpan Bound = EngineLocks.Bound;
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
            string dir = Path.Combine(Path.GetTempPath(), "xod-nested-reads", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            dirs.Add(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static int WaitingWriters(XodContext db) => EngineLocks.WaitingWriters(db);

        private static void Until(Func<bool> condition, string what) => EngineLocks.Until(condition, what);

        // What a predicate reads of the other table: by a lambda query, or by SQL.
        private static int ReadOther<TOther>(XodContext db, bool sql) where TOther : class =>
            sql ? db.Sql("SELECT Name FROM " + typeof(TOther).Name).Rows.Count : db.Select<TOther>().Count();

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Reads_nested_in_opposite_orders_behind_waiting_writers_both_finish(bool sql)
        {
            var db = new XodContext(NewDatabase(sql ? "sql" : "lambda"));
            bool finished = false;
            try
            {
                db.Insert(new SqLockA { Name = "a1" });
                db.Insert(new SqLockB { Name = "b1" });

                // Thread 1 reads A, its predicate then B; thread 2 reads B, its predicate then A.
                var insideA = new ManualResetEventSlim();
                var insideB = new ManualResetEventSlim();
                var go = new ManualResetEventSlim();
                int seenB = 0, seenA = 0;
                var readA = Task.Run(() => db.QueryDelegate<SqLockA>(a =>
                {
                    insideA.Set();
                    go.Wait();
                    seenB = ReadOther<SqLockB>(db, sql);
                    return true;
                }).ToList());
                var readB = Task.Run(() => db.QueryDelegate<SqLockB>(b =>
                {
                    insideB.Set();
                    go.Wait();
                    seenA = ReadOther<SqLockA>(db, sql);
                    return true;
                }).ToList());
                Assert.True(insideA.Wait(Bound) && insideB.Wait(Bound), "the reads never ran their predicates");

                // A writer of each table, waiting behind the reads.
                var writeA = Task.Run(() => db.Insert(new SqLockA { Name = "a2" }));
                var writeB = Task.Run(() => db.Insert(new SqLockB { Name = "b2" }));
                Until(() => WaitingWriters(db) == 2, "both writers waiting");

                go.Set();
                finished = SpinWait.SpinUntil(() => readA.IsCompleted && readB.IsCompleted && writeA.IsCompleted && writeB.IsCompleted, Bound);
                Assert.True(finished, "the reads and writers deadlocked");

                Assert.True(readA.IsCompletedSuccessfully && readB.IsCompletedSuccessfully, (readA.Exception ?? readB.Exception)?.ToString());
                Assert.True(writeA.IsCompletedSuccessfully && writeB.IsCompletedSuccessfully);
                // The nested reads ran before the writers, which waited for the outer reads to end.
                Assert.Equal(1, seenA);
                Assert.Equal(1, seenB);
                Assert.Equal(2, db.Count<SqLockA>());
                Assert.Equal(2, db.Count<SqLockB>());
            }
            finally
            {
                // A deadlocked context can't be closed: closing waits for the reads and writes under way.
                if (finished)
                    db.Dispose();
            }
        }
    }
}
