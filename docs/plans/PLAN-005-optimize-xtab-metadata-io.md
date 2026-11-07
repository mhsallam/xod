# PLAN-005: Optimize `.xtab` Table Metadata Disk I/O on Single-Row `Insert`

- **Task ID**: `TASK-010`
- **Fixes Issue**: [`ISS-010`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
In `XmlEngine.AddToPage`, every single `Insert` operation calls both `ioService.Save(pageFile)` AND `ioService.Save(tableFile)`.
Because a `.xpag` page holds up to `512 KB` of rows before becoming full, 99.9% of single-row inserts only append a `<Row>` to the existing active `.xpag` file and do not add a new `<Page>` to `.xtab`. Saving `.xtab` on every single insert doubles the number of disk writes and encrypt/compress cycles.

## Design & Refactoring Steps
1. Modify `IOService.OpenPageOrCreate(XFile table, out bool tableModified)` to report whether a new `<Page>` element was added to `table`.
2. In `XmlEngine.AddToPage`, track `bool tableDirty = tableModified` (or if the page transitioned to `full="true"`, or if a new `<Index>` element was added to `<Page>`).
3. Only call `ioService.Save(tableFile)` when `tableDirty == true`.

## Verification Plan
- Verify all existing tests pass and verify via unit test + benchmark that single-row `Insert` throughput improves while `.xtab` metadata remains accurate when pages are created or marked `full="true"`.
