# PLAN-020: Streaming Backward Page Traversal & `O(1)` LRU Page Cache

- **Task ID**: `TASK-025`
- **Fixes Issue**: [`ISS-025`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
1. **`Last<T>()` and Cold-Start `AutonumberService` Load the Entire 250 MB Table**:
   In `XmlEngine.SelectItems`, `backward == true` executes:
   ```csharp
   var rows = tableFile.Pages()
       .Where(...)
       .SelectMany(s => ioService.OpenFileOrCreate<TablePage>(...).Rows());
   if (backward)
       rows = rows.Reverse();
   ```
   Because `Enumerable.Reverse()` buffers the entire `SelectMany` sequence before yielding the first element, calling `db.Last<T>()` — or inserting 1 new row on cold start when `AutonumberService` calls `FindLastDelegate = t => Last(t)` — opens and parses **every single `.xpag` file in the 250 MB table (all 450 files!)** just to inspect the last row of the last page!
2. **Linear Cache Lookup & `O(C log C)` Eviction in `IOService`**:
   `IOService.cachedFiles` is a `List<FileCache>` (size 256) that performs a linear `FirstOrDefault` scan on every call and sorts the entire list (`OrderBy(s => s.LastCheckout)`) on every cache eviction.

## Design & Refactoring Steps
1. In `XmlEngine.SelectItems` and `XmlEngine.Select(skip, take)`:
   - When `backward == true`, reverse the `<Page>` sequence first (`pages.Reverse()`) and reverse each page's `<Row>` sequence (`pageFile.Rows().Reverse()`).
   - Now `db.Last<T>()` (`SelectItems(..., backward: true).FirstOrDefault()`) only opens the **single last `.xpag` file** (`O(1)` page read instead of `O(Pages)` full-database read)!
2. In `IOService`, replace `List<FileCache> cachedFiles` with an `O(1)` dictionary (`Dictionary<string, FileCache>`) for instant case-insensitive lookups and lightweight LRU eviction.

## Verification Plan
- Verify `Last<T>()`, `FindLast<T>()`, `SelectBackward<T>()`, and cold-start `Insert` on a 250 MB multi-page database complete in milliseconds without scanning historical pages.
