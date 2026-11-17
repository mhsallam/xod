using System;
using System.Collections.Generic;
using System.Threading;

namespace Xod.Engines.Xml
{
    /// <summary>
    /// ISS-132, ISS-175: whose turn it is, between a context's transaction and the reads and writes (operations) of
    /// other flows. A transaction belongs to the flow that began it (XmlEngine's flowTransaction); while it is open, an
    /// operation from any other flow waits for its end, and a transaction waits for the operations of other flows under
    /// way to end before it begins. Waiting operations and transactions go in the order they came (a ticket each): an
    /// operation that came before a waiting transaction goes before it, and one that came after waits for it, so neither
    /// a stream of operations nor a stream of transactions keeps the other out. Operations don't wait for each other
    /// here (the engine's locks order them).
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
        private int operations;
        private long nextTicket;
        // The tickets of the transactions and the operations waiting, the earliest first.
        private readonly SortedSet<long> transactionsWaiting = new SortedSet<long>();
        private readonly SortedSet<long> operationsWaiting = new SortedSet<long>();

        /// <summary>Waits until no transaction is open, no operation of another flow is under way, and nothing that came
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
                    while (!closed && (transactionOpen || operations > 0 || Before(operationsWaiting, ticket) || Before(transactionsWaiting, ticket)))
                    {
                        // Under the gate: a test's hook may only take note here, never wait.
                        Xod.Services.FaultInjection.Step("transaction-waits");
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

        /// <summary>Waits until no transaction is open and none that came before it waits, then counts an operation under
        /// way. False if <paramref name="timeoutMs"/> passed first (0: don't wait).</summary>
        internal bool TryBeginOperation(int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            lock (gate)
            {
                // Nothing open or waiting to order it against (operations don't wait for each other): it goes on, with no
                // ticket to keep. Every read and write comes here, so the common case stays cheap.
                if (closed || (!transactionOpen && transactionsWaiting.Count == 0))
                {
                    operations++;
                    return true;
                }

                long ticket = nextTicket++;
                operationsWaiting.Add(ticket);
                try
                {
                    while (!closed && (transactionOpen || Before(transactionsWaiting, ticket)))
                    {
                        // Under the gate: a test's hook may only take note here, never wait.
                        Xod.Services.FaultInjection.Step("operation-waits");
                        if (!WaitUntil(deadline))
                            return false;
                    }
                    operations++;
                    return true;
                }
                finally
                {
                    operationsWaiting.Remove(ticket);
                    Monitor.PulseAll(gate);
                }
            }
        }

        internal void EndOperation()
        {
            lock (gate)
            {
                if (--operations == 0)
                    Monitor.PulseAll(gate);
            }
        }

        /// <summary>The context closed: every wait ends at once, and the caller finds the context closed. Without it a
        /// transaction the close couldn't roll back (a close from inside a read) left every operation waiting to its
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

        /// <summary>How many operations are waiting for their turn now.</summary>
        internal int OperationsWaiting
        {
            get { lock (gate) return operationsWaiting.Count; }
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
