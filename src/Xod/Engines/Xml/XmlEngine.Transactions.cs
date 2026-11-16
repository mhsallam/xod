using System;
using System.Threading;
using Xod.Infra;
using Xod.Services;

namespace Xod.Engines.Xml
{
    public partial class XmlEngine
    {
        public IXodTransaction BeginTransaction()
        {
            // ISS-132: a transaction is its flow's (flowTransaction). This flow's own is open: a second can't begin.
            if (InThisFlowsTransaction)
            {
                throw new TransactionException("A transaction is already active on this database context.");
            }
            // Inside a write on this thread (a trigger), it would wait for that write to end; inside a read, for writes
            // the read holds back. Refused at once rather than after TransactionTimeoutMs.
            if (turnDepth.Value > 0)
            {
                throw new TransactionException("A transaction can't begin inside a write on the same thread (a trigger, for example): it would wait for that write to end. Begin it after the write has returned.");
            }
            if (engineReadDepth.Value > 0)
            {
                throw new XodException(InsideReadMessage);
            }

            ThrowIfWritesRefused();
            int timeoutMs = TransactionTimeoutMs;
            if (!turns.TryBeginTransaction(timeoutMs))
            {
                throw new TransactionException($"Timed out after {timeoutMs}ms waiting to acquire transaction lock.");
            }

            EnterSecond(EnterExclusiveLock, turns.EndTransaction);
            try
            {
                // Everything this context holds goes to disk first (with buffered durability, writes may wait in
                // memory): the transaction's before-images then hold it, and a rollback or a failed commit, which
                // forget every write still pending, forget only the transaction's.
                FlushToDisk();
                var countersAtBegin = autonumberService.Highs();
                ioService.BeginBatch();
                transactionBatchOpen = true;

                activeTransaction = new XodTransaction(
                    root,
                    exclusive: action =>
                    {
                        // The transaction's own commit or rollback, from whichever thread ends it: no turn.
                        EnterExclusiveLock();
                        try
                        {
                            action();
                        }
                        finally
                        {
                            ExitExclusiveLock();
                        }
                    },
                    onCommit: () =>
                    {
                        transactionBatchOpen = false;
                        ioService.EndBatch();
                        FaultInjection.Step("commit-written");
                        indexService.FlushDirtyIndexes();
                        FaultInjection.Step("commit-indexed");
                    },
                    onRollback: restoreFiles =>
                    {
                        // The batch is ended unwritten if the commit didn't get to it (its writes and deletes never
                        // reached the disk: ISS-164, ISS-166), the files of a commit undone go back, and the context
                        // forgets what it holds even if that failed: it must not show, or later write, a commit the
                        // disk doesn't hold.
                        try
                        {
                            if (transactionBatchOpen)
                            {
                                transactionBatchOpen = false;
                                ioService.DiscardBatch();
                            }
                            restoreFiles();
                        }
                        finally
                        {
                            // Whatever became of the files: a counter only rising keeps numbers unique.
                            autonumberService.RaiseTo(countersAtBegin);
                            ForgetEverythingHeld();
                        }
                        autonumberMarksPending = true;
                    },
                    onFaulted: message => transactionFault = message,
                    dropIndexFiles: indexService.DropIndexFiles,
                    onDispose: () =>
                    {
                        activeTransaction = null;
                        turns.EndTransaction();
                    });

                // Set here, in a method that isn't async, so it reaches the caller's flow and what it awaits or runs.
                flowTransaction.Value = activeTransaction;
                return activeTransaction;
            }
            catch
            {
                activeTransaction = null;
                turns.EndTransaction();
                throw;
            }
            finally
            {
                ExitExclusiveLock();
            }
        }
    }
}
