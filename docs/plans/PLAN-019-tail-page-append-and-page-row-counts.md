# PLAN-019: `O(1)` Tail-Page Append, Immediate Full-Page Flush & Page Row-Count Metadata

- **Task ID**: `TASK-024`
- **Fixes Issue**: [`ISS-024`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
When scaling a table to 250 MB (`~100,000` rows across `~450` `.xpag` pages of 512 KB each), three `O(Pages)` bottlenecks emerge:
1. **Quadratic `<Page>` Element Scans During Insert**: `IOService.OpenPageOrCreate` scans `table.Pages().FirstOrDefault(s => full != "true")` from the first page on every row, and `XmlEngine.AddToPage` scans `tableFile.Pages().FirstOrDefault(...)` again on every row. Across 450 pages and 100,000 rows, this performs ~45,000,000 redundant XML attribute checks.
2. **Unbounded RAM Growth During 250 MB `InsertRange`**: `dirtyBatchFiles` holds every modified `XFile` in memory until `EndBatch()`. On a 250 MB insert, all 450 `.xpag` `XDocument` DOMs remain in `dirtyBatchFiles` simultaneously instead of flushing each `.xpag` page as soon as it becomes `full="true"`.
3. **Full `.xpag` File Loading on `Select(skip, take)` and `Count<T>()`**: `Select(skip: 90000, take: 25)` opens and parses 400 `.xpag` files (~225 MB of XML) just to count and skip 90,000 `<Row>` elements.

## Design & Refactoring Steps
1. **Tail-First Page Lookup**: In `IOService.OpenPageOrCreate`, check the last `<Page>` element in `.xtab` first (`table.Pages().LastOrDefault(...)`) and return the matched `XElement pageElement` directly via an `out XElement pageElement` parameter so `AddToPage` does not re-scan `tableFile.Pages()`.
   - **Correction (2026-11-06, `PLAN-041`):** this lookup was not O(1). `Elements("Page").LastOrDefault()` enumerates every page, so each insert stayed O(pages), about 2.3 ns per page. `IOService.FindAppendPage` now reads the tail through `XContainer.LastNode`.
2. **Immediate Flush of Full Pages in Batch Mode**: When a `.xpag` page reaches `pageSize` and is marked `full="true"` inside `AddToPage`, flush that full page to disk (`ioService.FlushFullPageInBatch(pageFile)`) so `dirtyBatchFiles` only ever retains the single active tail page.
3. **Page `count="N"` Metadata in `.xtab`**: Maintain `count` attribute on each `<Page file="..." full="..." count="N" />` element in `.xtab`. Use this `count` metadata in:
   - `Select(Type type, int skip, int take, ...)` to skip whole `.xpag` files in `O(1)` without opening or parsing them from disk when `skip >= pageRowCount`!
   - `Count<T>()` (when `query == null`) to sum page counts directly from `.xtab` without loading `.xpag` files!

## Verification Plan
- Verify multi-page accuracy after inserts/deletes and benchmark `Select(skip, take)`, `Count<T>()`, and 250 MB `InsertRange`.
