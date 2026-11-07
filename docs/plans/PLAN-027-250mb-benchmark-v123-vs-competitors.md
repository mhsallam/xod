# PLAN-027: 250 MB Benchmark (`XOD++ v1.2.1` vs `v1.2.2` vs `v1.2.3` vs `SQLite` vs `LiteDB`)

- **Task ID**: `TASK-032`
- **Fixes Issue**: [`ISS-032`](../KNOWN-ISSUES.md)
- **Status**: Superseded by [PLAN-028](PLAN-028-fair-benchmark-harness-v2.md) - the v1 harness was not apples-to-apples (see ISS-033); v1.2.3 numbers measured with it (e.g. 100k bulk insert ~1.06-1.47 s, cold open + insert ~21-31 ms) are indicative only

## Objective
After implementing [`PLAN-025`](PLAN-025-zero-dom-utf8-span-reader-and-writer.md) and [`PLAN-026`](PLAN-026-lazy-xidx2-and-tail-page-append-io.md), execute the **250 MB (`100,000`-row) Benchmark** comparing:
1. **`XOD++ v1.2.1`** (`1078742`)
2. **`XOD++ v1.2.2`** (`bf7b400`)
3. **`XOD++ v1.2.3`** (`HEAD`)
4. **`SQLite (WAL)`** (`Microsoft.Data.Sqlite 9.0`)
5. **`LiteDB 5.0`** (`LiteDB 5.0.21`)

## Target Goals for `XOD++ v1.2.3`
- Beat **`LiteDB 5.0`** across **all 8 benchmark metrics** (`8/8`).
- Beat **`SQLite (WAL)`** on **Bulk Insert (`100k` rows)**, **Indexed Secondary Queries (`20 x 500` rows)**, **Table `Count()` + `Last<T>()`**, **Deep Pagination (`Skip 90k`)**, and **Total Disk Footprint** (`5/8` metrics), while closing the gap on random PK lookups and mutations to single-digit / low-double-digit milliseconds.
