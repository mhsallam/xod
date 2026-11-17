# PLAN-066: reads wait for another flow's transaction

- **Task ID**: `TASK-081`
- **Status**: Done (2026-11-17)
- **Issue**: `ISS-175` (filed by [`PLAN-065`](PLAN-065-a-transaction-is-its-flow-s.md))
- **Related**: [`PLAN-065`](PLAN-065-a-transaction-is-its-flow-s.md) (a transaction is its flow's: the turn every write
  takes)

## 1. The cause

A transaction writes into the context's caches, which every read shares: the cached page documents, the raw pages, the
indexes in memory and the object cache (a change to a cached file becomes a write still to make in the batch,
`IOService.Files.cs`). Its files change only at its commit, but a read takes the engine's read lock between the
transaction's statements and reads those caches, so another thread's `Select`, `Find`, `Count` or SQL saw a row the
transaction could still roll back.

So: **another flow's read sees a transaction's writes because the transaction's writes live in caches every read shares,
and a read of another flow could run between its statements. The change makes it impossible by making a read take the
same turn as a write: a read from another flow waits for the open transaction to end, and a transaction begins only
with no read or write of another flow under way.**

The direction was the owner's choice, of three: reads wait; reads see the last commit (copy-on-write of every cache,
its own plan); or dirty reads documented.

## 2. The change

- **A read takes a turn as a write does** (`EnterWithTurn`, now the one way a read or a write takes the engine's locks:
  `EnterTableReadLock`, `EnterTableWriteLock`, `EnterExclusive` and SQL's read). `TransactionTurns` counts both as an
  operation of another flow: one waits while a transaction is open, a transaction waits for those under way, and
  waiting ones go in the order they came. A read of the transaction's own flow goes on and sees its writes.
- **Checked under the locks**, as for a write (`TurnHolds`): a read of the transaction's own flow, decided before its
  locks, that finds another flow's transaction begun after its own commit starts over and waits.
- **Nested reads and writes go on in the outermost's turn** (a read or a write inside a query's predicate, a trigger's
  write): no transaction of another flow can be open under a turn. A write inside another flow's read is no longer
  refused while a transaction waits to begin: the transaction waits for the read, and the write is made in the read's
  turn (`PLAN-065`'s refusal, and its `passWaiting`, are gone).
- **Code of another flow run inside the transaction's own read or write on its thread** (an await's continuation run
  inline, `ExecutionContext.Run`) isn't nested in the transaction's turn: it can't wait for the transaction, which
  can't end under its own operation, and is refused.
- **`RegisteredTypes` takes a turn** like every read: it left out the tables a transaction of another flow had dropped
  before its commit (and `DropAll`, built on it, then skipped them).
- **The turn is cheap when nothing is open or waiting:** an operation then goes on without a ticket.
- **`BeginTransaction` inside a read** still throws the inside-a-read `XodException` (asked first, as a read now holds
  a turn too).

Reads of other flows now wait for a transaction, up to `TransactionTimeoutMs`, then throw `TransactionException`: a long
transaction holds up every other thread's reads, which is the cost of this choice. The other way round, a transaction
waits for reads of other flows under way, and one that outlasts `TransactionTimeoutMs` makes `BeginTransaction` throw
(it waited on the engine lock with no limit before). Every read now takes the turn: on the smallest cached read
(`First<T>`, about 0.6 µs) it cost about 7 to 10% on one thread and 3 to 5% with eight, measured on one machine against
the code before the change.

## 3. Tests

`XodTransactionFlowTests`:
- another flow's read during a transaction is refused with a short `TransactionTimeoutMs`, for `Select`, `Count`, a
  query and SQL: **red before the change** (each returned the transaction's row);
- given time, it waits and sees what the transaction committed, or nothing after its rollback; the transaction ends
  only once the read waits for it (the step `operation-waits`), so it can't pass by coming late (the first version
  could, and passed before the change);
- code of another flow run inside the transaction's read (`ExecutionContext.Run` in a query's predicate) is refused:
  **red before the change** (it read the uncommitted row);
- another flow's `RegisteredTypes` waits for a transaction that dropped a table: **red before the change** (it left
  the table out);
- the transaction's own flow reads its rows from a `Task.Run`, by query and by SQL;
- a write inside another flow's read goes on while a transaction waits to begin (the step `transaction-waits` tells the
  test the transaction is waiting for the read), and the transaction begins after;
- a read of the transaction's own flow, held between its turn and its locks while its transaction commits and another
  flow's begins and writes, doesn't see that one: it starts over, waits, and reads the commit's rows after the other
  rolls back. Red before the change, where a read took no turn to hold, and under break G10 (no check under the
  locks), where it saw the other transaction's row.

