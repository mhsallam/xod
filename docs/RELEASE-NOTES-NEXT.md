# Release notes for the next release (draft)

What changes for users in the next release, collected as each fix lands. The version number and the
release itself are the owner's decision; nothing here is published. Each item links the plan that
records it.

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
- **A commit that fails is undone at once**, wherever it failed, and the original exception is thrown; a commit
  is refused (`TransactionException`) while another commit's journal is in the folder
  ([PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md)).
- **`BeginTransaction` flushes what the context had buffered** (`DurabilityMode.Buffered`) before the
  transaction starts ([PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md)).

- **Autonumber seeds and increments can be set**: `[Property(AutoNumber = true, IdentitySeed = 1000,
  IdentityIncrement = 5)]` ([PLAN-052](plans/PLAN-052-autonumbers-password-service-migrator.md)).
  - A type whose settings can't number the property is refused when it is first used, with
    `AutonumberSeedException` (new): a seed below 1, an increment below 1, a value that isn't a whole number,
    a seed beyond the property's type, a seed or increment on a Guid autonumber. Before, a subclass of the
    attribute could set them and got repeated or overflowing numbers.
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

- **A folder holds one database** ([PLAN-053](plans/PLAN-053-statement-undo-counters-stored-values-locks.md)):
  opening a folder that holds more than one database file (`.xod`, `.xodj`, `.xodb`) is refused, and so is a
  path naming a new database file in a folder that already holds one (a mistyped name, or a case that differs
  on Linux). Every database file in a folder shares its tables, so a second one was another way into the same
  data that the first one's lock didn't cover. Move all but one to folders of their own.
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
  default date, a zero interval and a type default under another `[DefaultValue]` read back as stored, and a
  null reads back as null where a new instance holds something else, whichever way the row is written (an
  insert with or without `lazyLoad`, an update, a key filled in from a relationship). The files gain two forms,
  an empty element or attribute and an element marked `nil="true"`. XOD 2.0.0 and 2.0.1 read an empty or
  marked element as they read a value left out, and an empty attribute as `""`; their index leaves `""` out, so
  they don't find an indexed empty value through the index. Files written by XOD 1.x stored an empty string as
  an empty element: it now reads `""`, where every earlier version read null. An attribute-positioned property
  can't store null that way (ISS-094), and collection entries aren't covered: a dictionary's null value reads
  back `""` and a list drops a null item (ISS-097).
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
- **Closing a context waits** for reads and writes running on it in other threads, and takes its write lock
  for its last flush. A context closed from inside one of its own reads still closes, and leaves its indexes to
  be rebuilt by the next open.

## Version

The reviewer of `TASK-062` points out that the release adds a public type (`AutonumberSeedException`) and a
capability, and breaks source that reads `IdentitySeed` as a number: under semantic versioning, a minor
version rather than a patch. The owner decides.

## Site pages to update with the release

- `attributes.md` and `migrating.md`: `IdentitySeed` and `IdentityIncrement` can be set (they say they can't).
- `options.md`: `EnableCompression` is obsolete.
- `storage.md`, `cli.md` and `exceptions.md`: the migrator's refusals and `DatabaseLockedException`.
- Wherever `XodSecurityService` is shown (none found on 2026-11-12).

## Fixed

- Earlier unreleased fixes, each with its own behaviour notes: [PLAN-048](plans/PLAN-048-format-defect-fixes.md),
  [PLAN-049](plans/PLAN-049-property-encryption.md), [PLAN-050](plans/PLAN-050-password-change.md).
- ISS-043: see [PLAN-054](plans/PLAN-054-indexes-left-behind-by-a-crash.md).
- ISS-060, ISS-062 (mitigated), ISS-083 to ISS-088, ISS-091 (mitigated): see
  [PLAN-053](plans/PLAN-053-statement-undo-counters-stored-values-locks.md).
- ISS-047, ISS-048 (mitigated), ISS-059, ISS-069, ISS-072, ISS-075, ISS-080 to ISS-082: see
  [PLAN-052](plans/PLAN-052-autonumbers-password-service-migrator.md).
- ISS-070, ISS-071 (mitigated), ISS-073, ISS-074, ISS-076 to ISS-079: see
  [PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md) and [KNOWN-ISSUES](KNOWN-ISSUES.md).
