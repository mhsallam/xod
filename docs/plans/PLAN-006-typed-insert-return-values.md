# PLAN-006: Return Strongly-Typed `T` from `Insert<T>` and `InsertOrUpdate<T>`

- **Task ID**: `TASK-011`
- **Fixes Issue**: [`ISS-011`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
In XOD 1.x, `Insert<T>(T item)` and `InsertOrUpdate<T>(T item)` returned `object` (which at runtime was a `Dictionary<string, object>` of primary keys).
In modern C#, developers expect `var todo = db.Insert(new ToDo { Title = "Test" });` to return `ToDo` (with its auto-generated `Id` populated) while existing code that ignores the return value (`db.Insert(new ToDo { ... });`) or assigns it to `object` continues to compile without changes.

## Design & Refactoring Steps
1. Change `IXodContext.Insert<T>(T item, bool lazyLoad = false)` and `XodContext.Insert<T>(T item, bool lazyLoad = false)` to return `T`.
2. Change `IXodContext.InsertOrUpdate<T>(T item, bool lazyLoad = false, UpdateFilter filter = null)` and `XodContext.InsertOrUpdate<T>(...)` to return `T`.
3. Change `InsertAsync<T>(T item, ...)` to return `Task<T>`.
4. Keep the non-generic `object Insert(object item, ...)` and `object Insert(Type type, object item, ...)` returning `object` (`GetPrimaryValues`) for backward compatibility with reflection/untyped callers.

## Verification Plan
- Add unit test `Typed_Insert_And_InsertOrUpdate_Return_Entity_With_Populated_Id` verifying `T` is returned directly with its auto-generated `Id` and child keys populated.
