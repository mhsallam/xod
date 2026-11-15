# PLAN-058: A standalone table's failed insert taken back, a commit's undo past held index files, an InsertRange's index save left to the flush (TASK-070)

Status: **in the tree** (2026-11-13), not released: no version change, nothing pushed. `ISS-120` fixed for `Insert`
(the rest of a standalone write's failed write is `ISS-125`); `ISS-110` fixed; `ISS-115` mitigated as its fix
direction proposes.
Issues: `ISS-120`, `ISS-110` (filed by the review of `TASK-068`, [`PLAN-057`](PLAN-057-review-of-task-066-direct-writes-triggers-transactions.md)
section 5, and of `TASK-067`, [`PLAN-055`](PLAN-055-index-files-replaced-or-held.md)), `ISS-115`. Found and fixed while
doing it: the crash writer the tests ran (section 1.6). Found and fixed by its independent review: a regression of
its first version (section 1.4), a guard test that proved nothing (1.5), a residue of `ISS-110` (1.2). Filed:
`ISS-124` to `ISS-126`.

## 1. What was wrong, and why

1. **`ISS-120`, a standalone table's insert reported failed, then written.** *Cause:* a write to a standalone table
   (no reference properties, no triggers, no transaction) runs under the engine's read lock and its table's write
   lock, beside writes to other tables, and had no statement: `BeginStatement` made one only inside the engine-wide
   write lock (`globalWriteDepth`), because the statement's log was one field of the engine. So the end of the
   statement (`IOService.WriteSaved`) failed outside any undo, and the row stayed in the cached page and in the
   index for the next flush to write. *The change makes it impossible for an insert:* the log is kept per thread
   (`ThreadLocal<StatementLog>`), and the standalone lock is counted per thread (`standaloneWriteDepth`), so every
   write's outermost `Create` or `UpdateItem` owns a statement and its undo, whichever lock it holds. The undo:
   - leaves what the statement and the undo saved and didn't write to the next flush (`IOService.LeaveSavedToFlush`,
     in a `finally`), so the statement's end doesn't throw over the failure the caller should see;
   - writes as a statement's end does, its own saves and the indexes of the tables it took rows from (section 1.4),
     and a write of its own that fails stays to be made (`ISS-098`) instead of refusing every later write: the
     cache is right, only the disk is behind (it used to fault the context: a page another program held failed
     the statement and then its undo);
   - writes nothing in a context closed while the statement ran (`ISS-103`): every file write refuses a closed
     context (`ThrowIfWritesRefused`, in `BeforeFileMutate`), and the caller sees `ObjectDisposedException`. The
     first version returned early from the undo for a closed context; its write in a batch had met the close and
     faulted the context. Without the batch that guard duplicated the owner's refusal, and breaking it left its test
     green (B5), so it was removed.

   **Not fixed:** the same bad state through `InsertRange`, `Update` and `Delete` (review finding F2, `ISS-125`).
   The undo takes back rows a statement created; a batch's write fails after its statements have ended, and a
   changed or deleted row has no undo (`ISS-089`, `ISS-090`).
2. **`ISS-110`, a commit's undo failed on an index file another program held.** *Cause:* the undo deleted every
   index file itself (`TransactionService.RestoreBeforeImages`, `dropIndexes`), a second writer beside the index
   service, which already left an index file it can't delete to the marker; one held open failed the undo, and the
   context refused writes. *Now:* the undo puts the pages back and asks the index service
   (`IndexService.DropIndexFiles`, through `TryDeleteIndexFile`, the one deletion of an index file in a live
   context). One it can't delete leaves a marker no context holds (`IOService.LeaveIndexesBehind`), so no context
   trusts an index file until an open alone drops them. The open's recovery from a journal leaves that mark before
   it restores, and its marker step drops the files (it used to delete them in the restore). *Residue found by
   the review (F6):* a transaction took an image of an index file it deleted (a table's last row deleted removes
   its index), and the commit's undo wrote it back with `File.Move`, which fails onto a held file. A transaction
   keeps no image of an index file now (`TransactionService.CaptureBeforeMutate`): it is derived, never written
   while a transaction is open (`ISS-100`), and the undo drops them all.

   What "held" means, per the documentation for .NET 8 and 9: `File.Delete` throws `IOException` when "the
   specified file is in use"; `File.Move` with `overwrite` throws `UnauthorizedAccessException` when it can't get
   exclusive access to the destination. Measured on Windows 11 NTFS by the review: a delete fails only when the
   holder doesn't share `Delete` (with `FileShare.Delete` it succeeds and the name goes); a replace fails whatever
   the holder shares. On Unix an open file can be deleted (not run here).
