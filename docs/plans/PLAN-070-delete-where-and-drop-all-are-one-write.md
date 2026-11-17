# PLAN-070: DeleteWhere and DropAll are one write

- **Task ID**: `TASK-085`
- **Status**: Done (2026-11-17)
- **Issue**: `ISS-176`
- **Related**: [`PLAN-067`](PLAN-067-a-write-statement-is-all-or-nothing.md) (the write statement's savepoint, whose
  review found this), [`PLAN-068`](PLAN-068-a-failed-statement-writes-nothing-of-its-batch.md) (a statement's batch),
  [`PLAN-069`](PLAN-069-a-refused-drop-keeps-its-table.md) (a drop is a write statement)

## 1. The cause

`XodContext.DeleteWhere<T>` queried the matches in a read, then deleted each with `Delete`, a write statement of its
own. `XodContext.DropAll` dropped each registered type with `DropType`, a statement of its own too. Each statement is
all or nothing in direct mode (`ISS-125`, `ISS-178`); the call was not: one refused part way (a trigger) left the
deletes or the drops before it made. And between the query and the deletes another thread could change the rows.

So: **a refused `DeleteWhere` or `DropAll` kept part of its work because the call was many statements, assembled in the
context outside the engine, the owner of a write statement. The change makes that impossible by making each call one
statement in the engine: its deletes or drops nest in it, and its savepoint covers them all.**

## 2. The change

- **`IXodEngine` owns both operations**: `DeleteWhere(Type, Func<object, bool>)` and `DropAll()`. `XodContext` calls
  them, one path for every engine. Their default bodies are the old loops, for an engine with no statement of many
  writes (one written outside XOD); XOD's engines (`XmlEngine`, and `JsonEngine` and `BinaryEngine`, which derive from
  it) implement them. Two lines added to the pinned API (`fixtures/api-surface.txt`); `XmlEngine`'s own surface is
  unchanged (it implements them explicitly).
- **`DeleteWhere` is one write statement**: the matches are found under its lock, then each is deleted by `Delete`,
  nested in it, with its triggers. Their changes wait in the statement's batch (`InBatch`) and are written once, at its
  end; a delete refused part way ends the batch unwritten and the savepoint puts back what was done (`PLAN-068`,
  `PLAN-067`).
- **`DropAll` is one write statement**: the registered types are read under its lock, then each is dropped by
  `DropType`, nested in it. A drop refused part way puts back every table, its index and its counters
  (`AutonumberService.UndoStatement`, `PLAN-069`). Like a drop, it records no schema.

### What changes for callers

- **`DeleteWhere`'s predicate runs inside the write**, as a trigger does. Other threads' reads and writes of the
  context wait for it, so a predicate that waits for one (a `Task.Run` read, `.Wait()`ed) never ends; it used to run
  in a read, beside other reads. A write it makes is part of the `DeleteWhere`, undone with it in direct mode; it used
  to be refused (a write inside a read).
- **A `DeleteWhere` with no matches is a write**: like a `Delete` that finds nothing, it records its table's schema
  (`PLAN-060`) and is refused by a context that writes nothing more (`ISS-091`).

### What it doesn't do

- **In a transaction**, a `DeleteWhere` or `DropAll` refused part way keeps its part until the transaction ends, and a
  caller that commits commits it, as a refused `Delete` or drop: the rollback is the unit there, and a statement's own
  undo takes back created rows only (`ISS-062`).
- **Buffered mode**: the part is kept, and written by the next flush (the open half of `ISS-089` and `ISS-090`).
- **An engine of another kind** that doesn't implement the two members deletes and drops one by one, as before.

## 3. Tests

`XodStatementAtomicTests`:
- a `DeleteWhere` whose trigger refuses its second match leaves every match, in the context and after a reopen, and the
  same call then deletes them: **red before** (the first match was gone);
- a `DropAll` whose trigger refuses its second drop leaves every table and its rows, after a reopen too: **red before**
  (the first table was gone).

`XodFailedBatchTests`:
- a `DeleteWhere` its trigger refuses writes no data file, and the caller gets the trigger's exception even when every
  write would fail: **red before** (the first match's delete was written; with the writes failing, an `IOException`);
- a `DeleteWhere` that succeeds writes each data file once: **red before** (three times).

## 4. Breaks

Each applied alone, the whole suite run, the source restored and compared.

| Break | Caught by |
| :--- | :--- |
| G1 the engine leaves `DeleteWhere` to the interface's default (one by one) | 4 (every `DeleteWhere` test) |
| G2 the engine leaves `DropAll` to the interface's default (one by one) | 1 (a `DropAll` refused part way) |
| G3 the engine's `DeleteWhere` is no write statement (no savepoint) | 3 (a refused `DeleteWhere`: every match left, nothing written) |
| G4 the engine's `DropAll` is no write statement | 1 (a `DropAll` refused part way) |
| G5 the engine's `DeleteWhere` opens no batch | 3 (each file written once; a refused `DeleteWhere` writes nothing) |

## 5. Review

An independent review (given the code and the problem, not this plan), with probes run against the code before the
change too. It found the cause removed in direct mode: one statement wraps the query and every delete or drop, nested
deletes join its batch, and cascades, matches an earlier match's cascade deleted, a type with no table, a `DropAll` over
tables with relations and counters, and the locks (the same turn and lock as every write) are sound. Its findings:

- **The context kept a second copy for other engines**: `XodContext` tested for `XmlEngine` and kept the old loops for
  any other `IXodEngine`, so a wrapper around XOD's engine lost the one statement silently. Fixed as section 2 says:
  the interface owns both operations, and the loops are its defaults.
- **The predicate runs inside the write**: a predicate that waits for another thread's read never ends, and a write in
  it is no longer refused. Kept, as the design: the matches are those the statement deletes, with no other write
  between (the query used to run before the statement, and a row could change between them); the predicate is then
  part of the write, as a trigger is. Said in the release notes and section 2.
- **The release note said "with the default durability"**, which a transaction has too: now "outside a transaction".
- In a transaction and in buffered mode the part is kept, as before (section 2); a `DeleteWhere` with no matches
  records its schema, as a `Delete` does; the predicate went through a `dynamic` call for each row (passed as it is
  now).

## 6. Verdict

**Clean, with named debt.** The cause is removed in direct mode: each call is one write statement in the engine, the
owner of a write, and the context has one path to it. The debt, named: a transaction a caller commits after a refused
call, buffered mode (`ISS-089`, `ISS-090`), and an engine of another kind that keeps the one-by-one defaults.

## 7. How it was checked

- The new tests red against the code before the change (section 3).
- The breaks (section 4), each restored and compared.
- An independent review (section 5); its finding on the second copy is fixed as it pointed to, checked by the breaks G1
  and G2.
- The gate: both builds, the tests and the conformance suites on .NET 8 and 9, the format readers on the stored and
  fresh data, the stress harness's tests, the machine-details check.
- The whole suite on Linux (.NET 9, a container with no network, as a user other than root).
