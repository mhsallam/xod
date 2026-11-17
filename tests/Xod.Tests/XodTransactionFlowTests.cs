// ISS-132, ISS-175: a transaction belongs to the flow that began it (its thread, and an await or a Task.Run inside it).
// A write from another flow while it is open used to join it: told it succeeded, then taken back by its rollback; a read
// saw its writes before the commit. Both now wait for the transaction to end, up to TransactionTimeoutMs, and then go on,
// or are refused with nothing done.
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
    [Collection(InProcessHookCollection.Name)]
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
                Assert.Equal(new[] { 1, 2 }, await Task.Run(() => Ids(db, false)));
                Assert.Equal(2, await Task.Run(() => db.Sql("SELECT Id FROM FlowNote").Rows.Count));
                tx.Rollback();
            }
            Assert.Empty(Ids(db, false));
        }

        // ISS-175: another flow's read during a transaction doesn't see what the transaction has written. Given a short
        // wait, it is refused; before, it returned the transaction's rows, which its rollback then took back.
        [Theory]
        [InlineData("select")]
        [InlineData("count")]
        [InlineData("query")]
        [InlineData("sql")]
        public async Task Another_flow_s_read_during_a_transaction_does_not_see_it(string read)
        {
            string path = NewPath("read-refused-" + read);
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 100 };
            using var db = new XodContext(path, options: options);
            using (var tx = db.BeginTransaction())
            {
                db.Insert(new FlowNote { Id = 1, Text = "uncommitted" });
                var other = OnAnotherFlow(() => Read(db, read));
                var e = await Assert.ThrowsAsync<TransactionException>(() => other);
                Assert.Contains("transaction", e.Message);
                tx.Rollback();
            }
            Assert.Equal(0, Read(db, read));
        }

        // Given time, the other flow's read waits for the transaction's end, and sees what it committed, or nothing after
        // its rollback. The transaction ends only once the read waits for it (the step operation-waits); before, the read
        // saw the row at once and never waited.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Another_flow_s_read_waits_for_the_transaction_and_sees_what_it_committed(bool commit)
        {
            string path = NewPath("read-waits-" + commit);
            var options = new DatabaseOptions { InitialCreate = true };
            var marked = new AsyncLocal<bool>();
            using var readWaits = new ManualResetEventSlim();
            FaultInjection.InProcess = step =>
            {
                if (step == "operation-waits" && marked.Value)
                    readWaits.Set();
            };
            try
            {
                using var db = new XodContext(path, options: options);
                Task<int> other;
                using (var tx = db.BeginTransaction())
                {
                    db.Insert(new FlowNote { Id = 1, Text = "one" });
                    other = OnAnotherFlow(() => { marked.Value = true; return Read(db, "select"); });
                    Assert.True(readWaits.Wait(Deadline));
                    Assert.False(other.IsCompleted);
                    if (commit)
                        tx.Commit();
                    else
                        tx.Rollback();
                }
                Assert.Equal(commit ? 1 : 0, await other.WaitAsync(Deadline));
            }
            finally
            {
                FaultInjection.InProcess = null;
            }
        }

        // Code of another flow run on the transaction's thread, inside one of its reads (ExecutionContext.Run here; an
        // await's continuation run inline does the same), isn't the transaction's: its read is refused, not nested in the
        // transaction's turn. Before, it read the transaction's uncommitted row.
        [Fact]
        public async Task Another_flow_s_code_run_inside_the_transaction_s_read_does_not_see_it()
        {
            string path = NewPath("inline-other-flow");
            var options = new DatabaseOptions { InitialCreate = true };
            using var db = new XodContext(path, options: options);
            var otherFlow = await OnAnotherFlow(() => ExecutionContext.Capture());
            var inline = new InlineRead();
            using (var tx = db.BeginTransaction())
            {
                db.Insert(new FlowNote { Id = 1, Text = "uncommitted" });
                Assert.Single(db.Query<FlowNote>(n => ReadOnOtherFlow(db, otherFlow, inline)).ToList());
                tx.Rollback();
            }
            Assert.Null(inline.Count);
            Assert.IsType<TransactionException>(inline.Refused);
            Assert.Equal(0, db.Count<FlowNote>());
        }

        private sealed class InlineRead
        {
            public int? Count;
            public Exception Refused;
        }

        private static bool ReadOnOtherFlow(XodContext db, ExecutionContext otherFlow, InlineRead inline)
        {
            ExecutionContext.Run(otherFlow.CreateCopy(), _ =>
            {
                try { inline.Count = db.Count<FlowNote>(); }
                catch (Exception e) { inline.Refused = e; }
            }, null);
            return true;
        }

        // The tables a transaction has dropped are gone only at its commit: another flow's RegisteredTypes waits for it
        // (refused here, with a short wait) rather than leave them out before then. Before, it left the table out, and
        // the rollback then brought it back.
        [Fact]
        public async Task Another_flow_s_registered_types_wait_for_a_transaction_s_drop()
        {
            string path = NewPath("registered-types");
            var options = new DatabaseOptions { InitialCreate = true, TransactionTimeoutMs = 100 };
            using var db = new XodContext(path, options: options);
            db.Insert(new FlowNote { Id = 1, Text = "one" });
            using (var tx = db.BeginTransaction())
            {
                db.Drop<FlowNote>();
                await Assert.ThrowsAsync<TransactionException>(() => OnAnotherFlow(() => db.RegisteredTypes().ToList()));
                tx.Rollback();
            }
            Assert.Contains(typeof(FlowNote), db.RegisteredTypes());
        }

        private static int Read(XodContext db, string read) => read switch
        {
            "select" => db.Select<FlowNote>().Count(),
            "count" => db.Count<FlowNote>(),
            "query" => db.Query<FlowNote>(n => n.Id > 0).Count(),
            "sql" => db.Sql("SELECT Id FROM FlowNote").Rows.Count,
            _ => throw new ArgumentException(read),
        };

        // A write inside another flow's read, while a transaction waits to begin, is refused at once, as every write
        // inside a read is (ISS-125): it is in the read's turn, so it doesn't wait behind the transaction, which waits for
        // the read. The transaction begins after the read.
        [Fact]
        public async Task A_write_inside_another_flow_s_read_is_refused_while_a_transaction_waits()
        {
            string path = NewPath("write-inside-read");
            var options = new DatabaseOptions { InitialCreate = true };
            var marked = new AsyncLocal<bool>();
            using var transactionWaits = new ManualResetEventSlim();
            using var inRead = new ManualResetEventSlim();
            FaultInjection.InProcess = step =>
            {
                if (step == "transaction-waits" && marked.Value)
                    transactionWaits.Set();
            };
            try
            {
                using var db = new XodContext(path, options: options);
                db.Insert(new FlowNote { Id = 1, Text = "one" });
                var read = OnAnotherFlow(() => db.Query<FlowNote>(n => InsertOtherOnce(db, n, inRead, transactionWaits)).ToList());
                Assert.True(inRead.Wait(Deadline));
                var begin = OnAnotherFlow(() =>
                {
                    marked.Value = true;
                    var tx = db.BeginTransaction();
                    tx.Commit();
                    tx.Dispose();
                    return true;
                });
                var refused = await Assert.ThrowsAsync<XodException>(() => read.WaitAsync(Deadline));
                Assert.Contains("inside a read", refused.Message);
                Assert.True(await begin.WaitAsync(Deadline));
                Assert.Empty(db.Select<FlowOther>());
            }
            finally
            {
                FaultInjection.InProcess = null;
            }
        }

        // A read of the transaction's own flow, decided the transaction's before its locks, doesn't see another flow's
        // transaction begun after its own commit: checked under its locks, it starts over and waits for that one. Held at
        // the step between the decision and the locks, as the write in the test above.
        [Fact]
        public async Task A_read_of_the_transaction_s_flow_does_not_see_the_next_transaction_after_its_commit()
        {
            string path = NewPath("own-read-after-commit");
            var options = new DatabaseOptions { InitialCreate = true };
            var held = new AsyncLocal<bool>();
            using var atTurn = new SemaphoreSlim(0);
            using var go = new ManualResetEventSlim();
            int turnsSeen = 0;
            FaultInjection.InProcess = step =>
            {
                if (step != "turn" || !held.Value)
                    return;
                atTurn.Release();
                if (Interlocked.Increment(ref turnsSeen) == 1)
                    go.Wait(Deadline);
            };
            try
            {
                using var db = new XodContext(path, options: options);
                var tx = db.BeginTransaction();
                db.Insert(new FlowNote { Id = 1, Text = "own" });
                var read = Task.Run(() => { held.Value = true; return Ids(db, false); });
                Assert.True(await atTurn.WaitAsync(Deadline));
                tx.Commit();
                tx.Dispose();
                var next = await OnAnotherFlow(() =>
                {
                    var t = db.BeginTransaction();
                    db.Insert(new FlowNote { Id = 2, Text = "next" });
                    return t;
                });
                go.Set();

                // Before the check, the read went on and saw the other transaction's row.
                var startedOver = atTurn.WaitAsync(Deadline);
                Assert.Same(startedOver, await Task.WhenAny(read, startedOver));
                Assert.True(await startedOver);
                Assert.False(read.IsCompleted);

                next.Rollback();
                next.Dispose();
                Assert.Equal(new[] { 1 }, await read.WaitAsync(Deadline));
            }
            finally
            {
                FaultInjection.InProcess = null;
            }
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
                if (step != "turn" || !held.Value)
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

        // Inside the read: says so, waits until the transaction waits for the read, then writes.
        private static bool InsertOtherOnce(XodContext db, FlowNote n, ManualResetEventSlim inRead, ManualResetEventSlim transactionWaits)
        {
            inRead.Set();
            Assert.True(transactionWaits.Wait(Deadline));
            return InsertOther(db, n);
        }

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
