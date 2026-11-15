# PLAN-055: Index files another context replaced, or another program holds (ISS-042)

- **Task ID**: `TASK-067`
- **Status**: Fixed in the tree (2026-11-12), not released: no version change, nothing pushed. Revised twice
  after independent reviews: the first version lost changes in two ways (section 4), the second lost an update
  (section 5).
- **Related**: [`PLAN-041`](PLAN-041-insert-rate-independent-of-table-size.md) (the index log and blocks read
  when needed; ISS-042 filed), [`PLAN-054`](PLAN-054-indexes-left-behind-by-a-crash.md) (the marker a context
  keeps while its indexes are behind), [`PLAN-046`](PLAN-046-file-format-spec.md) section 8.1,
  [`KNOWN-ISSUES.md`](../KNOWN-ISSUES.md)

## 1. What was wrong, and why

**Defects.** All on the index file (`<table type>.xidx`) of a database two contexts, or a context and another
program, have open at once:

1. **A replaced file spoiled blocks not yet read.** A context reads an index file's property blocks when first
   needed, at the offsets it found when it loaded the file. Another context that wrote the file whole in between
   (a rebuild, or the fold of a large log at its close) moved every block, and the read then took other bytes:
   `EndOfStreamException`, or keys of another property. Reproduced deterministically: a reader loads the index
   and reads the primary key's block; a writer adds 400 rows and rebuilds the index file; the reader's next
   lookup by the unique key threw.
2. **A query failed on an index file it couldn't replace.** A file another program held without sharing
   couldn't be loaded, so the index was rebuilt from the pages; saving the rebuilt index over the held file
   threw, and failed the query.
3. **A write failed on an index file it couldn't delete.** A write to a table whose index isn't loaded
   (`EnableIndexing = false`) loads it to keep it current, and deletes a file it can't load. A file it couldn't
   delete threw, after the row was written. Deleting a table's last row, which deletes its index file, threw the
   same way after the row was gone (found by the review).

**Causes.**

1. A block's offset was trusted without anything saying which file it was found in: the file's name was the
   only link, and another context can put another file under that name. The change makes a read of another
   snapshot's bytes impossible: each snapshot carries a **generation**, new each time the file is written whole,
   and a block read checks it in the same open as it reads the block. A block found in another snapshot is
   never read; it is read from the pages instead.
2. Saving a rebuilt index, a cache for the next open, was done as part of the query that needed the index.
3. The deletion, which keeps a file missing this context's changes from being trusted later, had no other way
   to keep that file untrusted when it failed, so it failed the write.

## 2. The design

- **The layout `-5`** (`IndexService.SnapshotWithGeneration`; spec section 8.1): the `-4` layout with an int64
  generation after the marker: random (`RandomNumberGenerator`), never 0, new at each snapshot; appends to the
  log keep it. The library loads only `-5`; `-4`, written only by builds before this task (never released), is
  rebuilt from the pages like the older layouts. 2.0 reads `-5` as a negative property count and rebuilds, as
  it did `-4`. `conformance/format/read.mjs` reads `-5`, and its check of a freshly written set requires `-5`.
- **A block remembers its snapshot** (`UnhydratedIndexBlock.Type`, `.Generation`). `ReadIndexBlockBytes` opens
  the file, checks the header, the marker and the generation, and only then reads the block at its offset, all
  in one open; null when they differ, or when the file can't be read now (deleted, or held by another context's
  append, which shares nothing). It used to read whatever the file held at the old offset, and fall back to
  reading the whole file.
- **Unread blocks read from the pages, in place** (`HydrateUnreadFromPagesUnlocked`), when a block read gives
  null: every block of the type not yet read is built in one walk of the table's pages, read through the
  context's cache (so with what it has written and not yet saved), and merged with the keys the context added
  since the load, as a block read from the file is. That is what the blocks held, as the pages now hold it: no
  key is taken out of a block while it is unread (a removal reads a block before it takes a key out of it,
  `RemoveRowElement`; `RemoveItemCode` reads them all), so every row whose key it held is on the pages unless
  deleted since. **Nothing else of the
  type's index changes**: not the trees already read, not the log of changes still to save, not whether the type
  is to be saved. A caller in the middle of a change (a delete taking a row's keys out one property at a time, a
  flush writing a snapshot) goes on with the state it began with, and its changes are saved as before: appended
  to whatever file is there, or written whole. The trees are put in place only once the walk is done, so one
  that fails leaves the blocks unread. While a load replays its file (`loading`) the load fails instead, and its
  caller rebuilds the type from the pages.
