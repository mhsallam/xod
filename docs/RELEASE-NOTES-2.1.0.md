# Release notes: XOD 2.1.0 (2026-11-18)

What changed for users from 2.0.3, collected as each fix landed. Each item links the plan that records it. Before
upgrading, read [Upgrading](https://xod.sabaeon.com/docs/upgrading) or the section below.

**The release, decided by the owner on 2026-11-18** (`TASK-101`): everything in the tree ships as **2.1.0**, tagged
`v2.1.0` and published to NuGet from the GitHub release by the `publish` workflow, which runs the tests on both
frameworks first and waits for the owner's approval. A minor version, since 2.0 is young and most changes make it
do what it already claimed; the behaviour changes below are listed first so that an upgrade is checked.

## Upgrading from 2.0.3

Back up the database folder, and close every context of 2.0.3 before the first open with 2.1.0: one database is
never open from two versions at once. The file format is the same.

### What can now throw

- **A key part at its type's default.** A row whose key isn't an autonumber is refused
  (`MissingPrimaryKeyValueException`) when a key part is `0`, `false`, an enum's first member, an empty `Guid`, or an
  empty or null string. So is an update by such a key, and an update or delete by an example that sets some key
  parts and not others. A row 2.0.x stored that way can't be updated or deleted by its key: remove it with `DeleteWhere`.
- **A type XOD can't store or read back.** A class with a property of `Int128`, `Half`, `BigInteger`, `nint`, a tuple
  or a struct of your own, or a collection a `List<T>` can't be assigned to (`class Notes : List<Note>`, `HashSet<T>`,
  `ArrayList`), is refused with `XodException` when it is first used. Mark the property `[NotMapped]` or change its
  type. 2.0.3 stored these and read them back empty or at their default.
