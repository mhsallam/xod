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

            int timeoutMs = options.TransactionTimeoutMs > 0 ? options.TransactionTimeoutMs : 30000;
            if (!transactionGate.Wait(timeoutMs))
            {
                throw new TransactionException($"Timed out after {timeoutMs}ms waiting to acquire transaction lock.");
            }

            rwLock.EnterWriteLock();
            try
            {
                indexService.FlushDirtyIndexes();
                ioService.BeginBatch();
                activeTransactionThreadId = Environment.CurrentManagedThreadId;

                activeTransaction = new XodTransaction(
                    root,
                    onCommit: () =>
                    {
                        rwLock.EnterWriteLock();
                        try
                        {
                            ioService.EndBatch();
                            indexService.FlushDirtyIndexes();
                        }
                        finally
                        {
                            rwLock.ExitWriteLock();
                        }
                    },
                    onRollback: () =>
                    {
                        rwLock.EnterWriteLock();
                        try
                        {
                            ioService.DiscardBatch();
                            ioService.ClearCurrentCache();
                            itemsCacheService.ClearCurrentCache();
                            indexService.ClearAll();
                            autonumberService.Reset();
                        }
                        finally
                        {
                            rwLock.ExitWriteLock();
                        }
                    },
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