The `PLAN-065` tests still pass; its test of a write inside a read refused is replaced by the test of it going on.
`TransactionTurnsTests` count operations instead of writes.

## 4. Breaks

`PLAN-065`'s breaks against the code as it now is, and seven of reads (R4 to R7, and G7, G8, G13 again, after the
review's fixes). Each restored and the source compared before and
after the run.

| Break | Caught by |
| :--- | :--- |
| G1 every flow counts as the transaction's | 9 tests: the reads' refusals (4), the issue's write (2), the own flow's read and write after its commit, the second completion |
| G2 the transaction never marked as its flow's | many: every transaction's own writes refused |
| G3 the flow is the thread | 4 tests, the own flow's across awaits among them |
| G5 a turn never given back | many |
| G6 a transaction begins inside a write | the trigger test |
| G7 a transaction doesn't wait for operations under way | 5 tests: `TransactionTurnsTests` (3), the write held in its trigger, the write inside another flow's read |
| G8 operations don't give way to a waiting transaction | `TransactionTurnsTests` |
| G9 a turn not held over its operation | the write held in its trigger, the write inside another flow's read |
| G10 the decision not checked under the locks | the own flow's read and write after its commit |
| G12 a completion not checked under the lock | the second completion |
| G13 no arrival order | `TransactionTurnsTests` |
| G14 the close ends no wait | `TransactionTurnsTests` |
| R1 a read takes no turn | 8 tests: the reads' refusals (3, not SQL), the read waiting for the commit or the rollback (2), the own flow's read, the inline read, the write inside another flow's read |
| R2 an SQL read takes no turn | the SQL read's refusal |
| R4 a read or write inside a read takes a turn of its own | the write inside another flow's read (it waits behind the transaction that waits for its read) |
| R5 another flow's code nested in the transaction's turn goes on | the inline read |
| R6 `RegisteredTypes` takes no turn | its test |
| R7 the fast path passes an open transaction | 16 tests |

`PLAN-065`'s G4 and G11 (a write inside a read refused, or passing a waiting transaction) are gone with what they broke.

## 5. Review

One independent review, read-only, probes in a copy and against the code before the change. It found the cause removed
for data reads: every public read goes through `EnterTableReadLock` or SQL's turn, a transaction holds its turn from
its begin, no iterator or lazy load reads after the lock is let go, the engine's own parallel work takes no turn, every
turn is paired, and the .NET claims are right (two older ones about recursive read locks unconfirmed by the
documentation, from the runtime's source).

| Finding | What was done |
| :--- | :--- |
| Medium: code of another flow run inline on the transaction's thread (an await's continuation, `ExecutionContext.Run`) nests in the transaction's turn, and reads its uncommitted rows (probe; the same before) | Fixed: refused (section 2); test, break R5 |
| Medium-low: `RegisteredTypes` takes no turn and leaves out a transaction's uncommitted drops; `DropAll` from another flow then skips a table (probe) | Fixed: it takes a turn; test, break R6 |
| Low-medium: the test of a read waiting for the commit or rollback passed before the change (the read could come late) | Fixed: the transaction ends only once the read waits (step `operation-waits`); break R1 now catches it |
| Low: a long read of another flow makes `BeginTransaction` fail at `TransactionTimeoutMs` (it waited without limit) | Intended; in the release notes and section 2 |
| Low: a read that waits inside its predicate for a read on another thread stalls behind a waiting transaction until `TransactionTimeoutMs`, which then fails (it hung for good before) | Named: bounded now, by the transaction's deadline |
| Low: every read takes the turns' monitor, a ticket and a pulse: about 7% slower on one thread, 15 to 20% with eight, on the smallest cached read | Fixed in part: no ticket when nothing is open or waiting; measured again (section 2) |

## 6. Verdict

**Clean with named debt.** A read of another flow can no longer see what a transaction hasn't committed: every read
takes the turn writes take, checked under its locks, and code of another flow can't nest in the transaction's own. The
debt, named: a long transaction holds up other threads' reads, and a long read a transaction's begin, each up to
`TransactionTimeoutMs` (the owner's choice over snapshot reads); a read nested across threads stalls behind a waiting
transaction until its deadline; the turn's cost on the smallest reads.

## 7. How it was checked

- The reads' refusals, the inline read and `RegisteredTypes` red against the code before the change; every guard
  broken once (section 4), the source compared before and after.
- One independent review (section 5); every finding fixed, or named as the cost of the choice.
- The gate green on the final code; the whole suite on Linux.
