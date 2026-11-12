# PLAN-054: Indexes a crash left behind their pages (ISS-043), and writes that failed (ISS-098)

- **Task ID**: `TASK-064`; the rework after its review, `TASK-066` (sections 5 to 7)
- **Status**: Fixed in the tree (2026-11-12), not released: no version change, nothing pushed.
- **Related**: [`PLAN-041`](PLAN-041-insert-rate-independent-of-table-size.md) (ISS-043 filed, the index log),
  [`PLAN-051`](PLAN-051-commit-failure-and-shared-access.md) (recovery only alone),
  [`PLAN-053`](PLAN-053-statement-undo-counters-stored-values-locks.md) (the lock file),
  [`PLAN-046`](PLAN-046-file-format-spec.md) sections 2, 8 and 9, [`KNOWN-ISSUES.md`](../KNOWN-ISSUES.md)

## 1. What was wrong, and why

**Defect.** After a crash, an index that missed the last batch could be trusted: rows on the pages were missing
from it, so lookups by key missed them and unique keys weren't enforced, until something rebuilt it.
Reproduced deterministically for this plan: a batch's pages written, the process killed before its indexes,
every index's time set after the pages' (the window, made certain); the next open didn't find customer 26 by
its key.

**Cause.** An index is behind its pages from a page write until the context's next index write (a flush, a
batch's end, a close), and the open judged whether it was behind by file times: stale only when a page was
more than 50 ms newer than the index, and only the first two and last two pages were checked. An index written
just before pages a crash kept from catching up was newer than they were, and passed. Time can't say what
happened; only the writer knows when its indexes are behind.

**The first fix was a mitigation** (`TASK-064`, commit `c5374b5`; its review, section 5). It marked the indexes
behind by an event, but called them caught up wherever a caller said so, which was wrong after an index save
that failed and inside a transaction, and only an open alone (or a context without a hold) honoured a crash's
marker, so contexts opened while another had the database open still trusted a stale index. Each of the three
ended, reproduced, in duplicate unique keys that outlived the rebuild. Fixing the first showed a defect under it,
**ISS-098**: a page write of a batch or of the buffer that failed had already left the set of writes still to
make, so it was never made, though the context went on showing it. `TASK-066` reworked the design (section 2).

## 2. The design

As reworked by `TASK-066`; what changed from `TASK-064` is said where it did.

- **The marker** (`IOService.IndexesBehind`): before a context's first write of a page or table file since its
  indexes last caught up, a marker beside the database says its indexes are behind:
  `<database file>.<id>.xidxdirty`, one per context, so none touches another's. The hook that page and table
  writes pass (`XmlEngine`'s `BeforeFileMutate`) calls it; the writers that don't pass it are listed below. The
  context holds the file open while it is open, one byte long while its indexes are behind and empty once they
  have caught up, and (since `TASK-066`) shares nothing while it holds it: on Windows no other open of it
  succeeds, and on Unix .NET takes `flock` `LOCK_EX` for `FileShare.None` (with the write access the marker is
  opened with, which an NFS client needs; `SafeFileHandle.Unix.cs`, dotnet/runtime release/8.0 and 9.0).
- **Caught up is decided from state** (`XmlEngine.FlushToDisk`, since `TASK-066`): after a flush, the marker is
  emptied only if this thread holds the engine-wide write lock (which every writer's lock excludes, so no write
  of another thread is under way), no batch is open (a transaction's pages wait in its batch while its index
  changes are written), and neither service has anything left to write (`IOService.NothingPending`,
  `IndexService.AllSaved`). `TASK-064` let each caller say so, and two said it wrongly (section 5, F2 and F3).
  The close takes the write lock for its flush (a close from inside a read on its own thread can't, and leaves
  its marker marked: the next open rebuilds the indexes). A clean close deletes an empty marker before it lets go
  of the database.
- **A write that failed stays to be made** (ISS-098, `TASK-066`): an index type leaves the set still to save only
  once saved (`IndexService.FlushDirtyIndexes`), and a page or table file leaves a batch's or the buffer's set
  only once written (`IOService.FlushPending`); what a failed flush took goes back to the buffer, written by the
  next flush or dropped by a rollback (`DiscardPendingWrites`). A full page a batch writes on its own counted as
  written when its write began; one whose write failed is now written whole by the next flush
  (`AwaitBatchWrites`). Writing again a file that was written is harmless: each is written whole, or from where
  its last write that succeeded ended. After an index append that failed, the next save is a snapshot: the
  replay trusts a log whose last record ends a flush, so an append behind a torn record could be read wrongly.
- **Readers** (`IOService.IndexesLeftBehind`, asked by every index load since `TASK-066`): a marker that isn't
  empty and that no context holds means a context stopped with its indexes behind, so no index file is trusted
  and the index is rebuilt from the pages, in memory, until an open alone drops the files. A probe tells a live
  marker from a dead one: it opens the marker sharing reads and writes, which the holder's lock refuses (on Unix
  a `LOCK_SH` against the holder's `LOCK_EX`; two probes don't refuse each other). A marker that can't be opened
  at all counts as left behind unless it is empty. Where the file system takes no locks (an NFS client without
  lock support, .NET's file locking switched off), a live marker reads as dead: the indexes are rebuilt while it
  is marked, which costs time, not rows. `TASK-064` honoured markers only in an open alone and in a context
  without a hold (which read no index file while any marker was there, live or empty, judged once at its open).
- **Recovery** (`RecoverInterruptedWrites`, run only with the database to itself): a marker that is left behind
  drops every `.xidx` (a derived cache, rebuilt from the pages when next needed, PLAN-046 section 8), then the
  markers go. An empty marker (a crash after the indexes caught up) is deleted and costs the indexes nothing. A
  marker still held is a live context's (a password change gives up its shared hold before it takes the
  database alone) and is left. Since `TASK-066`, what can't be deleted doesn't stop the open: an index that
  can't be dropped leaves every marker, so no context trusts an index file until the next open alone tries
  again; a marker that can't be deleted stays (empty, it costs nothing; marked, it keeps the index files
  untrusted until it can be).
