# PLAN-062: the commit gate (`ISS-164`)

- **Task ID**: `TASK-076`
- **Status**: Done (2026-11-16). `ISS-164` resolved in part: a write no longer lands beside another context's commit
  (the gate); a commit no longer overwrites a write another context made after the transaction first changed a file, or
  takes it back by a rollback (the comparison, and a rollback that writes nothing). A page a context read and kept before
  that is `ISS-167`'s.
- **Related**: [`PLAN-061`](PLAN-061-high-issues-from-the-overall-review.md) section 2 (`ISS-147`, the look for a
  journal this makes a hold), [`PLAN-046`](PLAN-046-file-format-spec.md) section 9 (the lock files)

## 1. The causes

Two contexts of one database (two `XodContext` instances, in one process or two) share its files. Three paths let one
context's acknowledged write be lost to another's transaction:

- **A write beside a commit.** `ISS-147` made each write look for another context's commit journal first, but the look
  held nothing: a commit could write its journal between another context's look and its write. If that commit was then
  undone (a crash, a failure), its undo wrote its before-images back and took the write with it.
- **A commit over a newer write.** A commit writes its files from what its transaction holds in memory. A write another
  context made to one of those files after the transaction first changed it was overwritten by the commit, and put back
  as it was by the commit's undo.
- **A rollback over a newer write** (found by the review). A transaction writes no data file before its commit, yet its
  rollback wrote every before-image back: it could only ever take back what other contexts had written since.

## 2. The change

- **The commit gate**, `xod.commit.lock` beside `xod.lock`, never deleted (`IOService.CommitGate`). A commit holds it
  exclusively from before it compares its files with the disk and writes its journal until its journal is gone or its
  undo is done. Every write of a data file holds it shared, from its journal check (the engine's hook) to the end of the
  write: the statement's end (`WriteSaved`), a flush (`FlushPending`), a full page written in a batch, a delete, the
  put-back of what a rolled-back transaction set aside. So the look for a journal is now a hold. A write waits for a
  commit, and a commit for the writes, up to `TransactionTimeoutMs`; then the write is refused (`XodException`) or the
  commit is (`TransactionException`, nothing written). A commit that changed no file doesn't wait.
- **The comparison at commit** (`XodTransaction.RefuseOverAnotherContextsWrites`), under the exclusive gate: each file the
  transaction changed must be on disk as it first read it, and each file it created still absent; a page it set aside
  must not be at its name again. Otherwise the commit is refused before its journal, and nothing of it is written. A
  file it can't read to compare refuses it too.
- **A rollback writes nothing** but the put-back of the pages its transaction set aside. That a transaction writes its
  data files at its commit alone is now enforced where files are written (`IOService.RefuseWriteInsideTransaction`). A
  page set aside that another context wrote again is left as that context wrote it (its name is random, so that is a
  newer write of it); a table or index file made again at its fixed name is not dropped by the rollback, which fails
  and leaves it for the next open (`IOService.PutBackOne`). The next open still drops it, as it did before this
  change, and the dropped table's rows with it: `ISS-166`, now reproduced, not fixed here. A put-back that
  can't get the gate in time faults the context, as one another program blocks does: the next open puts it back. So
  does a commit refused at the gate, if its transaction set pages aside and another context's commit holds the gate
  past `TransactionTimeoutMs`.
- **A delete of a file that isn't on disk** (the page of a statement being undone that never reached it) changes only
  what the context holds: no gate, no mutation hook, no disk. A statement refused at the gate used to be undone through
  the gate and the hook's look for a journal, be refused again, and leave its context refusing every write.
- **Enforced where it is made**: a write of a data file outside the gate is refused where it would be made
  (`IOService.RequireCommitGate`): the write primitives, the set-aside moves. Index files and the database file are
  outside every journal and aren't gated.
- **The test harness**: a writer process's standard error is read from its start (`XodCrashSafetyTests.Launch`,
  `FinishWriter`, the one way every test reads a writer). A writer that failed with more on standard error than a pipe
  holds used to hang the run: the test read its output first and the writer blocked on the full pipe.

## 3. Tests, seen red first

`XodCommitGateTests` and `XodCommitGateSideTests` (new), against the engine before the change unless said:

- **A write past its checks outlives the undo of a commit beside it** (direct and buffered durability): another process
  paused past the check of its first file write while a commit over the same pages is cut short after writing them;
  the rename must survive the next open's undo. Red: the commit landed in the pause, the rename was refused at its next
  check with half of it written, and the undo took that half back. The pause is at the first write because a rename
  writes two pages in an order that follows their random names: at the second, the test held back the renamed row's
  page in some runs only (the first version of this test was red by chance; caught when its buffered case passed
  against a broken gate).
