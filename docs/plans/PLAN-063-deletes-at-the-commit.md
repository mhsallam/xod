# PLAN-063: a transaction's deletes take effect at its commit (`ISS-166`)

- **Task ID**: `TASK-077`
- **Status**: Done (2026-11-16). `ISS-166` fixed for transactions of this version: a transaction changes no data file
  before its commit, deletes included, so no other context sees a table it dropped as gone, and nothing is set aside to
  lose. A file an older version's crashed transaction set aside is still put back by the open, with the old rule.
- **Related**: [`PLAN-062`](PLAN-062-commit-gate.md) (the commit gate, which reproduced this), [`PLAN-054`](PLAN-054-indexes-left-behind-by-a-crash.md)
  section 8 and `ISS-107` (the set-aside this replaces), [`PLAN-046`](PLAN-046-file-format-spec.md) (the files)

## 1. The cause

A transaction's delete took effect on disk before its commit: since `ISS-107` the file was renamed aside to
`<file>.xdel` at once, so that a rollback could put it back. Every other context then saw the file gone. For a page that
mattered little (its name is random, and only a context that had read it wrote it again), but a table file has a fixed
name, `{Type}.xtab`. A transaction that dropped a table (`Drop<T>`), or deleted its last rows (a table with no
autonumber mark goes with its last page), left another context that had not read it free to insert into a new table at
that name. The rollback could not put the old table back over the new one and faulted its context; the next open alone
took the file at its name as a journal's undo and dropped what was set aside, with the old rows. A crash before the
commit led to the same open.

So the rows were lost because a transaction changed the disk before its commit. The change makes that impossible:
**a transaction changes no data file before its commit, deletes included.** Its writes already waited for the commit
(`ISS-100`, `ISS-164`); its deletes now wait too.

## 2. The change

- **Deletes wait for the commit** (`IOService.DeleteUnlocked`). Inside a transaction a delete of a data file records its
  name in `pendingDeletes` and leaves the file where it is. The mutation hook still runs first, so the transaction's
  before-image of the file is taken, as for a write.
- **The commit makes them** (`IOService.EndBatch`), holding the commit gate exclusively (`PLAN-062`), after its journal,
  which holds each file's bytes, and before the batch's writes. A file deleted and made again in one transaction (a table
  dropped and made anew) isn't deleted: the batch's write replaces it, so its name is never deleted and taken again in
  one commit (on Windows without POSIX delete a deleted file keeps its name until the last handle on it closes, per the
  `DeleteFileW` documentation, and a reader elsewhere could have failed the write). A delete that fails fails the
  commit, and its undo writes back every file from the journal, the deleted ones included (`RestoreBeforeImages` writes
  a file whether or not it is there).
- **Index files are the index service's** (`IndexService.DeleteIndexFile`, "the one deletion of an index file"). An
  index file is a cache outside every journal. Inside a transaction the index service doesn't delete it: it marks it
  (`undeletedIndexFiles`, so this context never loads it) and the commit's index flush deletes it after the pages are
  written, a file another reader holds being left for a later flush and never loaded meanwhile, as at any other time
  (`ISS-042`). `IOService` refuses an index delete inside a transaction, so no second path deletes one there. A
  rollback leaves the mark: the index is rebuilt from the pages when next needed, and the file replaced by the rebuilt
  index or deleted at the next flush, whichever comes first.
- **A rollback forgets them** (`DiscardBatch`, and `DiscardPendingWrites` with every other pending write). It touches no
  file, so it can't fail on one, and doesn't wait for the commit gate.
- **The commit's comparison** (`PLAN-062`) reads a deleted file at its name, where it still is: a write another context
  made to it since refuses the commit, as for any file the transaction changed. `BytesOnDisk` no longer reads a
  set-aside copy.
- **The transaction's own view** (`IOService.DeletedHere`, `OnDiskHere`). For the context whose transaction deleted it,
  the file is gone: every read of the disk asks first. `FileExists`, `OpenFileOrCreate`, `Load`, `PageForReading`,
  `Size`, the raw page reads and prefetches, the lists of pages and tables (`PageFileNames`, `TableFileNames`,
  `GetPageType`), and the statistics and `RegisteredTypes`. A dropped table's index file is never loaded again in the
  same transaction: the index service marked it. A file this context holds
  (cached) is read from what it holds first, so one made again in the transaction is the new one.
