# PLAN-067: a write statement outside a transaction is all or nothing

- **Task ID**: `TASK-082`
- **Status**: Done (2026-11-17)
- **Issue**: `ISS-125`; with it `ISS-089`, `ISS-090`, `ISS-108`, `ISS-124`, `ISS-131`, `ISS-133`, `ISS-137`, `ISS-163`,
  `ISS-169`
- **Related**: [`PLAN-053`](PLAN-053-statement-undo-counters-stored-values-locks.md) (the created rows' undo, `ISS-062`),
  [`PLAN-058`](PLAN-058-standalone-undo-held-index-files-insert-range-index-save.md) (`ISS-120`, `ISS-124`),
  [`PLAN-054`](PLAN-054-indexes-left-behind-by-a-crash.md) (`ISS-098`)

## 1. The cause

A write statement outside a transaction (`Insert`, `InsertRange`, `InsertOrUpdate`, `Update`, `Delete`, in direct
mode: no transaction, no buffer) changes the context's cached pages, table files and indexes as it goes, and writes its
files at its end, one by one. Nothing grouped them. A statement that failed part way, or whose end write failed on its
second file, kept whatever it had changed: in the cache (written by the next flush), and on disk. The `ISS-062` undo took
back the rows a statement had created, and only those: not an update's change, not a delete, not a range's batch
written after its statements had ended (`ISS-125`: an `InsertRange` reported failed was written by the next flush, and
its retry refused; an `Update` reported failed kept its change; a `Delete` reported failed lost its row).

So: **a failed statement outside a transaction leaves part of itself because nothing records what it changed, on disk
or in the cache, to put back. The change makes that impossible by giving every such statement a savepoint: each data
file it writes is noted before its first change, and a statement that fails puts its files back, forgets what the
context holds, and drops the indexes it touched.**

A savepoint needs the statement alone: standalone tables' writes ran side by side, one per table, under the engine's
read lock and their table's write lock (`ISS-124`), sharing the batch and the caches. Two choices were the owner's:

- **One write at a time**: every write takes the engine-wide write lock, as writes with relations always did.
- **A write inside a read is refused** for every table, as it already was for a table with relations (a read lock
  can't be upgraded).

A transaction per statement was measured and set aside: about 1.8 ms an insert against about 0.1 ms (its journal).

## 2. The change

### 2.1 One write at a time (step 1)

- **Every write statement takes the engine-wide write lock** (`XmlEngine.WriteStatement`, the one way a write takes its
  locks and ends). The table locks are gone: no writer took a table's write lock any more, so SQL's table read locks
  and their order (`PLAN-060` section 2.2) kept nothing out. A read takes the engine-wide read lock only
  (`EnterReadOperation`), which a thread that holds it enters again without waiting, so reads nested in opposite orders
  behind waiting writers can't wait for each other (`ISS-163`), and SQL inside a read is no longer refused.
- **A write inside a read is refused** for every table (`XodException`, the inside-a-read message): a standalone
  table's write used to go on inside a read on its thread. Inside a write it still goes on (a trigger's query whose
  predicate writes: the thread holds the write lock already), and ends what that read shares, as before
  (`ItemsCacheService.WriteStarted`, now called from the one write-lock entry, `EnterExclusiveLock`).
- **`ISS-124` is gone with it:** no statement runs beside another, so the batch and the caches are one statement's.

### 2.2 The savepoint (step 2)

- **The statement** (`WriteStatement`): the outermost one, in direct mode, opens a savepoint
  (`IOService.BeginStatement`) and the index service notes its types (`IndexService.BeginStatement`). Its end writes
  what it saved (`WriteSaved`, as before), then lets them go. When it fails, anywhere (a trigger, a refused value, a
  file write at its end), `UndoWriteStatement` puts its files back (`IOService.UndoStatement`), forgets what the
  context holds (`DiscardPendingWrites`, as a rollback does), and drops each index it changed, saved or built from the
  pages (`IndexService.ForgetStatement`: rebuilt from the pages when next needed). The counters stay where it left
  them, recorded in the tables' marks by the next flush (`ISS-083`).
- **What is noted** (`StatementSavepoint`), before the statement's first change to each data file: the bytes the
  change replaces, from where it starts to the file's end (a page appended to has only its closing tags there), the
  whole file for a file written whole or deleted, or that the file wasn't there. Taken from what the context holds: a
  cached page's bytes as it is about to change (`RawPagePendingUnlocked`, the one place a cached page changes), a
  document's bytes as last read or written (`XFile.DiskBytes`). Read from the file only when the context holds neither.
  On one machine, opening a file just written for reading took 3 to 14 ms (scanned again on open), against about
  50 µs for the write itself: the first version read each file before writing it, and an insert took about 17 ms.
- **What is put back**: only a file a write of the statement changed (`IOService.ChangedOnDisk`, the one report of a
  change on disk, to the statement and to a committing transaction). A file whose write failed before changing it
  (another program holds it) is left alone: the first version wrote it back too, failed on the same held file, and
  refused every write after.
- **Nothing to forget when nothing changed**: a statement refused before it changed anything the context holds (a key
  already used) keeps the cache. Every change to a cached file reports itself (a document's `Changed` event, a page's
  pending hook, a hint, a delete, a file made), so the context knows. Without this, a refused insert cost about 290 µs
  against 10 µs, the next statement loading its pages again.
- **A range is one statement** (`InsertRange`), in every mode: an item refused leaves nothing of the range, the items
  before it included. Each item used to be a statement of its own. In a transaction or buffered mode its own undo
  (`ISS-062`) now covers the whole range.
- **An index built from the pages, or saved, in the middle of a statement is the statement's only once the statement
  has changed what the context holds** (`IndexService.BuiltInStatement`, asking `IOService.StatementChangedAnything`).
  Built before that, it holds the pages as on disk, and stays: dropped with every refused statement (a key already
  used), it was built again from every page by the next one (the review: about 180 ms a refusal on 30,000 rows).
- **The bytes noted of a page are copied into pooled arrays** (`ArrayPool<byte>.Shared`), given back when the statement
  ends (`StatementSavepoint.Release`): a new array for each change, up to the page's end, cost the success path
  collections of the large object heap (the review: 71 gen2 collections for 2,000 updates on large pages, against 1).
- **A password switch is refused inside a write** (`InvalidOperationException`, as a close is): the switch rewrites
  every file itself, outside the file service the savepoint notes through, and a statement that failed after it put
  its files back as they were before the switch, under the old password; no password opened the database (the review).
- **A parallel write's one failure is thrown as itself** (`ParallelWrites`, `ISS-137`): with 4 files or more the writes
  run under `Parallel.ForEach`, whose `AggregateException` the caller didn't expect.
- **An undo that fails**: the caller gets an `XodException` carrying both failures, and the context writes nothing more
  until it is reopened (`ISS-091`, as before).
- **The created rows' undo (`ISS-062`) stays for a transaction and buffered mode**, where nothing reaches the disk before
  the commit or the flush; in direct mode it no longer runs, and its direct-mode write (and `LeaveSavedToFlush`) is gone.

### 2.3 What it doesn't do

- **Not across a crash.** The savepoint is held in memory. A crash in the middle of a statement's writes leaves each
  file whole, old or new, as before; a transaction is the unit that survives a crash.
- **Not in a transaction or buffered mode.** In a transaction the rollback is the unit. In buffered mode a failed
  statement's own undo still takes back only the rows it created, and what it changed of other rows stays, written by
  the next flush (`ISS-089` and `ISS-090` stay open there).
- **A trigger that catches a nested statement's failure** keeps what that statement did: the savepoint is the
  outermost statement's.
- **A read that waits for a write on another thread never ends** (`InsertAsync(...).Wait()` in a predicate): the write
  waits for the engine's write lock, which the read holds back. With one write at a time this holds for every table, as
  it did for tables with relations; said in the release notes.
- **`DeleteWhere` is many statements** (`ISS-176`), and **a failed range or delete writes its batch before it is put
  back**, a failure there replacing the statement's own (`ISS-177`): both filed, found by the review. **`DropType` is a
  write of its own, with no savepoint** (`ISS-178`): filed, found by the second review.
- **After a write inside a trigger's read, the rest of that read shares no objects**: two rows referencing one row get
  two objects, as before the change.
- **Not every copy is pooled**: a change below a page's first noted one joins the two into a new array (`Prepend`), and
  a file read for its note (`BeforeWrite`) is a new array, once a statement each. Page-sized arrays now come from two
  pools (`PageBufferPool`, `ArrayPool<byte>.Shared`). The pooling has no test: it changes the cost, not the result.

## 3. Tests

`XodStatementAtomicTests` (new; a write fails through `FaultInjection.InProcess` at the step `file-write`, for the file
named, so it fails the same way on every file system):
- an `InsertRange` whose page or table write fails leaves no row, in the context, after a flush and after a reopen, and
  its retry is accepted; standalone and relational, plain and encrypted: **red before the change** (rows kept, or
  written by the next flush);
- an `Update` whose page write fails changes nothing, plain and encrypted; a relational `Update` that drops a child
  keeps both (`ISS-089`): **red before**;
- a `Delete` whose page write fails deletes nothing: **red before**;
- a `Delete` refused by its trigger once the row is gone keeps the row and its unique key (`ISS-131`): **red before**
  (the row was lost); one of a table's last row gets the page and table files back: **red before**;
- an index built from the pages inside a failed statement (its trigger's query) is dropped with it;
- an insert that can't read its page (another program holds it, Windows) leaves the context writing (`ISS-133`): **red
  before** (every write refused until reopened);
- a page written by its statement (a trigger's flush) and then put back keeps the statement before it, and an
  encrypted page is put back encrypted; XML and JSON, plain and encrypted: **red before** (the failed update kept);
- a password switch inside a write is refused, and the write leaves nothing: **red before** (the switch was made);
- an index built by an insert refused before any change is kept, and saved: red against the change before the
  review's finding (dropped);
- an insert into a type whose index file is gone, failed after its trigger's query built the index, leaves its key
  free (green before too: a regression test).

`XodItemCacheTests`: a write inside a trigger's read is seen by the rest of that read: red against the change before
the review's finding (the read kept the row as it was before the write).

`XodWriteBesideReadTests` (added after the commit, once the two issues were seen to share this cause): a write to a
standalone table on another thread waits for a read that references its rows (`ISS-169`), and a child's write that
lengthens its row waits for its parent's read (`ISS-108`): **red before** (each write was made in the middle of the
read: it took the engine's read lock and its table's write lock only).

`XodNestedReadTests` (replaces `XodSqlLockTests`): reads nested in opposite orders, by a lambda query and by SQL,
behind a waiting writer of each table, both finish: **red before** (the lambda deadlocked, `ISS-163`; SQL was refused).

Changed with the behaviour:
- a write inside a read is refused (`XodItemCacheTests`, `XodTransactionFlowTests`: it went on);
- a write waiting when a close comes from inside a read fails with nothing written (`XodTrustedIndexTests`: it was under
  way beside the read);
- a range with a refused item leaves nothing, outside a transaction and in one (`XodStatementUndoTests`: the items
  before stayed);
- a range whose page write failed leaves nothing and its retry is made (`XodFailedWriteTests`: written by the next
  flush, `ISS-098`'s direct-mode half);
- an undo that fails refuses writes (`XodStatementUndoTests`, now through the step `undo-file`).

Removed, their premise gone: two standalone statements side by side (`XodTrustedIndexTests`), an undo beside a statement
on another table (`XodStatementUndoTests`, and the crash writer's `undo-beside` modes), SQL's table locks
(`XodSqlLockTests`).

## 4. Breaks

Each applied alone, the whole suite run (K1, P1 and S9, S10 again after the test for them: the statement tests), the
source restored and compared.

| Break | Caught by |
| :--- | :--- |
| S1 no savepoint begins | 11 tests (`XodStatementAtomicTests`, `XodStatementUndoTests`) |
| S2 a failed statement isn't undone | 10 or more (`XodStatementAtomicTests`, `XodFailedWriteTests`) |
| S3 a file written whole isn't noted | 7 (the relational range on its table file, the deletes, the indexes) |
| S4 a deleted file isn't noted | 2 (the deletes refused part way, of the last row) |
| S5 a change on disk isn't reported to the statement | 7 |
| S6 files no write changed are put back too | 8 (`XodFailedWriteTests`, `XodTrustedIndexTests`, `XodStatementUndoTests`) |
| S7 the context doesn't forget after a failed statement | 10 or more |
| S8 a change to what the context holds isn't counted | 10 or more |
| S9 a document's bytes on disk not renewed by its write | 3 (the page put back, JSON) |
| S10 a document's bytes on disk kept decrypted | 2 (the page put back, encrypted, first statement) |
| S11 a page with changes still to write converted with its bytes as on disk | **not caught**: no such page in direct mode (below) |
| S12 a page's cached bytes taken as on disk though changes from before wait | **not caught**: the same |
| S13 a page's change not noted from the cache | not caught, as expected: the file is read instead (correct, slow) |
| X1 the touched indexes aren't dropped | 14 |
| X2 an index change isn't the statement's | 12 |
| X3 an index built from the pages isn't the statement's | **not caught** alone: each build the tests reach is saved at once, which notes it (X4) |
| X3 and X4 together | 2 (the index built inside a failed delete, an undo that fails) |
| X4 an index saved isn't the statement's | 1 (an undo that fails) |
| K1 an index built before any change is the statement's | 1 (the index kept) |
| P1 a password switch allowed inside a write | 1 |
| W1 a parallel write's one failure stays wrapped | 4 (the relational ranges) |
| L1 a range isn't one statement in a transaction | 1 |
| L2 the created rows' undo runs in direct mode too | 1 (the insert that can't read its page) |
| U1 an undo that failed doesn't refuse writes | 1 |
| U2 the counters' marks aren't recorded after a failed statement | 1 |
| I1 a write inside a trigger's read doesn't end its sharing | 1 (`XodItemCacheTests`) |

S11 and S12 guard a page holding changes from before the statement. In direct mode none can: each statement writes
what it changed at its end, and a failed one's changes are forgotten. They keep `DiskBytes` and the cache's bytes
truthful if that changes. X3's note covers an index built and not saved at once (one whose file can't be read, rebuilt
from the pages), which no test reaches.

## 5. Cost

Measured on one machine, direct mode, against the code before the change (`HEAD`): 500 operations a run, three runs
in each of three rounds, the two builds taken in turn; the median and the fastest run, in µs an operation.

| Operation | Before: median | Before: fastest | After: median | After: fastest |
| :--- | ---: | ---: | ---: | ---: |
| Insert, standalone table | 117 | 94 | 124 | 103 |
| Update, standalone table | 156 | 126 | 167 | 139 |
| Insert, with relations | 1,708 | 1,437 | 1,851 | 1,613 |
| Update, with relations | 3,492 | 2,102 | 2,631 | 2,200 |
| Delete, standalone table | 651 | 526 | 672 | 554 |
| Insert refused (key already used) | 12 | 10 | 13 | 11 |
| Inserts on 3 threads, 3 standalone tables | 164 | 146 | 131 | 113 |

On large pages (20,000 rows on 5 pages, 2,000 updates or deletes spread over them, the review's probe), against the
code before the change: about 10% slower on updates, about the same on deletes, and one gen2 collection each, as
before (71 and 160 before the copies were pooled).

A statement that succeeds pays for the savepoint in memory only: what it notes comes from what the context holds (no
data file was read for it in these runs). The fastest runs put that at about 5 to 12% on a write. Writes on three
threads to three standalone tables, which ran side by side before, take turns now and were no slower in these runs.

## 6. Review

An independent review (given the code and the problem, not this plan), with probes against both copies. It found the
cause removed, and:

- **A password switch inside a write, then a failed statement, left a database no password opened** (new): fixed,
  refused inside a write (section 2.2), test above.
- **A refused statement dropped the index it had built from unchanged pages**, so each refusal rebuilt it from every
  page (new): fixed, kept until the statement has changed something (section 2.2), test above.
- **The copies of a page's bytes cost the success path collections of the large object heap** (new): fixed, pooled.
- **A write inside a trigger's read no longer ended that read's sharing** (new; the review saw a comment that claimed
  more than the code): fixed, `WriteStarted` restored at the write-lock entry, test above.
- **A read that waits for a write on another thread never ends** (new, the cost of one write at a time): said in the
  release notes and section 2.3.
- **A comment said the buffer takes back a failed statement's change**: it doesn't; corrected, and the issues' statuses
  say "in direct mode" (buffered mode is outside a transaction too).
- There before the change, and still: buffered mode (`ISS-089`, `ISS-090`), a trigger catching its nested statement's
  failure (section 2.3), `DeleteWhere` (`ISS-176`), the batch ended in `finally` (`ISS-177`).

A second independent review, of the fixes above only, found each cause removed: no pooled array used after it is
given back or given back twice, every write of a note by its count, no lock taken in a new order, no other way to a
password switch inside a write, and `ArrayPool<byte>.Shared` used as its documentation states it (`Rent` may return a
longer array; `Rent(0)` is never called). It found one claim wrong, fixed (`StatementChangedAnything` said false
outside a statement, and wasn't: now it is), one test named for more than it shows (renamed), and `DropType`
(`ISS-178`).

Found sound: the raw pages' notes (`Keep`, `Prepend`, `Covers`) on every change to a page and on its conversion to a
document; `DiskBytes` at every load and write, and dropped with the cache by a switch and a rollback; a file reported
changed only once it may differ; the four index changes and their undo; one write at a time, SQL under the read lock
alone; `ReaderWriterLockSlim`'s recursion as its documentation states it (a read lock entered again; a write lock from
a read lock refused, `LockRecursionException`; a read or write lock from a write lock entered).

## 7. Verdict

**Clean, with named debt.** The cause is removed in direct mode: every write statement there has a savepoint, and one
that fails leaves nothing, on disk, in the cache or in an index. The debt, named: not across a crash (section 2.3);
buffered mode (`ISS-089`, `ISS-090` open there); a nested failure a trigger catches; `ISS-176`, `ISS-177`, `ISS-178`;
the copies not pooled (section 2.3); S11 and S12 guard what direct mode can't reach.

## 8. How it was checked

- The new tests red against the code before the change (section 3), and the review's against the change before its
  fixes.
- The breaks (section 4), each restored and compared.
- The cost against the code before the change, the two builds in turn (section 5), and the review's probes on large
  pages.
- Two independent reviews (section 6), each finding checked in the code or by a test before it was acted on.
- The gate: both builds, the tests and the conformance suites on .NET 8 and 9, the format readers on the stored and
  fresh data, the stress harness's tests, the machine-details check.
- The whole suite on Linux (.NET 9, a container with no network, as a user other than root).