- **A commit over a file another context wrote since is refused**; **a commit creating a file another context created
  since is refused**; **a commit over a page it set aside that another context wrote again is refused** (that write
  stays, and the context goes on). Red: each commit succeeded, and the other write was lost.
- **A rollback leaves another context's write.** Red: the rollback wrote its before-images back.
- **A rollback that can't get the gate to put pages back refuses writes until reopened**, and the page comes back at the
  next open. Red (on the gate before this fix): the rollback threw and the context went on writing with the row missing.
- **A commit that can't read its file to compare is refused** with `TransactionException` (Windows only). Red: a raw
  `IOException`.
- **A commit that changed nothing doesn't wait for the gate.** Red: it waited and was refused.
- **A rollback doesn't drop a table another context made again without a word** (second review). Red (on the first
  revision): the rollback returned, and the dropped table's rows were gone.
- **A statement refused at the gate leaves its context writing** (second review), and **one refused beside a commit
  begun during it, journal and all** (third review; a trigger takes the gate and holds a journal mid-statement, before
  and after its action). Red: the context refused every write.
- **A write beside a commit that doesn't end is refused, and goes through after it.** New behaviour.

Each guard broken once, the full suite run each time (20 breaks, each restored and the tree compared with the diff before it, every one caught):

| Break | Caught by |
| :--- | :--- |
| A flush (`FlushPending`) without the gate | the guard: 265 tests |
| The same, with the guard off | the window test, buffered |
| A commit that takes no gate | the window test (both cases), and the guard where the commit deletes what it set aside |
| No comparison of the before-images | the two comparison tests |
| No check of created files | the created-file test |
| `BytesOnDisk` ignoring files set aside | 3 tests of transactions that delete pages |
| The statement's end (`WriteSaved`) without the gate | the guard: 367 tests |
| A delete without the gate | the guard: 37 tests |
| The rollback's put-back without the gate | the guard: 6 tests, the put-back test among them |
| A full page written in a batch without the gate | the guard: 15 tests |
| The rollback writing its before-images back | the rollback test |
| A page set aside and written again read as unchanged | the set-aside test |
| The put-back always moving the file back | the set-aside test, and a test of a set-aside page another program holds |
| The rollback's handler not knowing the gate's refusal | the put-back test |
| A commit that changed nothing taking the gate | its test |
| An unreadable file not turned into `TransactionException` | its test |
| A full page written inside a transaction | the new guard (`RefuseWriteInsideTransaction`), through a soak test |
| The rollback dropping a table file that is back | the dropped-table test |
| A delete of a file not on disk taking the gate | the refused-statement tests (3) |
| A delete of a file not on disk passing the mutation hook | the test of a statement refused beside a commit begun during it (both cases) |

## 4. Cost

On one machine, the engine before against after, Release, three runs each:

- 2,000 single-row inserts (direct durability), one statement each: 263 ms against 345 ms, about 40 µs more a
  statement, one open and close of the gate.
- 300 small transactions, one insert each: about 620 ms against 745 ms.
- A context committing 50 small transactions beside another writing rows without a pause: no starvation (commits
  averaged 3 to 6 ms, the longest 65 ms); the review saw the same against four writing threads (longest 124 ms).

## 5. The independent review, and what came of it

A read-only reviewer with probes, on the change and on the engine before it:

