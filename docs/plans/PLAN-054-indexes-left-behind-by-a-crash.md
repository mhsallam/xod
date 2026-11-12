# PLAN-054: Indexes a crash left behind their pages (ISS-043)

- **Task ID**: `TASK-064`
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

## 2. The design

- **The marker** (`IOService.IndexesBehind`): before a context's first write of a page or table file since its
  indexes last caught up, a marker beside the database says its indexes are behind:
  `<database file>.<id>.xidxdirty`, one per context, so none touches another's. The single place every file
  mutation passes (`XmlEngine`'s `BeforeFileMutate`) calls it, so no write path can skip it. The context holds
  the file open while it is open: one byte long while its indexes are behind, empty once they have caught up.
- **Caught up** (`FlushToDisk(caughtUp: true)`): where no write of the context can be under way, so a flush
  leaves its indexes holding every row its pages do: under the engine-wide write lock (`Flush`,
  `BeginTransaction`, a password change, `ClearCache`) and at the close. Not `GetStatistics`, which flushes under
  the read lock while another thread may be between a page write and its index change. A clean close deletes
  an empty marker before it lets go of the database.
- **Recovery** (`RecoverInterruptedWrites`, run only with the database to itself): a marker that isn't empty
  means a context stopped with its indexes behind, so every `.xidx` goes (a derived cache, rebuilt from the
  pages when next needed, PLAN-046 section 8), then the markers. An empty marker (a crash after the indexes
  caught up) is deleted and costs the indexes nothing.
- **While another context holds the database**, a marker may be live, so it is left, as a journal is (PLAN-051);
  the next open alone honours it.
- **A context without a hold** (a lock file it can't open, PLAN-053) can't drop the indexes, nor tell a crash's
  marker from a live one: while one is there it reads no index file and rebuilds what it needs from the pages,
  in memory only (it writes nothing). It used to be refused when anything was pending; markers alone no longer
  refuse it.
- **The time check stays** for databases written by 2.0.0 and 2.0.1, which leave no marker.
- **Cost.** Changing the marker's length is two cheap calls per flush. The first version created and deleted
  the file instead; a writer that flushes after every insert then paid about 0.2 ms a flush on Windows
  (1,000 inserts with a flush each: 235-272 ms before, 430 ms with it), so it was changed. Section 4 has the
  measurement of what was kept.

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

## 5. Independent review

To run on the committed tree (persistence and recovery are high-risk areas).

## 6. Verdicts

Provisional, until the review reports:

| Change | Verdict |
| :--- | :--- |
| ISS-043 indexes behind their pages after a crash | Clean with named debt: event-based for this version's writers; databases written by 2.0.0 and 2.0.1 keep only the time check; a crash while another context holds the database is honoured at the next open alone; after a writer's crash every index is rebuilt, not only those of the tables it wrote |

## 7. Remaining debt

- **ISS-042** stays open: contexts without `LockDatabase` don't coordinate their writes, and one can rewrite an
  index snapshot another has open.
- After any crash of a context that had written, every index of the database is rebuilt on first use, in time
  proportional to the data; the marker doesn't name the tables.
