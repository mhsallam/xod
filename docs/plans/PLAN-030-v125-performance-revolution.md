# PLAN-030: XOD++ v1.2.5 Performance Revolution (O(1) Row Slots, Tombstones & Cascade Pipelining)

- **Task**: `TASK-035`
- **Issue**: `ISS-035`
- **Status**: Implemented
- **Target**: `net8.0`, `net9.0`
- **Safety**: 100% backward-compatible (zero breaking changes for public consumers `using Xod;`)

---

## 1. Problem Statement & Motivation

Benchmark analysis against **SQLite 3 (WAL)** and **LiteDB 5.0** shows XOD++ v1.2.4 winning decisively in:
- **Bulk Insert (250 MB & 1 GB)**: XOD is 1.45× faster than SQLite (1.17s vs 1.70s for 100k rows; 5.4s vs 7.9s for 400k rows).
- **Deep Page Traversal**: XOD is >1,700× faster than SQLite (0.34ms vs 593ms at 1 GB).
- **Storage Compactness**: XOD Binary is 5.6× smaller than SQLite on disk (282 MB vs 1,580 MB).
- **High Concurrency Reads**: XOD delivers 3× higher throughput than SQLite (241k reads/s vs 80k reads/s on 8 readers).

However, XOD lags behind SQLite in four critical operations:
1. **1,000 PK Lookups**: 71.7 ms (XOD) vs 6.7 ms (SQLite) – caused by linear byte scanning inside `.xpag` page buffers when `RowIndex` is uninitialized (`AccessCount < 3`).
2. **Single-Row Updates / Deletes**: 53.9 ms (XOD) vs 4.4 ms (SQLite) – caused by shifting byte buffers and rewriting entire page files on every delete/update.
3. **Relational Graph Insert (10k)**: 4,523 ms (XOD) vs 628 ms (SQLite) – caused by 13,000 individual unbatched child table lock and page flush operations.
4. **Relational Cascade Delete**: 1,237 ms (XOD) vs 10.5 ms (SQLite) – caused by hydrating 2,600 C# POCO entity trees from XML before deleting each child row individually.

---

## 2. Proposed Architecture & Solutions

### Phase 1: Zero-Cost Row Indexing & Direct Point Lookups
- **Issue**: In `RawPageBuffer`, `RowIndex` is only built after a page is hit $\ge 3$ times (`AccessCount >= 3`). Random point lookups hit cold pages once or twice, incurring an $O(N)$ linear UTF-8 byte scan every time.
- **Solution**:
  1. **Instant Append Tracking**: In `TryAppendRawRowToPage`, automatically initialize `rawPage.RowIndex` on new pages and record `rawPage.RowIndex[itemCode] = (insertPos, rowByteLen)` at append time. Newly inserted rows have instant $O(1)$ lookup with **zero scanning overhead**.
  2. **Eager Row Index on First Hit**: In `RawPageBuffer.TryFindRow`, build `EnsureRowIndex()` immediately on first access. Generating the index on a 512KB page takes $<15\ \mu\text{s}$ (SIMD) and eliminates all subsequent $O(N)$ byte scans.

### Phase 2: Tombstone Slots & Zero-Shift Deletions/Updates
- **Issue**: In `IOService.RawPages.cs` (`SpliceRowInRawPage`), deleting a row copies all subsequent bytes backward in memory and flushes the entire file, invalidating `RowIndex` for all remaining rows on the page.
- **Solution**:
  1. **In-Place Tombstone Deletion**: When deleting a row in a raw page, overwrite the row bytes in-place with whitespace or standard XML comment padding (`<!--xod:tomb-->` padded to exact `oldByteLength`).
  2. **Offset Preservation**: Because `newPageLen == oldPageLen`, zero byte shifting occurs. All remaining rows on the page retain their exact byte offsets.
  3. **Index Preservation**: Simply `rawPage.RowIndex.Remove(deletedCode)` without invalidating the rest of the index.
  4. **In-Place Update**: When a replacement row is $\le$ `oldByteLength`, write in-place and pad the remainder with whitespace.

### Phase 3: Bulk Graph Insertion Pipelining
- **Issue**: In `XmlEngine.Relations.cs`, inserting complex entity graphs (e.g. `Customer` $\to$ `Orders` $\to$ `OrderLines`) cascades recursively through `Create(childType, itm)` for 13,000 items, acquiring table locks and managing transaction states individually.
- **Solution**:
  1. Maintain active batch depth across relation hierarchies so child tables (`Order.xtab`, `OrderLine.xtab`) append dirty raw pages to memory buffers without redundant disk flushes.
  2. Batch child foreign key index writes in `IndexService`.

