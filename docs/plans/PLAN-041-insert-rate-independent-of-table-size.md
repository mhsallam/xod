# PLAN-041: Insert rate independent of table size

- **Task ID**: `TASK-052`
- **Issue**: [`ISS-041`](../KNOWN-ISSUES.md)
- **Status**: Implemented - 10M rows insert at 232k-239k rows/s near ten million, against 162k-299k
  rows/s in the first 5M; suite 60/60 on net8.0 and net9.0
- **Related**: [`PLAN-040`](PLAN-040-stress-and-endurance.md) (the capacity run that showed it),
  [`PLAN-023`](PLAN-023-binary-xidx2-and-incremental-index-append.md) (the `.xidx` delta log),
  [`PLAN-019`](PLAN-019-tail-page-append-and-page-row-counts.md) (tail-page append)

## Symptom

The capacity profile (PLAN-040) inserted 41k rows/s near 1.25M rows and 3.8k rows/s at 9M: the cost
of an insert grew with the table. The phase counters (`XOD_DIAG=1`) put 3.4 us/row (1M) and 8.6 us/row
(3M) inside `TryAppendRawRowToPage`, but a row cost 24 us at 1.25M and 90 us at 3M, so most of the
time was outside every timed phase.

## How it was measured

`dotnet-trace` (`dotnet-sampled-thread-time` + `gc-collect`) for 15-20 s on a 3M-row capacity run,
converted to speedscope and folded by hand for the main thread; `dotnet-counters` (`System.Runtime`)
for GC over the whole run.

| main-thread wall time | at ~1M rows | at ~2.5M rows |
| :--- | ---: | ---: |
| `IndexService.WriteFullSnapshotToDisk` (from `FlushDirtyIndexes` after every batch) | **66%** | **75%** |
| of which `WriteFile` (disk) | 33% | 54% |
| `TryAppendRawRowToPage` | 25% | 21% |
| `EndBatch` (page writes) | 2% | 1% |

GC was not the cause: pause time stayed at or under 7% of wall time. The allocation rate was
~1.5 GB/s, mostly the `MemoryStream` that each snapshot serialises into.

## Causes

1. **The whole index was rewritten after every batch.** `IndexService.SaveToDisk` chose between
   appending the delta log and rewriting the `.xidx` snapshot by the size of *one flush*: 4,096 deltas
   or more meant a rewrite. A 10,000-row `InsertRange` with three indexed properties makes 30,000
   deltas, so every batch rewrote the file. That file is 1.1 GB at 10M rows: about 110 KB of index
   written per inserted row. The same rule let a stream of small flushes append forever within a
   session; it was only compacted on the next open.
2. **Finding the tail page walked every page.** `OpenPageOrCreate` and `TryAppendRawRowToPage` each
   found the tail with `pagesRoot.Elements("Page").LastOrDefault()`. `Elements()` is a lazy iterator,
   so `LastOrDefault` enumerates all of it: O(pages) per row, about 2.3 ns per page. That is all of
   the "unaccounted" raw-path time, linear in page count (2.2 us at ~975 pages, 7.0 us at ~2,925).

## Change