- **While another context holds the database**, an open doesn't drop anything (a marker may be live, as a journal
  may be, PLAN-051); the readers' probe covers what a crash left meanwhile.
- **A context without a hold** (a lock file it can't open, PLAN-053) writes nothing and drops nothing; it reads
  the index files by the same probe as any context. It used to be refused when anything was pending; markers
  alone don't refuse it.
- **The time check** (`IndexService.TryLoadFromDiskCore`) stays, as a guess, for what no marker can say: an index
  of this version's layout (`-4`) whose pages a writer without the marker (XOD 2.0.0 or 2.0.1) changed
  afterwards. `TASK-064` said it stayed for databases written by those versions; their indexes (`-3` and older)
  are never loaded, but rebuilt (review, F6).
- **Writers that don't pass the hook**, each argued at its site (review, F7): a rollback's restore of the files
  (`TransactionService.RestoreBeforeImages`: the context's marker is already set, and stays until a flush after
  the rollback; at the open, recovery runs alone and drops the indexes itself); the password change
  (`PasswordSwitch`: alone, from a flushed state, every file the indexes included replaced together); the
  migrator (alone, after its recovery, every file converted with its content unchanged); `IOService.SaveNow`
  (the database file only). Nothing fails if a new page writer skips the hook (section 7).
- **Cost.** Changing the marker's length is two cheap calls per flush. The first version created and deleted
  the file instead; a writer that flushes after every insert then paid about 0.2 ms a flush on Windows
  (1,000 inserts with a flush each: 235-272 ms before, 430 ms with it), so it was changed. Section 4 has the
  measurement of what was kept, and of `TASK-066`'s probe.

## 3. Tests

`XodIndexCrashTests` (5, new; the crash writer's new step `batch-written` kills it between a batch's pages and
its indexes):

- an index a crash left behind its pages is not trusted, whatever its time;
- an empty marker a crash left (a crash in the close, after its flush) costs the indexes nothing;
- a context without a hold (a lock file it can't read; Windows) doesn't trust indexes a marker says may be
  behind, finds every row, refuses writes, and leaves the marker for the next open alone;
- an open drops no index while another context has the database open, and the marker goes at that context's
  close;
- a clean close leaves its indexes trusted (the marker is one byte while behind, empty after a flush, gone after
  the close).

`TASK-066` (11, new; the crash writer gained a type with a unique key, `CrashMember`, and the modes `members` and
`txflush`):

- `XodIndexCrashTests` (7): a writer's crash while another context has the database open leaves no index trusted
  (F1); a flush inside a transaction keeps the indexes marked behind, through a crash before the commit (F3); an
  index save that failed is made by the next flush, and every row is found by its keys after the reopen (F2); an
  index the open can't drop is not trusted, and the marker goes only with it (F4); a marker the open can't delete
  doesn't stop it (F4); a live context's marker reads as live; a close from inside a read lets go of the database
  and leaves its indexes marked.
- `XodFailedWriteTests` (4, ISS-098): a buffered write that failed is made by the next flush, and by the close; a
  range whose page write failed is made by the next flush; a full page a range failed to write is made by the
  next flush.

## 4. How it was checked

- **Red first**: on the code before, the crash test failed with "customer 26 not found by its key" and the
  shared test found no marker.
- **Guards proven by breaking the code** (each break turned at least one test red; restored byte for byte): no
  marker before a page write; recovery keeping indexes left behind; a marker not counted as pending; the marker
  never emptied; indexes dropped while another context is open (also turned two PLAN-051/PLAN-053 tests red); a
  context without a hold trusting the index files; such a context refused for a marker; an empty marker dropping
  the indexes; the marker left after a clean close.
- **Cost** (a small timing program built against the previous commit `4a0ce19` and against the tree, run in
  turn, twice each, five runs per workload, medians; Windows, Release, net9.0): 1,000 inserts with a flush each,
  276 and 249 ms before, 283 and 293 ms after; 20,000 inserts then a close, 1,471 and 1,535 ms before, 1,491
  and 1,419 ms after; ten ranges of 10,000 then a close, 203 and 220 ms before, 205 and 209 ms after. The flush
  loop pays a few hundredths of a millisecond a flush; the rest is within the runs' spread.
- **Gate** (the tree before the commit, 2026-11-12): build with no warnings; `Xod.Tests` 282/282 on net8.0 and
  net9.0; conformance 14/14 on each; `read.mjs --check` (six fixtures) and `--check-fresh` on a freshly written
  set (five); `XodBench.Tests` 26/26.

**`TASK-066`:**

- **Red first** (each new test against the tree before the rework): F1 "customer 26 not found by its key"; F2
  "member 51 not found by its key"; F3 the row found by its old key returned null; F4 the open threw
  (`IOException` for the held index, `UnauthorizedAccessException` for the read-only marker); ISS-098 the pages
  held only the rows written before the failure (each of the four); the live-marker and close-in-a-read tests
  failed too (no probe; the marker emptied and deleted).
- **Guards proven by breaking the code** (12 breaks, each turning at least one test red; files restored byte for
  byte after each): a failed save dropping its type; caught up with a batch open; caught up without the write
  lock; the close taking the write lock inside a read; the marker shared while held; loads ignoring markers left
  behind; markers deleted though an index stayed; an index that can't be deleted stopping the open; a marker that
  can't be deleted stopping it; failed writes forgotten; a batch's failed writes kept in the batch's own set; a
  failed full page counted as written. Not proven by a break: the snapshot after a failed append (a torn record
  needs an I/O failure inside a write); a live marker left alone by recovery (only the password change's gap
  reaches it); a marker that can't be opened counting as left behind (its permissions).
- **Cost** (the same kind of timing program, built against `caf4e39` and against the tree, run in turn, twice
  each, five runs per workload, medians; Windows, Release, net9.0): 1,000 inserts with a flush each, 266 and
  321 ms before, 291 and 270 ms after; 20,000 inserts then a close, 1,438 and 1,399 ms before, 1,435 and
  1,390 ms after; 200 opens each finding a row by its unique key (an index load, so a probe, each), 1,045 and
  1,151 ms before, 1,063 and 1,039 ms after; the same while another context that had written kept the database
  open (its marker live, probed at each load), 1,103 and 1,165 ms before, 1,090 and 1,108 ms after. All within
  the runs' spread.
- **Linux** (a container of the .NET 9 SDK image, no network, as a non-root user): `XodIndexCrashTests`,
  `XodFailedWriteTests`, `XodCommitFailureTests`, `XodCrashSafetyTests` and `XodLockSafetyTests`, 38/38; there the
  live-marker test's probe is refused by `flock` (`LOCK_SH` against the holder's `LOCK_EX`).
- **Gate** (the tree before the commit, 2026-11-12): build with no warnings; `Xod.Tests` 312/312 on net8.0 and
  net9.0; conformance 14/14 on each; `read.mjs --check` (six fixtures) and `--check-fresh` on a freshly written
  set (five); `XodBench.Tests` 26/26.

## 5. Independent review

A fresh reviewer read `c5374b5` against its parent with the problem and no conclusions, ran probes against clean
builds of both (and of 2.0.1), and found the fix a mitigation. Every finding was reproduced on the tree before
acting, by the new tests that follow the reviewer's probes (section 4, red first: F1, F2, F3 and both F4 cases
failed as reported), and fixed in `TASK-066` unless marked.