- **The open** still puts back a file set aside (`PutBackSetAsideFiles`), for a crash of an older version's transaction.
  Its rule is unchanged, and so is its limit (a table file made again at its name is kept, and the set-aside rows are
  dropped): no transaction of this version leaves such a file.
- **Gone with the set-aside**: the rollback's put-back (`PutBackSetAside`, `PutBackOne`), its gate, its failure handling
  and the fault it set, and the exception type for a write refused at the gate (`CommitGateBusyException`), whose only
  reader was that handling (the write still throws `XodException`).

## 3. Tests, seen red first

New, in `XodCommitGateSideTests`, against the engine before the change:

- **A table dropped in a rolled-back transaction stays whole for another context**: the other context inserts into the
  table while the drop is pending; after the rollback and a reopen, every row is there, the old ones and the other
  context's. Red: the rollback threw ("couldn't be put back"), the other context had made a new table.
- **A drop committed over another context's write is refused, and every row stays.** Red: the commit was refused, but
  its undo couldn't put the table back beside the other context's new one, and the context refused every write after.
- **A table emptied in a rolled-back transaction stays whole for another context** (`ISS-166` as filed: the last rows
  deleted). Red as the first.
- **A transaction changes no file before its commit**: a delete that empties a page, an update and an insert, and every
  data file is byte for byte as it was until the commit. Red: the emptied page was renamed aside.
- **A transaction sees its own drop and can make the table again**: dropped, the table and its rows are gone for it, its
  index too (it inserts a key the old rows held); rolled back, every row is back; committed, only the new row and its
  page are left. Passed before too: the old design hid a deleted file from its context by moving it. It guards the reads
  (the breaks below).
- **A transaction's lists leave out a table it dropped** (with the schema from the classes, and from the table files):
  SQL, `RegisteredTypes` and `GetStatistics` show inside the transaction what they show once the drop is committed.
  Passed before too, for the same reason; added when the breaks of the lists went uncaught.

Rewritten, as the behaviour they tested is gone:

- **A commit deleting a page another context wrote since is refused** (was: a page set aside and written again). Red:
  the page was aside, not on disk.
- **A rollback doesn't wait for another context's commit** (was: a rollback that couldn't get the gate to put pages back
  faulted its context). Red: the rollback threw.
- **A delete the commit can't make is undone, and every row stays** (`XodTrustedIndexTests`, Windows only: the emptied
  page held open by another program). Red: no page on disk to hold.
- **A rollback needs no file its transaction deleted** (was: a rollback that couldn't put a page back faulted its
  context; Windows only): the emptied page held open by another program during the rollback. Red: no page on disk to
  hold (it had been renamed aside).
- **A crash before a commit keeps the rows its deletes took** (`XodIndexCrashTests`): the writer now reports what was
  set aside, and how many pages there are, at the moment it is killed. Red: one file set aside.
- The test of a rollback that could not put back a table another context made again is removed: the first new test
  covers that case, and no longer expects a fault.

Added after the review (section 5):

- **A held index file doesn't fail a commit that takes its table away** (`XodTrustedIndexTests`, dropped and emptied,
  Windows only): another reader holds the index file with `FileShare.Read`; the commit passes, and after a reopen the
  index agrees with the pages. Red on the first revision of this change: the commit failed with "being used by another
  process", from the commit's own delete of the index file. (It passed before this change: the rename aside of the held
  file failed and the index service marked it, its handling of a held file.)
- **A transaction changes no file before its commit** now drops a second table too, and checks its index file is on
  disk, byte for byte, until the commit, and gone after it.

Each guard broken once, the full suite run each time:

