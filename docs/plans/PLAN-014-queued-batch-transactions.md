# PLAN-014: Queued Concurrent Transactions & In-Memory Batch-on-Commit

- **Task ID**: `TASK-019`
- **Fixes Issue**: [`ISS-019`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
1. **Concurrent `BeginTransaction()` Contention**: In `XmlEngine.BeginTransaction()`, a single `activeTransaction` field is checked without queuing; if two threads sharing an `XodContext` call `BeginTransaction()` concurrently, the second thread immediately throws `TransactionException("A transaction is already active")`.
2. **Unbuffered Writes Inside Transactions**: `BeginTransaction()` does not activate `ioService.BeginBatch()`. Every `Insert`, `Update`, or `Delete` inside a transaction immediately serializes and writes `.xpag` files to disk, only to overwrite them again on the next statement or restore them from byte snapshots on `Rollback()`.

## Design & Refactoring Steps
1. Add `SemaphoreSlim transactionGate = new SemaphoreSlim(1, 1)` (with configurable `DatabaseOptions.TransactionTimeoutMs = 30000`) in `XmlEngine`:
   - Calling `BeginTransaction()` waits on `transactionGate` so concurrent threads queue up fairly instead of throwing `TransactionException`.
2. Connect `BeginTransaction()` with `ioService.BeginBatch()`:
   - On `BeginTransaction()`, invoke `ioService.BeginBatch()` so all file mutations within the transaction stay buffered in `dirtyBatchFiles` in memory until `Commit()`.
   - On `Rollback()`, discard uncommitted `dirtyBatchFiles` via `ioService.DiscardBatch()`, restore any pre-transaction files, and release `transactionGate`.
   - On `Commit()`, flush all `dirtyBatchFiles` atomically via `ioService.EndBatch()`, flush dirty `.xidx` indexes, and release `transactionGate`.

## Verification Plan
- Add `Enterprise_Concurrent_Transactions_Queue_And_Batch_Commit_Across_16_Threads` in `XodStressTests.cs`.
