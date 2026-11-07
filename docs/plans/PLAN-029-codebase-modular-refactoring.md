# PLAN-029: Codebase Modular Refactoring & Separation of Concerns

- **Task**: `TASK-034`
- **Issue**: `ISS-034`
- **Status**: **Implemented**
- **Target**: `net8.0`, `net9.0`
- **Safety**: 100% backward-compatible (zero breaking changes for public consumers `using Xod;`)

---

## 1. Problem Statement & Motivation
Prior to this refactoring, core XOD++ subsystems were concentrated in oversized monolithic files:
- `XmlEngine.cs`: ~3,749 lines containing queries, mutations, relation traversal, constraint validation, password security, stats, and transactions.
- `IOService.cs`: ~2,293 lines combining low-level SIMD/zero-DOM byte slicing, page file caching, multi-process file locking, and crash recovery.
- `IndexService.cs`: ~1,614 lines containing in-memory B-Trees, binary `XIDX2` serialization, expression-tree query planning, and radix normalization.
- `Models.cs`: ~525 lines mixing schema definitions, caching, transaction models, runtime metadata, and `XFile`.
- `Attributes.cs` (~194 lines) and `Exceptions.cs` (~86 lines): multiple unrelated classes jammed together in single files.

This structure reduced maintainability, made navigating code difficult, and increased merge conflict risks.

---

## 2. Refactoring Architecture

The refactoring preserved public API signatures and namespaces (`namespace Xod`) while partitioning monoliths into domain directories and partial classes:

### Phase 1: Attributes, Enums, & Exceptions
- `src/Xod/Attributes/`: 12 individual files (`PrimaryKeyAttribute.cs`, `ForeignKeyAttribute.cs`, `ParentKeyAttribute.cs`, `ChildrenAttribute.cs`, etc.).
- `src/Xod/Enums/`: `ValuePosition.cs`, `CascadeOptions.cs`, `CryptoMethod.cs`, `StorageFormat.cs`.
- `src/Xod/Exceptions/`: `XodException.cs`, `ValidationExceptions.cs`, `StorageExceptions.cs`.

### Phase 2: Configuration, Metadata, & Storage Models
- `src/Xod/Storage/XFile.cs`: Self-contained `XFile` wrapper.
- `src/Xod/Configuration/`: `DatabaseOptions.cs`, `DatabaseStatistics.cs`, `TriggerEventArgs.cs`, `UpdateFilter.cs`.
- `src/Xod/Metadata/`: `TypeMetadata.cs`, `PropertyInfoItem.cs`.
- `src/Xod/Storage/Models/`: `TableSchemaModels.cs`, `RowModels.cs`, `TrackingModels.cs`, `CacheModels.cs`.

### Phase 3: Storage Subsystem (`IOService`)
- `IOService.cs`: Core lifecycle, batching, properties (~188 lines).
- `IOService.Locks.cs`: Cross-process file locking (~44 lines).
- `IOService.Files.cs`: File-level caching, atomic saving, `.tmp` crash recovery (~373 lines).
- `IOService.RawPages.cs`: Zero-DOM UTF-8 raw page parsing & SIMD row slicing (~1,313 lines).
- `RawPageBuffer.cs`: Dedicated raw page buffer abstraction (~142 lines).
- `PageBufferPool.cs`: ArrayPool buffer management (~44 lines).

### Phase 4: Indexing Subsystem (`IndexService`)
- `IndexService.cs`: In-memory B-Tree index maps & CRUD operations (~443 lines).
- `IndexService.Persistence.cs`: Binary `XIDX2` disk format & incremental WAL append log (~597 lines).
- `IndexService.QueryPlan.cs`: Expression tree query planning & range queries (~278 lines).
- `IndexService.Normalization.cs`: Radix/binary sortable value normalization (~104 lines).
- `UnhydratedIndexBlock.cs`: Framed lazy hydration record (~12 lines).

### Phase 5: Engine Subsystem (`XmlEngine`)
- `XmlEngine.cs`: Core engine fields, constructor, table-locking helpers, and `Dispose()` (~280 lines).
- `XmlEngine.Queries.cs`: `Select`, `Count`, `Query`, `QueryExpression`, `QueryByExample`, `Find`, `First`, `Last`, `Read` (~1,176 lines).
- `XmlEngine.Mutations.cs`: `Insert`, `InsertRange`, `InsertOrUpdate`, `Update`, `Delete`, `DropType`, `Write`, `AddToPage` (~990 lines).
- `XmlEngine.Relations.cs`: Relationship traversal, foreign keys, cascade deletes, parent-child mapping (~418 lines).
- `XmlEngine.Validation.cs`: Primary key data types, unique constraints, and required fields (~232 lines).
- `XmlEngine.Maintenance.cs`: Statistics, password changes, encryption toggle, and cache clearing (~202 lines).
- `XmlEngine.Transactions.cs`: ACID transaction creation & commit/rollback coordination (~79 lines).

---

## 3. Verification & Results
- **Compilation**: `dotnet build` succeeds with zero errors and zero warnings across `net8.0` and `net9.0`.
- **Test Suite**: `dotnet test` passes 28 of 28 tests (`XodStressTests` and unit tests).
- **Public API**: Zero breaking changes. All public classes remain in root `namespace Xod` or their original interfaces.
- **Line Count**: Monolithic 3,749-line file eliminated; all files now cleanly partitioned and cohesive.