| Break | Caught by |
| :--- | :--- |
| The reads ignore the transaction's deletes (`DeletedHere` always false) | 7 tests: the own-view test, the lists test (both cases), the emptied table, the committed and rolled-back deletes of `XodTrustedIndexTests`, a rolled-back drop's autonumber reset |
| A table file opened from disk though deleted (`OpenFileOrCreate`, `Load`) | the own-view test |
| The commit deleting nothing | 7 tests, among them the own-view test (pages left), the no-change test (the dropped table's files left) and the delete the commit can't make |
| The commit deleting after its writes | the own-view test (the table made again was deleted) |
| A rollback keeping the deletes (both places that forget them) | 10 tests |
| A data file's delete inside a transaction made at once | 22 tests |
| The lists of tables and pages showing deleted files | the lists test, with the schema from the table files (added for this break) |
| `RegisteredTypes` listing a dropped table | the lists test, both cases (added) |
| The statistics counting a dropped table | the lists test, both cases (added) |
| The index service deleting an index file at once inside a transaction | the guard in `IOService`: 19 tests |
| The same with the guard off | the held-index test (both cases), and a rolled-back index file not trusted again (`XodIndexReplacedTests`) |
| Index files among the commit's deletes, as in the first revision | the held-index test (both cases), and the same `XodIndexReplacedTests` test |
| `FileExists` ignoring the deletes | **not caught**: it was caught through the dropped table's index load, which the index service's mark now answers first; its other callers ask about pages a table lists, and a table no longer lists a page deleted |
| A raw page read from disk though deleted | **not caught**: a page is read by its name, from the table that lists it, as above. Both kept so that every read of the disk asks the one owner |
| A file the batch writes again deleted first | **not caught**: one machine uses POSIX delete, where the name is free at once; the change follows the `DeleteFileW` documentation |

Not separately broken: the page list's filter (`PageFileNames`; its one caller, a password change, opens each page through
`OpenFileOrCreate`, which asks), `Size` and `PageForReading`. Each break restored, and the source and tests compared
with their diff before the run.

## 4. Cost

Not measured. A delete inside a transaction no longer renames a file, and the commit deletes it instead of deleting its
set-aside copy. Each read of the disk asks one set first, under its own lock; the set is empty outside transactions that
deleted something.

## 5. The independent review, and what came of it

A read-only reviewer, given the code and the problem, with probes in its own copies of the tree and of the code before
the change (full suite there: 543 passed).