1. **One owner for the `.xidx` layout: `IndexService.DeltaLog`.** Per type it knows where the
   snapshot ends and how long the log after it is (measured from the file, set on load, append and
   snapshot). A flush appends however many deltas it carries. A snapshot is written only when the log
   would outgrow `max(256 KB, snapshot)`. The log can therefore at most double the file, and
   snapshots are written at geometrically spaced sizes, so the work of persisting the index is
   proportional to the rows written. Recording, the decision, the append and the snapshot all go
   through `DeltaLog`; the old 4,096 cap was applied in five places (three recorders, the save and
   the load).
   - **A change is recorded only against an index this context holds whole** (`HoldsWholeIndex`):
     loaded from the file, rebuilt from the pages, or new. Callers may change rows without having
     loaded the index (indexing off; a delete on a type without a primary key finds its row by
     scanning pages), so the owner loads the file first. A file it cannot load (missing, stale,
     unreadable) cannot be kept current: it is deleted, and the next load rebuilds the index from the
     pages. `SaveToDisk` never writes a snapshot of an index it does not hold whole.
   - **Every flush appended to the log ends with a flush-end record** (`DeltaLog.OpFlushEnd`), and
     snapshots now carry marker `-3` instead of `-2`. A v3 log that does not end with that record
     was cut by a crash during the append, even when the cut falls between two whole records; the
     load rejects it and the index is rebuilt from the pages. A load that stops part way now drops
     what it had filled before that rebuild starts. A file with `-2` (or the older unframed layout)
     cannot tell a cut log from a whole one, so nothing is appended to it: loading one makes a
     snapshot due, and its first save rewrites it as `-3` (one full index write per type). A log cut
     before this plan, in a file written without these records, still cannot be detected. Older
     builds reading a `-3` file fail the load and rebuild from the pages.
   - A flush with nothing pending writes nothing. Before, it rewrote the snapshot (for example,
     every `Flush()` or `Dispose()` after an `Immediate`-mode insert that had already appended).
   - The append goes at the end of whatever the file holds, as before this plan, so records another
     context appended are kept. The record size used for the decision is exact
     (`DeltaLog.RecordBytes`, next to `DeltaLog.WriteRecord`, which writes the record).
   - A pending list that grew past 65,536 records is trimmed after its flush.
   - A rebuild from the pages (`IndexRowElement`) records no deltas, so it marks a snapshot due.
   - An encrypted index is never appended to, as before.
   - **On a clean close**, `XmlEngine.Dispose` folds into a snapshot any log over 256 KB that this
     context appended to (`IndexService.FoldLargeLogs`), so the next open replays at most 256 KB. A
     context that only read writes nothing at close; a large log left by a crash is replayed at
     each open (at most the snapshot's size) until a writing context closes cleanly.
2. **One owner for the append page: `IOService.FindAppendPage` / `AddPageElement`.** The tail is
   read through `XContainer.LastNode`, which LINQ to XML keeps as a field; the pages are scanned only
   when the tail is full, which happens once per new page. Both write paths call it.
3. **Harness** (`benchmarks/XodBench --stress`): `--sample-seconds N`; checkpoint lines gain
   `closeMs` (flush + dispose, where the fold now runs), and the sample clock restarts after a
   checkpoint so the close and reopen are not counted as insert time.

Dependency claims, checked in the .NET runtime source for `release/8.0` and `release/9.0` (identical
for these members):
- `XContainer.LastNode` returns the `content` field, the last node of a circular child list, and
  `Elements(XName)` is a `yield` iterator from the first child:
  [XContainer.cs](https://github.com/dotnet/runtime/blob/release/9.0/src/libraries/System.Private.Xml.Linq/src/System/Xml/Linq/XContainer.cs).
- `Enumerable.LastOrDefault` enumerates the whole sequence unless it is an `IList<T>` or a
  System.Linq `Iterator<T>` (`IPartition<T>` in 8.0):
  [Last.cs](https://github.com/dotnet/runtime/blob/release/9.0/src/libraries/System.Linq/src/System/Linq/Last.cs).
- `XNode.PreviousNode` walks from the first sibling (O(n)), so the fallback does not use it:
  [XNode.cs](https://github.com/dotnet/runtime/blob/release/9.0/src/libraries/System.Private.Xml.Linq/src/System/Xml/Linq/XNode.cs).

## Tests

`XodIndexLogTests` (each seen red against the code before its fix, then green):
- `A_flush_of_many_deltas_extends_the_log_instead_of_rewriting_the_snapshot`: after a 4,500-delta
  batch the file's first bytes are unchanged and longer; a cold open answers every index, removes
  included, and still enforces the unique key. Red: "the batch rewrote the index snapshot".
- `The_log_never_outgrows_its_snapshot_and_a_clean_close_folds_it_back`: 30 batches of 3,000
  deltas; after each, the log is at most `max(256 KB, snapshot)`; after `Dispose` it is at most 256 KB.
  Red: "log 296,000 bytes over a snapshot of 221,857 bytes at 4,000 rows".
- `A_change_to_an_index_this_context_never_loaded_keeps_every_other_key` (review finding 1): a
  delete on a keyless type in a fresh context. Red: the next context could not find `K0050`.
- `A_session_with_indexing_off_keeps_the_index_whole` (finding 1): one insert with indexing off.
  Red: the next context could not find `SKU-00000050`.
- `A_log_cut_between_two_records_is_not_mistaken_for_a_whole_one` (finding 3): the file is cut 31
  records before its end. Red: the last ten rows were missing from the index. It also fails again
  when only the flush-end check is switched off.
- `An_index_file_from_before_flush_end_records_is_rewritten_before_it_is_appended_to` (second
  review): a `-2` file receives a batch. Red: the batch was appended and the file stayed `-2`.

`XodVolumeTests` (`XOD_VOLUME_TEST=1`: 1M rows of 1 KB, 200k deletes, cold reopen) passes in 9 s on
net9.0; `TASK-047` recorded 31 s.

The tail-page change has no unit test of its own: it changes complexity, not results, and the
existing suite covers which page a row goes to. Its evidence is the measurement below.

## Result (2026-11-06, same machine and command as PLAN-040, `--sample-seconds 2`)

Run file: `benchmarks/results/unpublished/stress-capacity-20261106_171342.jsonl` (the final code,
after the review fixes). It sits outside `results/stress/` so the site does not pick it up until the
owner decides to publish it.

| rows | insert rate before | insert rate after | close (`closeMs`) | cold reopen | probe p50 / p99 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1M | ~41,000/s (1.25M) | 280k/s (0.89M), 299k/s (1.61M) | 125 ms | 1.6 ms | 278 us / 1.4 ms |
| 5M | ~7,100/s (5.8M) | 227k/s (4.61M), 272k/s (5.55M) | 819 ms | 2.0 ms | 191 us / 0.62 ms |
| 10M | ~3,800/s (9M) | 232k-239k/s (9.09M-9.58M) | 1,378 ms | 2.3 ms | 179 us / 0.45 ms |

- 10M rows in 52 s end to end (three checkpoints included), against 23 min 52 s before.
- Raw path: 1.05 us/row averaged over 10M rows (`rawMs=10547`), against 8.6 us/row at 3M before.
- 4.85 GB on disk at 10M; the `.xidx` is 1.10 GB, one snapshot after the close.
- RSS 10-12 GB at 10M rows (16.4 GB before). Still high; see below.

## Named debt

- **One batch per doubling pays a full rewrite.** At 8.62M rows one sample fell to 14.2k rows/s: the log had reached the size of the 5M snapshot, and that batch wrote the ~1 GB index.
  That cost is amortised (each rewrite follows as many rows as the snapshot holds), but it shows up
  as latency on that batch. A bucketed index (`TASK-042` / `PLAN-037`) would bound it.
- **The fold moved into close:** `Dispose` after a session that wrote more than ~256 KB of index
  deltas rewrites the snapshot (1.4 s at 10M rows). Before, every batch paid that. A large log
  left by a crash is replayed at each open until a writing context closes cleanly.
- **Encrypted databases still rewrite the index on every save:** the file is encrypted as a whole.
  Unchanged by this plan.
- **Memory:** RSS stays around 10-12 GB on a 4.9 GB database at 10M rows. It is not attributed
  yet; the raw page cache (`CACHE_SIZE` 65,536 pages) keeps every page it has written. This is the
  next ceiling for a 20M+ run (PLAN-040).
- **A remove against an index the context never loaded is expensive:** with indexing off, or a
  delete on a type without a primary key, the first remove loads the index and reads every
  property block it removes from (at 10M rows, the whole index). Results are right; before this plan
  that case appended one delta and later wrote a partial snapshot. Not measured at scale.
- **Two loaders decide whether the index is whole** (`XmlEngine.EnsureIndexHydrated`,
  `IndexService.HoldsWholeIndex`), with different locks and failure handling. They should become one,
  in `IndexService`, with the page rebuild passed in.
- **`ForgetTypeUnlocked` and `Remove` leave the type's reverse-map entries** (`codeToIndexEntries`):
  memory only, since removals are idempotent.
- **Delete** finds its page element by scanning the table's pages (`XmlEngine.Mutations.cs`, the two
  `Pages().FirstOrDefault(... file ...)` calls): O(pages) per delete. Not measured; not in scope.

- **Index staleness after a crash is judged by file times** (`ISS-043`): a page written within
  50 ms of the last index write is not detected as newer, so a crash between `EndBatch` writing the
  pages and the index append can leave an index that misses up to one batch. This was there before;
  batches now take ~40 ms, inside that window. The event-based fix (a flush-start record before the
  pages are written) touches every page-write path and is filed, not done here.
- **Other contexts on the same files** (`ISS-042`): a context's lazily read index blocks hold byte
  offsets into the `.xidx`; another context that rewrites the file (a snapshot, a fold at close)
  leaves them pointing at other bytes. The next read fails. Before this plan the block was dropped
  before the read, so the failure turned into silently empty results; now the failure repeats. This
  was there before (any snapshot rewrite); concurrent writers need `LockDatabase`.

## Independent review

A fresh reviewer read the diff with the problem statement and no conclusions (rule 6). It confirmed
both causes removed and the dependency claims, and reported ten findings. Each serious one was
reproduced by a probe before acting:

| # | finding | done |
| :--- | :--- | :--- |
| 1 | A context that never loaded a type's index (indexing off; keyless delete) snapshotted its partial index over the file: High, a regression | fixed: `HoldsWholeIndex`; two tests |
| 2 | Staleness judged by file times with 50 ms slack; batches now fit inside it | filed `ISS-043` (existed before) |
| 3 | A crash cutting the log between two records went undetected | fixed: flush-end record, `-3` marker, reset on failed load, older files converted on their first save; two tests |
| 4 | A fold at close moves the bytes another open context's lazy blocks point at | filed `ISS-042`; comment corrected; no more silent empty results |
| 5 | The pending list kept its peak capacity | fixed: trimmed above 65,536 records |
| 6 | A crash-left log is replayed until a writer closes cleanly | named above |
| 7 | The size forecast counted characters, not UTF-8 bytes | fixed: exact size beside the writer |
| 8 | The length guard's comment was wrong, and its snapshot dropped another writer's records | fixed: append at the end, as before |
| 9 | A fold that throws in `Dispose` left files and locks held | fixed: `Dispose` releases in `finally` |
| 10 | Test comment named a missing member; encrypted save recorded before writing | fixed |

The same reviewer then re-reviewed the fixed tree: probes for findings 1, 3, 5 and 7 pass, the new
tests each fail against the code they guard, and the 400k-row timing is unchanged (within noise).
It found no new correctness regression and four further points:

| finding | done |
| :--- | :--- |
| `-2` and unframed files were still appended to, so the cut-log check never covered existing databases | fixed: loading one makes a snapshot due; one test |
| A remove in a context that never loaded the index (indexing off, or a keyless delete) now loads the index and reads every property block it removes from: the whole index, in memory, for one delete | named below |
| Two loaders: `XmlEngine.EnsureIndexHydrated` (under `hydrationLock`, rebuilds on failure) and `HoldsWholeIndex` (deletes on failure). No reachable double load was found; safety rests on the engine's table locks | named below |
| With several open contexts, a sharing violation can make a load fail and then the delete throw after the row was written | folded into `ISS-042` |
| `ForgetTypeUnlocked` leaves reverse-map entries (memory only; `Remove` had the same gap before) | named below |
| The 4-byte flush-end record was not counted in the size bound | fixed |

## Verdicts

| change | verdict |
| :--- | :--- |
| `.xidx` log policy (`DeltaLog`, `HoldsWholeIndex`, flush-end records) | clean, with named debt (the rewrite stall per doubling; the fold at close; the cost of a remove against an unloaded index; two loaders; `ISS-042` and `ISS-043` were there before) |
| Append page (`FindAppendPage`) | clean |
| `Dispose` releases resources when a flush throws | clean |
| Harness (`--sample-seconds`, `closeMs`, sample restart) | clean |