| # | Finding | Done |
| :--- | :--- | :--- |
| F1 | **High**: a writer that crashed while another context held the database left a marker that only an open alone honoured; contexts opened meanwhile trusted the stale index, missed rows by key and accepted a duplicate unique key, which the rebuild kept | every index load probes the markers: one that isn't empty and that no context holds leaves the index files untrusted; the marker is held sharing nothing, so a live one refuses the probe |
| F2 | **Medium-high**: an index save that failed dropped its type from those still to save, so the next flush called the indexes caught up and the close deleted the mark | a type stays to be saved until its save succeeds; caught up is decided from what both services hold |
| F3 | **Medium**: `Flush` inside a transaction wrote its index changes and called the indexes caught up while the transaction's pages waited in its batch; a crash before the commit left the index holding the transaction's keys, trusted | caught up needs no batch open |
| F4 | **Medium (regression)**: an open alone threw when it couldn't delete a marker (read-only, even an empty one) or an index (held open) | an index it can't drop leaves the markers (no context trusts the files meanwhile); a marker it can't delete stays |
| F5 | The spec said the marker is created empty and deleted on catching up; the release note that it exists only while the indexes are behind | corrected: PLAN-046 sections 2, 8 and 9, the release notes |
| F6 | "Databases written by 2.0.0 and 2.0.1 keep the time check" was wrong: their indexes are never loaded | corrected here, in the code and in PLAN-046 section 8; the check is kept, as a guess, for a `-4` index whose pages a 2.0.x writer changed afterwards |
| F7 | "No write path can skip the hook" was wrong: four writers don't pass it | each argued at its site and listed in section 2; no structural guard (section 7) |
| - | Low, reasoned: the no-hold context's untrust was a snapshot taken at its open, set by empty markers too | gone: the probe at each load replaces it |
| - | Low, reasoned: an open alone in a password change's gap could delete a live marker | the marker shares nothing while held (Windows refuses the deletion) and recovery leaves a held marker alone |
| - | Low, reasoned: the close emptied the marker without the engine lock | the close takes the write lock for its flush |

