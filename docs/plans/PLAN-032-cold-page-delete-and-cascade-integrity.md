# PLAN-032: Cold-Page Deletes and Cascade Integrity (JSON / Binary)

- **Task ID**: `TASK-037`
- **Fixes Issue**: [`ISS-037`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement

Deleting a parent entity on JSON or Binary storage removed only the parent. Its `[Children]` rows
stayed on disk and `Delete` returned `true`. Nothing failed, nothing logged.

The 40k relational benchmark caught it first (764 orphan `OrderLine` rows), the same run was then
refused by the harness's own count validation, and `XodIntegrityTests` reproduced it at 200
customers.

## Root Cause

`Delete` resolves children by **item code** (`pageCode.rowCode`) and calls
`IOService.TryDeleteRow`. That method answers from three places, in order:

1. the parsed-file cache (`cachedFiles`) — hit, row deleted;
2. the raw-page cache (`RawPages.TryGetRowFromRawPage`) — **XML only**: the function returns
   `false` for any other format, because it parses `<Row code="...">` text out of the page bytes;
3. otherwise `false`.

On XML, the row text is parseable, so a cold page still deletes. On JSON and Binary there is no raw
fallback and no load-on-demand, so a child page that was not already cached was reported as
"row not found" — which the caller cannot distinguish from a genuinely missing row. The parent row
was always cached (that is how it was found), so the parent deleted cleanly and the children
silently did not.

This is the "perform, then observe" failure in the small: a delete that only works when the right
cache is warm.

## Change

`IOService.TryDeleteRow` now loads a page through the normal file path (`OpenFileOrCreate`) before
reporting a miss:

```csharp
XFile coldPage = OpenFileOrCreate(pageFileName, type: null, openOnly: true);
if (coldPage != null)
{
    deletedRow = coldPage.GetRowByCode(rowCode);
    if (deletedRow != null)
    {
        coldPage.RemoveRow(deletedRow);
        pageEmpty = !coldPage.Rows().Any();
        Save(coldPage);
        return true;
    }
}
```

One owner for the delete: the page is loaded, the row is removed and the file saved through the
same path the rest of the engine uses. No caller changes, no format special-casing.

## Verification

- `XodIntegrityTests` — 9 cases: a 3-level graph, deletes of shell and hydrated entities, every
  format, cold reopen, exact counts, surviving graphs still hydrate, idempotent double delete.
  Before the fix: 6 failing. After: 9/9.
- 40k relational benchmark (200k orders, 800k lines) on JSON and Binary: the harness's cascade-count
  validation now passes for both (`cascadeDelete100Ms` 2,421 ms JSON / 1,839 ms Binary). It failed
  before with `cascade counts (39900, 199500, 798764)`.
- Full suite: 37/37 tests, no skips.

## Known debt

The 40k cascade is correct but slow (1.8–2.4 s for 100 customer deletions) because each cold child
page is loaded and saved individually. That is the price of correctness here; batching the loads is
a separate performance task and is not required for the 2.0 gate.
