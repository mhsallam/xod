# PLAN-013: Engine-Level Streaming Pagination & Comprehensive Torture Test Suite

- **Task ID**: `TASK-018`
- **Fixes Issue**: [`ISS-018`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
1. Calling `db.Select<T>().Skip(5000).Take(20)` in XOD 1.x / Phase 2 deserializes all `10,000` rows in the table into `T` objects before LINQ `.Skip(5000).Take(20)` discards `9,980` of them!
2. To prove `xod2` is ready for real production workloads, we need a comprehensive **Torture, Concurrency, Crash-Recovery, Scale & Fuzz Test Suite** (`XodStressTests.cs`).

## Design & Refactoring Steps
1. Add `Select<T>(int skip, int take, bool backward = false, string include = "*")` to `IXodContext` / `XodContext` / `IXodEngine` / `XmlEngine`:
   - Skip `<Row>` elements at the XML/storage cursor level *before* calling `Read(type, row, include)`, so `Select<T>(skip: 9000, take: 25)` only deserializes `25` entities instead of `9,025`!
2. Create `tests/Xod.Tests/XodStressTests.cs` covering all 6 torture categories:
   - **Scale & Multi-Page Split Torture**: Insert `5,000+` entities across multiple `.xpag` pages (using a small page size threshold to force dozens of page splits), then benchmark `O(1)` point lookups, `O(log N)` updates/deletes, and streaming `Select(skip, take)` pagination.
   - **Multi-Threaded Concurrency Torture**: `32` concurrent threads performing simultaneous `Insert`, `Update`, `Delete`, `Find`, `Query`, and re-entrant trigger reads.
   - **Crash-Recovery & Fault-Injection Torture**: Simulate leftover `.tmp` files and out-of-sync `.xidx` files on disk and verify automatic self-healing on startup.
   - **Fuzz & Boundary Torture**: Extreme strings (100 KB+, Unicode/Emoji/RTL, XML special characters `&<>'"`), `decimal.MinValue`/`MaxValue`, `DateTime` extremes, deep object graphs, and circular references.
   - **Soak / Endurance Churn Torture**: Hundreds of rapid insert-update-delete-transaction rollback cycles verifying zero row drift and zero index drift.

## Verification Plan
- Run the full test suite (`XodTests` + `XodStressTests`) and verify 100% pass rate.
