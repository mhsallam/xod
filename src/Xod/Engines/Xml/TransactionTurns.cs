using System;
using System.Collections.Generic;
using System.Threading;

namespace Xod.Engines.Xml
{
    /// <summary>
    /// ISS-132: whose turn it is to write, between a context's transaction and the writes of other flows. A transaction
    /// belongs to the flow that began it (XmlEngine's flowTransaction); while it is open, a write from any other flow
    /// waits for its end, and a transaction waits for the writes of other flows under way to end before it begins.
    /// Waiting writes and transactions go in the order they came (a ticket each): a write that came before a waiting
    /// transaction goes before it, and one that came after waits for it, so neither a stream of writes nor a stream of
    /// transactions keeps the other out. Writes don't wait for each other here (the engine's locks order them).
    /// <para>
    /// A monitor and counts, not a ReaderWriterLockSlim: a transaction may end on another thread than the one it began
    /// on (after an await), which a thread-affine lock refuses. Every wait has a deadline (TransactionTimeoutMs), and the
    /// context's close ends every wait.
    /// </para>
    /// </summary>
    internal sealed class TransactionTurns
    {
        private readonly object gate = new object();
        private bool transactionOpen;
        private bool closed;
        private int writes;
        private long nextTicket;
        // The tickets of the transactions and the writes waiting, the earliest first.
        private readonly SortedSet<long> transactionsWaiting = new SortedSet<long>();
        private readonly SortedSet<long> writesWaiting = new SortedSet<long>();

        /// <summary>Waits until no transaction is open, no write of another flow is under way, and nothing that came
        /// before it waits, then opens one. False if <paramref name="timeoutMs"/> passed first.</summary>
        internal bool TryBeginTransaction(int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            lock (gate)
            {
                long ticket = nextTicket++;
                transactionsWaiting.Add(ticket);
                try
                {
                    while (!closed && (transactionOpen || writes > 0 || Before(writesWaiting, ticket) || Before(transactionsWaiting, ticket)))
                    {
                        if (!WaitUntil(deadline))
                            return false;
                    }
                    transactionOpen = true;
                    return true;
                }
                finally
                {
                    transactionsWaiting.Remove(ticket);
                    // What waited behind this transaction goes on if it stopped waiting.
                    Monitor.PulseAll(gate);
                }
            }
        }

        internal void EndTransaction()
        {
            lock (gate)
            {
                transactionOpen = false;
                Monitor.PulseAll(gate);
            }
        }

        /// <summary>Waits until no transaction is open and none that came before it waits, then counts a write under
        /// way. False if <paramref name="timeoutMs"/> passed first (0: don't wait). With <paramref name="passWaiting"/>
        /// only an open transaction holds it back (a write inside a read, which can't wait, and goes before a
        /// transaction that is only waiting: that one holds no lock yet).</summary>
        internal bool TryBeginWrite(int timeoutMs, bool passWaiting = false)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            lock (gate)
            {
                if (passWaiting)
                {
                    while (!closed && transactionOpen)
                    {
                        if (!WaitUntil(deadline))
                            return false;
                    }
                    writes++;
                    return true;
                }

                long ticket = nextTicket++;
                writesWaiting.Add(ticket);
                try
                {
                    while (!closed && (transactionOpen || Before(transactionsWaiting, ticket)))
                    {
                        if (!WaitUntil(deadline))
                            return false;
                    }
                    writes++;
                    return true;
                }
                finally
                {
                    writesWaiting.Remove(ticket);
                    Monitor.PulseAll(gate);
                }
            }
        }

        internal void EndWrite()
        {
            lock (gate)
            {
                if (--writes == 0)
                    Monitor.PulseAll(gate);
            }
        }

        /// <summary>The context closed: every wait ends at once, and the caller finds the context closed. Without it a
        /// transaction the close couldn't roll back (a close from inside a read) left every write waiting to its
        /// deadline.</summary>
        internal void Close()
        {
            lock (gate)
            {
                closed = true;
                Monitor.PulseAll(gate);
            }
        }

        /// <summary>How many transactions are waiting for their turn now.</summary>
        internal int TransactionsWaiting
        {
            get { lock (gate) return transactionsWaiting.Count; }
        }

        /// <summary>How many writes are waiting for their turn now.</summary>
        internal int WritesWaiting
        {
            get { lock (gate) return writesWaiting.Count; }
        }

        // Something in the queue came before this ticket.
        private static bool Before(SortedSet<long> waiting, long ticket) => waiting.Count > 0 && waiting.Min < ticket;

        // Under the gate: waits for a pulse until the deadline; false once it has passed.
        private bool WaitUntil(long deadline)
        {
            long left = deadline - Environment.TickCount64;
            if (left <= 0)
                return false;
            Monitor.Wait(gate, (int)Math.Min(left, int.MaxValue));
            return true;
        }
    }
}