### Phase 4: Fast Index-Driven Cascade Deletions
- **Issue**: In `XmlEngine.Mutations.cs` (`Delete`), cascade deletion currently requires re-reading the full XML entity graph from disk and calling `Delete()` recursively on each child POCO.
- **Solution**:
  1. Query the foreign key B-Tree index in `IndexService` to resolve child `PageCode.RowCode` directly from parent key values without materializing full C# objects.
  2. Direct-tombstone the child rows in their respective page buffers.

---

## 3. Implementation Plan & Phases

| Phase | Description | Target Benchmark Metric | Target Improvement |
| :--- | :--- | :--- | :--- |
| **Phase 1** | Zero-cost append indexing + eager first-hit row map | `pkLookup1000Ms` | **71 ms $\to$ sub-15 ms** |
| **Phase 2** | Tombstone row deletion + in-place slot updates | `updateDeleteMs` | **54 ms $\to$ sub-15 ms** |
| **Phase 3** | Bulk relation batch pipelining | `graphInsertMs` | **4.5s $\to$ sub-2.0s** |
| **Phase 4** | Index-driven cascade deletion (zero C# hydration) | `cascadeDelete100Ms` | **1.2s $\to$ sub-150 ms** |
| **Phase 5** | Test verification, benchmark run, version bump to 1.2.5 | Full suite (28/28 passing) | Zero regressions |

---

## 4. Verification Gate
- `dotnet build` succeeds on both `net8.0` and `net9.0`.
- All 28 tests in `Xod.Tests` pass with zero failures.
- Benchmark run (`XodBench`) demonstrates tangible latency drops in PK lookups and update/delete/cascade operations.

---

## 5. Measured (v1.2.5, `87532ba`, 2026-11-04)

Full run: `benchmarks/results/bench-v1.2.5-87532ba-20261104_*.json`, clean stamp, medians of three
iterations at 250 MB and 10k, single iteration at 1 GB and 40k. Rendering:
`benchmarks/dashboard.html`.

| Target from section 3 | Actual v1.2.5 | Verdict |
| :--- | :--- | :--- |
| `pkLookup1000Ms` 71 ms -> sub-15 ms | **88.9 ms** at 250 MB, 185 ms at 1 GB | **Missed** (still 13x slower than SQLite's 6.6 ms) |
| `updateDeleteMs` 54 ms -> sub-15 ms | **46.2 ms** at 250 MB, 100 ms at 1 GB | **Missed** (SQLite: 4.2 ms) |
| `graphInsertMs` 4.5 s -> sub-2.0 s | **3.99 s** at 10k | **Missed**, but 10% faster than v1.2.4 |
| `cascadeDelete100Ms` 1.2 s -> sub-150 ms | **73.3 ms** at 10k | **Met** (17x faster than v1.2.4's 1,265 ms) |

What crossed the target is the tombstone and index-cascade work; what did not is point lookup and
single-row mutation, where the page-local design is still 11-13x behind SQLite and the phantom
"sub-15 ms" number in the plan was never measured.

Other medians from the same run (XOD++ XML / SQLite / LiteDB):

| Workload | XOD++ v1.2.5 | SQLite 3 (WAL) | LiteDB 5.0 |
| :--- | ---: | ---: | ---: |
| Bulk insert, 250 MB (100k) | **1,140 ms** | 1,763 ms | 2,359 ms |
| Bulk insert, 1 GB (400k) | **5,344 ms** | 7,446 ms | 8,035 ms |
| 20 indexed queries, 250 MB | **18.7 ms** | 37.7 ms | 109.4 ms |
| 20 indexed queries, 1 GB | 4,953 ms | **232 ms** | 369 ms |
| Deep page (skip 90%/360k) | **0.2 / 0.3 ms** | 22.4 / 553 ms | 135 / 1,361 ms |
| Footprint, 250 MB / 1 GB | 254 / **1,016 MB** | 395 / 1,580 MB | 293 / 1,172 MB |
| Relational graph insert, 10k | 3,995 ms | **592 ms** | 2,396 ms |
| 8 readers + writer, reads/s | **187,080** | 96,994 | 137,906 |
| 8 readers + writer, writes/s | **5,963** | 5,669 | 6,099 |
| 1 reader + writer, reads/s | 7,295 | **52,923** | 24,728 |

The concurrency picture changed after v1.2.5: the writer no longer collapses under reader load
(5,963 writes/s at 8 readers, against 1,150 in the pre-release data), and read throughput at 8
readers is now the best of the three. At one reader XOD is still 7x behind SQLite.

Two defects surfaced while recording this and are open: JSON/Binary cascade delete leaves 764
orphan `OrderLine` rows at 40k (ISS-037), and JSON/Binary indexed queries take 74-100 s at 1 GB
(ISS-038). Neither has a plan yet.
