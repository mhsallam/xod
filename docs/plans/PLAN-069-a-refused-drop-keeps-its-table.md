# PLAN-069: a refused drop keeps its table

- **Task ID**: `TASK-084`
- **Status**: Done (2026-11-17)
- **Issue**: `ISS-178`
- **Related**: [`PLAN-067`](PLAN-067-a-write-statement-is-all-or-nothing.md) (the write statement's savepoint, `ISS-125`,
  whose second review found this)

## 1. The cause

`DropType` (`Drop<T>`, `Drop(Type)`, `DropAll`) deletes a table's page and table files, clears its counters and drops
its index, then runs its `AfterAction` trigger. It was a write of its own: it took the engine's lock and the write depth
by hand, and wrote what was saved in a `finally`, failed or not. It never went through `WriteStatement`, the one owner of
a write statement, so it had no savepoint: a drop its trigger refused after the deletes kept them, the table gone.

So: **a refused drop kept its deletes because the drop was a second way into a write, beside `WriteStatement`, without
its savepoint. The change makes that impossible by making the drop a write statement: in direct mode its deletes are
noted first and put back if it fails.**

## 2. The change

- **`DropType` goes through `WriteStatement`**: the lock, the depth, the savepoint, and the end written only when the
  statement succeeds. A drop that fails anywhere (its `AfterAction`, a delete) is undone in direct mode: its files put
  back, the cache forgotten, the index dropped and rebuilt from the pages when next needed.
- **A drop records no schema** (`WriteStatement`'s `recordSchemas`, false for a drop): it deletes its table, and leaves
  the tables it reaches as they were (`XodSchemaManifestTests`). The one difference between a drop and the other
  statements, passed as an argument.
- **The counters come back with the table, from the statement's undo**: a drop clears its table's counters, and a
  number a failed statement took is held only in memory until the next flush records it in the table's mark
  (`ISS-083`): the table the undo puts back doesn't hold it. While a statement with a savepoint is under way,
  `AutonumberService` notes each counter it clears with the high it had (`ClearType`, the one change that lowers a
  counter), and the statement's undo raises them back (`AutonumberService.UndoStatement`, from `UndoWriteStatement`).
  So a drop that fails anywhere is covered: its trigger, its statement's end, or another statement it ran inside (a
  trigger's drop) that fails after it. Raising never lowers a counter the statement raised since.

### What it doesn't do

- **Buffered mode**: a drop's deletes are made at once there, and there is no savepoint; a refused drop still keeps them
  (the open half of `ISS-090`).
- **In a transaction**, a drop its trigger refuses and whose caller goes on to commit is committed, as a refused delete
  is: the transaction's rollback is the unit there, and its statement undo takes back created rows only (`ISS-062`).
- **`DropAll`** drops each type as a statement of its own: one refused leaves the drops before it (added to `ISS-176`,
  with `DeleteWhere`).

## 3. Tests

`XodStatementAtomicTests`:
- a drop its `AfterAction` refuses, once the files are deleted, keeps the table: its rows, by key too, in the context and
  after a reopen: **red before** (the rows were gone);
- a drop refused keeps the numbers already taken: a number a failed insert took isn't handed out again after it:
  **red before** (the table was gone, and the next insert took 1);
- a drop that fails at its statement's end (writing what its trigger saved) keeps the table and its numbers, after a
  reopen too; and a drop inside another statement's trigger, which then fails, the same: both red against the change
  before its review (the number 2 handed out twice);
- a drop that succeeds restarts the numbering, and a later failed statement doesn't bring the old numbers back.

## 4. Breaks

Each applied alone, the whole suite run, the source restored and compared.

| Break | Caught by |
| :--- | :--- |
| D1 the drop is no write statement (no savepoint) | 4 |
| D2 a drop's cleared counters aren't noted | 3 (the numbers kept: refused, failed at its end, inside a failed statement) |
| D3 a drop records its schemas | 1 (`XodSchemaManifestTests`: the drop leaves the tables it reaches as they were) |
| D4 a failed statement doesn't give cleared counters back | 3 (the same) |
| D5 a statement that succeeds leaves its counter record open | **not caught**: the next statement's start empties it |

D5: `EndStatement`'s reset is defensive. The record is emptied when each statement with a savepoint starts, and only
such a statement's undo reads it, so a record left open is never read.

## 5. Review

An independent review (given the code and the problem, not this plan), with probes against the code before the change
too. It found the files and the index clean: the drop goes through the one writer, and its deletes are put back. It
found the first version's counters a patch at the wrong layer: the drop took a copy of the counters and raised them
back only when its own trigger or index drop failed, so a drop failing at its statement's end, or inside another
statement that failed after it, put the table back and handed a number out twice (the second was there before the
change too). Fixed as section 2 says, at the owner of the counters and from the statement's undo; its two probes are
tests, red against that first version.

Also from it, said in section 2: a refused drop in a transaction commits if the caller commits; buffered mode keeps a
refused drop's deletes; `DropAll` is many drops (`ISS-176`). Found sound: a drop of a type with no table and a drop
its `BeforeAction` cancels change no file and write no schema; a drop that succeeds restarts the numbering; a drop
nested in a refused drop puts both tables back with their counters; an index after a refused drop finds by key and
refuses a duplicate key, live and after a reopen; a drop failing on a held table file puts the pages back; the locks
are those the drop took by hand, in the same order.

## 6. Verdict

**Clean, with named debt.** The cause is removed in direct mode: the drop is a write statement, and its undo puts back
its files, its index (rebuilt from the pages) and its counters. The debt, named: buffered mode, a transaction a caller
commits after a refused drop, `DropAll` (`ISS-176`).

## 7. How it was checked

- The new tests red against the code before the change, and the review's against its first version (section 3).
- The breaks (section 4), each restored and compared.
- An independent review (section 5); the fix of its finding is the design it pointed to, checked by its probes as
  tests and by the breaks D2 and D4, not reviewed again.
- The gate: both builds, the tests and the conformance suites on .NET 8 and 9, the format readers on the stored and
  fresh data, the stress harness's tests, the machine-details check.
- The whole suite on Linux (.NET 9, a container with no network, as a user other than root).
