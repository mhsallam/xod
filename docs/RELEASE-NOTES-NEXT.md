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
- **A property XOD can't store is refused.** A property of `Int128`, `Half`, `BigInteger`, `nint`, a tuple or a struct of your own (alone, nullable, or in an array, a list or a map) used to be accepted, written as text and read back as its default. Now its class is refused when it is first used, with an `XodException` naming the property: mark it `[NotMapped]` or change its type. An `object` property holding such a value is refused by the write. ([`PLAN-073`](plans/PLAN-073-a-value-xod-cant-store-is-refused.md))
- **A cascade delete is the same however the row is found.** A delete by key now deletes a single `[Property(Cascade = Delete)]` reference and fires the triggers of each row its cascade deletes, as a delete found by a scan did. The rows a cascade deletes go after their parent's row, so a child's trigger no longer finds its parent stored. `DeleteWhere` returns the number of rows it deleted and fires one pair of triggers per row, equal rows too. ([`PLAN-074`](plans/PLAN-074-a-row-is-deleted-by-its-code.md))
- **A row's objects and lists without a key, and its children, go with it.** Deleting a row deletes its plain lists of objects without a key, with their `Delete` triggers; they were kept. A `[Children]` list is deleted with its parent whatever its `Cascade` says; `Cascade = CascadeOptions.Update` kept it. An `object` property no longer deletes the row of a type with a key it holds, unless it declares `Cascade = Delete`. A cascade declared on a reference acts on an update too: clearing the reference deletes its row. ([`PLAN-077`](plans/PLAN-077-one-rule-for-the-rows-a-row-owns.md))

## Fixed

- **An update no longer leaves rows behind.** An update that replaced or cleared an object without a key kept its old row (one more row at every update), one that cleared a `[Property(Cascade = Delete)]` reference kept its row, and a child without a key kept the rows of its own list without a key each time its parent was updated. ([`PLAN-077`](plans/PLAN-077-one-rule-for-the-rows-a-row-owns.md))
- **An update of a parent whose list holds children without a key works.** It threw `MissingPrimaryKeyValueException` and changed nothing, for a plain list and a `[Children]` list. Each such child is written again: its old row is deleted and a new one inserted, with their triggers. ([`PLAN-076`](plans/PLAN-076-an-update-with-keyless-children-and-unwritten-tables.md))
- **In buffered mode before a flush, and in a transaction that made the table, the rows an update drops and the rows a cascade through an `object` property names are deleted, and `DropAll` drops the table.** They were kept. ([`PLAN-076`](plans/PLAN-076-an-update-with-keyless-children-and-unwritten-tables.md))
- **An `Update` or `Delete` by example of a type without a key reaches every matching row.** It changed or deleted only the matches on the first page that had one, and returned true. ([`PLAN-075`](plans/PLAN-075-a-keyless-update-or-delete-reaches-every-page.md))
- **A delete or an update no longer takes rows that only look the same.** Updating a parent wrote its children without a key again and then deleted them, with every other parent's equal children; deleting a parent with indexing off deleted other parents' equal children; `DeleteWhere` over equal rows returned too few. A delete by key left a single cascade reference behind, and two rows whose cascades named each other overflowed the stack. Each row is now deleted by its own identity. ([`PLAN-074`](plans/PLAN-074-a-row-is-deleted-by-its-code.md))
- **A `char` that is a space, a tab or another whitespace character reads back as written.** It read back `'\0'` (a `char?` as null), and a query by it found the row only until the index was rebuilt. ([`PLAN-072`](plans/PLAN-072-a-char-reads-back-as-written.md))
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
