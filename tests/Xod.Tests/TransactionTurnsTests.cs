// ISS-132: the order TransactionTurns keeps between a transaction and the writes of other flows. A write takes its turn
// before its locks, so an engine-level test can't hold one between the two; these hold each step at the owner.
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
            Assert.True(turns.TryBeginWrite(0));
            Assert.False(turns.TryBeginTransaction(0));
            Assert.Equal(0, turns.TransactionsWaiting);
            turns.EndWrite();
            Assert.True(turns.TryBeginTransaction(0));
            Assert.False(turns.TryBeginWrite(0));
            turns.EndTransaction();
            Assert.True(turns.TryBeginWrite(0));
        }

        // A waiting transaction goes before writes that come after it, so a stream of writes can't keep it out; it
        // begins once the writes it waited for have ended.
        [Fact]
        public async Task A_waiting_transaction_goes_before_later_writes()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginWrite(0));
            var transaction = Task.Run(() => turns.TryBeginTransaction(Long));
            Assert.True(SpinWait.SpinUntil(() => turns.TransactionsWaiting == 1, Long));

            Assert.False(turns.TryBeginWrite(0));
            Assert.False(transaction.IsCompleted);
            turns.EndWrite();

            Assert.True(await transaction);
            Assert.Equal(0, turns.TransactionsWaiting);
            Assert.False(turns.TryBeginWrite(0));
            turns.EndTransaction();
            Assert.True(turns.TryBeginWrite(0));
        }

        // A write that can't wait (made inside a read) goes before a transaction that is only waiting: that one holds no
        // lock yet and waits for the write to end. An open transaction still refuses it.
        [Fact]
        public async Task A_write_that_can_t_wait_passes_a_waiting_transaction_only()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginWrite(0));
            var transaction = Task.Run(() => turns.TryBeginTransaction(Long));
            Assert.True(SpinWait.SpinUntil(() => turns.TransactionsWaiting == 1, Long));

            Assert.True(turns.TryBeginWrite(0, passWaiting: true));
            turns.EndWrite();
            Assert.False(transaction.IsCompleted);
            turns.EndWrite();
            Assert.True(await transaction);
            Assert.False(turns.TryBeginWrite(0, passWaiting: true));
            turns.EndTransaction();
        }

        // Waiting writes and transactions go in the order they came: a write that waited for an open transaction goes
        // before a transaction that came after it, so transactions back to back don't keep it out to its deadline.
        [Fact]
        public async Task A_write_that_came_before_a_waiting_transaction_goes_first()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginTransaction(0));
            var write = Task.Run(() => turns.TryBeginWrite(Long));
            Assert.True(SpinWait.SpinUntil(() => turns.WritesWaiting == 1, Long));
            var next = Task.Run(() => turns.TryBeginTransaction(Long));
            Assert.True(SpinWait.SpinUntil(() => turns.TransactionsWaiting == 1, Long));

            turns.EndTransaction();
            Assert.True(await write);
            Assert.False(next.IsCompleted);
            turns.EndWrite();
            Assert.True(await next);
            turns.EndTransaction();
        }

        // The context's close ends every wait at once: the waiter goes on and finds the context closed.
        [Fact]
        public async Task The_close_ends_every_wait()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginTransaction(0));
            var write = Task.Run(() => turns.TryBeginWrite(Long));
            var next = Task.Run(() => turns.TryBeginTransaction(Long));
            Assert.True(SpinWait.SpinUntil(() => turns.WritesWaiting == 1 && turns.TransactionsWaiting == 1, Long));

            turns.Close();
            Assert.True(await write);
            Assert.True(await next);
        }

        // A transaction that stops waiting at its deadline lets the writes it held back go on.
        [Fact]
        public void A_transaction_that_times_out_lets_writes_go_on()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginWrite(0));
            Assert.False(turns.TryBeginTransaction(20));
            Assert.Equal(0, turns.TransactionsWaiting);
            Assert.True(turns.TryBeginWrite(0));
        }

        // A write waits for an open transaction and goes on at its end.
        [Fact]
        public async Task A_write_waits_for_an_open_transaction()
        {
            var turns = new TransactionTurns();
            Assert.True(turns.TryBeginTransaction(0));
            Assert.False(turns.TryBeginWrite(0));
            var write = Task.Run(() => turns.TryBeginWrite(Long));
            turns.EndTransaction();
            Assert.True(await write);
        }
    }
}
