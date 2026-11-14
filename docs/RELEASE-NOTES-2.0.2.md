# Release notes: XOD 2.0.2 (2026-11-14)

What changed for users from 2.0.1 (which changed only the package's README from 2.0.0), collected as each
fix landed. Each item links the plan that records it. Before upgrading, read
[Upgrading from 2.0.0 or 2.0.1](https://xod.sabaeon.com/docs/upgrading).

**The release, decided by the owner on 2026-11-14:** everything in the tree ships as **2.0.2**, tagged `v2.0.2` and
published to NuGet from the GitHub release by the `publish` workflow, which runs the tests on both frameworks
first and waits for the owner's approval. The site's `**Next release:**` labels became 2.0.2 the same day.

## New

- **XOD SQL, version 1 (SELECT only)**, in .NET and in the CLI
  ([PLAN-060](plans/PLAN-060-xod-sql-select-manifest-cli.md), the language of
  [PLAN-045](plans/PLAN-045-query-language-spec.md)):
  - `db.Sql("SELECT ...")` returns a `SqlResult`: the columns with their SQL types, the rows, and warnings.
    `db.Sql<T>(...)` puts each row in a new `T`, column by property name.
  - Parameters: `?` by position (`db.Sql(sql, "kitchen", 20m)`), or `@name` from a dictionary.
  - The language: inner, left and cross joins; paths along references (`o.Customer.Name`); children
    collections (`JOIN c.Orders o`); maps and lists (`Tags['k']`, `'x' = ANY(Labels)`, `CARDINALITY`);
    `GROUP BY`, `HAVING`, `DISTINCT`, `ORDER BY` with `NULLS FIRST`/`LAST`, `LIMIT`, `OFFSET`; subqueries; the
    functions of PLAN-045 section 5. `EXPLAIN SELECT ...` says how each table is read.
  - An error is an `XodSqlException` with a stable `Code` (`XQ001` to `XQ012`); warnings are in
    `SqlResult.Warnings` (`XQ008`, `XQ013`, `XQ014`).
  - `INSERT`, `UPDATE` and `DELETE` are refused (`XQ001`): writes come in a later release (PLAN-045 D3).
  - Conditions on a key, unique or indexed column of a table whose class the program has are answered by the
    index `Query<T>` uses. The answer is the same with indexing on or off, rows in stored order.
  - A `[Crypto(AES256)]` column reads as plain text only when the database was opened with its password or
    `DatabaseOptions.PropertyEncryptionKey`. Otherwise it reads as NULL, with warning `XQ008`. A value 2.0
    encrypted with its built-in key (ISS-050) reads as NULL with `XQ008` too; the lambda API still opens it.
  - `DatabaseOptions.SqlSchema`: `Classes` (the default) types a table from its class when the program has
    it; `Recorded` reads every table by the schema its file records, as `xod query` does.
- **Each table file records the schema of its rows** (`<Schema>`, [PLAN-046](plans/PLAN-046-file-format-spec.md)
  section 10). A table file is created with it, and every write checks the files it may write and replaces
  a missing or different one, as part of the write (a rollback takes it back). A read never writes it. A
  database written by 2.0.0 or 2.0.1 gets it at its next write. 2.0.0 and 2.0.1 keep the element when they
  rewrite a table file, but don't update it.
- **`xod query <db> "SELECT ..." [--format table|csv|json] [--password <pwd>]`**: reads the tables through the
  schema their files record, without the application's classes. It writes no rows and no schema, and doesn't
  create a missing database; opening it still holds `xod.lock` and, as any open does, puts back a commit a
  crash cut short. A table whose file records no schema is
  read with every column as text, with warning `XQ013`. `json` writes values as the conformance set encodes
  them. An error prints `XQnnn: message` and exits with `1`.
- **Every `xod` command writes UTF-8 when its output goes to a file or a pipe.** It used the console's code
  page, where Arabic text, an em dash or an accented letter became `?` or a byte other tools misread. A
  console window is unchanged.

## Behaviour changes

- **Every context holds the database's lock file** (`<database file>.lock`): shared, or exclusively with
  `DatabaseOptions.LockDatabase` ([PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md)).
  - A context opened with `LockDatabase` and contexts opened without it no longer share a database: whichever
    opens second gets `DatabaseLockedException`. Before, `LockDatabase` locked out only other `LockDatabase`
    contexts.
  - A password change (`Secure`, `Loose`, `ChangePassword`) is refused while another context has the database
    open.
  - The lock file stays in the folder after the database is closed, whoever opened it. Don't delete it while
    the database is open.
  - A database whose folder can't be written and has no lock file opens for reading only; writes are refused
    with the reason. Before, a write failed when it reached the disk.
  - Don't run processes of XOD 2.0.0 or 2.0.1 on a database a newer version has open: they take no hold, so
    the newer version can't see them.
  - Every context also holds the folder's lock file (`xod.lock`), of the same kind, before the database file's
    ([PLAN-059](plans/PLAN-059-one-database-per-folder-recorded-format-value-collections.md)): every database file in a folder shares the folder's files, so a hold has to cover the
    folder. An open naming a new database file beside one another context had open used to hold its own lock
    alone, recover the folder as if it were alone (undoing that context's commit under way), and only then be
    refused (ISS-096). It stays in the folder too.
  - The open that creates a new database holds it alone until its database file is written. Another open
    meanwhile, of that database or another in its folder, waits up to `LockRetryTimeoutMs`, then gets
    `DatabaseLockedException`, as it does while an open recovers what a crash left.
  - A password change whose hold alone failed for a reason other than another context's (too many open files, a
    network error) left the context holding nothing while it went on writing; it holds the database again.
- **A commit that fails is undone at once**, wherever it failed, and the original exception is thrown; a commit
  is refused (`TransactionException`) while another commit's journal is in the folder
  ([PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md)).
- **`BeginTransaction` flushes what the context had buffered** (`DurabilityMode.Buffered`) before the
  transaction starts ([PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md)).

- **Autonumber seeds and increments can be set**: `[Property(AutoNumber = true, IdentitySeed = 1000,
  IdentityIncrement = 5)]` ([PLAN-052](plans/PLAN-052-autonumbers-password-service-migrator.md)).
  - A type whose settings can't number the property is refused when it is first used, with
    `AutonumberSeedException` (new): a seed below 1, an increment below 1, a value that isn't a whole number,
    a seed beyond the property's type, a seed or increment given on a Guid autonumber. Before, only a subclass
    of the attribute could set them: a number autonumber took them and could get repeated or overflowing
    numbers, and a Guid autonumber ignored them. **A subclass that sets either one in its constructor, even to
    1, now makes a Guid autonumber it marks refused**; one that sets neither, and `[Property]` without them,
    are accepted on a Guid as before (ISS-117).
  - A float or double autonumber stops at 2^24 or 2^53 with `OverflowException`, as an int stops at its range,
    instead of handing out a number twice.
  - `IdentitySeed` and `IdentityIncrement` are now `object`, not `dynamic`: binary compatible, but code that
    reads them as a number (`int s = attr.IdentitySeed;`) needs a cast.
- **Updates and autonumbers** ([PLAN-052](plans/PLAN-052-autonumbers-password-service-migrator.md)): an update
  whose new item leaves an autonumber unset (0) keeps the row's value, and the new item isn't changed; a value
  an update changes is never handed out to a later insert, in this context or after reopening.
- **An update with an `UpdateFilter`** keeps the values it leaves out that are stored as attributes (they were
  dropped, a key among them), and checks unique keys and indexes against the row as stored, not against the new
  item's values for the properties it leaves out.
- **Obsolete** (warning CS0618; an error in a build with warnings as errors): `DatabaseOptions.EnableCompression`
  (it never had an effect; use `StorageFormat.Binary`), and `XodSecurityService` / `IXodSecurityService` (use
  `XodContext.Secure`, `Loose` and `ChangePassword`). The service's methods now open a context and call it, so
  they keep encrypted values readable and are refused while another context has the database open.
- **`XodMigrator.ConvertDatabase`** ([PLAN-052](plans/PLAN-052-autonumbers-password-service-migrator.md)):
  - refused while a context has the source open (`DatabaseLockedException`), and no context opens it during a
    conversion;
  - recovers what a crash left in the source first, as an open would;
  - refused into a target folder that holds anything, and for a folder without a database file;
  - into another folder: the target appears whole or not at all (a crash leaves a `.<target>.converting-<id>`
    folder beside it, which can be deleted); in place: each file is replaced whole.
- **A database is written in the encoding its database file records** ([PLAN-059](plans/PLAN-059-one-database-per-folder-recorded-format-value-collections.md)): unless
  `DatabaseOptions.StorageFormat` is set, a context writes new and rewritten files in the encoding the database was
  created in or `XodMigrator` converted it to, and a new database in the one its extension picks (`.xod` XML,
  `.xodj` JSON, `.xodb` Binary; for a database opened through its folder, the extension of the file in it).
  A record that names no format counts as none. A database converted in place keeps its name, and went on being written in its
  extension's encoding; options that didn't set a format meant XML whatever the extension (ISS-093). A format set
  is still the one written, and leaves the record as it is. Each file is read in its own encoding, as before.

- **A folder holds one database** ([PLAN-053](plans/PLAN-053-statement-undo-counters-stored-values-locks.md)):
  opening a folder that holds more than one database file (`.xod`, `.xodj`, `.xodb`) is refused, and so is a
  path naming a new database file in a folder that already holds one (a mistyped name, or a case that differs
  on Linux). Every database file in a folder shares its tables, so a second one was another way into the same
  data that the first one's lock didn't cover. Move all but one to folders of their own. Two opens creating
  different databases in one empty folder at the same moment both went on (ISS-096, [PLAN-059](plans/PLAN-059-one-database-per-folder-recorded-format-value-collections.md)): now one
  does, and the other is refused and creates nothing.
- **Lock files**: a read-only lock file holds on a local file system. Where the file system gives no exclusive
  lock (an NFS client and a lock file the user can only read), `LockDatabase`, the recovery at open and a
  password change are refused rather than run unprotected. A context that can't open or create the lock file
  (a folder it can't write, read-only media) still opens for reading, but an open that finds a write cut short
  and can't take the lock is refused (it can't tell a crash's leftovers from another context's write under
  way), and the migrator is refused whenever it can't hold the source.
- **Writes and maintenance from inside a read on the same thread** (inside a query's predicate): an insert,
  update or delete, `BeginTransaction`, a commit or rollback, `Flush`, `ClearCache`, `Drop`, `GetStatistics`
  and a password change are refused with an `XodException` that says so, and the context goes on working.
  Before, they threw `LockRecursionException`, and a write left the context unusable. `GetStatistics` now
  takes the context's write lock, since it writes what the context holds first.
- **A statement that fails outside a transaction removes the rows it created**: a parent whose second child is
  refused leaves no first child behind. It is not all or nothing: changes a failed update made to rows that
  were there before aren't put back, and a crash leaves the created rows. Use a transaction for all or nothing.
  If removing the rows fails too, the `XodException` carries both failures and the context writes nothing more
  until it is reopened; under `DurabilityMode.Buffered`, writes still waiting in its buffer are then dropped, as
  at a crash.
- **A number handed out in a rolled-back transaction, or by a failed statement, isn't handed out again**, after
  a reopen either, unless the process stops before the context's next flush. A table dropped inside a
  transaction that is rolled back keeps its numbering.
- **Scalar values read back as written**: an empty string reads back as `""` (it read as null), `Guid.Empty`, a
  default date and a zero interval read back as stored (`[DefaultValue]` has no effect, ISS-118), and a
  null reads back as null where a new instance holds something else, whichever way the row is written (an
  insert with or without `lazyLoad`, an update, a key filled in from a relationship). The files gain two forms,
  an empty element or attribute and an element marked `nil="true"`. XOD 2.0.0 and 2.0.1 read an empty or
  marked element as they read a value left out, and an empty attribute as `""`; their index leaves `""` out, so
  they don't find an indexed empty value through the index. Files written by XOD 1.x stored an empty string as
  an empty element: it now reads `""`, where every earlier version read null. An attribute-positioned property
  can't store null that way (ISS-094). Maps and lists: the next entry.
- **Maps and lists read back as written** ([PLAN-059](plans/PLAN-059-one-database-per-folder-recorded-format-value-collections.md)): a dictionary's null value reads back as null (it
  read `""`), a list or array keeps its null items (they were dropped), an empty dictionary reads back empty (it
  read null), and a list or array of a nullable type (`List<int?>`) can be stored (it threw `XmlException`)
  (ISS-097). The files gain a marked entry and item (`<Entry key="k" nil="true" />`, `<String nil="true" />`).
  XOD 2.0.0 and 2.0.1 read a marked entry or item as its empty text (`""` for a string, null for a nullable type),
  and can't read a row that holds a list of a nullable type. An update that only sets a dictionary's value to
  null, or only reorders a list, is written (it was dropped, and reported made). A dictionary property of another
  type, a `SortedDictionary`, a `Hashtable` or a class deriving from `Dictionary<,>`, reads back as that type (it
  threw once it held anything; 2.0.x reads an empty one stored now the same way). A null list or dictionary the
  class initializes still reads back initialized (ISS-127).
- **`XodSecurityService`** (obsolete) accepts the database's folder, as a context does, and refuses a mistyped
  database file beside the database rather than doing nothing.
- **Deleting a child removes it from its parent's list**, whichever way the delete finds the row, and whether or
  not the child has a key property for its parent (it stayed listed); a child with no parent property still
  does (ISS-095). A child inserted again with the key of one deleted before is listed again.

- **Indexes after a crash** ([PLAN-054](plans/PLAN-054-indexes-left-behind-by-a-crash.md)): a context that
  writes keeps a small file beside the database, `<database file>.<id>.xidxdirty`, open while the context is,
  which says whether its indexes are behind its pages; it deletes the file when it closes cleanly. After a
  crash, whether or not other contexts had the database open, no context trusts the index files until the next
  open that has the database to itself drops them; they are rebuilt from the pages as they are needed (the
  first queries take longer), rather than trusted when they may miss the last rows written. Don't delete these
  files while the database is open.
- **A write that fails stays to be made** (ISS-098): a page write of `InsertRange`'s batch or of the buffer
  (`DurabilityMode.Buffered`) that failed, for example on a page file another program held open, was dropped:
  the context went on showing the rows, the disk never got them, and the indexes named rows the pages didn't
  hold. It now stays to be written by the next flush (`Flush`, the next transaction, the close), or is dropped
  by a rollback; an index save that failed is made by the next flush the same way. The call that failed still
  throws.
- **An index file another context rewrote** ([PLAN-055](plans/PLAN-055-index-files-replaced-or-held.md)):
  a context reads an index's parts when it first needs them. When another context, in this process or another,
  had rewritten the index file meanwhile, the read failed (`EndOfStreamException`) or could give wrong results.
  Each index file now says which version of it this is, and a context that finds it changed reads the parts it
  hasn't read yet from the table's pages instead, keeping everything else it holds.
- **An index file another program holds** (ISS-042, ISS-109): a query no longer fails because the index it rebuilt
  can't be saved over the file; it is rebuilt again at the next open. With a password, that save is made at once
  or not at all: it used to wait, and then failed the later writes, flushes and the close. A write no longer
  fails, after its row was written or deleted, because an index file it can't load, or a table's index once its
  last row is deleted, can't be deleted (the file is not trusted, and is dropped later).
- **Closing a context waits** for reads and writes running on it in other threads, and takes its write lock
  for its last flush. A context closed from inside one of its own reads still closes, and leaves its indexes to
  be rebuilt by the next open.
- **A write that fails without a transaction or a buffer stays to be made** (ISS-099, [PLAN-057](plans/PLAN-057-review-of-task-066-direct-writes-triggers-transactions.md)):
  in the default mode a page or table write that failed (a page file another program held open) was dropped, as
  ISS-098 was for batches and the buffer. A statement's writes are now made when it ends, after its index
  changes; one that fails throws and stays to be written by the next flush. An insert whose write fails is taken
  back, on any table, so it can be retried (ISS-120, [PLAN-058](plans/PLAN-058-standalone-undo-held-index-files-insert-range-index-save.md)). One gap remains (ISS-125): an
  `InsertRange`, an `Update` or a `Delete` whose write fails throws, but its change is still written by the next
  flush.
- **Index files another program holds, at a commit's undo and after `InsertRange`** ([PLAN-058](plans/PLAN-058-standalone-undo-held-index-files-insert-range-index-save.md)):
  a commit undone after it wrote its index files no longer fails on one another program holds open, which made the
  context refuse every later write until it was reopened (ISS-110); a file it can't delete is left untrusted, and
  the next open that has the database to itself drops it. On Windows, an `InsertRange` whose rows are written no
  longer throws because another context is reading its table's index file: the next flush saves the index
  (ISS-115). Until then the other contexts may read that index file behind the pages (ISS-126).
- **Transactions and files** ([PLAN-057](plans/PLAN-057-review-of-task-066-direct-writes-triggers-transactions.md)): no index file is written while a transaction is open,
  `Flush` inside it included; the commit writes them after its pages and a rollback forgets them (ISS-100). A
  page file that a transaction's delete emptied is set aside as `<file>.xdel` until the commit has written its
  files, rather than deleted at once, so its rows come back when the commit is refused, rolled back or cut short
  by a crash (ISS-107): they were lost. Don't delete `.xdel` files while the database is open; the next open
  alone clears them.
- **Triggers, closes and markers** ([PLAN-057](plans/PLAN-057-review-of-task-066-direct-writes-triggers-transactions.md)): a `Flush` from inside a write (a trigger) leaves
  the indexes to be checked rather than calling them caught up mid-statement, and closing a context from inside
  one of its own writes is refused with `InvalidOperationException` (ISS-101). A write on another thread that was
  under way when the context closed fails with `ObjectDisposedException` instead of landing after the close
  (ISS-103). A crash's marker file that another program holds no longer keeps stale indexes trusted at an open
  that has the database to itself (ISS-102); while another context has the database open it still does (ISS-119).
- **An update records only the index keys it changes**
  ([PLAN-056](plans/PLAN-056-reads-that-scale-and-writes-that-dont-stall-them.md)). An update used to record every
  indexed property's key again, changed or not. The index file grew with every update, and once its log outgrew
  its last full copy, a flush rewrote the whole index while every reader waited. An update that changes no
  indexed value now writes nothing to the index. And a page whose row grew keeps its row map, which the next
  read used to rebuild. Under a steady writer that means fewer garbage collections and no index rewrites from
  updates alone (measured in a private copy, section 6.4; not committed benchmark evidence). One side effect
  (ISS-114): if the table file was written after the index (a non-indexed autonumber, a rollback's counters),
  the open's check by time can rebuild a correct index from the pages. The result is correct; that open is
  slower.

## Version

The reviewer of `TASK-062` points out that the release adds a public type (`AutonumberSeedException`) and a
capability, and breaks source that reads `IdentitySeed` as a number: under semantic versioning, a minor
version rather than a patch. SQL adds more public types (`XodSqlException`, `SqlResult`, `SqlSchemaSource`) and
`DatabaseOptions.SqlSchema`. The owner decided on 2026-11-14 to ship it as **2.0.2** (the top of this file).

## Site pages to update with the release

Drafted on 2026-11-13 in `xod-site` and checked against the tree as of that day; rechecked on 2026-11-14 (below).
Committed (`xod-site` `2153a0d`) and deployed on 2026-11-14, on the owner's go: the docs describe 2.0.2 as the next
release while 2.0.1 is the one on NuGet.

- **Docs:** 17 pages in `src/pages/docs` and a new `upgrading.md`, the checklist for a 2.0.x application, with
  its entry in `src/content/docs-nav.ts`. Each claim was checked in the code, and the behaviour claims were
  probed on the tree and on v2.0.0. An independent review followed, and its findings were fixed.
- **What's new or different is labelled `**Next release:**`,** next to what 2.0.x does. At the release, replace
  the label with the version number. Recheck the pages against any engine change made after 2026-11-13.
  The failed-write text (`transactions.md`, `limits.md`, `upgrading.md`) was rechecked against `c0fd701`
  (TASK-070: an `Insert` taken back; `InsertRange`, `Update` and `Delete` still made, ISS-125).
- **The pages describe a `Guid` autonumber as working,** which holds now that ISS-117 is fixed (committed
  2026-11-14).
- **Rechecked on 2026-11-14** against the engine changes since the draft, `TASK-071` (`c614e30`) and `TASK-072`
  (SQL, in the tree). Added, each labelled `**Next release:**`:
  - the format a database's file records, and a folder's name no longer picking one (`storage`, `options`,
    `api`, `upgrading`);
  - `xod.lock`, and the open that creates a database holding its folder alone (`storage`, `transactions`,
    `upgrading`);
  - maps and lists that read back as written, and the map types that read back as their own (`models`,
    `storage`, `upgrading`);
  - the `<Schema>` element of each table file (`storage`, `upgrading`);
  - SQL and `xod query` (`reading`, `cli`, `api`, `options`, `exceptions`, `limits`, `index`, `storage`);
  - UTF-8 output from `xod` (`cli`, `upgrading`).

  Not on the pages: `PLAN-056`'s index keys (`c5ccf66`), which changes how much an update writes, not what a
  call does.

  Also corrected: `transactions.md`'s durability paragraph. The draft said "a write that has returned
  survives the process crashing", which is wrong for `Buffered` and, under ISS-147, for writes beside a commit
  a crash cut short. The new wording was approved by the owner on 2026-11-14.
- **The pages also correct older statements that were wrong in 2.0.x too:**
  - durability against a power cut;
  - "ACID", "write-ahead journal" and "streamed" reads;
  - what the file extensions do;
  - the getting-started file sample;
  - "optional" cascade delete.
- **Landing page (`src/pages/index.astro`)**, approved by the owner on 2026-11-13:
  - "ACID" is gone from the title, the description, the headline, the lede and the chip;
  - "crash-safe writes when power is cut" now says a commit cut short by a process crash is undone at the
    next open;
  - the code samples use `Orders = new() { … }`, since the old form threw `NullReferenceException`;
  - "indexed query, not a table scan" became "query with a lambda", since `Status` isn't indexed.

## Fixed

- Earlier unreleased fixes, each with its own behaviour notes: [PLAN-048](plans/PLAN-048-format-defect-fixes.md),
  [PLAN-049](plans/PLAN-049-property-encryption.md), [PLAN-050](plans/PLAN-050-password-change.md).
- ISS-042: see [PLAN-055](plans/PLAN-055-index-files-replaced-or-held.md).
- ISS-043, ISS-098: see [PLAN-054](plans/PLAN-054-indexes-left-behind-by-a-crash.md).
- ISS-060, ISS-062 (mitigated), ISS-083 to ISS-088, ISS-091 (mitigated): see
  [PLAN-053](plans/PLAN-053-statement-undo-counters-stored-values-locks.md).
- ISS-047, ISS-048 (mitigated), ISS-059, ISS-069, ISS-072, ISS-075, ISS-080 to ISS-082: see
  [PLAN-052](plans/PLAN-052-autonumbers-password-service-migrator.md).
- ISS-070, ISS-071 (mitigated), ISS-073, ISS-074, ISS-076 to ISS-079: see
  [PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md) and [KNOWN-ISSUES](KNOWN-ISSUES.md).
