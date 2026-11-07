# PLAN-023: Binary `XIDX2` Index Persistence, Incremental Append Log & Lazy Reverse Index (`v1.2.2`)

- **Task ID**: `TASK-028`
- **Fixes Issue**: [`ISS-028`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
In `v1.2.1`, `.xidx` sidecar index files are serialized as XML (`XDocument`) containing one `<Entry key="..." codes="..." />` element per distinct key (`300,000` XML elements / `~16 MB` for `100,000` rows with 3 indexes). This causes three bottlenecks against `SQLite` and `LiteDB`:
1. **Full 300,000-Entry `.xidx` Rebuild on Single-Row or Small-Batch `Flush()`**:
   Inserting 1 row on cold start (`ColdOpenPlus1InsertMs`: `963 ms`) or updating/deleting 100 rows (`Update100Delete100Ms`: `374 ms`) marks the type dirty in `IndexService`, causing `FlushDirtyIndexes()` to rebuild and write the entire 300,000-entry XML `.xidx` file (`~480 ms`).
2. **Cold-Start `.xidx` XML Parsing & Eager Reverse-Index Allocation (`~450 ms`)**:
   `TryLoadFromDisk` checks all 424 `.xpag` file timestamps (`848` filesystem syscalls), parses `16 MB` of XML into `XDocument`, and eagerly builds `codeToIndexEntries` (`300,000` `List` allocations) even when no `Update` or `Delete` is executed.
3. **Temporary `HashSet<string>` Copy in `TryPlanExpression`**:
   For exact equality queries (`x => x.Id == id` or `x => x.CategoryId == cat`), `EvaluateIndexNode` allocates a new `HashSet<string>` and copies all matching codes via `UnionWith` instead of returning the `IReadOnlyCollection<string>` bucket directly.

## Design & Refactoring Steps
1. **Compact Binary `XIDX2` Format + Incremental Delta Append Log**:
   - Write `.xidx` using a binary framing (`XIDX2` header) directly from `propertyIndexes` (supporting transparent fallback reading of legacy XML `.xidx` files and encrypted `.xidx` files).
   - Track incremental index deltas (`Add` / `Remove`). When `FlushDirtyIndexes()` runs on an existing unencrypted `XIDX2` file and the pending delta count is below the compaction threshold (`4,096` ops), append only the delta records (`~40 bytes` per op) to the tail of `.xidx` in `< 1 ms`.
   - **Superseded (2026-11-06, `PLAN-041`):** a threshold on one flush made every 10,000-row batch (30,000 deltas) rewrite the whole index, and let small flushes append without limit. The log is now folded only when it would outgrow its snapshot (`IndexService.DeltaLog`), and on a clean close when it exceeds 256 KB.
2. **Lazy Reverse Index (`codeToIndexEntries`) & Fast Staleness Check**:
   - In `TryLoadFromDisk`, check `.xtab` and the tail `.xpag` file timestamp in $O(1)$ instead of all 424 `.xpag` files.
   - Defer building `codeToIndexEntries` for disk-loaded types until `RemoveItemCode` (`Update`/`Delete`) is actually called.
3. **Zero-Copy Exact Index Plan**:
   - Add `TryPlanExpression<T>(..., out IReadOnlyCollection<string> candidateCodes, out bool isFullyCovered)` so single-predicate equality queries return the underlying index bucket with zero copying.

## Verification Plan
- Verify all 28 unit and stress tests (including `.xidx` cold-start and crash self-healing tests) pass and measure `ColdOpenPlus1InsertMs`, `Update100Delete100Ms`, and `BulkInsert100kMs` on the 250 MB benchmark.