3. **`ISS-115`, an InsertRange failed on its index save after its rows were written.** *Cause:* on Windows a reader
   in another context (a load or a block read keeps the index file open) refuses the writer's replace of that file,
   and `InsertRange` saved its index after its batch's pages were written: it threw, the caller was told its rows
   weren't written, and a retry met their keys. *Mitigation, as the issue's first fix direction proposes:* an index
   save made after the statement's rows are written (`InsertRange`'s end, an undo's) doesn't fail the statement:
   `IndexService.SaveAfterRowsWritten` leaves it to the next flush, which reports it if it fails again; until then
   the type stays to be saved. The reader still refuses the replace; what changes is who is told.
   **Not fixed:** a commit's index save still fails the commit, which is undone (consistent, but a reader in
   another context fails commits to its table); and while the writer lives its marker is held, so the other
   contexts read the index file it couldn't save: a `Find` by key in another context missed rows a scan found
   (review finding F3, the same before this change; `ISS-126`). The comment that said "nothing reads a stale index"
   is corrected, and so is `ISS-109`'s status, which said the same.
4. **The undo opened the context's batch (review finding F1, a regression of this change's first version).**
   Standalone statements now have an undo, and the first version wrote it in a batch. A batch is the context's
   (`IOService.batchDepth`, `dirtyBatchFiles`), and statements on standalone tables run side by side: a statement on
   another table that ended while the undo's batch was open joined it, its end wrote nothing (`WriteSaved` returns
   in a batch), and its caller was told it succeeded before its row was written; when the undo's write then
   failed, the undo left it, and a crash lost that row (reproduced by the reviewer). *Now:* the undo writes as a
   statement's end does, with no batch: this thread's saves (`WriteSaved`), then the indexes of the tables it took
   rows from (`SaveAfterRowsWritten(types)`: not every type to be saved, since another thread's may be in the
   middle of its statement). **Not fixed:** `Delete` and `InsertRange` on a standalone table still open the batch
   (`ISS-124`).