- **Readers share reading only**, as before this task. The first version opened them with
  `FileShare.Read | FileShare.Delete` and said this let another context's snapshot replace the file; it doesn't
  (`File.Move` over a file open with `FileShare.Delete` still fails on Windows, which the review showed), so it
  was put back.
- **One walk of a table's pages for its index** (`ForEachPageRow`), used by the one builder
  (`BuildFromPages`: the engine's ensure path) and by the in-place read above; **one reading of a row for the
  index** (`RowKeys`), used by both; **one selection of the indexed properties** (`IndexedProperties`), which
  five methods and the engine's ensure path had each spelled out.
- **The walk moves no page between the page caches** (`IOService.PageForReading`): a page is read from its
  cached document, from a document made from a copy of its cached bytes, or from the file, and nothing cached
  changes. It used to open each page through `OpenFileOrCreate`, which takes a page held as bytes out of that
  cache; an update finds its row's offset in those bytes, then checks its keys and changes the index (where the
  walk can run), then writes at the offset, and found no page (`ISS-113`).
- **A row write finds its page wherever it is held** (`IOService.SpliceRowInRawPage`): when the page is no longer
  held as bytes (opened as a document, or evicted, since its row was found), the row is replaced or removed in the
  page as a document and saved as any page is. It used to return without writing, so `Update` returned true and
  changed nothing. Either change alone keeps the update; both are kept, the second because a page can leave the
  bytes cache by other ways than the walk.
- **A rebuilt index is saved best-effort** (`IndexService.SaveBuilt`): a plain file that can't be written (held,
  a folder that can't be written) is left as it was, and the query goes on. Whatever kept the file from being
  loaded still applies to it: held files are reported by the flushes that do write (a later change to the table
  saves the index whole, and a failure there throws and stays to be made, `ISS-098`); a stale or foreign file is
  still refused by the checks that refused it. The next open builds the index again. An encrypted index is saved
  through the file service, which in a batch or with buffered writes holds the save for its next flush, where a
  failure fails that flush (`ISS-109`, as before this task).
- **A snapshot that can't be moved into place leaves no `.tmp`**: the snapshot writer deletes it and rethrows.
  A `.tmp` with no file beside it is moved into place at the next open (`RecoverInterruptedWrites`), and one left
  by a failed move would miss everything that changed after it.
- **One deletion of an index file** (`DeleteIndexFile`), for a context that no longer holds the type's index
  (`AbandonIndexFile`, `Remove`: a table's last row deleted, a table dropped, a failed statement's undo) and for
  the retry at each flush. A file that can't be deleted stays, untrusted (`undeletedIndexFiles`): the context
  never loads it (`TryLoadFromDiskCore`), each flush tries the deletion again, and while it is there the context's
  indexes don't count as saved (`AllSaved`), so its marker (`PLAN-054`) stays marked at a crash or a close and
  the next open alone drops the index files. A snapshot of the type replaces the file and clears it. Not
  cleared with the caches: it says what is on disk. Nor in a transaction, whose rollback puts back the files it
  deleted or replaced: a rollback used to bring back a file the record had been cleared for, which was then
  trusted (section 5). Other contexts open at the same time don't know of it, as with any index a context leaves
  behind while others are open (`ISS-043`, mitigated).

## 3. Tests

`tests/Xod.Tests/XodIndexReplacedTests.cs`, written first and run red:

| Test | Red against | Proves |
|---|---|---|
| `A_snapshot_another_context_wrote_doesn_t_spoil_blocks_not_yet_read` | `6624e12`: `EndOfStreamException` | defect 1 |
| `A_query_isn_t_failed_by_an_index_file_it_can_t_load_or_replace` | `6624e12`: `UnauthorizedAccessException`; the first version: a `.tmp` left beside the file | defect 2, and no `.tmp` left |
| `A_write_isn_t_failed_by_an_index_file_it_can_t_delete` | `6624e12`: `IOException` | defect 3, and a later open finds the row and refuses its key again |
| `An_index_file_that_couldn_t_be_deleted_isn_t_loaded_later` | `6624e12`: `IOException` | the file left behind isn't loaded once readable (its time set after the pages', so the guess by time doesn't catch it) |
| `A_replaced_snapshot_doesn_t_lose_changes_not_yet_saved` | passed at `6624e12` (the replacing file held the same bytes) | a buffered reader's unsaved key survives the replacement and is saved |
| `A_flush_that_meets_a_replaced_file_keeps_the_changes_it_saves` | the first version: the new rows' keys missing (`Find` null), a duplicate accepted after reopening | review finding 1 |
| `A_delete_after_a_replaced_snapshot_is_kept` | the first version: `ReservedPrimaryKeyException` on inserting the deleted key again | review finding 2 |
| `Deleting_a_table_s_last_row_isn_t_failed_by_an_index_file_held_open` | the first version (and `6624e12`): `IOException` after the row was gone | review finding 4c |
| `An_update_after_a_replaced_snapshot_is_kept` (the new file with the same rows; with 400 more) | the second version: the row unchanged, its old key gone from the index and the new one pointing at it; `6624e12`: passed by chance (same rows), `EndOfStreamException` (more rows) | second review, finding 1 (`ISS-113`) |
| `A_rollback_doesn_t_bring_back_an_index_file_as_trusted` | the second version: after the close, the row's key not found and a second row with it accepted | second review, finding 2 |

