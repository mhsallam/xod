# PLAN-022: Pre-Computed `TypeMetadata` & Single-Pass Read/Write Fast Paths (`v1.2.2`)

- **Task ID**: `TASK-027`
- **Fixes Issue**: [`ISS-027`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
Profiling the 250 MB (`100,000`-row) benchmark in `v1.2.1` revealed four per-row CPU & allocation overheads inside `XmlEngine` and `PropertyService`:
1. **Repeated LINQ Filtering & Sorting on Every Row (`800,000+` times per 100k insert)**:
   `ValidatePrimaryKeyTypes`, `ValidateRequiredProperties`, `Autonumber`, `ValidateUniqueAndPrimaryConstraints`, `GetPrimaryValues`, `Write`, `Read`, and `IndexService.IndexItem` each execute `.Where(...).OrderBy(...).ToList()`/`.ToArray()` on `propertyService.Properties(type.FullName)` on every single row.
2. **Per-Row `Guid.NewGuid()` & `ItemsCacheService` Overhead on Standalone Entities**:
   `XmlEngine.Read` calls `Guid.NewGuid()` (an OS CSPRNG syscall) and allocates an `ItemCache` entry in `ItemsCacheService` on every deserialized row, even when the entity type is a standalone table (`IsStandalone == true`) with no child or reference navigation properties that could ever reference `ItemsCacheService`.
3. **Quadratic `element.Element(prop.PropertyName)` Linked-List Scans in `XmlEngine.Read`**:
   Calling `element.Element(prop.PropertyName)` for each property walks the `XElement` child linked list from the beginning for every property ($O(P^2)$).
4. **Redundant Full-Entity Deserialization in `Delete` and `Update` on Standalone Tables**:
   `Delete` calls `Read(type, row, "*")` on every row before deleting it solely to inspect cascade-delete and parent-reference properties, even when the table has none.

## Design & Refactoring Steps
1. Add `TypeMetadata` in `PropertyService` (cached per `Type` during `LoadType`) with pre-built arrays and maps:
   - `PropertyInfoItem[] WriteProperties`
   - `PropertyInfoItem[] ReadProperties`
   - `Dictionary<string, PropertyInfoItem> ReadPropertyMap`
   - `PropertyInfoItem[] PrimaryKeyProperties`
   - `PropertyInfoItem[] UniqueProperties`
   - `PropertyInfoItem[] IndexedProperties`
   - `PropertyInfoItem[] RequiredNonAutoProperties`
   - `PropertyInfoItem[] AutonumberProperties`
   - `bool IsStandalone`, `bool HasAttributeProperties`, `bool HasDefaultValueProperties`, `bool HasCascadeDeleteOrParentProperties`
2. In `XmlEngine.Read`:
   - For standalone types without reference navigation properties, use a single-pass `foreach (var e in element.Elements())` with `meta.ReadPropertyMap` lookup and skip `Guid.NewGuid()` / `ItemsCacheService.Add`.
3. In `XmlEngine.Delete` and `XmlEngine.Update`:
   - Skip `Read(type, row, "*")` in `Delete` when `!meta.HasCascadeDeleteOrParentProperties`.
   - Use `meta` pre-computed property arrays across all write, validation, autonumber, and indexing paths.

## Verification Plan
- Run all 28 unit and torture tests and verify throughput improvements on the 250 MB (`100,000`-row) benchmark.