| Finding | Disposition |
| :--- | :--- |
| Regression: index files joined the commit's deletes, made with a plain delete outside the index service's handling of a held file, so an index file another reader held (another context loading it holds it with `FileShare.Read`) failed a commit that dropped or emptied its table (reproduced; it passed before) | Fixed: index files never wait among the commit's deletes; the index service marks them inside a transaction and deletes them at the commit's index flush, with its handling; `IOService` refuses an index delete inside a transaction; tested |
| Rows still lost when a context deletes the last rows it sees from a stale cache: it read the table, another context inserted, it deleted what it saw in a transaction and committed (reproduced, the same before the change) | Not this cause: the commit compares from the first change of a file, not from the bytes the context loaded. That is `ISS-167`'s, which this extends with the case |
| The commit deleted a file and wrote it again (a table dropped and made anew), which on Windows without POSIX delete can refuse the write while another program has the old file open (read from the `DeleteFileW` documentation; worked on one machine) | Fixed: a file the batch writes again isn't deleted; the write replaces it. Not testable on one machine, where POSIX delete is used |
| Records still described the set-aside (`ISS-164`'s resolution, `ISS-121` items 2, 4 and 5, the format spec's list of gated writes, `PasswordSwitch`'s comment) and had no entry for this | Corrected: the records below; `PasswordSwitch` now says an older version |
| Comments claimed the journal held every file pending deletion, and the undo wrote each back: false for index files, then among them | True now: only data files wait there, and the comment says so |
| The crash test's comment said every page was where it was, the assertion showed only that some page was | The comment says what it checks: nothing set aside, the pages there, and the rows (the count after the reopen) |
| `A_commit_undone_keeps_no_row_of_a_page_whose_first_capture_failed` undoes no commit: the context refuses writes after the failed capture and the commit has nothing to undo (the same before) | Filed in `ISS-121`: the test's name claims more than it shows |
| `pendingDeletes` compares names ignoring case, where the table list is ordinal (two tables whose names differ by case on Linux) | Named debt: the page cache already ignores case; one rule for names is `ISS-121`'s |
| On Windows a reader of a page in another context now fails the commit that deletes it, not the delete statement | No change: the commit's writes already conflict with readers the same way. Its undo restores the page once the reader lets go; while one still holds it the undo fails too, and the next open restores it (second pass) |
| XOD 2.0.2 contexts beside this version still set files aside, so `ISS-166` is reachable with mixed versions | Named: the release notes already say not to mix 2.0.2 and newer contexts on one database |
| The .NET claims (`File.Delete` throws `IOException` for a file in use and nothing for a missing one; elsewhere a file held open can be deleted); crash points between the journal, the deletes and the writes; nested batches; encrypted databases; two transactions in sequence; a drop, a re-insert and a failed statement's undo in one transaction | No change: confirmed, some by probe |

A second pass on the two revisions above (index files left to the index service; a file the batch writes again not
deleted), with probes, on the revised tree and on the code before the change:

| Finding | Disposition |
| :--- | :--- |
| The cause is removed: index files have one deletion path, the index service's, at the commit's index flush, with its handling of a held file; only data files reach the commit's deletes. Breaking both halves brings back the first revision's failure (three tests) | No change |
| The guard against an index delete in a transaction fired after the delete had dropped the file from the caches and the dirty sets: a refusal would have left a half-done delete | Fixed: the guard is the first thing `Delete` does; its breaks run again, caught |
| A table dropped and made again in one transaction still fails its commit when another reader holds the old index file and the index was rebuilt in the transaction: the old file is left, and the new snapshot can't replace it (reproduced, the same before the change; the rows are kept) | Not this cause: `ISS-115`'s open case (a commit's index save fails on a held file), extended with it |
| A data file another program holds through the commit that deletes it (the dropped table's file) fails the commit and its undo, and the context refuses writes until the next open, which restores every row (reproduced). Before, the file was renamed aside at the delete, so nothing could hold it at the commit | Named debt, in `ISS-121`: the same class as a held file a commit writes; the rows are never lost |
| A crash between the commit's page writes and its index flush leaves the dropped table's index file on disk (before, it was gone); it isn't trusted: the indexes-behind marker is still set, and the next open undoes the commit from its journal (reproduced) | No change |
| `DropIndexFiles` (after a commit undone) calls `TryDeleteIndexFile` directly, past `DeleteIndexFile`; safe only because it runs once the transaction is completed | Named debt, in `ISS-121` |
| The skip of a file the batch writes again relies on that write: a raw page whose bytes aren't held is skipped by the raw writes without a word. Unreachable: only the table file has a fixed name, and it is always written as a document | Named |
| Three lines of this plan and a comment in `IndexService.Persistence` claimed more than the code (an index file rebuilt only at the next flush, the held-index test passing by the old set-aside, the undo always restoring a held page, a rollback putting an index file back) | Corrected |
| A commit undone after its index flush or after its page writes, a rollback then more writes, two transactions in sequence, a password, buffered durability, a rewrite that fails | No change: the same as before the change, by probe (buffered durability read) |

## 6. Verdicts

- **A transaction's deletes at its commit** (`ISS-166`): **clean, with named debt.** The cause, a transaction changing
  the disk before its commit, is gone for data files and enforced where files are deleted; a rollback has nothing to
  undo. Debt: a file an older version's crash set aside is still put back by the open with the old rule; XOD 2.0.2
  contexts beside this version still set files aside; a data file another program holds through the commit faults the
  context until the next open; the case-insensitive names (`ISS-121`).
- **Index files at the commit** (the first review's regression): **clean, with named debt.** One deletion path, guarded
  in `IOService`. Debt: `DropIndexFiles` past `DeleteIndexFile`; a held index file and a table made again, `ISS-115`'s.
- **A file made again not deleted at the commit**: **clean**, though not testable on one machine.
- **The transaction's own reads**: **clean**, two of the checks (`FileExists`, the raw page reads) not reachable by any
  test, kept so that every read asks the one owner.
- Not fixed here, and not this cause: a context that deletes the rows it sees from a stale cache loses another
  context's newer row (`ISS-167`, extended).

## 7. How it was checked

- The new and rewritten tests seen red against the code before the change (9 red, 2 guards that passed before too);
  the held-index test red against the first revision.
- The breaks above, each restored and the tree compared with its diff before the run.
- The gate (`.github/workflows/tests.yml`'s steps, run locally, and the machine-details check): green, net8.0 and
  net9.0, conformance, the format reader, the stress harness.
- The whole suite on Linux (the .NET 9 SDK container, no network, a read-only folder mounted for the read-only test):
  545 passed, 1 skipped (the `ISS-167` test). A first run failed the read-only test only because its folder still held
  the row of an earlier run; with an empty folder it passed.
