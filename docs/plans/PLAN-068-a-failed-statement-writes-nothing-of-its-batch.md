# PLAN-068: a failed statement writes nothing of its batch

- **Task ID**: `TASK-083`
- **Status**: Done (2026-11-17)
- **Issue**: `ISS-177`
- **Related**: [`PLAN-067`](PLAN-067-a-write-statement-is-all-or-nothing.md) (the statement's savepoint, `ISS-125`, whose
  review found this)

## 1. The cause

`InsertRange` and a `Delete` gather their changes in a batch, written at its end, each file once. Both ended the batch
in a `finally`, so the end ran whether the statement succeeded or failed: a statement that failed part way (a trigger
that throws, a value refused) wrote what it had done. In direct mode its undo then put the files back
(`PLAN-067`): a needless write, and a window in which the disk held the failed statement. In buffered mode the range's
rows before the refused item were written at once, the statement's own undo (`ISS-062`) taking them back in the cache
for the next flush. And a write that failed there (a file another program holds) replaced the statement's own
exception with the write's: the caller saw an `IOException` for a trigger's refusal.

So: **a failed statement's batch was written because its end didn't know whether the statement had failed. The change
makes that impossible by giving a statement's batch one owner, `XmlEngine.InBatch`, which ends it written only once the
statement's body has returned, and unwritten when it throws.**

## 2. The change

- **`XmlEngine.InBatch`**: the one way a statement opens and ends its batch (`InsertRange`, `Delete`). The body
  returns: the batch is written (`IOService.EndBatch`). The body throws: the batch ends unwritten
  (`IOService.EndBatchUnwritten`), before the statement's undo, and the exception goes on as it was.
- **`IOService.EndBatchUnwritten`**: the depth goes down; at 0 what the batch holds moves to the buffer's sets, as after
  a batch write that failed (`FlushPending`). The three ends of a batch (written, unwritten, discarded) count the depth
  through one helper (`OutermostBatchEndsUnlocked`), and the two ways a write waits in the buffer after all go through
  one (`WaitInBufferUnlocked`). In direct mode the statement's undo forgets it with the rest of the cache;
  in buffered mode the statement's own undo takes its rows back in those files, and the next flush writes them; inside
  an outer batch (a transaction's) the outer batch keeps it all. No delete waits at depth 0: only a transaction's are
  deferred, and its batch is the outer one.
- **The range's index save** (`SaveAfterRowsWritten`) runs once the batch is written, as before, and no longer after a
  failure: the indexes the failed range changed are dropped by its undo (direct mode) or saved by the next flush.

The transaction's batch is not a statement's: its commit writes it and its rollback discards it (`DiscardBatch`), as
before.

## 3. Tests

`XodFailedBatchTests` (new; the data-file writes counted, or failed, through `FaultInjection.InProcess` at the step
`file-write`):
- a range its trigger refuses at its third item writes no file, and the caller gets the trigger's exception even when
  every write would fail; no row of it after a reopen; the next write is made, and the close leaves no marker of
  indexes behind; direct and buffered: **red before** (two files written; an `IOException` thrown);
- a delete its `AfterAction` refuses, the same: **red before**;
- a trigger that writes another table inside a range refused later: its write is not written, and gone after a reopen;
  direct and buffered: **red before**;
- a delete that cascades to its children inside its batch, refused by its `AfterAction`: nothing written, the trigger's
  exception, the parent and its children kept: **red before**;
- a range that succeeds writes its batch.

## 4. Breaks

Each applied alone, the whole suite run, the source restored and compared.

| Break | Caught by |
| :--- | :--- |
| B1 a failed statement writes its batch | 10 (`XodFailedBatchTests`) |
| B2 a failed statement leaves its batch open | 10: the next write was never written (lost at the reopen) |
| B3 the unwritten batch is dropped, not moved to the buffer | **not caught** (below) |
| B4 the unwritten batch stays in the batch's sets | 3: the close left the marker of indexes behind |

B3: in direct mode the statement's undo forgets every write still to make, so dropping and moving are the same; in
buffered mode the statement's own undo marks each file it changes again. They differ only for a file the range changed
and its undo didn't, which is buffered mode's open half of `ISS-090`: moved, the cache and the disk end up agreeing, as
after a batch write that failed.

## 5. Review

An independent review (given the code and the problem, not this plan), with probes against the code before the change
too. It found the cause removed and no defect the change brings. It checked, by probe: the batch's depth back to 0
after a failure in both modes, a range inside a transaction, a trigger that writes inside a refused range, a refused
cascade delete, a refused delete that empties its page, a range that succeeds and whose end write fails, a range
refused inside a `DropType` trigger, and no marker of indexes behind after a clean close in any of them. On .NET, per
the C# reference (exception-handling statements): `throw;` rethrows the same exception, and exception filters run before
the stack unwinds, which changes nothing here (the filters on this path read locals set before the body ran).

Acted on: its two nested cases became tests (above); the three copies of the depth count and the two of the wait in
the buffer became one helper each; the release note says what a refused statement writes, not more.

Before the change, and still (named):
- **A range nested in a trigger whose failure the trigger catches** keeps its rows in direct mode (the outermost
  statement succeeds, and its savepoint is the only one) and not in buffered mode (the range's own undo): `PLAN-067`
  section 2.3.
- **A buffered `Delete` refused by its `AfterAction` keeps its delete**: a buffered delete opens no batch; `ISS-089`,
  `ISS-090` (open in buffered mode).
- **A buffered range that succeeds writes its batch at its end**, not at the next flush: a design question, not filed.

## 6. Verdict

**Clean.** The cause is removed: a statement's batch has one owner, which writes it only once the statement has
returned. B3 is not caught for the reason in section 4.

## 7. How it was checked

- The new tests red against the code before the change (section 3).
- The breaks (section 4), each restored and compared.
- An independent review (section 5), its findings checked before they were acted on.
- The gate: both builds, the tests and the conformance suites on .NET 8 and 9, the format readers on the stored and
  fresh data, the stress harness's tests, the machine-details check.
- The whole suite on Linux (.NET 9, a container with no network, as a user other than root).
