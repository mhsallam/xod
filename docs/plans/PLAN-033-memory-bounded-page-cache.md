# PLAN-033: Page Cache Bounded by Memory, Not by Entry Count

- **Task ID**: `TASK-038`
- **Fixes Issue**: [`ISS-038`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement

20 indexed queries over 400k rows took 74 s on JSON and 100 s on Binary at 1 GB, against 4.95 s on
XML and 0.23 s on SQLite. The same metric at 250 MB was 8–10 ms.

## Root Cause

The parsed-page cache was capped at 1,024 **entries**. A page is not the same size in every format:
a Binary page is ~29 KB compressed and 512 KB once expanded, a JSON page is ~700 KB on disk. So:

- a 1 GB database has ~1,700 pages, more than the cap, on every format;
- the benchmark's `CategoryId = i % 200` scatters each category across the whole table, so a query
  touches nearly every page;
- each miss parses (JSON) or Brotli-decompresses and parses (Binary) a whole page to return one row.

Measured with a temporary counter: ~40 s of the 62 s JSON run and ~189 s of the 245 s Binary run
were inside page loading. With the cache unbounded, the same queries took 2.7 s (Binary) and 7.9 s
(JSON) — proof that cache thrash, not query planning, was the cost.

## Change

`CacheFile` is now the one place a parsed page enters the cache, and it evicts least-recently-used
pages until **both** fit:

- an entry cap (65,536), and
- a byte budget: a quarter of the memory available to the runtime, clamped to 64 MB – 4 GB.

Cost per page is `LastSavedSize`, with a full page (512 KB) assumed when a page has not been saved
yet and as the floor for Binary, whose on-disk size is compressed. The estimate does no file I/O:
the first version called `FileInfo` on every save, which made bulk insert 2.7× slower on its own.

## Verification (same machine, Binary, 1 GB flat)

| Metric | before | after |
| :--- | ---: | ---: |
| 20 indexed queries | 140,510 ms | **61 ms** |
| 1,000 PK lookups | 3,873 ms | 2,947 ms |
| Bulk insert 400k | 27,225 ms | 25,700 ms |
| Peak working set | 4,896 MB | 2,763 MB |

XML at 1 GB: indexed queries 4,953 ms (recorded) → 112 ms. JSON: 73,765 ms → 71 ms.
`XodCacheTests` (3 formats) pushes 20,000 rows through a 4 KB page size so eviction happens on
purpose, then re-reads, updates and deletes, and reopens cold to assert the data survived the
eviction cycle. Full suite: 40/40.

## Known limits

- The budget is a quarter of available memory, not a fixed number. On a small machine the working
  set still will not fit and the queries degrade again — the README should say so.
- Random primary-key lookups at 1 GB are still 0.4–2.9 s per 1,000 (SQLite: 9 ms). That is now the
  slowest operation and is filed separately; it is not caused by the cache policy.
- 250 MB numbers moved with the machine during measurement (~1.2× slower than the recorded v1.2.5
  medians), so no regression is claimed or hidden either way.