| Finding | Disposition |
| :--- | :--- |
| A rollback wrote its before-images back outside the gate, taking back another context's acknowledged write (reproduced, before and after) | Fixed: a rollback writes nothing but its put-back, enforced where files are written; tested |
| The comparison read a set-aside page where it waits, not at its name: another context's write there was lost (reproduced) | Fixed: a page at its name again refuses the commit, and the rollback keeps that write; tested |
| Regression: the put-back of set-aside pages waited for the gate, and a timeout escaped the rollback's handling (a rollback went on writing with the row missing) | Fixed: the gate's refusal has its own type (`CommitGateBusyException`), handled as a put-back that couldn't be made; tested |
| A page cached before the transaction's first change is still overwritten by its commit, and two contexts writing outside transactions overwrite each other's pages | Out of scope here: `ISS-167`, extended with the structural fix (a write compares the page with the bytes it was loaded from, under the gate) |
| The guard against an ungated write passes when any thread of the context holds the gate; the recovery paths (open, password change, migrator) aren't asked, safe under the database held alone | Named debt |
| The comment on network file systems was wrong: on NFS the gate holds between processes, not between contexts of one process, and a close can drop a commit's hold | Corrected, per flock(2) and fcntl_locking(2); `ISS-104` extended |
| XOD 2.0.2 contexts take no gate; the records weren't updated | Release notes, `ISS-164`, this plan |
| An index delete waited for the gate, though index files are outside every journal | Fixed: not gated |
| A commit that changed nothing waited for the gate; the commit's refusal said "writing" when it waited for a commit; an unreadable file gave a raw `IOException` | Fixed; tested |
| The window test passed whatever made the commit fail | Fixed: it checks the gate's refusal |
| The .NET claims (flock modes, Windows sharing) for 8.0 and 9.0; no self-wait or deadlock (a commit holds its engine's write lock); crash points safe | No change |

A second pass on the revision:

| Finding | Disposition |
| :--- | :--- |
| Findings 1, 2, 5 and 8 fixed (each probe again); the NFS comment right | No change |
| The revision's put-back rule dropped what was set aside whenever the file was back: right for a page, wrong for a table file, whose fixed name another context reuses for a new table, so a rollback of a dropped table lost its rows without a word (reproduced) | Fixed: dropped for a page only; a table or index file fails the put-back and waits for the open (`ISS-166` for what the open does); tested |
| A statement refused at the gate was undone through the gate, refused again, and faulted its context (reproduced) | Fixed: a delete of a file that isn't on disk takes no gate; tested |
| A commit refused at the gate, whose transaction set pages aside, still faults its context while another commit holds the gate (reproduced) | Named debt: the put-back needs the gate (another commit may be making that file), and only another commit held past `TransactionTimeoutMs` blocks it |
| No ordinary test makes the two guards fire | They are proven by breaking the paths they guard (section 3) |
| The NFS comment left out `local_lock`/`nolock`, SMB before Linux 5.5, and a read-only gate file (no exclusive lock on NFS) | Added (`IOService.Locks.cs:376-379` opens a read-only lock file for reading) |

A third pass on those two fixes:

| Finding | Disposition |
| :--- | :--- |
| The dropped table's rows are still lost, at the next open, whose rule drops a set-aside file that is back (reproduced: rollback, and a commit refused and undone); the rollback's message promised the open would put the file back | `ISS-166`, now reproduced and extended; the behaviour is the engine's before this change (the live put-back failed, the open dropped it). The message corrected |
| A delete of a file not on disk still passed the hook, whose look for a journal refused it beside a commit holding its journal, faulting the context (reproduced with a trigger beginning a commit mid-statement) | Fixed: no hook for such a delete; tested |
| Skipping the gate and the disk for a file not on disk harms nothing: no context makes a file at that name in between but by a write that should stay; a dropped table rolled back alone puts everything back | No change |

## 6. The verdict

- **The gate: clean, with named debt.** A write of a context that takes the gate no longer lands beside another
  context's commit, on Windows and on local Unix file systems. Debt: XOD 2.0.2 contexts don't take it (don't mix them
  with newer ones on one database); on NFS it keeps nothing apart between contexts of one process (`ISS-104`); about
  40 µs more for each statement that writes; the guard is per context; a transaction that set pages aside and is
  refused at the gate, or rolled back, while another context's commit holds the gate past `TransactionTimeoutMs`,
  leaves its context refusing writes until the database is reopened; a table dropped inside a transaction and made
  again by another context loses its old rows at the next open (`ISS-166`).
- **The comparison and the rollback: a mitigation (the real fix filed as `ISS-167`).** A commit no longer overwrites, and
  a rollback no longer takes back, a write made after the transaction first changed a file. A page read before that,
  and writes outside transactions, are still compared with nothing.

## 7. What changes for users

In `RELEASE-NOTES-NEXT`: the new lock file; writes wait for commits and commits for writes, up to
`TransactionTimeoutMs`; a commit is refused when another context wrote one of its files since the transaction first
changed it (decided per file: two contexts changing different rows of one page conflict); don't open one database from
XOD 2.0.2 and a newer version at once.

## 8. How it was checked

- **Gate** (the final tree, 2026-11-16, Windows): engine and crash writer build with 0 warnings; `Xod.Tests` 529/530 on
  net8.0 and 537/538 on net9.0, the `ISS-167` test skipped on each; conformance 24/24 on each; the independent reader
  `--check` and `--check-fresh` ok; `XodBench.Tests` 26/26; no machine details in the tracked files.
- **Linux** (2026-11-16, `mcr.microsoft.com/dotnet/sdk:9.0`, a non-root user, the container's own file system, no
  network, a folder mounted writable and read-only for the read-only test): the whole of `Xod.Tests`, 537 passed, the
  `ISS-167` test skipped. A first run without the read-only mounts failed only that test (its variables were set but
  empty).
- **The test harness**: the first run of these breaks hung for hours on the first one (a writer's standard error filled
  its pipe; section 2). With the fix, that break fails in a second.
