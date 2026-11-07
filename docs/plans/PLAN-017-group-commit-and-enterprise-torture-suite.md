# PLAN-017: Buffered Group-Commit Durability Mode & Enterprise Torture Suite

- **Task ID**: `TASK-022`
- **Fixes Issue**: [`ISS-022`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
By default, every single-row `db.Insert(item)`, `db.Update(item)`, or `db.Delete(item)` immediately serializes and atomically swaps the `.xpag` file on disk (`DurabilityMode.Immediate`). For high-write-throughput workloads (thousands of individual writes per second across concurrent threads), coalescing page flushes (`DurabilityMode.Buffered` + explicit or periodic `db.Flush()`) avoids redundant `.xpag` re-serialization while keeping all in-memory reads and B-Tree indexes 100% up-to-date.

## Design & Refactoring Steps
1. Add `public enum DurabilityMode { Immediate = 0, Buffered = 1 }` and `public DurabilityMode Durability { get; set; } = DurabilityMode.Immediate;` to `DatabaseOptions`.
2. Add `void Flush()` to `IXodContext`, `XodContext`, `IXodEngine`, and `XmlEngine`:
   - When `Durability == DurabilityMode.Buffered`, `IOService.Save(XFile)` records modified `XFile` instances in `dirtyBufferedFiles` without writing to disk on every single row mutation.
   - Calling `db.Flush()`, committing a transaction, calling `ClearCache()`/`GetStatistics()`, or disposing the context flushes all `dirtyBufferedFiles` and dirty `.xidx` indexes atomically to disk.
3. Expand `XodStressTests.cs` with all 4 Phase 4 enterprise concurrency, `.xwal` crash recovery, lock-retry, and buffered throughput torture tests.

## Verification Plan
- Run the complete `XodTests` + `XodStressTests` test suite (`28` tests) and verify 100% pass rate.
