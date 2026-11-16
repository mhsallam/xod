// ISS-132: a transaction belongs to the flow that began it (its thread, and an await or a Task.Run inside it). A write
// from another flow while it is open used to join it: told it succeeded, then taken back by its rollback. It now waits
// for the transaction to end, up to TransactionTimeoutMs, and then is written, or refused with nothing written.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xod;
using Xod.Services;
using Xunit;

namespace Xod.Tests
{
    public class XodTransactionFlowTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-transaction-flow-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string NewPath(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "flow.xod");
        }

        // Work on another flow: Task.Run and Thread.Start carry the caller's flow (its AsyncLocal values) unless told not to.
        private static Task<T> OnAnotherFlow<T>(Func<T> work)
        {
            using (ExecutionContext.SuppressFlow())
                return Task.Run(work);
        }

        private static void Write(XodContext db, bool related, int id)
        {
            if (related)
                db.Insert(new FlowParent { Id = id, Name = "parent " + id, Children = new List<FlowChild> { new FlowChild { Id = id, Name = "child " + id } } });
            else
                db.Insert(new FlowNote { Id = id, Text = "note " + id });
        }

        private static int[] Ids(XodContext db, bool related) => related
            ? db.Select<FlowParent>().Select(p => p.Id).OrderBy(i => i).ToArray()
            : db.Select<FlowNote>().Select(n => n.Id).OrderBy(i => i).ToArray();

        // The issue's case: another thread's write during a transaction, which the rollback then took back after it had
        // returned. It waits for the transaction now; given a short wait, it is refused, and was never told it succeeded.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Another_flow_s_write_during_a_transaction_does_not_join_it(bool related)
        {
            string path = NewPath("refused-" + related);
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 100 };
            using (var db = new XodContext(path, options: options))
            {
                using (var tx = db.BeginTransaction())
                {
                    Write(db, related, 1);
                    var other = OnAnotherFlow(() => { Write(db, related, 2); return true; });
                    var e = await Assert.ThrowsAsync<TransactionException>(() => other);
                    Assert.Contains("transaction", e.Message);
                    tx.Rollback();
                }
                Assert.Empty(Ids(db, related));
                Write(db, related, 3);
            }
            using var reopened = new XodContext(path, options: options);
            Assert.Equal(new[] { 3 }, Ids(reopened, related));
        }

        // Given time, the other flow's write waits for the transaction's end and is then written: a rollback doesn't
        // take it back, and it isn't part of the transaction's commit either.
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Another_flow_s_write_waits_for_the_transaction_and_is_then_written(bool related, bool commit)
        {
            string path = NewPath("waits-" + related + "-" + commit);
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                Task<bool> other;
                using (var tx = db.BeginTransaction())
                {
                    Write(db, related, 1);
                    other = OnAnotherFlow(() => { Write(db, related, 2); return true; });
                    if (commit)
                        tx.Commit();
                    else
                        tx.Rollback();
                }
                Assert.True(await other);
                Assert.Equal(commit ? new[] { 1, 2 } : new[] { 2 }, Ids(db, related));
            }
            using var reopened = new XodContext(path, options: options);
            Assert.Equal(commit ? new[] { 1, 2 } : new[] { 2 }, Ids(reopened, related));
        }

        // The transaction's own flow goes on across an await and into the async wrappers (Task.Run): their writes are
        // the transaction's, and its rollback takes them back.
        [Fact]
        public async Task The_transaction_s_own_flow_writes_in_it_across_awaits()
        {
            string path = NewPath("own-flow");
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 100 };
            using var db = new XodContext(path, options: options);
            using (var tx = db.BeginTransaction())
            {
                await db.InsertAsync(new FlowNote { Id = 1, Text = "async" });
                await Task.Yield();
                await Task.Run(() => db.Insert(new FlowNote { Id = 2, Text = "run" }));
                Assert.Equal(new[] { 1, 2 }, Ids(db, false));
                tx.Rollback();
            }
            Assert.Empty(Ids(db, false));
        }

        // A write from inside a read on another flow can't wait for the transaction (the transaction's commit needs the
        // lock that read holds), so it is refused at once. Given ten minutes, a wait would hang the test: it waits for
        // the write before it ends the transaction.
        [Fact]
        public async Task Another_flow_s_write_inside_a_read_is_refused_while_a_transaction_is_open()
        {
            string path = NewPath("inside-read");
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 600_000 };
            using var db = new XodContext(path, options: options);
            db.Insert(new FlowNote { Id = 1, Text = "one" });
            using (var tx = db.BeginTransaction())
            {
                db.Insert(new FlowNote { Id = 2, Text = "two" });
                var other = OnAnotherFlow(() => db.Query<FlowNote>(n => InsertOther(db, n)).ToList());
                var e = await Assert.ThrowsAnyAsync<Exception>(() => other);
                Assert.IsType<TransactionException>(e);
                Assert.Contains("inside a read", e.Message);
                tx.Commit();
            }
            Assert.Equal(new[] { 1, 2 }, Ids(db, false));
            Assert.Empty(db.Select<FlowOther>());
        }

        // A transaction waits for another flow's write under way, on its turn, and gives up at TransactionTimeoutMs. The
        // write is held inside its trigger (past its turn and its locks) until the begin has given its answer; a begin
        // that didn't wait for the write's turn would wait on the engine lock instead, with no deadline, and begin only
        // after the write let go (here after ten seconds).
        [Fact]
        public async Task A_transaction_waits_for_another_flow_s_write_under_way()
        {
            string path = NewPath("write-under-way");
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 100 };
            using var db = new XodContext(path, options: options);
            using var inWrite = new ManualResetEventSlim();
            using var answered = new ManualResetEventSlim();
            db.BeforeAction += (s, e) =>
            {
                inWrite.Set();
                answered.Wait(TimeSpan.FromSeconds(10));
            };
            var other = OnAnotherFlow(() => { db.Insert(new FlowNote { Id = 1, Text = "one" }); return true; });
            Assert.True(inWrite.Wait(TimeSpan.FromSeconds(30)));

            Exception begin = null;
            try { db.BeginTransaction().Dispose(); }
            catch (Exception ex) { begin = ex; }
            answered.Set();

            Assert.IsType<TransactionException>(begin);
            Assert.True(await other);
            Assert.Equal(new[] { 1 }, Ids(db, false));
        }

        // A write of the transaction's own flow is decided the transaction's before it takes its locks. Its commit, from
        // another thread of the flow, can end the transaction in between, and another flow's transaction begin (the
        // write took no turn, so nothing holds that one back). Checked again under its locks, the write isn't taken into
        // the other transaction: it starts over outside any, and waits for that one to end. Held at the step between the
        // decision and the locks (FaultInjection.InProcess, for this test's write only), so the order is exact.
        [Fact]
        public async Task A_write_of_the_transaction_s_flow_does_not_join_the_next_transaction_after_its_commit()
        {
            string path = NewPath("own-flow-after-commit");
            var options = new DatabaseOptions { InitialCreate = true };
            var held = new AsyncLocal<bool>();
            using var atTurn = new SemaphoreSlim(0);
            using var go = new ManualResetEventSlim();
            int turnsSeen = 0;
            FaultInjection.InProcess = step =>
            {
                if (step != "write-turn" || !held.Value)
                    return;
                atTurn.Release();
                if (Interlocked.Increment(ref turnsSeen) == 1)
                    go.Wait(Deadline);
            };
            try
            {
                using (var db = new XodContext(path, options: options))
                {
                    var tx = db.BeginTransaction();
                    var write = Task.Run(() => { held.Value = true; db.Insert(new FlowNote { Id = 1, Text = "own" }); });
                    Assert.True(await atTurn.WaitAsync(Deadline));
                    tx.Commit();
                    tx.Dispose();
                    var otherTx = await OnAnotherFlow(() => db.BeginTransaction());
                    go.Set();

                    // Before the check, the write went on into the other transaction and returned.
                    var startedOver = atTurn.WaitAsync(Deadline);
                    Assert.Same(startedOver, await Task.WhenAny(write, startedOver));
                    Assert.True(await startedOver);
                    Assert.False(write.IsCompleted);

                    otherTx.Rollback();
                    otherTx.Dispose();
                    await write.WaitAsync(Deadline);
                    Assert.Equal(new[] { 1 }, Ids(db, false));
                }
                using var reopened = new XodContext(path, options: options);
                Assert.Equal(new[] { 1 }, Ids(reopened, false));
            }
            finally
            {
                FaultInjection.InProcess = null;
            }
        }

        // A transaction ends once, while it is the open one. Its dispose, on another thread, found it open and was held
        // before the engine's lock (FaultInjection.InProcess at transaction-ending); meanwhile its commit ended it and
        // another flow's transaction began. The dispose, let go, leaves that one as it is: still open (another flow's
        // write waits for it) and its rollback takes its own write back. Before, the dispose rolled back the other
        // flow's transaction, which wrote on outside any.
        [Fact]
        public async Task A_second_completion_of_a_transaction_does_not_end_the_next_one()
        {
            string path = NewPath("second-completion");
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 100 };
            var held = new AsyncLocal<bool>();
            using var atEnd = new ManualResetEventSlim();
            using var go = new ManualResetEventSlim();
            FaultInjection.InProcess = step =>
            {
                if (step != "transaction-ending" || !held.Value)
                    return;
                atEnd.Set();
                go.Wait(Deadline);
            };
            try
            {
                using (var db = new XodContext(path, options: options))
                {
                    var tx = db.BeginTransaction();
                    db.Insert(new FlowNote { Id = 1, Text = "first" });
                    var dispose = Task.Run(() => { held.Value = true; tx.Dispose(); });
                    Assert.True(atEnd.Wait(Deadline));
                    tx.Commit();

                    var next = await OnAnotherFlow(() =>
                    {
                        var t = db.BeginTransaction();
                        db.Insert(new FlowNote { Id = 2, Text = "next" });
                        return t;
                    });
                    go.Set();
                    await dispose.WaitAsync(Deadline);

                    Assert.True(next.IsActive);
                    await Assert.ThrowsAsync<TransactionException>(() => OnAnotherFlow(() => { db.Insert(new FlowNote { Id = 3, Text = "other" }); return true; }));
                    next.Rollback();
                    next.Dispose();
                    Assert.Equal(new[] { 1 }, Ids(db, false));
                }
                using var reopened = new XodContext(path, options: options);
                Assert.Equal(new[] { 1 }, Ids(reopened, false));
            }
            finally
            {
                FaultInjection.InProcess = null;
            }
        }

        // The tests that hold an interleaving with FaultInjection.InProcess, one process-wide slot, are in this class,
        // whose tests xUnit runs one at a time. Their waits have a deadline, so a regression fails instead of hanging.
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

        private static bool InsertOther(XodContext db, FlowNote n)
        {
            db.Insert(new FlowOther { Id = n.Id });
            return true;
        }

        // A transaction can't begin inside a write on its own thread (a trigger): it would wait for that write to end.
        [Fact]
        public void A_transaction_can_t_begin_inside_a_write()
        {
            string path = NewPath("inside-write");
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 100 };
            using var db = new XodContext(path, options: options);
            Exception inside = null;
            db.AfterAction += (s, e) =>
            {
                try { using var tx = db.BeginTransaction(); }
                catch (Exception ex) { inside = ex; }
            };
            db.Insert(new FlowNote { Id = 1, Text = "one" });
            Assert.IsType<TransactionException>(inside);
            Assert.Contains("inside a write", inside.Message);
        }
    }

    public class FlowNote
    {
        [PrimaryKey]
        public int Id { get; set; }
        public string Text { get; set; }
    }

    public class FlowOther
    {
        [PrimaryKey]
        public int Id { get; set; }
    }

    public class FlowParent
    {
        [PrimaryKey]
        public int Id { get; set; }
        public string Name { get; set; }
        [Children]
        public List<FlowChild> Children { get; set; }
    }

    public class FlowChild
    {
        [PrimaryKey]
        public int Id { get; set; }
        public int FlowParentId { get; set; }
        public string Name { get; set; }
        [ParentKey(nameof(FlowParentId))]
        public FlowParent Parent { get; set; }
    }
}