5. **The `ISS-091` guard was untested (review finding F4).** The test injected the undo's failure before it had
   taken any row back, so the index and the pages agreed whatever the undo did, and removing the guard (the
   indexes of the statement's tables dropped) left every test green. The step now follows each row taken back
   (`undo-statement:n`); the test fails the undo after the first, and checks that both tables' index files are
   dropped: the other contexts trust index files while this context's marker is held.
6. **The crash writer the tests ran.** `XodCrashSafetyTests.WriterDll` took whichever built writer it found first,
   which can be one another build left (a Debug writer), built against an older library: a guard broken in the
   library left a crash-writer test green (B6 of the first round). It now runs the writer built in the tests' own
   configuration and framework, and fails clearly when there is none.

## 2. Tests

New:
- `XodTrustedIndexTests.An_insert_whose_page_write_failed_can_be_made_again` (standalone and relational): the page
  held, the insert fails, the retry succeeds, one row after a reopen.
- `XodTrustedIndexTests.Two_standalone_statements_side_by_side_each_take_their_own_row_back`: one statement held
  inside its own table while another fails on its held page: each keeps its own log.
- `XodStatementUndoTests.An_undo_whose_write_fails_leaves_it_to_be_made`: the undo meets the held page; the caller
  sees the statement's failure, the context goes on writing.
- `XodStatementUndoTests.An_undo_leaves_a_statement_on_another_table_its_own_write` (crash writer `undo-beside`:
  the undo paused, an insert on another standalone table meanwhile is on disk when it returns).
- `XodIndexReplacedTests.A_commit_undone_isn_t_failed_by_an_index_file_held_open`,
  `XodSharedAccessTests.A_commit_undone_at_the_open_isn_t_stopped_by_an_index_file_held_open` (marker left or not:
  2.0.0 and 2.0.1 write none), `XodSharedAccessTests.A_held_index_file_with_an_undone_commit_s_keys_isn_t_trusted`
  (crash writer `txmember`: the commit paused once its index files are written, the member index held with the
  commit's key in it while the commit fails and is undone; then, the file let go, the context inserts that key),
  `XodIndexReplacedTests.A_commit_undone_writes_no_index_file_back`.
- `XodIndexReplacedTests.An_insert_range_isn_t_failed_by_another_context_reading_its_index_file`.

Changed to the designed behaviour: `XodStatementUndoTests.An_undo_that_fails_leaves_the_index_agreeing_with_the_pages`
(the fault injected mid-undo, the index files' drop checked), `XodStatementUndoTests.A_row_whose_page_write_fails_is_taken_back`,
`XodIndexCrashTests.An_index_save_that_failed_is_made_by_the_next_flush` (the statement no longer throws).

## 3. Each guard broken

Each guard broken alone, the affected classes run on net9.0 (the crash writer rebuilt against the broken library),
every file restored and checked by hash afterwards.

| Break | Guard broken | Result |
|---|---|---|
| B1 | standalone writes get no statement (`BeginStatement` only under the engine-wide lock) | red: `An_insert_whose_page_write_failed_can_be_made_again`, `Two_standalone_statements_side_by_side_each_take_their_own_row_back`, `An_undo_leaves_a_statement_on_another_table_its_own_write` |
| B2 | one statement log for the context | red: `Two_standalone_statements_side_by_side_each_take_their_own_row_back` |
| B3 | an undo whose write fails faults the context | red: `An_undo_whose_write_fails_leaves_it_to_be_made` and three more |
| B4 | a failed statement's saves left to its end (`LeaveSavedToFlush` removed) | red: `An_undo_whose_write_fails_leaves_it_to_be_made` |
| B5 | an undo in a closed context returns early (the guard removed) | green: the guard duplicated `ThrowIfWritesRefused`, and was removed (section 1.1) |
| B6 | no fault step in the undo | red: `An_undo_that_fails_leaves_the_index_agreeing_with_the_pages`, `An_undo_leaves_a_statement_on_another_table_its_own_write` |
| B7 | the undo opens the context's batch (the first version) | red: `An_undo_leaves_a_statement_on_another_table_its_own_write` |
| B8 | a failed undo keeps the indexes (`indexService.Remove` removed) | red: `An_undo_that_fails_leaves_the_index_agreeing_with_the_pages` |
| B9 | an undone commit's index files not dropped | red: `A_commit_that_fails_after_its_indexes_leaves_no_index_file`, `A_commit_that_fails_after_buffering_its_indexes_does_not_write_them_later`, `A_commit_undone_isn_t_failed_by_an_index_file_held_open` |
| B10 | an index file the undo can't drop left trusted (no marker) | red: `A_held_index_file_with_an_undone_commit_s_keys_isn_t_trusted`, `A_commit_undone_isn_t_failed_by_an_index_file_held_open` |
| B11 | the journal's undo leaves no mark | red: `A_commit_undone_at_the_open_isn_t_stopped_by_an_index_file_held_open` |
| B12 | a transaction keeps an index file's image | red: `A_commit_undone_writes_no_index_file_back` |
| B13 | an `InsertRange` fails on its index save after its rows are written | red: `An_insert_range_isn_t_failed_by_another_context_reading_its_index_file`, `An_index_save_that_failed_is_made_by_the_next_flush` |

B10 was green at first: the new test held the index file through the context's later writes, whose own failed
saves left the ordinary marker at the close, and the pause let the open's guess by time (a page more than 50 ms
newer than its index) catch the stale file. The test now lets go of the file after the undo and brings its time level
with the restored pages, as an undo with no pause leaves it, before the context reuses the undone commit's key.

## 4. The independent review

A fresh reviewer had the change, the problems as filed and the four questions, not the design; read-only, it built
and ran the suite in a copy and wrote probes. Its findings, each checked here:

| Finding | What it is | Done |
|---|---|---|
| F1 | the undo's batch takes in another standalone statement's write (reproduced) | fixed (section 1.4); `Delete`, `InsertRange`: `ISS-124` |
| F2 | `InsertRange`, `Update`, `Delete` on a standalone table: reported failed, then written; `InsertRange` without a unique key stores its rows twice (reproduced; the same before) | filed `ISS-125` |
| F3 | another context reads an index save left for the flush (reproduced; the same before); a comment said nothing stale is read | comment and `ISS-109`'s status corrected; filed `ISS-126` |
| F4 | the `ISS-091` guard untested (mutation stayed green) | fixed (section 1.5), B8 |
| F5 | the held-file test never held a file with the undone commit's keys | new test (section 2), B10 |
| F6 | a transaction's image of an index file written back over a held one (reasoned) | fixed (section 1.2), B12 |
| smaller | `WriteSaved`'s doc comment attached to the new method; "the next statement's end" where only a statement saving the same file writes it | fixed |

Second copies (question 2): the statement's state has two owners, the engine (`statementLogs`,
`standaloneWriteDepth`, `globalWriteDepth`) and `StatementSaves`, kept in step by the undo calling
`LeaveSavedToFlush` (added to `ISS-121`). Index files are dropped in two places on purpose: a live context
(`TryDeleteIndexFile`) writes a new marker for one it can't drop, since its own marker is held and can't say behind;
an open alone (`DropIndexesLeftBehind`) keeps the markers it found. The statement's end is written in four places
(`ISS-121` item 1, unchanged).

The two `ThreadLocal`s are not disposed: a close racing a write on another thread (`ISS-103`) would meet a disposed
one, and their finalizer frees them (per the `ThreadLocal<T>` documentation).

## 5. Verdicts

| Change | Verdict |
|---|---|
| `ISS-120` | clean for `Insert`; the rest of the bad state filed (`ISS-125`) |
| `ISS-110` | clean, with named debt: a marker no context holds makes every context rebuild indexes from the pages until an open alone |
| `ISS-115` | mitigation (the reader still refuses the replace); commits still fail on it; stale reads filed (`ISS-126`) |
| the undo without a batch | clean for the undo; `Delete` and `InsertRange` filed (`ISS-124`) |
| `WriterDll` | clean |

Named debt besides: a crash after an undo whose write was left to the flush can leave on disk a row its caller was
told failed (not across a crash, `ISS-090`).

## 6. Gate

The working tree, with the other sessions' uncommitted work in it: the library builds with no warning; 380 of
380 tests pass on net8.0 and on net9.0; the conformance set (14 of 14 on each), the format reader on it and on
data written fresh, and the benchmark tests (26 of 26) pass.

The commit alone (this task on `830d05b`, exported from the index): 362 of 362 on net8.0 and on net9.0 on Windows,
and 362 of 362 on Linux (the .NET 9 SDK container, no network, a user other than root).
