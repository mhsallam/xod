# PLAN-057: The review of `TASK-066`: failed direct writes, a forgotten transaction's index files, flushes and closes from triggers, markers others hold, a close racing a write, deletes in a transaction (TASK-068)

Status: **in the tree** (2026-11-13), not released: no version change, nothing pushed. `ISS-099`, `ISS-100`,
`ISS-101`, `ISS-103` and `ISS-107` fixed, `ISS-099` and `ISS-107` with named debt; `ISS-102` fixed for an open alone
only (`ISS-119`); `ISS-109` fixed for a rebuilt index.
Issues: `ISS-099` to `ISS-103`, `ISS-107` (filed by the review of `e62bc30`, [`PLAN-054`](PLAN-054-indexes-left-behind-by-a-crash.md)
section 8). Found and fixed while doing it: an index saved before its statement's page with no mark (section 1.7),
one file written twice at once (section 1.8), and the regressions the independent review found in the first version
(section 5). Filed: `ISS-119` to `ISS-121`.

## 1. What was wrong, and why

Each cause, and what the change makes impossible.

1. **`ISS-099`, a direct page write that failed was forgotten.** A change to a cached page or table became a write
   still to make only when its caller saved it, and in direct mode (no batch, no buffer: the default) the save wrote
   it at once. So a change made without a save (an insert counts its row in the table file's page entry; a statement
   that fails part way, as when its trigger throws, can leave one) and a save whose write failed were in no set of
   writes still to make, and the next flush called the indexes caught up while the cache held rows the disk didn't. *The change makes
   it impossible* by moving the decision to the owner of the cache: the document of every cached file reports each
   change (`XObject.Changed`, `IOService.Watch`), and `IOService.FilePendingUnlocked` is the one place a change
   becomes a write to make (raw pages: `RawPagePendingUnlocked`, called before a cached raw page changes). In direct
   mode what a statement saved is written when the statement ends (`IOService.WriteSaved`, called by
   `XmlEngine.ExitTableWriteLock` at the outermost write, and inside `Create` and `UpdateItem`'s statement undo), after
   its index changes; a write that fails stays to be made, and the call that ended the statement throws.
   What a statement saved is kept by the thread running it (`StatementSaves`): statements on standalone tables run
   side by side, one per table, and one statement's end writes only its own (section 5, finding 8). In a transaction a
   changed file's before-image is taken before the file joins the batch, so a capture that fails is tried again by the
   next change (finding 1). A new database's file is written by the open itself (`SaveNow`), not at the first
   statement's end (finding 2). The page count an insert keeps in its table file's page entry is changed under
   `IOService.ChangeHint`, which the watch doesn't count as a change to write: that count is exact on disk only as of
   the file's last write (`PLAN-046`, `ISS-055`), and writing the table file for it made a flush after every insert
   take twice as long (section 4).
2. **`ISS-100`, a forgotten transaction's index files.** Index files were written while a transaction was open
   (its statements' saves, and a `Flush` inside it), holding the transaction's keys while its pages waited in its
   batch; a commit refused before its journal put nothing back. *Impossible now*: `IndexService.SaveToDisk`, the
   one writer of an index file, keeps the type to be saved by the commit while a transaction is open (the commit saves
   it once its pages are written; a rollback forgets it). `IndexService.SaveBuilt` (an index rebuilt for a query)
   saves nothing in a transaction: a cache, saved now or not at all; held for the commit, an index file another
   program holds would have failed a commit that only read the table. Outside a transaction it is written now or not
   at all, with a password too (`IOService.SaveCacheNow`; section 5, finding 3): queued for the next statement's end or
   flush, an encrypted index file another program held failed every later write and the close (`ISS-109`, which said
   so of buffered writes). A second copy of the first rule, in `FlushDirtyIndexes`, was removed: it hid the rule from
   its test (section 3, B3).
3. **`ISS-101`, a flush or close from a trigger.** `XmlEngine.FlushToDisk` asked
   `ReaderWriterLockSlim.IsWriteLockHeld`, which is true in a recursive write and which its documentation reserves
   for asserts. *Now*: the engine-wide write lock is taken and let go through one pair, `EnterExclusive` and
   `ExitExclusive` (and the close), which count it (`exclusiveDepth`); no write of the context is under way when this
   thread holds it and is inside none of its own writes (`NoWriteUnderWay`: `exclusiveDepth > 0 &&
   globalWriteDepth == 0`). A flush from a trigger therefore leaves the indexes marked behind. A close from inside
   one of the context's own writes is refused before it does anything (`InvalidOperationException`); a close from
   inside a read takes no write lock (`EnterWriteLock` throws `LockRecursionException` there, per its documentation),
   flushes without it and leaves its marker. `DropType` became a statement like any write.
4. **`ISS-102`, a crash's marker another program holds.** `ProbeMarker` read every `IOException` as a live context.
   *Now*: only a refusal for sharing reads as held (`HeldByAnother`: Windows' sharing and lock violations; on Unix
   `EWOULDBLOCK`, the one `flock` failure .NET throws for: `SafeFileHandle.Unix.cs`, dotnet/runtime release/8.0 and
   9.0; 11 on Linux, 35 on macOS and the BSDs, the latter not confirmed against Apple's headers); any other failure
   reads as behind unless the marker is empty. An open that has the database to itself drops the indexes even for a
   held marker that isn't empty: no live context's marker can be held then, but for a password change's.
   **This fixes an open alone only.** An open while another context has the database runs no recovery, and its
   index loads still read a held marker as live, so a crash's marker that a program holds without sharing keeps the
   stale indexes trusted there, as before (section 5, finding 4; filed as `ISS-119`). A reader that shares reading,
   as backup tools do, never failed the probe on Windows or Linux: it already read as behind. The same defect sat in
   `ProveExclusive` and `OpenLockFile`, which read every `IOException` as held; both now ask `HeldByAnother`. In
   `ProveExclusive` that was the unsafe side (any failure of the second open counted as proof of the hold): one that
   fails for another reason now lets the hold go and fails the open (`DatabaseLockedException`).
5. **`ISS-103`, a close racing a write.** The close let the write lock go before the hold and the marker, and writers
   didn't ask whether the context was closed. *Now*: the close sets `disposed` and lets go of the hold and the marker
   while it still holds the write lock; `EnterRead` and `EnterExclusive` refuse once closed (`ObjectDisposedException`),
   asked after the lock is taken. The locks themselves aren't disposed any more: a thread may still wait on one.
   A close from inside a read takes no write lock, so a standalone table's write on another thread, already past
   `EnterRead`, went on after the close: it landed in a database another context then held alone, and left a marker
   no context closed (section 5, finding 5). The file service now refuses every write once the close has let go of
   the hold and the marker (`IOService.released`, asked in `NoteFileMutation`, which every page, table and index
   write passes first), so that write fails and nothing of it lands. A second close that waited for the first one's
   write lock returns once it has it (finding 6).
6. **`ISS-107`, deletes in a transaction.** A delete that emptied a page deleted the file at once, outside the
   transaction's batch. *Now* `IOService.Delete` sets the file aside (`<file>.xdel`) while a transaction's batch is
   open: deleted once the commit has written its files (`EndBatch`; one that can't be deleted fails the commit, which
   is undone); put back by a rollback (`DiscardBatch`; one that can't be put back faults the context, which then
   refuses writes); after a crash, the next open alone deletes one whose file is back (a journal's undo) and puts back
   the others (`PutBackSetAsideFiles`, after the journal). A file set aside counts as a write pending
   (`WritesPending`), so the open does its recovery for it alone.
7. **Found while building it: an index saved before its statement's page, with no mark.** Writing a statement's
   pages when it ends meant a row that adds a page saved its table's index first. When that was the first write
   since the indexes caught up, no marker said they were behind, so a crash before the page left a key with no row,
   trusted. *Now* `IOService.NoteFileMutation` marks the indexes behind for an index file written while writes of the
   context still wait. (A first version marked on any change while anything waited; it deferred every index save in
   direct mode with hints pending and broke `XodIndexLogTests`; withdrawn.)
8. **Found by the suite on the port: one file written twice at once.** The sets of writes still to make held the
   documents, not the files, and an encrypted index is saved as a new document each time. The first insert into a
   table emptied earlier builds its index and saves it, then saves it again with the row: two documents of one file,
   which the statement's end wrote side by side (from four files): they collided on the file's `.tmp` ("being used
   by another process", in about one suite run in two), or the older could land last. Introduced by item 1's
   statement-end write, which put both saves in one set (buffered mode never held both). *Now* `PendingFiles` is the
   owner of those sets: one write per file, known by its name, of the document last saved for it (the one the cache
   holds).

## 2. Tests

`tests/Xod.Tests/XodTrustedIndexTests.cs` (25 cases), `XodOneWritePerFileTests.cs` (1), and two in
`XodIndexCrashTests.cs`. Run against the committed code (`9a34e74`, `git archive`), 17 fail; why each passing one
still belongs is below. The tests added for the review's findings 1 to 3 pass there: those were regressions of this
change's first version, and breaks B18 to B20 prove the tests.

| Test | Issue | At `9a34e74` |
|---|---|---|
| `A_direct_page_write_that_failed_isn_t_forgotten` (4 cases) | `ISS-099` | 2 red (JSON without a second write, XML with one) |
| `A_direct_update_whose_page_write_failed_isn_t_forgotten` (2) | `ISS-099` | red |
| `A_statement_a_trigger_fails_leaves_the_index_agreeing_with_the_pages` | `ISS-099` (the watch) | red |
| `A_commit_refused_before_its_journal_leaves_no_keys_behind` | `ISS-100` | red |
| `A_query_in_a_transaction_doesn_t_fail_its_commit_on_a_held_index_file` (2: plain, password) | `ISS-100` (`SaveBuilt`) | green: guards a regression item 2 would bring (B5) |
| `A_close_from_a_trigger_doesn_t_split_the_index_from_the_pages` | `ISS-101` | red |
| `A_flush_from_a_trigger_leaves_the_indexes_marked_behind` | `ISS-101` | red |
| `A_crash_marker_another_program_holds_doesn_t_keep_a_stale_index_trusted` | `ISS-102` | red |
| `A_closed_context_refuses_writes` | `ISS-103` | green: the old close disposed the locks, which then threw `ObjectDisposedException`; the new close keeps them (B10) |
| `A_commit_refused_before_its_journal_keeps_the_rows_it_deleted` | `ISS-107` | red |
| `A_transaction_s_deletes_leave_nothing_aside` (2) | `ISS-107` | green: nothing was set aside (B12, B13) |
| `A_set_aside_file_a_commit_couldn_t_delete_is_cleared_by_the_next_open` | `ISS-107` | red (Windows only: elsewhere a held file can be deleted) |
| `A_rollback_that_can_t_put_back_a_page_refuses_writes` | `ISS-107` | red (Windows only) |
| `XodIndexCrashTests.A_crash_before_a_commit_keeps_the_rows_its_deletes_took` | `ISS-107` | red |
| `XodIndexCrashTests.A_crash_before_a_statement_s_pages_are_written_leaves_no_key_in_the_index` | item 7 | red, but only because the crash step it uses doesn't exist there; B6 proves it |
| `XodOneWritePerFileTests.A_statement_writes_an_index_it_saved_several_times_once` | item 8 | red (two writes in order there; side by side in the tree before `PendingFiles`) |
| `A_commit_undone_keeps_no_row_of_a_page_whose_first_capture_failed` | finding 1 | green: guards the first version's regression (B18) |
| `A_new_database_s_file_is_on_disk_after_the_open`, `An_AES256_key_one_context_stored_survives_another_context_s_close` | finding 2 | green: guard the first version's regression (B19) |
| `A_read_that_rebuilt_an_encrypted_index_doesn_t_fail_later_writes` (2) | finding 3, `ISS-109` | buffered red; direct green (B20) |
| `A_write_under_way_when_a_close_comes_from_inside_a_read_doesn_t_land_after_it` | `ISS-103`, finding 5 | red |

On Unix a page "held" by a test is made read-only, which fails a write made in place but not a whole page renamed
over it (JSON storage): there the failed-write tests check only that the index and the pages agree.

`XodStatementUndoTests.An_undo_that_fails_leaves_the_index_agreeing_with_the_pages` was changed: its trigger reads the
table file to find the page, which a statement's first row no longer puts on disk before the statement ends; it now
inserts a row first, and counts the refused row by its own id. What it checks is unchanged.

## 3. Breaks

Each guard broken alone (`scratchpad/breaks068.py`, `breaks068c.py` for B16 to B21a after the review), and the
TASK-068 test classes run against it (net9.0, Windows).

| Break | Result |
|---|---|
| B1 changes to a cached file not watched | red: `A_statement_a_trigger_fails...`, `A_close_from_a_trigger...` |
| B2 a direct save of a document written at once | no correctness test red; the one-write test red (two writes) |
| B2r a direct row append to a raw page written at once | red: `A_direct_page_write_that_failed_isn_t_forgotten` |
| B2s a direct row update in a raw page written at once | green |
| B3 an index file saved inside a transaction | red: `A_commit_refused_before_its_journal_leaves_no_keys_behind` (green while `FlushDirtyIndexes` repeated the rule; that copy was removed) |
| B5 a rebuilt index saved inside a transaction | red: `A_query_in_a_transaction...` |
| B6 an index file written while writes wait not marked | red: `A_crash_before_a_statement_s_pages...` |
| B7 a flush from a trigger calls the indexes caught up | red: `A_flush_from_a_trigger...`, `A_close_from_inside_a_read...` |
| B8 a close from a trigger not refused | red: `A_close_from_a_trigger...` (green until the test checked that the context stays open) |
| B9 every `IOException` from the probe read as held | green: no test makes the probe fail but for sharing (`ISS-121`) |
| B10, B10b a closed context not refused at the write lock, at the read lock | red: `A_closed_context_refuses_writes` |
| B11 a delete in a transaction not set aside | red: `A_commit_refused_before_its_journal_keeps_the_rows...`, `A_crash_before_a_commit...` |
| B12 set-aside files not deleted at the commit | red: `A_transaction_s_deletes_leave_nothing_aside` |
| B13 set-aside files not put back by a rollback | red: `A_transaction_s_deletes_leave_nothing_aside` |
| B14 set-aside files not put back at the open | red: `A_crash_before_a_commit...` |
| B15 a set-aside file not a write pending | red: `A_set_aside_file_a_commit_couldn_t_delete...` (green before that test) |
| B16 pending files known by their document | red: `A_statement_writes_an_index_it_saved_several_times_once` |
| B17 a rollback that can't put back a file sets no fault | red: `A_rollback_that_can_t_put_back...` |
| B18 a file joins the batch before its capture (finding 1) | red: `A_commit_undone_keeps_no_row...` |
| B19 a new database's file queued at the open (finding 2) | red: `A_new_database_s_file...`, `An_AES256_key...` |
| B20 a rebuilt encrypted index queued (finding 3) | red: `A_read_that_rebuilt_an_encrypted_index...` |
| B21a writes after the close not refused by the file service (finding 5) | red: `A_write_under_way_when_a_close...` (green while a second copy of the refusal, in `ExitTableWriteLock`, stood in for it; that copy was removed) |

B2 and B2s: writing a statement's saved documents and its row updates at once, rather than when it ends, keeps the
index and the pages agreeing in every test (the statement's own undo, or the update's order of writes, covers a write
that fails). Only the row append needs it. The statement-end write is kept for all three as one rule with one writer
(`WriteSaved`): a statement's writes are made once, after its index changes, and a file saved twice in a statement is
written once (B2's red).

## 4. Gate, Linux, cost

- Final tree: gate (`scratchpad/gate18.sh`) green, tests 364/364 on net8.0 and net9.0 (the count includes the
  `PLAN-056` tests then in the working tree), conformance 14/14 each, `read.mjs` on stored and fresh databases,
  bench 26/26; Linux 364/364. After two test cases were added (the password and buffered cases), 366/366 on each
  framework.
- First version: gate (`scratchpad/gate17.sh`, PLAN-053 section 6): library build clean; tests 344/344 on net8.0 and net9.0;
  conformance 14/14 on each; `read.mjs` on the stored databases and on fresh ones; bench 26/26. The full suite ran
  three more times on each framework, 339/339 each before the last tests were added (the one-write failure, about one
  run in two before `PendingFiles`, didn't recur).
- Linux (`mcr.microsoft.com/dotnet/sdk:9.0`, `--network none`, a non-root user, a folder mounted read-only for the
  read-only-media test): 344/344.
- Cost (`scratchpad/perf068`, median of 5 runs, the committed code against the final tree, Release, net9.0):
  1,000 inserts with a flush each 511 / 431 ms; 20,000 inserts then a close 2,415 / 2,139 ms; 5,000 updates then
  a flush 15,179 / 15,283 ms; 20 transactions of 100 inserts 73 / 73 ms; a transaction deleting 5,000 rows 25 /
  31 ms; 2,000 inserts under a password 28,292 / 28,126 ms. Two later rounds were disturbed by load on the machine
  (slow runs that came and went within one process), so the short workloads were run three more times,
  interleaved and in run order: within noise of each other. The first version's flush per insert took twice as
  long (watching the page count); `ChangeHint` removed that.

## 5. Independent review

A fresh agent reviewed the first version in a copy of the tree, given the diff and the issues, not this plan's
reasoning, and probed each claim with tests of its own in both the changed tree and the one before it. Its verdict:
two High regressions, so it shouldn't land as it was. Each finding, and what was done:

| # | Finding | Response |
|---|---|---|
| 1 | High, regression: in a transaction a file joined the batch before its before-image was taken; a capture that failed (a page another program held) left it in the batch with nothing to put back, and no later change tried again, so a commit undone after writing it kept the transaction's row | Fixed: the capture comes first (`FilePendingUnlocked`, `RawPagePendingUnlocked`). Test `A_commit_undone_keeps_no_row_of_a_page_whose_first_capture_failed`; B18 |
| 2 | High, regression: a new database's file was queued for the first statement's end, so it wasn't on disk after the open; a second context's close then wrote its copy over the AES256 key another had stored, and the value no longer read | Fixed: the open writes it (`SaveNow`). Tests `A_new_database_s_file_is_on_disk_after_the_open`, `An_AES256_key_one_context_stored_survives_another_context_s_close`; B19 |
| 3 | Medium, regression: with a password, an index a read rebuilt was queued, and a held index file then failed every later write and the close | Fixed: `IOService.SaveCacheNow`, written now or not at all. Test `A_read_that_rebuilt_an_encrypted_index_doesn_t_fail_later_writes` (direct and buffered, `ISS-109`); B20 |
| 4 | Medium: `ISS-102` isn't fixed while another context has the database open: a held marker still reads as live there | Not fixed here: it needs a liveness signal that doesn't rest on the file lock. Filed as `ISS-119`; section 1.4 says so |
| 5 | Low/Medium: a close from inside a read let a standalone table's write on another thread land after it, in a database another context then held alone, leaving a marker no context closed | Fixed: the file service refuses writes once the close let go of the hold (`IOService.released`). Test `A_write_under_way_when_a_close_comes_from_inside_a_read_doesn_t_land_after_it`; B21a |
| 6 | Low, reasoned: two closes at once could close twice | Fixed: a close that waited for the write lock returns once it has it, if the other closed. Reasoned, no test (`ISS-121`) |
| 7 | Debt: a standalone table's insert whose write failed is reported failed, then written (no statement there) | Filed as `ISS-120` |
| 8 | Medium/Low, reasoned: a statement's end wrote every thread's saves, and could serialize a document another thread was changing | Fixed: `StatementSaves`, per thread. Reasoned, no test (`ISS-121`) |
| 9 | Tests: `A_closed_context_refuses_writes` passed before the change and tested no race; the held-index query test used no password; no test covered findings 1 and 2; CA1416 warning | The race has its test (finding 5); the query test runs with and without a password; findings 1 and 2 have tests; the warning is gone (the Unix-only code moved into a method marked for it) |
| - | Second copies: `ProveExclusive` and `OpenLockFile` read any `IOException` as held | Fixed (`HeldByAnother`; section 1.4) |
| - | Second copies: the statement end in four places; two put-back routines; `Dispose`'s own lock taking; the raw append path's batch branch | Filed as `ISS-121` |
| - | `ISS-107` debt: a rollback whose put-back fails skips `restoreFiles`; a commit failing after it took its set-aside files leaves them to the next open alone | Filed as `ISS-121` |

Its dependency claims were checked against the documentation for the versions in use: `EnterWriteLock` throws
`LockRecursionException` for a thread in read mode without the write lock, and `IsWriteLockHeld` is for asserts
only (both the .NET 9 `ReaderWriterLockSlim` reference); disposing a held lock throws `SynchronizationLockException`
(seen); a sharing violation reports `0x80070020` on Windows and 11 on Linux (.NET 9 SDK container). Not confirmed:
35 on macOS, and `ERROR_LOCK_VIOLATION` from an open.

## 6. Verdicts

| Change | Verdict |
|---|---|
| Every change to a cached file watched; one place makes it a write; a statement's writes made when it ends, per thread (`ISS-099`) | Clean with named debt: a standalone table's insert has no statement (`ISS-120`); the statement end is decided in four places (`ISS-121`) |
| `PendingFiles`: one write per file, by name | Clean |
| No index file written inside a transaction; a rebuilt index written now or not at all (`ISS-100`, `ISS-109`) | Clean |
| `NoteFileMutation` marks an index written while writes wait | Clean |
| `exclusiveDepth` and `NoWriteUnderWay`; a close from a write refused (`ISS-101`) | Clean, with `Dispose`'s own lock taking argued at the site (`ISS-121`) |
| `HeldByAnother` in the probe, `ProveExclusive` and `OpenLockFile` (`ISS-102`) | Fixes an open alone; does not fix an open beside another context (`ISS-119`) |
| A closed context refused at its locks and by the file service (`ISS-103`) | Clean |
| A transaction's deletes set aside (`ISS-107`) | Clean with named debt (`ISS-121`) |
| A new database's file written by the open; a capture before the batch; `ChangeHint` | Clean |

## 7. Remaining debt

- `ISS-119`: a crash's marker held without sharing reads as live while another context has the database open.
- `ISS-120`: a standalone table's insert whose write failed is reported failed, then written.
- `ISS-121`: the statement end in four places; two put-back routines; `Dispose`'s lock taking; a rollback whose
  put-back fails skips `restoreFiles`; set-aside files a failed commit leaves to the next open; the raw append
  path's batch branch; untested: B9 (no test makes the probe fail but for sharing, which the test machines can't
  stage), two closes at once, the per-thread saves.
- `ISS-104` (no locks on the file system) stays open, as filed.
- `ISS-110` (a commit's undo deletes the index files itself, past `IndexService.DeleteIndexFile`) is not part of this
  task.
