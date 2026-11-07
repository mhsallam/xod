# PLAN-021: 250 MB 4-Way Benchmark (`xod2` Phase 4 vs `xod2` Phase 5 vs `SQLite` vs `LiteDB`)

- **Task ID**: `TASK-026`
- **Fixes Issue**: [`ISS-026`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Objective
Benchmark a **~250 MB database (`100,000` records, ~2.35 KB per record)** across four engines in `Release` mode (`.NET 9.0`):
1. **`XOD++ v2.0` (`d11e565` — Phase 4)**
2. **`XOD++ v2.1` (`HEAD` — Phase 5 with `PLAN-018`, `PLAN-019`, `PLAN-020`)**
3. **`SQLite` (`Microsoft.Data.Sqlite 9.0` with `WAL` mode & secondary indexes)**
4. **`LiteDB` (`LiteDB 5.0.21` pure .NET BSON embedded document store)**

## Results (`100,000` Records / ~234–395 MB)

| Workload / Metric | `XOD++ v2.0` (`d11e565`) | `XOD++ v2.1` (`HEAD`) | `SQLite (WAL)` | `LiteDB 5.0` | `XOD++ v2.1` Gain vs `v2.0` |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Database Disk Size** (`100k` rows + 3 indexes) | `233.84 MB` (`424` pages) | **`233.85 MB`** (`424` pages) | `394.79 MB` (`1` file + WAL) | `275.55 MB` (`1` file) | **Smallest footprint** (`40.8%` smaller than SQLite, `15.1%` smaller than LiteDB) |
| **1. Bulk Insert `100,000` Rows + Flush** | `4,783 ms` (`20,907/s`) | **`3,619 ms`** (`27,632/s`) | `2,993 ms` (`33,411/s`) | `2,330 ms` (`42,918/s`) | **1.32x faster** (`24.3%` faster; within `1.21x` of native C SQLite) |
| **2. Cold-Start Open + `.xidx` Load + 1 Autonumber Insert** | `2,676 ms` | **`963 ms`** | `3 ms` | `24 ms` | **2.78x faster** |
| **3. 100 Random PK Point Lookups** (across `424` pages) | `190 ms` | **`173 ms`** | `3 ms` | `34 ms` | **1.10x faster** |
| **4. `Count<T>()` + `Last<T>()` on `100,001` Rows** | `2,593 ms` | **`4 ms`** | `2 ms` | `86 ms` | **648.3x faster than v2.0** & **21.5x faster than LiteDB** |
| **5. 20 Indexed Category Queries** (`10,000` rows hydrated) | `16,893 ms` | **`548 ms`** | `74 ms` | `132 ms` | **30.8x faster than v2.0** |
| **6. Deep Cursor Pagination (`Skip 90,000, Take 25`)** | `840 ms` | **`< 1 ms` (`0.4 ms`)** | `192 ms` | `357 ms` | **>840x faster than v2.0, >192x faster than SQLite, >357x faster than LiteDB** |
| **7. 100 Random PK Updates + 100 Deletes + Flush** | `564 ms` | **`374 ms`** | `12 ms` | `57 ms` | **1.51x faster than v2.0** |
| **Total Post-Insert Workload Time** (Ops 2–7 Combined) | `23,756 ms` | **`2,062 ms`** | `286 ms` | `690 ms` | **11.52x faster overall post-insert workload** |