- **A write inside a read** on the same thread (a query's predicate, or while its results are enumerated) throws
  `XodException`, for every table. Make the write after the read.
- **Inside a trigger,** `BeginTransaction` throws `TransactionException`, and setting, changing or removing the
  password throws `InvalidOperationException`.

### Behaviour to check

- **A transaction belongs to the code that began it.** Other threads' reads and writes wait for it to end, up to
  `TransactionTimeoutMs`, and no longer see or join its writes. Keep transactions short, and begin one where its work
  is done.
- **Writes are made one at a time,** for every table.
- **`InsertRange`, `DeleteWhere` and `DropAll` are each one write.** Outside a transaction, with the default
  durability, one refused item leaves none of it done. `DeleteWhere` returns the number of rows it deleted.
- **A parent's update writes its `[Children]` and the relations declared `Cascade = CascadeOptions.Update`, and no
  others.** A plain list or reference of objects with a key only links them. Declare
  `[Property(Cascade = CascadeOptions.Update)]` on it, or update those objects yourself.
- **Deleting a row deletes its children and its plain lists of objects without a key,** with their triggers. An
  `object` property deletes the row of a keyed type it holds only with `Cascade = Delete`. Clearing a
  `Cascade = Delete` reference in an update deletes its row.
- **A child without a key is written again** at each update of its parent: its old row is deleted and a new one
  inserted, with their triggers. Give it a key to have it updated in place.
- **The obsolete `CryptoHelper.EncryptProperty` and `DecryptProperty`,** given a passphrase, take about 25 ms a call.
- **A password-protected database written by 2.0.x** reads each file a little slower (about 13 ms, once per context)
  until the file is written again.

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
- **A key part can't be its type's default.** A row whose key isn't an autonumber is refused (`MissingPrimaryKeyValueException`) when a key part is `0`, `false`, an enum's first member, an empty `Guid`, or an empty or null string; it was stored, and never kept unique. An update or delete by a key with such a part, or by an example that sets some key parts and not others, is refused too, with and without indexing: it named different rows with indexing on and off. A row an earlier version stored with a key part at its default can't be updated or deleted by its key: remove it with `DeleteWhere`. ([`PLAN-079`](plans/PLAN-079-a-key-part-at-its-default-is-no-value.md))
- **A parent's update writes the values of its children and of the rows a relation declares `Cascade = Update` for, and no others.** A plain list or reference of objects with a key only links them: their values are no longer written by the parent's update. Declare `[Property(Cascade = CascadeOptions.Update)]` on the property to keep that, or update the objects themselves. ([`PLAN-080`](plans/PLAN-080-what-a-parent-s-update-writes-into-related-rows.md))
- **A collection XOD can't read back is refused.** A property of a class that derives from a list (`class Notes : List<Note>`), a `HashSet<T>`, an `ArrayList` or another collection a `List<T>` can't be assigned to used to be stored, and read back empty or failed to read. Now its class is refused when it is first used, with an `XodException` naming the property: declare it as an array, a `List<T>`, an interface a `List<T>` implements, or a map, or mark it `[NotMapped]`. ([`PLAN-082`](plans/PLAN-082-a-collection-xod-cant-read-back-is-refused.md))

- **The obsolete `CryptoHelper.EncryptProperty` and `DecryptProperty`, given a passphrase, derive its key at each call.** They kept every key derived in a cache for the life of the process; a call now takes about 25 ms. A `[Crypto(AES256)]` property is encrypted by the database itself. Without a passphrase they are as before. ([`PLAN-083`](plans/PLAN-083-a-context-owns-its-file-keys.md))

## Fixed

- **A child moved to another parent leaves the old one.** Updating a child read on its own to point at another parent left it listed by both, and deleting the old parent deleted it. ([`PLAN-085`](plans/PLAN-085-a-moved-child-leaves-the-parent-its-row-names.md))
- **A row a parent holds through a reference stays with its own parent.** A row with a `[ParentKey]` that another row of the parent's type held through a `Cascade = Update` reference was taken as that row's child, and its key rewritten. A row with two parent properties of one type kept the first one's parent for both, and a parent with two `[Children]` lists of one type listed a child written on its own in the first. ([`PLAN-085`](plans/PLAN-085-a-moved-child-leaves-the-parent-its-row-names.md))
- **With a password, writes are fast again and memory no longer grows.** Each write derived a key afresh and kept it for the life of the process: an insert took about 20 ms, and memory grew at every write. A database derives its key once now, and lets it go with its context. Files are written in the same format. ([`PLAN-083`](plans/PLAN-083-a-context-owns-its-file-keys.md))
- **A crash while a new page or table file is written no longer leaves the table unreadable.** The next open put the part written in place, and every read of the table failed. ([`PLAN-084`](plans/PLAN-084-every-file-written-whole-goes-through-its-tmp.md))
- **A child without a key, inserted or updated on its own, keeps its place in its parent's list.** It took the place of the first child listed, so the parent listed only the newest. ([`PLAN-081`](plans/PLAN-081-a-child-s-entry-is-found-by-its-row.md))
- **A value that holds `<Row code="` or `</Row>`, or a property or class named `Row`, no longer cuts a row short.** `Select(skip, take)` dropped rows, `Last` returned a row made of a value's text, `Find` read the properties after a `Row` property as null, and an update wrote that loss back. ([`PLAN-078`](plans/PLAN-078-a-page-s-rows-found-by-one-reader.md))
- **A row that can't be read is an error, not a missing row.** A damaged row or page throws `DatabaseFileException` naming the page; it read as absent, or with a value at its default. ([`PLAN-078`](plans/PLAN-078-a-page-s-rows-found-by-one-reader.md))
- **Reads on several threads at once no longer fail.** A `Last` beside another read could throw `ArgumentOutOfRangeException`, or read another page's bytes. ([`PLAN-078`](plans/PLAN-078-a-page-s-rows-found-by-one-reader.md))
- **An update can point a reference at another row, or at a new object.** It threw `ReservedPrimaryKeyException`. A row's edit alone, with its parent unchanged, is written by the parent's update when the relation cascades updates; it was lost. A copy of a row held by a parent no longer overwrites a newer one. ([`PLAN-080`](plans/PLAN-080-what-a-parent-s-update-writes-into-related-rows.md))
- **A row whose key comes from a `[ParentKey]` or `[ForeignKey]` is kept unique.** A second row with the same key was stored. ([`PLAN-079`](plans/PLAN-079-a-key-part-at-its-default-is-no-value.md))
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