Of these, the three before the last two are the first reviewer's probes P1, P2 and P8, and the last two the second
reviewer's PD, PD2 and PB, made deterministic: the other context deletes the index file and rebuilds it, so a new
generation is certain.

**Each guard broken in turn** (`XodIndexReplacedTests`; a script applies one break, runs, restores):

| Break | Red |
|---|---|
| B1: the block read doesn't compare the generation | `A_snapshot_another_context_wrote...` |
| B2, B3: a block that can't be read loads the type again (the first version's reload: forget the type, load the file as it is now) | `A_flush_that_meets...`, `A_delete_after...`, `A_replaced_snapshot...`, `A_snapshot_another_context_wrote...` |
| B4: `SaveBuilt` lets a failed save through | `A_query_isn_t_failed...` |
| B5: `DeleteIndexFile` lets a failed deletion through | `Deleting_a_table_s_last_row...`, `A_write_isn_t_failed...`, `An_index_file_that_couldn_t_be_deleted...` |
| B6: `AllSaved` ignores a file left undeleted | `A_write_isn_t_failed...`, `An_index_file_that_couldn_t_be_deleted...` |
| B7: the load doesn't refuse a file left undeleted | `An_index_file_that_couldn_t_be_deleted...` |
| B8: the snapshot writer leaves its `.tmp` when the move fails | `A_query_isn_t_failed...` |
| B9: `Remove` deletes the file itself, not through `DeleteIndexFile` | `Deleting_a_table_s_last_row...` |
| B10: the walk opens pages through `OpenFileOrCreate` (the row write still finds its page) | green: the row write keeps the update |
| B11: the row write returns when the page isn't held as bytes (the walk moves nothing) | green: the walk keeps the update |
| B12: both B10 and B11 | `An_update_after...` (both cases) |
| B13: `DeleteIndexFile` clears the record in a transaction | `A_rollback_doesn_t_bring_back...` |

Not proven by a test: the snapshot's own clearing of the record, skipped in a transaction (reaching it needs a
`Flush` inside a transaction after a deletion failed; `TASK-068` makes it unreachable: no index file is written
in a transaction, `ISS-100`); the refusal while a load replays the log (`loading`; reaching it needs a snapshot written
between two reads of one load), and the merge of the keys added since the load into the trees read from the
pages. That merge gives the same shape as a block read from the file; no test can tell it apart while every
row is on its page before its key is added (`XmlEngine.AddToPage`, `IOService.TryAppendRawRowToPage`).

`XodIndexLogTests`' helpers read the `-5` layout (the generation after the marker).

## 4. Independent review of the first version

A fresh reviewer had the change and the problem, not the design rationale, and probed it (scratch tests P1 to
P8). Its findings, each checked here before acting on it:

1. **Critical: a flush lost the changes it was saving.** The first version loaded the type again when a block
   couldn't be read, from the new file unless the type was "unsaved" (`dirtyTypes`). A flush takes its types out
   of that set before it saves them, and a snapshot reads every block first, so a large batch after another
   context's rebuild loaded the other file and dropped the batch's keys: rows the index didn't find, and a
   duplicate unique key accepted after reopening. A regression against `6624e12`.
2. **Critical: a delete lost its removals.** `RemoveRowElement` takes the type's log, then reads each property's
   block in turn; a reload part way through replaced the log and the trees under it, so the removals already
   made were lost and the rest recorded into a log nothing saved. The deleted key was refused again.
   **Structural cause of both:** a read performed a structural replacement of the type's whole index state,
   underneath callers in the middle of a change, deciding from a set that doesn't say whether changes are
   pending. Removed: a read now builds only the blocks it can't read, and touches nothing else (section 2).
3. **`FileShare.Delete` didn't do what it claimed** (a snapshot still can't replace a file a reader holds).
   Put back to `FileShare.Read`; the claim is gone.
