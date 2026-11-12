# PLAN-053: Statement undo, counters across a rollback, values that read back as written, and the lock fixes from the reviews (ISS-060, ISS-062, ISS-083 to ISS-088, ISS-091)

- **Task ID**: `TASK-063`
- **Status**: Fixed in the tree (2026-11-12), not released: no version change, nothing pushed.
- **Related**: [`PLAN-048`](PLAN-048-format-defect-fixes.md) section 7 (ISS-060 and ISS-062 filed),
  [`PLAN-052`](PLAN-052-autonumbers-password-service-migrator.md) (ISS-083 filed; its round-2 review found
  ISS-084 to ISS-087), [`PLAN-051`](PLAN-051-commit-failure-and-shared-access.md) (the lock file and recovery),
  [`PLAN-046`](PLAN-046-file-format-spec.md) sections 2, 7.2, 8 and 9, [`KNOWN-ISSUES.md`](../KNOWN-ISSUES.md)

## 1. What was wrong, and why

| Issue | Defect | Cause |
| :--- | :--- | :--- |
| ISS-062 | Outside a transaction, a parent whose second child was refused left its first child in the child table, with a parent key matching no row. | Each row a statement creates is written as the object graph is written (`AddToPage` saves the child from inside the parent's write), and nothing groups a statement's writes. |
| ISS-083 | A rollback reset the autonumber counters, so a number handed out inside the rolled-back transaction was handed out again. | The rollback's "forget everything held" (`ForgetEverythingHeld`) reset the counters with the caches, though counters only ever rise. |
| ISS-060 | Some values didn't read back as written: `""` as null, `Guid.Empty` as a new instance's Guid, a default date, an interval of zero, a type default under a different `DefaultValue`, a null whose new instance holds a value. | Both writers left a value out of the row when it equalled the type's default, while a read gives a missing value the property's `DefaultValue` or what a new instance holds: two rules for one question. |
| ISS-084 | A read-only lock file let the migrator past an open context (in place it rewrote the files under it) and let an open or a conversion recover without a hold: with a commit paused mid-way in another process, the conversion's recovery undid that live commit and deleted its journal; the commit then reported success with its rows gone (round 2 of the `TASK-062` review, reproduced). | The exclusive open asked for write access, so a read-only lock file failed it with `UnauthorizedAccessException`, which both callers took for "a folder nothing can write" and went on without a hold. |
| ISS-085 | A context opened through a second database file in the folder wrote to the shared tables while a conversion held the first (reproduced; predates). | A folder is one database (tables are named by type), but each database file has its own lock file, and the engine created whatever database file it was given, beside another. |
| ISS-086 | A write to a standalone table from inside a read of it on the same thread (a `QueryDelegate` predicate) wedged the context: transactions refused, other threads' writes waiting for ever, `Dispose` throwing (reproduced; predates). A transaction begun there kept the transaction gate, so later ones timed out. | `EnterTableWriteLock` took the engine's read lock, then the table's write lock; the second threw `LockRecursionException` (a read can't be upgraded) and the first was never given back. `BeginTransaction` the same with its gate. |
| ISS-087 | `XodSecurityService` given the database's folder (which a context accepts) changed nothing and reported nothing. | It acted only when its path was an existing file. |
| ISS-088 | Deleting a child found through the primary-key index left it in its parent's list; the undo of ISS-062 did the same (found by its review; the delete case predates). | `RemoveFromParentElements` passed the bare row code to `RemoveFromParent`, which compares the parent's entries, item codes ("page.row"), so it matched none. |
| ISS-091 | An undo that failed part-way (a page file held open) left the index without a row the page still held: a lookup missed a row a scan found (found by the review of the undo). | The delete drops the index entry before the page change reaches the disk, and the undo saved the index anyway. |

## 2. The design

### Statement undo (ISS-062): a mitigation

- The outermost `Create` or `UpdateItem` of a statement keeps the list of rows it creates
  (`statementCreates`), only inside `EnterTableWriteLock`'s engine-wide write lock, which every type with
  relations takes (a standalone row creates nothing else). Whether a statement is under way is asked of
  `globalWriteDepth`, kept by the lock helpers: `ReaderWriterLockSlim.IsWriteLockHeld` is for asserts and
  debugging only, per its documentation.
