# PLAN-012: Cross-Process Database File Locking

- **Task ID**: `TASK-017`
- **Fixes Issue**: [`ISS-017`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
If two separate OS processes (or two uncoordinated `XodContext` instances when file locking is enabled) open and mutate the same `.xod` database directory concurrently, they maintain separate in-memory caches (`IOService`, `AutonumberService`, `IndexService`) and can overwrite each other's files.

## Design & Refactoring Steps
1. Add `public bool LockDatabase { get; set; } = false;` to `DatabaseOptions` (opt-in or configurable so existing single-process multi-context tests continue to work, while production deployments can enforce exclusive process locking).
2. In `IOService`, when `LockDatabase == true`, acquire an exclusive `FileStream` lock on `{databaseFile}.lock` (`FileShare.None`) for the lifetime of the `XodContext` and release/delete it on `Dispose()`.
3. Throw a descriptive `DatabaseLockedException` (inheriting from `XodException`) if another process or context holds the lock.

## Verification Plan
- Add unit/stress test `Exclusive_Database_Lock_Prevents_Concurrent_Context_Writers` in `XodStressTests.cs`.
