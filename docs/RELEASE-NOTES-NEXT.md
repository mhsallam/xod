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

## Fixed

- Earlier unreleased fixes, each with its own behaviour notes: [PLAN-048](plans/PLAN-048-format-defect-fixes.md),
  [PLAN-049](plans/PLAN-049-property-encryption.md), [PLAN-050](plans/PLAN-050-password-change.md).
- ISS-070, ISS-071 (mitigated), ISS-073, ISS-074, ISS-076 to ISS-079: see
  [PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md) and [KNOWN-ISSUES](KNOWN-ISSUES.md).
