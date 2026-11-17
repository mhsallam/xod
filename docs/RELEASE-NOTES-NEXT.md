# Release notes for the next release (draft)

What changes for users in the next release, collected as each fix lands. The version number and the
release itself are the owner's decision; nothing here is published. Each item links the plan that
records it. The last release: [2.0.3](RELEASE-NOTES-2.0.3.md).

## New

## Behaviour changes

- **A transaction belongs to the code that began it, and other threads wait for it.** While it is open, a read or a
  write from another thread waits for it to end, up to `TransactionTimeoutMs`, then goes on, or throws
  `TransactionException` with nothing done. A write no longer joins the transaction (to be taken back by its rollback
  after it had returned), and a read no longer sees what the transaction has written before its commit. An `await`, a
  `Task.Run` or an `*Async` method inside the transaction is still part of it. So a long transaction holds up other
  threads' reads: keep transactions short. The other way round, `BeginTransaction` waits for other threads' reads and
  writes under way, and throws `TransactionException` if one outlasts `TransactionTimeoutMs`. `Flush`, `ClearCache`, `GetStatistics`, `DropType` and a password change
  wait too. `BeginTransaction` inside a write (a `BeforeAction` or `AfterAction` handler) now throws
  `TransactionException`. A transaction begun inside an async method belongs to that method, so its caller's reads
  and writes wait for it; begin it where its work is done. A transaction never disposed keeps other threads waiting
  until the context closes. ([`PLAN-065`](plans/PLAN-065-a-transaction-is-its-flow-s.md),
  [`PLAN-066`](plans/PLAN-066-reads-wait-for-another-flow-s-transaction.md))
- **Writes are made one at a time, and a write inside a read is refused.** Writes to tables without relations used to
  run side by side, one per table; now each waits for the write under way, as writes with relations always did. A
  write from inside a read on the same thread (a query's predicate, or while its results are enumerated; not in a trigger) now throws
  `XodException` for every table, as it did for tables with relations: make it after the read. A write on another
  thread waits for the read too, so a read that waits for one (`InsertAsync(...).Wait()` in a predicate) never ends.
  A database password can't be set, changed or removed inside a write (a trigger) any more: `InvalidOperationException`. ([`PLAN-067`](plans/PLAN-067-a-write-statement-is-all-or-nothing.md))
- **`InsertRange` is one write.** An item refused (a trigger, a key already used) leaves none of the range; the items
  before it used to stay. ([`PLAN-067`](plans/PLAN-067-a-write-statement-is-all-or-nothing.md))
- **`DeleteWhere` and `DropAll` are each one write.** Outside a transaction, with the default durability, a delete or a
  drop refused part way (a trigger) leaves every row or table as it was; the ones before it used to be gone.
  `DeleteWhere`'s predicate now runs inside that write, as a trigger does: other threads wait for it, so it must not
  wait for them, and a write it makes is part of the `DeleteWhere` (it used to be refused). `IXodEngine` has both as
  members; an engine of your own that doesn't implement them deletes and drops one by one, as before.
  ([`PLAN-070`](plans/PLAN-070-delete-where-and-drop-all-are-one-write.md))

## Fixed

- **A write that fails leaves nothing behind.** Outside a transaction, with the default durability, an `Insert`, `InsertRange`, `InsertOrUpdate`,
  `Update` or `Delete` that throws (a trigger refuses it, a file another program holds) leaves the database, and what
  the context shows, as they were. It could keep part of its change, or have it written by the next flush, so a retry
  was refused or stored twice. A crash in the middle of a write is not covered: use a transaction for that. Nor is
  buffered durability, where a failed write still keeps part of its change. ([`PLAN-067`](plans/PLAN-067-a-write-statement-is-all-or-nothing.md))
- **A refused `InsertRange` or `Delete` throws its own exception.** When a trigger refused one, a file write that failed at its end (a file another program holds) used to replace the trigger's exception with an `IOException`; and with buffered durability a refused range's rows before the refused item were written at once. A refused range or delete writes nothing when it fails. ([`PLAN-068`](plans/PLAN-068-a-failed-statement-writes-nothing-of-its-batch.md))
- **A read no longer sees a write on another thread made in its middle.** A write to a table without relations could run while a read that reached its rows was under way, so the read could show some rows from before the write and some from after. ([`PLAN-067`](plans/PLAN-067-a-write-statement-is-all-or-nothing.md))
- **A drop its trigger refuses keeps the table.** A `Drop` whose `AfterAction` threw kept its deletes: the table was gone. Now its files, its rows and its numbering are as they were, with the default durability. ([`PLAN-069`](plans/PLAN-069-a-refused-drop-keeps-its-table.md))
- **Queries nested in other queries' predicates no longer wait forever** behind writers, and SQL run inside a read is
  no longer refused. ([`PLAN-067`](plans/PLAN-067-a-write-statement-is-all-or-nothing.md))

- **A transaction completed twice at once no longer ends the next one.** A `Commit` on one thread and a `Dispose` on
  another could both go ahead; the second then ended, discarded or committed the transaction another thread had
  begun meanwhile. A transaction now ends once. ([`PLAN-065`](plans/PLAN-065-a-transaction-is-its-flow-s.md))
