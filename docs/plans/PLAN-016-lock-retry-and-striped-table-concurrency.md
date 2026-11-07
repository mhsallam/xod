# PLAN-016: Cross-Process Lock Retry Backoff & Per-Table Concurrency Striping

- **Task ID**: `TASK-021`
- **Fixes Issue**: [`ISS-021`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
1. When `DatabaseOptions.LockDatabase = true`, a second `XodContext` or process attempting to open the database while another short-lived context holds the `.lock` file fails immediately on the first attempt (`0 ms`) with `DatabaseLockedException`.
2. In `XmlEngine`, single-table writes acquire a database-wide exclusive lock (`rwLock.EnterWriteLock()`), blocking reads and writes on completely unrelated tables even though XOD stores each table in its own `.xtab`/`.xpag`/`.xidx` files.

## Design & Refactoring Steps
1. Add `public int LockRetryTimeoutMs { get; set; } = 0;` to `DatabaseOptions`:
   - In `IOService.AcquireDatabaseLock(int retryTimeoutMs)`, when `retryTimeoutMs > 0`, retry acquiring `{dbPath}.lock` with exponential backoff (starting at `5 ms` up to `50 ms` intervals) until `retryTimeoutMs` elapses before throwing `DatabaseLockedException`.
2. Add per-table `ReaderWriterLockSlim` striping in `XmlEngine`:
   - Maintain `ConcurrentDictionary<string, ReaderWriterLockSlim> tableLocks` keyed by `type.FullName`.
   - Use `GetTableLock(Type type)` so concurrent reads and writes on independent leaf tables can proceed safely while database-wide operations (`DropType`, `ChangePassword`, `ClearCaches`, `GetStatistics`) coordinate across locks.

## Verification Plan
- Add `Enterprise_MultiContext_LockRetry_And_Parallel_MultiTable_Concurrency` in `XodStressTests.cs`.
