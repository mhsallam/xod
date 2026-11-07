# PLAN-001: Decouple `LazyLoad` Read Option from Write Persistence

- **Task ID**: `TASK-006`
- **Fixes Issue**: [`ISS-006`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
In XOD 1.x, `XmlEngine.Create` and `XmlEngine.Write` computed:
```csharp
lazyLoad = lazyLoad || LazyLoad;
```
and inside `WriteProperty`, when `lazyLoad == true`, reference properties and child collections were completely skipped during serialization! Consequently, if a developer initialized `new XodContext(path, options: new DatabaseOptions { LazyLoad = true })` (intending to lazy-load navigation properties when reading), calling `db.Insert(order)` silently dropped all child items and navigation references on write.

## Design & Refactoring Steps
1. Rename the internal parameter on `Write` and `WriteProperty` from `bool lazyLoad` to `bool shallowWrite` (used only internally when building a scalar-only comparison element for `GetRow` / `SelectItemsByExample`).
2. Never conflate `this.LazyLoad` (the read-time option on `DatabaseOptions` / `IXodEngine`) with `shallowWrite` during `Create`, `AddToPage`, or `UpdateItem`, unless the caller explicitly passed `shallowWrite: true`.
3. Keep the optional `bool lazyLoad = false` parameter on `Insert` for 100% source compatibility with XOD 1.x callers, where passing `lazyLoad: true` explicitly to `Insert(item, lazyLoad: true)` performs a shallow insert of the root entity only, while `DatabaseOptions.LazyLoad = true` only affects read operations (`Read`).

## Verification Plan
- Add unit test `LazyLoad_Option_On_DatabaseOptions_Does_Not_Drop_Relationships_On_Insert` verifying that a database opened with `LazyLoad = true` still persists 1-M and 1-Shared relationships on `Insert`, lazy-loads them when `include: ""` or default is used, and eagerly loads them when `include: "*"` or `include: "Employees"` is requested.
