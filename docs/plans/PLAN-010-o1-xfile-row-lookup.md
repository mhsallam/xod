# PLAN-010: `O(1)` In-Memory Row Lookup Index in `XFile`

- **Task ID**: `TASK-015`
- **Fixes Issue**: [`ISS-015`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
1. When the B-Tree index resolves a query to a row code `"pageCode.rowCode"` in `O(log N)`, `XmlEngine.GetRawData` still locates the `<Row>` element inside `pageFile` via a linear scan:
   ```csharp
   file.Rows().FirstOrDefault(s => s.Attribute("code")?.Value == nameParts[1]);
   ```
   With hundreds or thousands of rows per `.xpag` page, retrieving `K` rows becomes `O(K * RowsPerPage)` instead of `O(K)`.
2. Similarly, `IOService.GetItemPage(Type type, string code)` scans every `.xpag` file in the table linearly when hydrating child collections.

## Design & Refactoring Steps
1. Enhance `XFile` (`src/Xod/Models.cs`) with a lazily-populated, automatically maintained `Dictionary<string, XElement> rowMap` keyed by the `<Row code="...">` attribute:
   - Expose `XFile.GetRowByCode(string rowCode)` (`O(1)` lookup), `XFile.AddRow(string rowCode, XElement rowElement)`, and `XFile.RemoveRow(string rowCode, XElement rowElement)`.
   - Invalidate/rebuild `rowMap` when `ReplaceDocument` is called.
2. Update `XmlEngine.GetRawData`, `AddToPage`, and `Delete` to use `XFile.GetRowByCode`, `AddRow`, and `RemoveRow`.
3. Update `XmlEngine.Read` when hydrating collection elements (`<Employee Id="1">pageCode.rowCode</Employee>`) to extract the `pageCode` directly from the stored `pageCode.rowCode` string instead of scanning all pages via `ioService.GetItemPage`!

## Verification Plan
- Add scale torture test `Scale_10000_Records_MultiPage_Point_And_Range_Lookups` in `XodStressTests.cs`.
