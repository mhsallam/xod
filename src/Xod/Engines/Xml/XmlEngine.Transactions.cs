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
            if (activeTransaction != null && activeTransaction.IsActive &&
                activeTransactionThreadId == Environment.CurrentManagedThreadId)
            {
                throw new TransactionException("A transaction is already active on this database context.");
            }

            ThrowIfWritesRefused();
            int timeoutMs = options.TransactionTimeoutMs > 0 ? options.TransactionTimeoutMs : 30000;
            if (!transactionGate.Wait(timeoutMs))
            {
                throw new TransactionException($"Timed out after {timeoutMs}ms waiting to acquire transaction lock.");
            }

            EnterSecond(() => EnterWrite(rwLock.EnterWriteLock), () => transactionGate.Release());
            try
            {
                // Everything this context holds goes to disk first (with buffered durability, writes may wait in
                // memory): the transaction's before-images then hold it, and a rollback or a failed commit, which
                // forget every write still pending, forget only the transaction's.
                FlushToDisk(caughtUp: true);
                var countersAtBegin = autonumberService.Highs();
                ioService.BeginBatch();
                transactionBatchOpen = true;
                activeTransactionThreadId = Environment.CurrentManagedThreadId;

                activeTransaction = new XodTransaction(
                    root,
                    exclusive: action =>
                    {
                        EnterWrite(rwLock.EnterWriteLock);
                        try
                        {
                            action();
                        }
                        finally
                        {
                            rwLock.ExitWriteLock();
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
                        // The batch is ended unwritten if the commit didn't get to it, the files go back, and the
                        // context forgets what it holds even if that failed: it must not show, or later write, a
                        // commit the disk doesn't hold.
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
                    onDispose: () =>
                    {
                        activeTransaction = null;
                        activeTransactionThreadId = 0;
                        transactionGate.Release();
                    });

                return activeTransaction;
            }
            catch
            {
                activeTransaction = null;
                activeTransactionThreadId = 0;
                transactionGate.Release();
                throw;
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }
    }
}
