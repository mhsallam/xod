# PLAN-056: Reads that scale with threads, and writes that don't stall them (ISS-044)

- **Task ID**: `TASK-069`
- **Status**: Investigated 2026-11-12: the causes are found and measured (sections 3 and 4).
  - Proposals 1 and 2 (re-key only the keys that changed; keep the page's row map when a row grows) are
    **committed** (2026-11-14, not released; 6.9). They were written and reviewed in a private copy (section 6)
    and ported onto `TASK-067`'s second revision and `TASK-068` (6.7). The gate is green there.
  - Proposals 3 and 4 (reads without exclusive locks; a snapshot outside the write lock) are not implemented. They
    are design work and need an independent review (concurrency).
- **Related**: [`PLAN-042`](PLAN-042-benchmark-reporting-refresh.md) and
  [`PLAN-043`](PLAN-043-stress-publishing-run.md) (the runs that showed it), [`PLAN-041`](PLAN-041-insert-rate-independent-of-table-size.md)
  (the index log and its snapshot rule), [`PLAN-055`](PLAN-055-index-files-replaced-or-held.md) (index blocks
  read when needed), [`KNOWN-ISSUES.md`](../KNOWN-ISSUES.md) `ISS-044`, `ISS-105`, `ISS-106`, `ISS-108`

## 1. The question

`ISS-044`: primary-key lookups on one shared `XodContext` barely scale with threads: with no writer, 1 reader
did about 0.70M lookups a second, 2 did 1.15M and 8 did 1.7M. With one writer committing every second, reads
fell and GC pauses took about 20% of the time; the 45-minute soak's slowest read took 856 ms and its slowest
commit 554 ms.

## 2. Method

- **Code**: `HEAD` (`6624e12`) exported to a scratch folder, because the shared tree held another task's
  uncommitted changes in `src/Xod`. Release, net9.0, workstation GC (the harness's default), pinned to the
  performance cores.
- **Workload**: the harness's soak (`--profile soak --seed-rows 200000 --hot-rows 50000 --writers 0|1`), and a
  probe that seeds the same rows (200,000 seeded, `Find(x => x.Id == id)` uniform over the first 50,000) and
  measures allocation per `Find` and the rate of N threads. The probe reproduces the harness: 0.70M/s on 1
  thread, 1.25M on 2, 1.87M on 4, 1.82M on 8. For the writer, the soak at the soak's own scale (2,000,000 rows,
  200,000 hot, 8 readers, 1 writer, 180 s).
- **What settled it**:
  1. **Stage timers**: a timestamp around each stage of the read path, summed per thread (scratch copy).
  2. **Ablation**: a switch per shared critical section on the read path, each removing one; with no writer and
     the index loaded before the measurement, removing a lock changes no answer, only what the lock costs.
  3. `dotnet-trace` 10: contention events, and GC events with allocation ticks (`gc-verbose`).
  4. Writer-side prototypes, A/B against `HEAD` on the same workload.
- **What didn't work, and why**: the EventPipe sample profiler (`dotnet-sampled-thread-time`) gives the same
  leaf frames for 1 thread as for 8 (50% `TryReadEntityFromRawPage`, 27% `IsTypeHydrated`, 13% reflection,
  9% `TryPlanExpression` on 1 thread): it records a thread where it reaches a safe point (lock and allocation
  helpers), not where its time goes. Kernel sampling (ETW) needs an elevated prompt, which this session didn't
  have. Contention events count only blocking waits, not spinning.
- **A mistake, caught**: the first ablation ran against the unmodified build (a path in the probe's project
  file wasn't replaced), and showed no gain from removing every lock. It was discarded and run again; the stage
  timers found the error.

## 3. Reads: the cause

**Readers serialize because every `Find` makes seven exclusive acquisitions on four memory words that every
reader writes: `IndexService.syncLock` twice (`IsTypeHydrated`, then the planner's index lookup),
`IOService.syncLock` once (the page cache), and the engine-wide and table `ReaderWriterLockSlim`s (the table's
for a type without relationships, as here), whose `EnterReadLock` and `ExitReadLock` each take that lock's
internal spin lock. They are there because the index and
the page cache are plain collections that reads also fill (an index loaded or a block read on first use, a page
loaded on a miss), so one monitor guards both the one-time fill and every lookup after it.**

### 3.1 Stage timers (probe, one expression per thread, nanoseconds per `Find`, thread time)

| stage | 1 thread | 2 | 4 | 8 |
| :--- | ---: | ---: | ---: | ---: |
| `EnsureIndexHydrated` (`IsTypeHydrated`: `IndexService.syncLock`) | 43 | 116 | 266 | **1,391** |
| planner: resolve the comparison (constant, key) | 137 | 129 | 131 | 165 |
| planner: index lookup (`IndexService.syncLock`) | 268 | 298 | 452 | **1,733** |
| page lookup (`IOService.syncLock`) | 64 | 84 | 97 | 129 |
| row found in the page | 153 | 155 | 126 | 172 |
| row parsed into the object | 393 | 381 | 378 | 458 |
| `EnterTableReadLock` / `ExitTableReadLock` | 83 / 30 | 85 / 66 | 112 / 89 | 136 / 111 |
| lookups a second, all threads | 0.67M | 1.22M | 2.01M | 1.69M |

On 8 threads, 3.1 of about 4.7 microseconds per `Find` are spent in the two sections under
`IndexService.syncLock`; every other stage is within 1.5x of its 1-thread time.

### 3.2 Contention events (8 readers, no writer, 8 s)

28,548 blocking waits, 1.30 s in all, every one on a monitor in the read path: 54% `IsTypeHydrated`, 38%
`TryPlanExpression`, 8% `TryReadEntityFromRawPage`. (Spinning before a wait isn't counted.)

### 3.3 Ablation (probe, `Find(x => x.Id == id)`, median of 3 interleaved rounds)

Millions of lookups a second. Each column removes one more set of shared sections. This build has the stage
timers' timestamp calls compiled in (about 0.5 microseconds per `Find`), so its rates are below the harness's;
compare across the row. Batches run at other times of the day differed by up to 30% in absolute terms, with
the same ratios (an earlier single batch: 1.76M on 8 threads with every lock, 4.11M with none).

| threads | every lock (`HEAD`) | `IndexService.syncLock` off | and `IOService.syncLock` off | and both `ReaderWriterLockSlim`s off |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 0.50 | 0.49 | 0.49 | 0.53 |
| 4 | 1.34 | 1.55 | 1.61 | 1.95 |
| 8 | **1.26** (1.32, 1.25, 1.26) | 2.08 (2.46, 2.08, 1.84) | 1.93 (1.89, 1.93, 2.07) | **2.94** (2.95, 2.94, 2.91) |
| per thread on 8, against 1 | 31% | 53% | 49% | 69% |

The index monitor is the largest single cost; with every shared section gone, 8 threads do 2.3x what they do
today. The page-cache monitor's own share is within the spread of the runs and isn't claimed separately.

### 3.4 The dependency, checked

- `ReaderWriterLockSlim`, .NET 9 source (`dotnet/runtime` `release/9.0`,
  `src/libraries/System.Private.CoreLib/src/System/Threading/ReaderWriterLockSlim.cs`): `TryEnterReadLockCore`
  takes `_spinLock.Enter(EnterSpinLockReason.EnterAnyRead)` on both its recursive and non-recursive paths, and
  `ExitReadLock` begins with `_spinLock.Enter(EnterSpinLockReason.ExitAnyRead)`; readers update the lock's
  `_owners` under it. The spin lock's state is one `int` (`SpinLock.cs`, `_owner`). So every reader writes the
  same word of each lock twice per read; the engine takes two such locks per read.
- `Dictionary<TKey,TValue>` (Microsoft Learn, Thread Safety): "can support multiple readers concurrently, as
  long as the collection is not modified". A lookup without the monitor is safe only while nothing modifies that
  dictionary, which is what the proposal below has to guarantee.

### 3.5 Ruled out, by measurement

| suspect | test | result |
| :--- | :--- | :--- |
| The caller's expression tree (`x => x.Id == id` builds one per call, through reflection) | build the expression only, N threads | 3.6M/s on 1 thread, 18.5M/s on 8: scales |
| Allocation (1,965 bytes per `Find`, about 3.5-4 GB/s) | allocate 2 KB per operation, N threads | 33-37 GB/s on 4-8 threads: 10x headroom |
| Gen0 collections | `DOTNET_GCgen0size` 256 MB (7x fewer GCs); server GC | 1.73M/s on 8 threads (same); server GC worse (1.32M/s, 28% pauses) |
| Hyperthread siblings | one thread per physical core | 1.98M/s on 8 (same) |

### 3.6 "Eight readers use about one core": they spin, they don't wait

The harness's `processCpuPct` read 0.1-1.4% for 8 reader threads in every run here (0.03-0.45 of a core, as a share of every
logical processor). The probe measured the same runs two ways (medians of the runs in 3.3, `HEAD`):

| threads | `Process.TotalProcessorTime` | `QueryProcessCycleTime` |
| ---: | ---: | ---: |
| 1 | 0.55 of a core | 2.4 G cycles a second (one core, fully busy) |
| 4 | 0.62 | 9.4 G |
| 8 | 0.48 | 17.5 G (about 7 cores) |

The cycle count is the scheduler's own record of time on the processor; the time counter, which the harness
uses, under-reads a busy thread by about 2x here and 8 busy threads by about 15x. So the 8 readers are on the
processor nearly all the time: the contended sections are spent spinning (the monitor's spin phase, the
`ReaderWriterLockSlim` spin lock), not asleep. The issue's "about one core between them (readers mostly
waiting)" came from the time counter and is withdrawn (`ISS-106`).

## 4. Writes: what they cost the readers

### 4.1 The slowest read is the index loading, not a stall

The 856 ms slowest read in the 45-minute soak (PLAN-042) and the 1,006 ms one in the hour soak (PLAN-043,
`stress-soak-20261107_204205.jsonl`) are both in the **first** 30-second sample, where the slowest commit was 25
ms. Reproduced here: 856 ms and 1,141 ms in the first sample of two 2M-row runs, then 10-47 ms. The first lookups
load the 2M-row index (the "first answer" after a reopen is 0.9-1.1 s for the same reason), and every other
reader waits for that load. After warm-up, the slowest read is a commit's length.

### 4.2 The slowest commits are index snapshots under the engine-wide write lock

- In the hour soak, 23 of the 119 samples after warm-up have a read over 100 ms, and the same 23 have a commit
  over 100 ms (302-537 ms, about every two minutes; the median sample's slowest commit is 30 ms). In 22 of them
  the slowest read **equals** the slowest commit (537 = 537 ms, 470 = 470, 419 = 419 ...); in the 23rd it is 8 ms
  shorter.
- Timed inside `FlushToDisk` (scratch copy, 2M rows, 180 s): one commit took 345 ms, of which 336 ms was
  `WriteFullSnapshotToDisk`, writing the whole 2M-row index. That sample's slowest read: 352 ms, its slowest
  commit: 352 ms. `Flush` holds the engine-wide write lock, so every reader waits for the snapshot.
- Why an update-only workload snapshots at all: **every update whose row changed re-keys every indexed property**
  (`RemoveRowElement`, then `IndexItem`), even when none of their values changed. The benchmark's writer changes
  `Price` only, and still records six delta records (Id, Sku, CategoryId removed and added) per update. The log
  grows until it outgrows the snapshot (`DeltaLog.Outgrows`), and the next flush writes the index whole. The
  update paths do this in three copies: `XmlEngine.Mutations.cs` 581-582, 652-654, 693-695.

### 4.3 GC: two sources, both found by prototype

The writer turns GC from 2-6% of the time into 12-20%: medium-lived garbage, promoted and then collected in gen1
and gen2. With a writer (200k rows, 30 s, 8 readers): 23% of GCs are gen1 (none without the writer).

1. **The page's row map is thrown away when a row grows.** `IOService.SpliceRowInRawPage` keeps the page's
   `RowIndex` (row code to offset and length) current when a row is deleted, overwritten in place or appended,
   but sets it to null when a row grows ("Row expanded": `IOService.RawPages.cs` 616-652). The next reader of the
   page rebuilds the whole map under the page's lock: a dictionary and a string for each of the page's 1,204
   rows. `Price += 0.5` makes a row longer about half the time (`3` to `3.5`). The maps live tens of milliseconds,
   long enough to be promoted.
2. **Re-keying unchanged keys** (4.2) churns index entries and the delta log on every update.

Prototype A/B (`HEAD` and the prototype alternated, 8 readers, 1 writer):

| | reads a second | GC pause | gen1 a second | gen2 a second |
| :--- | ---: | ---: | ---: | ---: |
| 200k rows, `HEAD` | 1,086k / 684k | 12.3% / 14.2% | 11.3 / 14.1 | 1.00 / 1.36 |
| 200k rows, row map kept | 1,207k / 869k | 6.8% / 7.4% | 11.1 / 10.9 | 0 / 0 |
| 200k rows, row map kept, unchanged keys skipped | 954k / 974k | 6.5% / 5.0% | 0.1 / 0.1 | 0.05 / 0 |
| 2M rows, 180 s, `HEAD` (after warm-up) | 502-557k | 19.3-19.6% | 15-20 | 0.4-0.6 |
| 2M rows, 180 s, both (after warm-up) | 557-736k | 3.0-3.7% | 0.6-0.9 | 0-0.03 |

At 2M rows with both prototypes, no snapshot was written during the 180 s (817,055 updates); the slowest commit
was 26 ms and the slowest read after warm-up 27 ms (`HEAD`: 352 ms each, at the snapshot). Every run's data check
after a reopen passed. Run-to-run variation on 8 threads is large (684k-1,086k reads a second for `HEAD` on the
same workload), so only differences well outside it are claimed.

## 5. Proposal (1 and 2 implemented in section 6; 3 and 4 not)

In this order: the first two are small and local; the third is the read-scaling work and needs a design review.

1. **One operation re-keys a row, and records only the keys that changed** (writes, GC, commit tails).
   `IndexService` gets the one operation an update uses ("this row's keys go from the stored row to this
   item"); it compares each indexed property's key before and after and records removals and additions only for
   those that differ. The three update copies call it. Proven by: an update that changes no indexed value records
   no index delta (the log's pending records, and the `.xidx` length after a flush, unchanged); one that changes
   an indexed value still finds the row by the new value and not the old (both seen red first where they can
   be). Expected: no snapshot in an update-only workload; gen1 collections near zero.
2. **The page writer keeps its row map current when a row grows**, as its other three paths already do: shift
   the offsets of the rows after the grown one, instead of dropping the map. The map is derived from the page
   bytes the writer owns, under the table's write lock, which excludes the table's own readers (not a reader of
   another type that loads this type's rows: `ISS-108`, which the bytes already had). Proven by: after a
   growing update, the page's map is the one a rebuild would give (every row found at its offset, compared with
   `EnsureRowIndex` on a copy), red first on the map being null.
3. **Reads that take no exclusive lock once nothing is left to load** (read scaling). Measured ceiling: section
   3.3, all sections off.
   - **Index**: separate the one-time fill from the lookup. Per type, an index object published once it is
     whole (a volatile reference, or a `ConcurrentDictionary` for the map of types: today `propertyIndexes` is
     one `Dictionary` for every type, so without the monitor, loading type A's index would race a write to
     type B's). Loads and block
     reads take a lock and check again; lookups read the per-property dictionaries without one. That is safe only
     if every change to a type's index runs under that type's write exclusion: the invariant to prove, path by
     path (writes, rollback, transaction commit, `ClearCache`, the reload `PLAN-055` adds). `TryPlanExpression`
     already returns a live `HashSet` out of the lock on that assumption.
   - **Page cache**: a lookup that hits takes no lock (`ConcurrentDictionary`); a miss loads under the lock.
     Needs `ISS-105` fixed first: today eviction gives a page's buffer back to the pool while a reader may still
     be parsing it.
   - **The engine's read lock**: one acquisition per read instead of two `ReaderWriterLockSlim`s, and then a
     lock whose readers don't all write one word (per-processor reader counts). Design work: `Flush`,
     transactions and triggers rely on `rwLock`.
   - Guard test, deterministic: hold `IndexService`'s lock on another thread and run a `Find` on a loaded index;
     it must return. Red today (it blocks).
4. **A snapshot that doesn't hold up the readers**, for workloads that do change keys: build it from a frozen
   copy outside the engine-wide write lock, and only swap it in under the lock. Design work.
5. Not proposed here: the 2 KB allocated per `Find` (the row's strings are the caller's; the caller's
   expression tree, the planner's closure and lists are not).

## 6. Proposals 1 and 2: implemented, reviewed, and in the tree

Written and tested on 2026-11-12 in a private copy of the tree: `6624e12` with `TASK-067`'s uncommitted changes,
as they stood at 22:42 that day. It is not in the tree, because `TASK-068` and the review of `TASK-067` were open
in the same files (`IndexService`), and two sessions must not write one file at once. The patch is five source
files (+296/-64) and one test file. By 23:09 the review of `TASK-067` had changed `IndexService` under it, so it
must be put onto that work, not applied as is (6.6). It went into the tree on 2026-11-13 (6.7).

### 6.1 What changed

- **One operation re-keys a row** (`IndexService.ReplaceRowKeys`). An update now makes one call: this row's keys
  go from the stored row to this item.
  - For each indexed property, it reads the old key back from the stored row, as `RemoveRowElement` does. It
    computes the new key from the item, as `IndexItem` does.
  - Only a key that differs is moved. The old key is taken out of the whole tree, after the property's unread
    index blocks are read in, because a block read later merges additions only. The new key goes in, and one
    remove and one add are logged.
  - An update that changes no key logs nothing and leaves the type clean.
  - The reverse map (row to keys) is rewritten for the row only when a key moved.
  - The three update copies in `XmlEngine.Mutations.cs` (the raw-page path, the standalone row, the row with
    references) call it in place of `RemoveRowElement` then `IndexItem`. Inserts and deletes are unchanged.
  - A stored row whose entity can't be found falls back to removing every key and adding every key, as before.
- **A change to an index reads its blocks before it records anything** (added after the review, 6.5).
  - `ReplaceRowKeys` and `RemoveRowElement` first compute every key and read the blocks they need. Only then do
    they take the delta log and change the trees. (`RemoveItemCode` already read everything first.)
  - A load of a replaced index file (`ReloadReplacedUnlocked`) now leaves the type's whole index in memory, every
    tree built. A block read before the load is read again by it, and nothing loads the type again after it.
  - A load while a change is being applied throws (`applyingChange`, set by every change to an index). A load
    during a load already did.
- **The page's row map is kept when a row grows** (`RawPageBuffer.RowGrew`, called from
  `IOService.SpliceRowInRawPage`, "Row expanded"). The writer shifts the offsets of the rows after the grown one
  and sets the grown row's own entry, instead of dropping the map.
- **The row map has one owner** (added after the review). Its four writers are now methods of `RawPageBuffer`
  (`RowRemoved`, `RowRewritten`, `RowGrew`, `RowAppended`); they were split between it and `IOService`. The map
  is null or complete: an append to a page whose map no reader has built leaves it null. It used to make a map
  of the new row alone, which hid the page's other rows from every reader. A row without a code drops the map.

### 6.2 Tests (`tests/Xod.Tests/XodRowKeysTests.cs`), red first

Run against the copy without the change (net9.0): 5 of 11 failed, on the causes named.

| Test | Without the change |
| :--- | :--- |
| An update that changes no indexed value adds nothing to the index file (Xml, Json, Binary) | red: `.xidx` 33,434 bytes, expected 20,430 |
| The same for a type with references (the third update copy) | red: 2,862 bytes, expected 2,614 |
| A read after a row grew does not rebuild the page's row map (allocations of one `Find`, under 32 KB) | red: the read allocated 115,200 bytes |
| An update that changes one key moves that key and keeps the others, after a reopen (Xml, Json, Binary) | passes; guards the new code (below) |
| A row whose key moved leaves no key when deleted (Xml, Json) | passes; guards the new code |
| Rows read back as written after updates that lengthen rows anywhere in a page (first, middle, last, and one large enough to grow the page buffer) | passes; guards the new code |

Added after the review (6.5). Each was run against the first version of the change, whose `RemoveRowElement` is
the tree's:

| Test | Against the first version |
| :--- | :--- |
| Keys an update moves after the index file was replaced are saved | red: after a reopen the new unique value found nothing (expected 7, actual null) |
| Keys a delete frees after the index file was replaced stay free | red: `ReservedUniqueKeyException` when the freed value is used again |
| A key whose block was read before the index file was replaced is still taken out | guards the reload's full read (below) |
| An update filter moves only the keys it lets through | passes; guards the filter's left-out keys |

Each guard was proven by breaking the code it guards:

- Without reading the property's index blocks in before the old key is removed, "moves that key" fails in all
  three formats: after a reopen the old key comes back.
- With the offset shift off by one byte, "rows read back as written" fails, and so does "adds nothing" (Xml).
- Without the reload's full read, "a key whose block was read before" fails (`ReservedUniqueKeyException`).
- With a block read moved back into the change, both replaced-file update tests fail loudly with
  `InvalidOperationException` ("would be loaded again in the middle of a change"). The same move used to lose the
  change silently.
- The reviewer's own mutants were also caught: removing the shift, the grown row's own entry, the block read, or
  the type's dirty mark.

### 6.3 The gate

| Check | First version | After the review |
| :--- | :--- | :--- |
| `Xod.Tests`, net9.0 and net8.0 | 328 of 328, both | 332 of 332, both |
| `Xod.Conformance`, net9.0 and net8.0 | 14 of 14, both | 14 of 14, both |
| `conformance/format/read.mjs` check, and the fresh-write check | exit 0, both | exit 0, both |
| `XodBench.Tests` | 26 of 26 | 26 of 26 |

### 6.4 Measured: the copy without the change against the copy with it

XodBench stress profile `soak`: 2M rows, 200k hot, 8 readers, 1 writer, 180 s each, one after the other on the same
machine. Each row is one 30 s sample; the first is the warm-up and is listed apart.

| | reads a second | slowest read | p99 read | updates a second | slowest commit | GC pause | gen1 a second | gen2 a second |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| without, warm-up | 457k | 822 ms | 127 us | 3,941 | 24 ms | 20.6% | 23.7 | 0.60 |
| without, 30-180 s | 311-412k | 37-50 ms | 164-217 us | 2,920-3,097 | 30-42 ms | 19.5-20.1% | 12.0-13.4 | 0.27-0.43 |
| with, warm-up | 540k | 856 ms | 135 us | 3,682 | 14 ms | 5.0% | 7.7 | 0.40 |
| with, 30-180 s | 494-625k | 11-62 ms | 135-143 us | 3,393-5,441 | 10-62 ms | 3.8-4.1% | 0.07-0.67 | 0-0.03 |
| final version, warm-up | 380k | 1,577 ms | 172 us | 4,804 | 16 ms | 5.5% | 7.9 | 0.73 |
| final version, 30-180 s | 372-493k | 14-37 ms | 152-184 us | 4,717-5,369 | 14-88 ms | 3.7-4.0% | 0.10-0.17 | 0-0.03 |

- In 180 s: 70.8M reads and 571,285 updates without, 100.5M reads and 805,710 updates with (+42%, +41%). Both
  runs' data check after a reopen passed, with no misses.
- The final version (after the review, 6.5) was run once more, 30 minutes later: 75.8M reads and 897,037
  updates, GC pauses 3.7-4.0%, data check passed. Its reads are lower than the first version's run and its warm-up
  read slower. The machine was shared with another session's builds: other processes used 5-20% of the CPU
  across its samples, against 4-11% in the first version's run and 9-19% in the run without the change
  (`ISS-106`: these shares under-read). That is within the run-to-run spread noted in 4.3, so neither difference
  is claimed. The review's changes don't touch the read path.
- After the run: the last flush took 36 ms without and 10 ms with. The first answer after the reopen took
  2,305 ms without and 479 ms with. The index log the reopen reads is that much shorter; this was not
  traced further.
- The 62 ms read and the 62 ms commit fall in the same sample (60 s). Probably a read waited on that commit;
  this was not traced.
- No commit in either run took more than 62 ms, so neither wrote an index snapshot (one takes about 350 ms at 2M
  rows, 4.3). This run therefore does not show the snapshot tails of 4.2, which came every two minutes or so in
  the hour-long soak. The change removes their cause for an update-only workload: an update that changes no key
  now logs nothing (6.2), so the log can't outgrow the snapshot.
- The warm-up's slowest read is the index loading on first use in both (4.1); this change doesn't touch it.
- The implementation matches the prototype's figures in 4.3 (GC pause 3.0-3.7% there).

### 6.5 The independent review, and what it changed

A fresh reviewer was given the change and the problem, not this plan's reasoning, and answered the owner's four
questions. It ran its experiments in its own copies, ran the suite, and tried mutants of the change. Each finding
below was checked here against the code.

1. **High, made worse by the first version: a change in which the index file is found replaced records to a log
   no flush writes** (`ISS-112`). `RemoveRowElement` (in the tree, since `TASK-067`'s reload) and the first
   `ReplaceRowKeys` took the delta log, then read blocks. A block read that finds the file replaced loads the
   type again and drops that log. The tree's update path loses its removals this way (stale keys); the first
   version lost the additions too, so a unique value could be stored twice (reproduced). **Fixed** (6.1, "reads
   its blocks before it records anything"): red first, then green, and the guard proven.
2. **High, existing: an update is lost when the index is rebuilt from the pages during it** (`ISS-113`). The
   rebuild takes the page out of the raw cache, and the splice then returns without writing. Not fixed here. The
   change removes one case: an update that changes no key reads no block now. The second revision of `TASK-067`
   then fixed it in the tree (6.6).
3. **Medium, existing: the table's write lock doesn't keep out a reader of another type that loads this type's
   rows** (`ISS-108`). The first version's comment and 5.2 claimed it did; both are corrected. The change
   doesn't widen the race: the writer already moved those bytes under the same locks.
4. **Low, introduced: an open can rebuild a correct index after updates that changed no key** (`ISS-114`). The
   open's check by time assumed every update writes to the index. Filed, not fixed: the fix is to replace the
   guess by time, which is outside this change.
5. **Gaps in the tests.** Added: the update filter and the replaced-file cases (6.2). Not added: the reverse map
   (row to keys), which an update rewrites only when a key moved. Its one reader, `RemoveItemCode`, is reached
   by the engine only for a stored row without an entity, so no test through the public API can reach it, and a
   mutant there survives (named debt). 5.2's "compare with a rebuild" became reading rows back plus the
   allocation bound.
6. **Second copies.** The row map's writers were split between two classes; now `RawPageBuffer` owns them all
   (6.1). `RemoveRowElement` repeats `ReplaceRowKeys`' removal, in the same two phases.
7. **Dependencies.** The reviewer checked the claims against the Microsoft Learn pages for .NET 9 (unchanged in
   8), and they hold: `CollectionsMarshal.GetValueRefOrNullRef` (no adds or removes while the ref is used),
   `KeyCollection.CopyTo`, and `ArrayPool<T>.Rent`/`Return`.

**Verdict, per change:**

| Change | Verdict |
| :--- | :--- |
| One operation re-keys a row; only changed keys move | Clean, with named debt: `ISS-114` (comes with it); the reverse map's refresh is untested |
| A change reads its blocks before it records; a reload leaves the index whole; a load during a change throws | Clean on the design it was written for: removes the cause of `ISS-112`. On the tree's new design that cause is gone another way, and only the two-phase order carries over (6.6) |
| The row map kept when a row grows; one owner, null or complete | Clean. `ISS-108` is unchanged, and its comment is corrected |
| `ISS-113` (an update lost when a rebuild runs during it) | Not fixed here; filed (High), then fixed in the tree by the second revision of `TASK-067` |

### 6.6 Putting it into the tree

By 23:09 on 2026-11-12, the review of `TASK-067` had replaced `ReloadReplacedUnlocked` in the tree with
`HydrateUnreadFromPagesUnlocked`. When a block can't be read, the new function builds the type's unread blocks
from the pages and keeps its log and its trees. On that design:

- **`ISS-112`.** Its cause is gone in the tree: nothing drops the log during a change. Two of the review's repros
  pass on a copy of the tree.
- **`ISS-113` was worse at 23:09, and was fixed by 23:22.** At 23:09 the walk of the pages ran whenever a block
  couldn't be read, saved changes or not. On a copy of the tree with nothing unsaved, an update that changed a
  key returned true and wrote nothing, in memory and after a reopen. The second revision of `TASK-067` fixed it
  (below).
- **Carried over unchanged:**
  - `ReplaceRowKeys` and its three callers.
  - The two-phase order in `ReplaceRowKeys` and `RemoveRowElement`. It is what `HydrateUnreadFromPagesUnlocked`'s
    own comment assumes: no key is taken out of a block while the block is unread.
  - The row map's one owner, and the tests.
- **Dropped:** the reload's full read, and the guard in `ReloadReplacedUnlocked`, since that function is gone. The
  guard belongs wherever a type's log and trees can still be dropped (`ForgetTypeUnlocked`), if anything can call
  it during a change.
- **The replaced-file tests now also check the row itself, not only its keys.** On the tree's design, a test of
  the keys alone would pass while the update is lost (`ISS-113`). Run on a copy of the tree as of 23:09, without
  this patch, both replaced-file update tests fail on the row (`Expected "ZZZ-MOVED", Actual "SKU-000007"`;
  Bucket 42 against 7), and the delete test passes. The second revision of `TASK-067` fixed `ISS-113`: the walk
  reads pages without moving them between caches, and a splice into a page that left the raw cache writes the row
  to the page as a document. On a copy of the tree as of 23:22 these tests pass. The six still red there are the
  ones this patch fixes: the index's size after updates that change no key (four), and the row map's allocations.
- After it goes in: the gate, and the 180 s soak (6.4), run again in the tree.

### 6.7 In the tree (2026-11-13)

`TASK-067` was committed (`a8015fc`) and `TASK-068` applied in the tree, uncommitted. Nothing in those files had
changed since 00:06, and the session that wrote them was idle, so the port went ahead.

- **How:** a three-way merge in a scratch repository. The change was replayed onto an exact copy of the tree as
  of 06:13, and the result applied to the tree as a patch, after checking that the six files still matched that
  copy. Line endings are unchanged (CRLF).
- **The one conflict:** `IndexService.Persistence.cs`, where the first version changed the reload that
  `TASK-067`'s second revision had removed. The tree's side was taken: no reload, so no full read at a reload
  and no check there.
- **The check moved to what can still drop a type's log and trees.** `applyingChange` is kept: every change to
  an index sets it while it applies. `ForgetTypeUnlocked` and `ClearAll` now throw if they run during a change.
  Proven: a drop injected into `ReplaceRowKeys`' apply phase fails three tests with `InvalidOperationException`
  ("would be dropped in the middle of a change"). The comments in `RemoveRowElement` and `ReplaceRowKeys` now
  describe the tree's design: a block that can't be read is built from the pages while they still hold the row.
- **Merged without conflict:** `ReplaceRowKeys` and its three callers, `RemoveRowElement`'s two phases, the row
  map's owner in `RawPageBuffer` (the tree's `ISS-113` fix sits in the same method, `SpliceRowInRawPage`), and
  the tests.
- **The gate in the tree:** `Xod.Tests` 359 of 359 on net9.0 and on net8.0. `Xod.Conformance` 14 of 14 on both.
  `read.mjs --check` and the fresh-write check exit 0. `XodBench.Tests` 26 of 26.
- **The 180 s soak on the tree's code** (run from the scratch copy, so no results file lands in the tree; same
  profile as 6.4):

| | reads a second | slowest read | updates a second | slowest commit | GC pause | gen1 a second | gen2 a second |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| tree with the change, warm-up | 571k | 654 ms | 6,535 | 10 ms | 4.7% | 8.2 | 0.43 |
| tree with the change, 30-180 s | 478-579k | 14-22 ms | 4,834-6,825 | 10-22 ms | 2.9-3.2% | 0.10-0.83 | 0-0.03 |
| tree without it, warm-up | 447k | 772 ms | 3,811 | 28 ms | 19.8% | 24.4 | 0.77 |
| tree without it, 30-180 s | 313-506k | 32-7,919 ms | 2,671-5,176 | 15-7,919 ms | 13.5-21.3% | 11.2-17.4 | 0.37-0.57 |

  In 180 s: 95.8M reads and 1,106,080 updates with the change; 73.2M reads and 718,022 updates without it
  (+31%, +54%). Both data checks after a reopen passed, with no misses. Other processes used 5-10% of the CPU in
  both runs.

  Without the change, one commit took **7,919 ms**, in the sample ending at 120 s, and a read waited as long. It
  is most likely the index snapshot of 4.2: an update-only workload falls due for one about every two minutes,
  and a snapshot is the one commit that holds the engine's write lock that long. But at `6624e12` a snapshot
  took 336-554 ms at this size, so this one is about 15-20 times slower on the tree as `TASK-067` and `TASK-068`
  left it. Not investigated; filed as `ISS-116`. The change hides it for an update-only workload (no snapshot
  in 180 s), not for a workload that changes keys.

### 6.8 Against the overall review (2026-11-14)

The review of `7ac53ae` (`REVIEW-2026-11-14-overall.md`) filed four issues in the areas this change touches. The
change fixes none of them, and none of their fix directions conflicts with it. Checked by reading the change
against each issue's code:

- **`ISS-129`** (a full page written in the background from a pooled buffer that another path gives back): the
  change doesn't touch the background write or the byte pool. Its one pool use is a `string[]` that `RowGrew`
  rents and returns within the call, under `IOService`'s `syncLock`. The row map lives on the `RawPageBuffer`,
  which leaves the cache when its buffer is given back (`IOService.Files.cs` 128-131), so no map outlives its
  bytes.
- **`ISS-138`** (the item cache serves stale references): the three `ReplaceRowKeys` calls replace index calls
  only. The cache clears the issue names are unchanged, and the standalone fast update still clears nothing.
- **`ISS-141`** (a reader never replays index log records another context appends): unchanged. An update now
  appends only the keys it changes. That leaves fewer records to miss, but it isn't a fix.
- **`ISS-144`** (failures swallowed on the read path): the change adds no `catch`, apart from a test's cleanup
  of its temporary folder. But it relies on the row map more: the map is now kept when a row grows, where it used
  to be dropped and rebuilt. `TryFindRow` trusts the map and doesn't check the row code at the offset, so the
  read path doesn't detect a wrong entry. A parse that fails there is swallowed and the page is parsed whole
  (`ReadRowColdOrCached`). An entry that lands on another whole row would return that row (by reading; not
  probed). The guard is the writer. It splices at the mapped offset, so a wrong map corrupts the page visibly,
  and the tests catch an offset one byte off, a missing shift and a missing entry for the grown row (6.2).
  **Named debt:** checking the row code at the offset in `TryFindRow` would detect a wrong entry at the owner,
  and fixing `ISS-144` would make it loud.
- **Line numbers:** the review cites `7ac53ae`, and the change moves these lines:
  - `IOService.RawPages.cs`, from line 612 on: 3 to 7 lines earlier (`ISS-129`'s 870-888 become 863-881);
  - `XmlEngine.Mutations.cs`, from 627 on: 1 to 3 lines earlier (`ISS-138`'s 681, 1123 and 1274 become 680, 1120
    and 1271);
  - `IndexService.Persistence.cs`, from line 20 on: 5 lines later (`ISS-141`'s 213-274 become 218-279, and
    `ISS-144`'s 63 becomes 68).

### 6.9 Committed (2026-11-14)

The owner asked for the commit on 2026-11-14. Proposals 1 and 2 are one commit on `69156ee`, with this plan and
their rows. The ISS-117 fix (`PLAN-052` section 8) is the next commit. The tree also held another task's
uncommitted work (`TASK-072`), so the gate ran on a copy: `HEAD` exported, with only these two changes applied.

- **The gate at the commit:** `Xod.Tests` 407 of 407 on net9.0 and on net8.0; `Xod.Conformance` 14 of 14 on both; `read.mjs --check`, and
  `--check-fresh` after the `data` and `stored-values` writers, exit 0; `XodBench.Tests` 26 of 26; no warning in
  the changed files.
- **The six code files** held only this change's lines when they were staged. In the shared docs, only this
  plan's rows and lines were staged.

## 7. Not done (named)

- Proposals 1 and 2 are committed (6.9) but not released.
- `TryFindRow` doesn't check the row code at the offset it finds (6.8).
- Proposals 3 and 4 are not implemented. Their measurements are of prototypes in a scratch copy (sections 3.3 and
  4.3), and their invariants (5.3) are to be proven when they are implemented, with an independent review.
- No kernel-level CPU profile (it needs an elevated prompt).
- The hour-long soak was not rerun with proposals 1 and 2. The 180 s A/B (6.4) shows the GC and throughput
  change; the snapshot tails need the hour (4.2).
