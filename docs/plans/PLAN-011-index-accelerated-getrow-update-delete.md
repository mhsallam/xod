# PLAN-011: Index-Accelerated `GetRow`, `GetTree`, and `SelectItemsByExample`

- **Task ID**: `TASK-016`
- **Fixes Issue**: [`ISS-016`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
In XOD 1.x and XOD++ Phase 2:
- `Update(entity)`, `Delete(entity)`, `InsertOrUpdate(entity)`, and foreign-key hydration all call `GetRow(type, example)`, `GetTree(type, example)`, or `SelectItemsByExample(type, example)`.
- Those three methods build an XML template (`XElement test = Write(type, item, true, ...)`) and scan every `.xpag` file in the table using `test.ElementMatch(...)` (`O(N)` full table scan), completely bypassing `IndexService` even when looking up an entity by its `[PrimaryKey]`!
- On a 10,000-row table, updating or deleting 100 items performs `100 * 10,000 = 1,000,000` XML element comparisons!

## Design & Refactoring Steps
1. In `XmlEngine`, add a helper `TryFindRowCodesByIndex(Type type, object example, out IReadOnlyCollection<string> candidateCodes)`:
   - If `options.EnableIndexing` is enabled:
     - Check if the example object has a non-default single `[PrimaryKey]` value. If so, look up the exact row code(s) via `indexService.LookupExact(type, pkProp.PropertyName, pkVal)` in `O(log N)`!
     - Otherwise, if the example object specifies any non-default indexed/unique properties, intersect their `indexService.LookupExact` sets to narrow down the candidate row codes!
2. Update `GetRowByExampleObject`, `GetTree`, and `SelectItemsByExample` to use `TryFindRowCodesByIndex`:
   - When the example object only specifies indexed properties (such as a Primary Key lookup in `Update`, `Delete`, `InsertOrUpdate`, or ForeignKey resolution), resolve the matching `<Row>` directly via `GetRawData(code)` (`O(1)`) without scanning `.xpag` pages or constructing an `XElement test` template!

## Verification Plan
- Verify `Update`, `Delete`, and `InsertOrUpdate` on a 10,000-row table complete in milliseconds in `XodStressTests.cs`.