Found while fixing F2: **ISS-098** (filed High, fixed here): a page write of a batch or of the buffer that failed
was dropped from the set of writes still to make, and a full page a batch wrote on its own counted as written
before it was.

Confirmed sound by the reviewer: the hook's sites for every page and table write in `IOService`; one owner for
the decision to trust an index file; the crash points of a flush, a close, a commit, a rollback, a password
change, the migrator and `ClearCache`. The marker's length changes and the renames aren't flushed to the device:
this holds for a process crash, not a power cut, as PLAN-046 section 9 says.

## 6. Verdicts

After the review and `TASK-066`:

| Change | Verdict |
| :--- | :--- |
| ISS-043 indexes behind their pages after a crash | Clean with named debt: marked and caught up by state, honoured by every reader; after a writer's crash every index is rebuilt, not only those of the tables it wrote; an index whose pages a 2.0.x writer changed is caught only by the time check; where the file system takes no locks a live marker reads as dead (rebuilds, not lost rows); four writers stay outside the hook by argument, not structure |
| ISS-098 a write that failed | Clean with named debt: a call that failed still throws though its rows are written by the next flush (`InsertRange`, as under `Buffered`); the snapshot after a failed append is reasoned, not proven by a test |

## 7. Remaining debt

- **ISS-042** stays open: contexts without `LockDatabase` don't coordinate their writes, and one can rewrite an
  index snapshot another has open.
- After any crash of a context that had written, every index of the database is rebuilt on first use, in time
  proportional to the data; the marker doesn't name the tables. While a crash's marker waits for an open alone
  (other contexts keep the database open), every context rebuilds each index it loads, in memory.
- The writers outside the hook (section 2) are right by argument at each site; nothing fails if a new page
  writer skips the hook.
- `InsertRange` whose batch write failed throws, and its rows are written by the next flush: the caller is told
  the write failed, though it will be made (as under `DurabilityMode.Buffered`).
