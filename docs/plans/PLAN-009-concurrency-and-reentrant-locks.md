# PLAN-009: Multi-Threaded Concurrency & Re-Entrant Lock Hardening

- **Task ID**: `TASK-014`
- **Fixes Issue**: [`ISS-014`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
1. `XmlEngine` initializes `ReaderWriterLockSlim` with `LockRecursionPolicy.NoRecursion`.
   - Any re-entrant call (for example: `InsertOrUpdate` calling `Update`, `Delete` cascading into `Delete`, or a user's `BeforeAction`/`AfterAction` trigger querying the context during an `Insert`/`Update`/`Delete`) throws `LockRecursionException`!
2. Under high multi-threaded contention (e.g., 32 concurrent reader and writer threads), `ItemsCacheService` and `AutonumberService` need strict synchronization and snapshot isolation so concurrent enumerations never throw `InvalidOperationException: Collection was modified`.

## Design & Refactoring Steps
1. Change `XmlEngine.rwLock` to `new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion)`.
2. Audit all lock acquisitions in `XmlEngine` (`Select`, `Query`, `QueryExpression`, `Insert`, `InsertRange`, `InsertOrUpdate`, `Update`, `Delete`, `DropType`, `GetStatistics`, `ClearCache`) to ensure consistent read/write lock discipline.
3. Harden `ItemsCacheService` so all reads and writes to the per-session cache are synchronized under a lock.

## Verification Plan
- Add multi-threaded torture test `Concurrency_32_Threads_Simultaneous_Read_Write_Update_Delete_And_Triggers` in `XodStressTests.cs`.
