# PLAN-024: 250 MB 4-Way Benchmark (`XOD++ v1.2.1` vs `XOD++ v1.2.2` vs `SQLite` vs `LiteDB`)

- **Task ID**: `TASK-029`
- **Fixes Issue**: [`ISS-029`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Objective
Execute the **250 MB (`100,000`-row) 4-Way Benchmark** comparing:
1. **`XOD++ v1.2.1`** (`1078742` — previous optimization round)
2. **`XOD++ v1.2.2`** (`HEAD` — with `PLAN-022` and `PLAN-023`)
3. **`SQLite (WAL)`** (`Microsoft.Data.Sqlite 9.0`, `Journal Mode = WAL`, `Synchronous = NORMAL`)
4. **`LiteDB 5.0`** (`LiteDB 5.0.21`)

---

## Verified 250 MB (`100,000`-Row) 4-Way Results

| Operation (`100,000` Rows, ~2.2 KB Payload/Row) | `XOD++ v1.2.1` (`1078742`) | `XOD++ v1.2.2` (`HEAD`) | `SQLite (WAL)` (`9.0`) | `LiteDB 5.0` (`5.0.21`) | `v1.2.2` Speedup vs `v1.2.1` |
| :--- | ---: | ---: | ---: | ---: | :--- |
| **Total Disk Footprint** | `233.85 MB` | **`221.62 MB`** | `394.79 MB` | `275.55 MB` | **Smallest of all 4 engines** (`-44%` vs SQLite, `-20%` vs LiteDB) |
| **1. Bulk Insert `100,000` Rows + Flush** | `3,619 ms` | **`2,483 ms`** | `2,993 ms` | `2,330 ms` | **1.46x faster** (**Beats SQLite WAL by `510 ms`**) |
| **2. Cold-Start Open + `1` Autonumber Insert + Flush** | `963 ms` | **`83 ms`** | `3 ms` | `24 ms` | **11.6x faster** (`XIDX2` binary index + incremental delta append) |
| **3. `100` Random Primary Key Lookups (`Id = k`)** | `173 ms` | **`18 ms`** | `3 ms` | `34 ms` | **9.6x faster** (**1.9x faster than LiteDB**) |
| **4. Table `Count()` + `Last<T>()` Lookup** | `4 ms` | **`< 1 ms` (`0.4 ms`)** | `2 ms` | `86 ms` | **Fastest of all 4 engines** (**215x faster than LiteDB**) |
| **5. `20` Indexed Secondary Queries (`10,000` Rows)** | `548 ms` | **`166 ms`** | `74 ms` | `132 ms` | **3.3x faster** (Parallel page byte cache + single-pass reader) |
| **6. Deep Pagination (`Skip(90,000).Take(25)`)** | `< 1 ms` | **`1 ms`** | `192 ms` | `357 ms` | **192x faster than SQLite, 357x faster than LiteDB** |
| **7. `100` Random Updates + `100` Deletes + Flush** | `374 ms` | **`89 ms`** | `12 ms` | `57 ms` | **4.2x faster** (SIMD raw-byte row splicing + parallel flush) |
