# PLAN-007: Persistent `.xidx` Sidecar B-Tree Index Storage for Cold-Start Queries

- **Task ID**: `TASK-012`
- **Fixes Issue**: [`ISS-012`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
`IndexService` maintains sorted B-Tree / range indexes in memory, but on a cold database open (`new XodContext(path)`), `EnsureIndexHydrated(type)` must open and parse all `.xpag` files of that table to rebuild the in-memory index. For multi-page tables, cold-start point queries (`db.Find<User>(u => u.Email == "...")`) shouldn't have to read every `.xpag` file.

## Design & Refactoring Steps
1. Add persistent index serialization to `IndexService` using a per-table sidecar file `{Type.FullName}.xidx` (serialized via `IOService` so it automatically benefits from the active `StorageFormat` — XML, JSON, or Binary — as well as AES-256-GCM encryption and `XodTransaction` copy-on-write snapshots!).
2. Structure `{Type.FullName}.xidx` as an `XDocument`:
   ```xml
   <TableIndex type="Namespace.TypeName">
     <Property name="Email">
       <Entry key="alice@example.com" codes="page1.row1" />
     </Property>
   </TableIndex>
   ```
3. On cold start (`EnsureIndexHydrated`), if `{Type.FullName}.xidx` exists on disk, load the B-Tree directly from `{Type.FullName}.xidx` without opening any `.xpag` files! Only fall back to scanning `.xpag` files if `.xidx` does not exist yet (e.g. legacy XOD 1.x database), and then save `.xidx`.
4. Update `XodSecurityService` and `XodMigrator` to include `*.xidx` files alongside `.xod`, `.xtab`, and `.xpag`.

## Verification Plan
- Add unit test `Persistent_Xidx_Index_File_Is_Created_And_Used_On_Cold_Start` verifying that `{Type.FullName}.xidx` is created on disk, encrypted/migrated properly, and used on a fresh `XodContext` instance.