- A statement that fails deletes the rows it created, newest first, through `DeleteByItemCode` without its
  cascade (a cascade could reach rows the statement didn't create), which takes each out of the parent that
  lists it (ISS-088). The counters stay where the statement left them, and are recorded at the next flush, as
  after a rollback (ISS-083).
- **If the undo fails too** (ISS-091): the caller gets an `XodException` carrying both failures; the indexes of
  the tables the statement wrote are dropped, in memory and on disk (a derived cache, rebuilt from the pages,
  PLAN-046 section 8); and the context writes nothing more until it is reopened, since what it holds may not be
  what the disk holds. Such a context rebuilds an index it needs without saving it, and writes nothing when it
  closes.
- **What it is not** (ISS-090, filed): all or nothing. It removes created rows only, while the process runs.
  An update that fails after it replaced a row leaves that change in the cached page for the next write to save
  (ISS-089, predates); a crash leaves the created rows; a failure a trigger catches inside a statement that
  succeeds is not undone. The structural fix is a statement savepoint or an implicit transaction (ISS-090).

### Counters across a rollback (ISS-083)

- `ForgetEverythingHeld` no longer resets the counters. A rollback sets `autonumberMarksPending`; the next flush
  records every counted type's counter in its table's mark (`RecordCountersInMarks`, creating a table file the
  rollback took away), so a reopen doesn't hand the numbers out again. `Dispose` flushes after the rollback of a
  transaction left open.

### Values that read back as written (ISS-060)

- **One rule** (`StoredValue.IsLeftOut`, both writers): a value is left out of the row only when a read gives
  it back. An empty string is stored as an empty element (`<Name></Name>`) or attribute; a null whose new
  instance holds something else is stored as `<Name nil="true" />`. What a read gives back anyway stays out, so
  the format doesn't grow for it.
- **Readers**: the document reader, the fast row reader and the index rebuild (`ReadBackFromRow`) read the nil
  mark as null and an empty element as `""`; so does the independent reader (`conformance/format/read.mjs`),
  whose index check keys an empty element `""` and a nil one not at all. PLAN-046 sections 7.2 and 8.3
  describe it. A conformance fixture written by the tree, `conformance/data/stored-values` (two tables, one
  per row writer), pins the forms for every reader (PLAN-047 section 6.1).
- **Older readers**: 2.0.0 and 2.0.1 skip an empty value and leave what a new instance holds (their reader's
  code), so they read the new forms as they read a value left out: as they read their own files.
- **Limitation (ISS-094, filed)**: an attribute can't say null, so a null in an attribute-positioned property
  whose new instance holds a value still reads back as that value.

### The lock file (ISS-084)

- `IOService.OpenLockFile` opens the lock file with read access for both kinds of hold, so a read-only lock
  file holds as well as any: on Windows the share mode does the locking; on Unix .NET takes `flock` `LOCK_EX`
  for `FileShare.None` whatever the access (`SafeFileHandle.Unix.cs`, dotnet/runtime release/9.0, read for this
  change).
- Nothing recovers or converts without a hold. An open that finds something pending and can't take the lock
  file (it can't be read, or created) is refused with the reason; the migrator is refused whenever it can't
  hold the source. A context with no hold still opens for reading when nothing is pending.

### One database per folder (ISS-085)

- `DatabaseFiles.Resolve` is the one place that decides which database a path names, for the context, the
  password service and the migrator: a database file's path, or a folder's database file (`data.xod` for a new
  one). A folder holding more than one database file is refused, and so is a path naming a new database file
  beside one (a mistyped name, a case that differs on a case-sensitive file system).

### Lock helpers (ISS-086)

- Each lock helper returns holding everything it took or throws holding nothing (`EnterSecond`), and
  `BeginTransaction` gives its gate back the same way. A write from inside a read of its table is refused with
  an `XodException` that says so, in place of the lock's `LockRecursionException`.

### The password service (ISS-087)

- `XodSecurityService` finds the database through `DatabaseFiles.Resolve`, so a folder works and a mistyped
  file beside the database is refused; a database that isn't there still only records the password, as before.

## 3. Tests

- `XodStatementUndoTests` (13): an insert whose second child is refused leaves no child, in every encoding; a
  range keeps the items before a refused one; an update whose second new child is refused leaves no new child;
  the undo leaves rows the statement only referred to; an after-trigger that throws leaves nothing; inside a
  transaction, nothing is left for the commit; a failed insert of a child leaves its parent without it;
  deleting a child takes it out of its parent's list; a failed statement's number isn't handed out again after
  a reopen; an undo that fails leaves the index agreeing with the pages, the context refusing writes, and a
  reopen that writes (Windows: a file held open blocks its deletion there only).
- `XodAutonumberTests` (+3, ISS-083): a number handed out in a rolled-back transaction isn't handed out again;
  nor one handed out in a transaction the close rolls back, after reopening; nor one from a rolled-back first
  insert (the rollback took the table away), after reopening.
- `XodStoredValueTests` (10) and `XodWriterAgreementTests` (+1, ISS-060): every value a read wouldn't give
  back is stored, by the document writer and the fast row writer, in every encoding; an index rebuilt from the
  pages keys an empty string and not a marked null; values a read gives back are still left out; both writers
  store an empty string and a marked null the same way.
- The conformance fixture `stored-values`: `read.mjs --check` and `--check-fresh` read it to
  `format/content-stored-values.json`, and its index keys match its pages.
- `XodMigratorSafetyTests` (+3): a read-only lock file doesn't let a conversion past an open context, nor undo a
  live commit (the crash writer paused mid-commit); a folder with two database files is refused.
- `XodSharedAccessTests` (+3): an open recovers nothing while another context holds the database, even with a
  read-only lock file; an open that can't take a hold (a lock file it can't read, Windows) recovers nothing and
  is refused while something is pending, and so is a conversion; a second database file in a folder is refused.
- `XodLockSafetyTests` (3, new): a write refused inside a read of its table, standalone and with relations,
  leaves no lock held; a transaction refused inside a read leaves the next one free.
- `XodSecurityServiceTests` (+2): a change through the database's folder changes the database; a mistyped
  database file beside the database is refused.

## 4. How it was checked

- **Red first**: every new test failed on the code before, for the reason it names (13 for the review
  findings, recorded 2026-11-12; the ISS-060, ISS-062 and ISS-083 tests before them).
- **Guards proven by breaking the code** (each break turned at least one test red, or failed the reader's
  check; the code was restored and checked byte for byte):
  - ISS-060 (11 breaks): a value left out whatever a read gives; a null left out whatever a read gives; the
    document writer dropping the marked null; the reader ignoring an empty string; the reader ignoring the
    nil mark; the index rebuild ignoring it; the fast reader ignoring it; the fast reader ignoring an empty
    string; the fast writer leaving out an empty string, a default date, `Guid.Empty`. And the independent
    reader (4): the nil mark ignored; empty text read as missing; the index check skipping `""`; the index
    check keying a nil element.
  - ISS-083 (4): the counters reset by a rollback; the marks not recorded after one; the close not flushing
    after its rollback; a table the rollback took away getting no mark.
  - ISS-062 (before the review fixes, 6): no undo on failure; rows not recorded; an update with no
    statement; a nested create owning its own statement; the top row recorded after its trigger; the undo
    cascading (it deleted rows the statement only referred to).
  - The review fixes (16): the exclusive hold asking for write access; an open recovering without a hold;
    the migrator converting without one; a folder with two database files allowed; a new database file
    beside one allowed; the password service acting on a file path only; the second lock not released when
    taking it throws; the lock's exception not said in XOD's terms; the transaction gate kept on failure;
    statements never owned; the unlink by the bare row code; the undo recording no counters; a failed undo
    keeping the indexes; a failed undo going on writing; a context that refuses writes saving a rebuilt
    index; such a context flushing at its close.
  - Not proven by a break: that the migrator's refusal without a hold is reached on Unix by a lock file of
    another user (the Windows test denies read access instead).
- **Gate**: section 6.

## 5. Independent review

To run on the committed tree (concurrency, persistence and the format are high-risk areas).

## 6. Verdicts

Provisional, until the review reports:

| Change | Verdict |
| :--- | :--- |
| ISS-062 statement undo | Mitigation (fix filed as ISS-090): created rows only, in-process only |
| ISS-083 counters across a rollback | Clean with named debt: the marks wait for the next flush, so a crash before it can still reissue a number |
| ISS-060 stored values | Clean with named debt: an attribute can't hold a null (ISS-094); the independent reader and a fixture pin the forms |
| ISS-084 lock file access, no recovery without a hold | Clean: an exclusive hold needs no write access; without a hold nothing recovers or converts |
| ISS-085 one database per folder | Clean: one owner decides the database; processes of 2.0.0 and 2.0.1 can still create a second file |
| ISS-086 lock helpers | Clean: each helper holds all or nothing |
| ISS-087 password service path | Clean with named debt: the service can't pass `DatabaseOptions` |
| ISS-088 parent unlink | Clean with named debt: two copies of the unlink remain (`Delete`'s scan path and `RemoveFromParentElements`) |
| ISS-091 failed undo | Mitigation within ISS-090's: the index is dropped and the context refuses writes; if the index file's own deletion fails, the next open has only its time-based staleness check (ISS-043) |

**Gate** (the tree as committed, 2026-11-12): `Xod.Tests` 277/277 on net8.0 and on net9.0; conformance 14/14
on each; the independent reader `read.mjs --check` (six fixtures, `stored-values` among them) and
`--check-fresh` ok; `XodBench.Tests` 26/26; the library builds with 0 warnings.

## 7. Remaining debt

- **ISS-089** (filed, Medium, predates): a failed update leaves the changes it made to existing rows in the
  cached page, and the next write to that table saves them.
- **ISS-090** (filed, Medium): a statement outside a transaction isn't all or nothing; the undo is a mitigation.
- **ISS-092** (filed, Low): what an update's triggers and the caller's item see depends on whether the row got a
  copy: triggers get the caller's item, while relationship write-backs land on the copy (round 2 of the
  `TASK-062` review).
- **ISS-093** (filed, Low, predates): a `.xod` database converted to Binary in place doesn't stay Binary: nothing
  reads the `Format` the migrator records, so later writes use the encoding the extension picks.
- **ISS-094** (filed, Low): a null in an attribute-positioned property whose new instance holds a value reads
  back as that value.
- The migrator compares folders ignoring case on Windows and macOS, so on a case-sensitive volume there it
  could take another folder for the source.
- Two copies of the unlink from parents (`Delete`'s scan path and `RemoveFromParentElements`).
