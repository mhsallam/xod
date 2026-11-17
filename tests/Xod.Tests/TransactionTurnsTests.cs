// ISS-132, ISS-175: the order TransactionTurns keeps between a transaction and the reads and writes (operations) of other
// flows. An operation takes its turn before its locks, so an engine-level test can't hold one between the two; these hold
// each step at the owner.
using System;
using System.Threading;
using System.Threading.Tasks;
using Xod.Engines.Xml;
using Xunit;

namespace Xod.Tests
{
    public class TransactionTurnsTests
    {
        private const int Long = 60_000;

        // A transaction doesn't begin beside a write under way: that write, past its turn, would take its locks after
        // the begin and be written into the transaction (ISS-132 again, in the gap between the turn and the locks).
        [Fact]
        public void A_transaction_waits_for_a_write_under_way()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginOperation(0));
            Assert.False(turns.TryBeginTransaction(0));
            Assert.Equal(0, turns.TransactionsWaiting);
            turns.EndOperation();
            Assert.True(turns.TryBeginTransaction(0));
            Assert.False(turns.TryBeginOperation(0));
            turns.EndTransaction();
            Assert.True(turns.TryBeginOperation(0));
        }

        // A waiting transaction goes before writes that come after it, so a stream of writes can't keep it out; it
        // begins once the writes it waited for have ended.
        [Fact]
        public async Task A_waiting_transaction_goes_before_later_writes()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginOperation(0));
            var transaction = Task.Run(() => turns.TryBeginTransaction(Long));
            Assert.True(SpinWait.SpinUntil(() => turns.TransactionsWaiting == 1, Long));

            Assert.False(turns.TryBeginOperation(0));
            Assert.False(transaction.IsCompleted);
            turns.EndOperation();

            Assert.True(await transaction);
            Assert.Equal(0, turns.TransactionsWaiting);
            Assert.False(turns.TryBeginOperation(0));
            turns.EndTransaction();
            Assert.True(turns.TryBeginOperation(0));
        }

        // Waiting writes and transactions go in the order they came: a write that waited for an open transaction goes
        // before a transaction that came after it, so transactions back to back don't keep it out to its deadline.
        [Fact]
        public async Task A_write_that_came_before_a_waiting_transaction_goes_first()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginTransaction(0));
            var write = Task.Run(() => turns.TryBeginOperation(Long));
            Assert.True(SpinWait.SpinUntil(() => turns.OperationsWaiting == 1, Long));
            var next = Task.Run(() => turns.TryBeginTransaction(Long));
            Assert.True(SpinWait.SpinUntil(() => turns.TransactionsWaiting == 1, Long));

            turns.EndTransaction();
            Assert.True(await write);
            Assert.False(next.IsCompleted);
            turns.EndOperation();
            Assert.True(await next);
            turns.EndTransaction();
        }

        // The context's close ends every wait at once: the waiter goes on and finds the context closed.
        [Fact]
        public async Task The_close_ends_every_wait()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginTransaction(0));
            var write = Task.Run(() => turns.TryBeginOperation(Long));
            var next = Task.Run(() => turns.TryBeginTransaction(Long));
            Assert.True(SpinWait.SpinUntil(() => turns.OperationsWaiting == 1 && turns.TransactionsWaiting == 1, Long));

            turns.Close();
            Assert.True(await write);
            Assert.True(await next);
        }

        // A transaction that stops waiting at its deadline lets the writes it held back go on.
        [Fact]
        public void A_transaction_that_times_out_lets_writes_go_on()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginOperation(0));
            Assert.False(turns.TryBeginTransaction(20));
            Assert.Equal(0, turns.TransactionsWaiting);
            Assert.True(turns.TryBeginOperation(0));
        }

        // A write waits for an open transaction and goes on at its end.
        [Fact]
        public async Task A_write_waits_for_an_open_transaction()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginTransaction(0));
            Assert.False(turns.TryBeginOperation(0));
            var write = Task.Run(() => turns.TryBeginOperation(Long));
            turns.EndTransaction();
            Assert.True(await write);
        }
    }
}