4. Held files elsewhere: (a) an encrypted index's save from a query is held for the next flush in a batch or
   with buffered writes, so a query-only context's close (or a commit) failed on a held file: as before this
   task, filed as `ISS-109`; (b) with indexing on, saving the index of a write to a table whose index file is
   held fails, at the write or at the next flush: reported, as before this task; noted in `ISS-109`; (c) deleting a table's last row, or dropping a
   table, failed after the rows were gone: fixed (`DeleteIndexFile`); (d) a commit's undo deletes every index
   file, and one held open fails the undo, so the context refuses writes until reopened: filed as `ISS-110`.
5. A block read that meets another context's append (which shares nothing, for a moment) counts as replaced.
   With the in-place read it costs a walk of the table's pages, not correctness: filed as `ISS-111`.
6. Other contexts open at the same time trust an index file a context couldn't delete: the live gap of
   `ISS-043`'s mitigation, not new; the `ISS-042` row no longer reads as covering it.
7. Low: the encrypted snapshot doesn't clear `undeletedIndexFiles` (the next flush then deletes the file the
   snapshot wrote: a rebuild at the next open, no wrong answer); `ForgetTypeUnlocked` keeps the type's entries in
   the reverse map (`codeToIndexEntries`; a later build adds them again, and a removal of a stale pair finds
   nothing); a failed snapshot left its `.tmp` (fixed, B8). The first two are recorded in `ISS-111`.

## 5. Independent review of the second version

A fresh reviewer, briefed as the first, probed it (scratch tests PA to PF; the same probes against `6624e12`):

1. **High: an update after a replacement was lost** (probes PD, PD2). The walk opened each page through
   `OpenFileOrCreate`, which takes a page held as bytes out of that cache; the update had found its row's offset
   there, and its write then found no page and returned. `Update` returned true; the row stayed as it was, and the
   index held the new key. With the same rows in the new file, `6624e12` passed by chance (a regression); with more
   rows it threw. The work on `PLAN-056` found the same and filed it as `ISS-113`. **Cause:** the walk was a second
   user of the page caches that changed which cache a page sits in, under a writer holding an offset into one,
   and the writer's owner (`SpliceRowInRawPage`) reported nothing when the page was gone. Fixed both ways (section
   2): the walk moves nothing; the row write finds its page wherever it is held.
2. **Medium: a rollback brought back an index file recorded as untrusted, which was then trusted** (probe PB). The
   record of a file that couldn't be deleted describes the disk, but sat outside the transaction's undo, which
   changes the disk. Fixed: the record isn't cleared in a transaction.
3. **Medium, pre-existing: another context's read of an index file fails a writer's snapshot**, after the
   statement's rows are written (probe PC: 13 times in 189 rounds; the reader's 1,500 lookups all right). Filed as
   `ISS-115`.
4. **Low: a held index file still fails a write's flush, the close included**, with indexing on, or off when the
   file can be loaded (PA1 to PA3; the marker stays, so nothing stale is trusted). `ISS-109`'s wording widened.
5. **Low: claims.** The comment and this plan said a removal reads every block of its row's type first; it reads
   each block just before it takes a key out of it (the argument holds; corrected). The engine still spelled out
   its own selection of indexed properties (now `HasIndexedProperties`).

Also checked and found sound by the reviewer: the generation check under a concurrent rewriter (no wrong answer in
1,500 lookups), a buffered context's unsaved key, an encrypted query-only context with its index held, and
`File.Move` over a file open with `FileShare.Read | FileShare.Delete` failing on Windows (confirming section 4,
finding 3). Its Linux reasoning (a rename leaves an open handle on the old file, so the header and the block come
from one file) is from POSIX semantics, not checked against the .NET docs: unconfirmed. The lock order the walk
uses (the index lock, then the page cache's) has no reverse path today; it is written down nowhere but here.

## 6. Verdicts

| Change | Verdict |
|---|---|
| The generation (layout `-5`), checked in the block read's own open | Clean |
| Unread blocks read from the pages, in place | Clean: a read no longer replaces a type's index state; the one remaining cost is `ISS-111` |
| The walk moves no page; a row write finds its page wherever it is held | Clean |
| One walk of the pages, one row reading, one property selection | Clean |
| `SaveBuilt` best-effort | Mitigation: a cache save no longer fails a query; plain files only (`ISS-109` for encrypted ones) |
| `DeleteIndexFile` and `undeletedIndexFiles` | Mitigation: a file that can't be deleted no longer fails a write and isn't trusted by this context or after its close, a rollback included; other live contexts aren't told (`ISS-043`) |
| No `.tmp` left by a failed snapshot | Clean |
| `read.mjs` reads `-5`, requires it of a fresh set | Clean |
