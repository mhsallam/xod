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
- ISS-047, ISS-048 (mitigated), ISS-059, ISS-069, ISS-072, ISS-075, ISS-080 to ISS-082: see
  [PLAN-052](plans/PLAN-052-autonumbers-password-service-migrator.md).
- ISS-070, ISS-071 (mitigated), ISS-073, ISS-074, ISS-076 to ISS-079: see
  [PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md) and [KNOWN-ISSUES](KNOWN-ISSUES.md).
