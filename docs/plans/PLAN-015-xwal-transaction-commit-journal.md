# PLAN-015: Native Write-Ahead Commit Journal (`.xwal`) for Multi-Table Crash Recovery

- **Task ID**: `TASK-020`
- **Fixes Issue**: [`ISS-020`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
While `PLAN-008` made individual `.xpag`/`.xtab`/`.xidx` file writes atomic via `.tmp` + `File.Move`, a multi-table transaction that modifies `Orders.xpag`, `OrderItems.xpag`, and `Accounts.xpag` must flush multiple files during `Commit()`. If power is lost or the process terminates after flushing `Orders.xpag` but before flushing `Accounts.xpag`, the database would have partial transaction state across tables.

## Design & Refactoring Steps
1. Enhance `XodTransaction` and `IOService` with an XOD-native **Transaction Journal (`data.xwal`)**:
   - Before `Commit()` flushes any buffered files to disk, `XodTransaction.WriteJournal(walPath)` atomically writes a `.xwal` XML journal recording the pre-transaction state of every file being modified or created in the transaction.
   - Next, `ioService.EndBatch()` and `indexService.FlushDirtyIndexes()` atomically flush all modified `.xpag`, `.xtab`, and `.xidx` files.
   - Once all files in the transaction are safely on disk, `XodTransaction` deletes `data.xwal`.
2. In `IOService.RecoverInterruptedWrites()`, check for any leftover `*.xwal` file on database startup:
   - If a `.xwal` file is present (indicating an interrupted multi-file commit), parse the `.xwal` entries, restore original file bytes, delete newly-created files from the aborted commit, and remove the `.xwal` file before any table or index is loaded.

## Verification Plan
- Add fault-injection test `Enterprise_Xwal_Journal_Recovers_Interrupted_MultiTable_Transaction_On_Startup` in `XodStressTests.cs`.
