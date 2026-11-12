# PLAN-053: Statement undo, counters across a rollback, values that read back as written, and the lock fixes from the reviews (ISS-060, ISS-062, ISS-083 to ISS-088, ISS-091)

- **Task ID**: `TASK-063`; the fixes from its review, `TASK-065` (sections 5 and 6)
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
- **Older readers**: 2.0.0 and 2.0.1 skip an empty or marked element and leave what a new instance holds (their
  reader's code), as they read a value left out; an empty attribute they read as `""`, which is harmless, but
  their index rebuild leaves `""` out, so they don't find an indexed empty value through the index. Files of
  XOD 1.x stored `""` as an empty element, which every earlier version read as null and this one reads as `""`
  (corrected by the review, section 5).
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

Two fresh reviewers read the committed tree (`45f7a12..4a0ce19`) with the problem and no conclusions, one on
shared access, locks, recovery, the migrator and the counters, one on the statement undo and stored values. Both
ran probes (one also in a Linux container, as a second user and on a read-only mount). Every finding below was
reproduced on the tree before acting (their probes run against it: 12 of 14 failed as reported), and fixed in
`TASK-065` unless marked filed.

| # | Finding | Done |
| :--- | :--- | :--- |
| 1 | **Regression (ISS-083)**: a `Drop` inside a transaction reset the counter, and the rollback kept the reset, so the next insert got a number a restored row held (`ReservedPrimaryKeyException`, or a silent duplicate) | fixed: a rollback raises each counter to where it was when the transaction began (`AutonumberService.Highs`/`RaiseTo`), in the rollback's `finally` |
| 2 | **Regression (ISS-060)**: a relationship key a new instance holds as `""`, null on insert with its navigation set, was marked `nil` by the scalar pass; the write-backs set the text beside the mark or added a second element, and it read back null | fixed: every write-back of a key (`WritePropertyMap`, both parent paths) goes through `RewriteKeyProperty`, which writes the key again by the one writer, in place |
| 3 | The stored-value rule was bypassed: an update keeping a value read a `nil` or missing null as `""` (`KeepStoredValue`); `lazyLoad: true` kept 2.0's rule; a type without a parameterless constructor took `""` for what the reader makes as null | fixed: `KeepStoredValue` reads through `StoredScalar` (the reader's rule); `IsLeftOut` lost its `shallowWrite` case; `ReadBackOfMissing` makes the instance as the reader does (`ValueHelper.CreateInstance`) |
| 4 | Collections: a dictionary's null value reads `""`, a list drops a null item (predates) | filed `ISS-097`; the release note says scalars |
| 5 | The undo missed a parent entry written while the child's row was built (a refused grandchild, `ReservedChildException`), and a row whose own page write failed after it was in the cached page | fixed: a row is noted once its path is chosen, before anything is written for it; entries added to an existing parent's list are noted as they are added and taken out by the undo |
| 6 | ISS-088 covered one shape: a child with a parent property but no key property for it stayed listed after its delete and its undo, and `Delete`'s scan path kept its own unlink; a stale entry with a natural key hid a child inserted again with it | fixed: one unlink (`RemoveFromParentElements`) for every delete path, finding the parent from the row's `refType="parent"` element first; `RemoveChildEntry` is the one place an entry leaves a list; a stale entry with the child's key is pointed at the new row |
| 7 | A child with no parent property is still left listed by its own delete | filed `ISS-095` (reads skip the entry, pinned by a test) |
| 8 | Under `Buffered`, a context whose undo failed threw at its close (the IO service flushed its buffer on its own) and left its other resources undisposed | fixed: the IO service's close writes nothing (the engine's close decides), and every resource is let go even if one before it fails; the buffer is dropped, as at a crash (named) |
| 9 | **Regression on an NFS client (ISS-084)**: the exclusive open with read access takes no lock there (flock(2): LOCK_EX needs a file open for writing), and .NET ignores the failure | fixed: write access where the lock file allows it, read access otherwise; an exclusive hold counts only once a second open of the file is refused (`ProveExclusive`), unless the caller switched .NET's file locking off |
| 10 | A read-only medium without a lock file was refused as "locked" (EROFS is a plain `IOException`) | fixed: an `IOException` with no lock file there is a lock file that can't be created; proven in a Linux container on a read-only mount |
| 11 | Two opens naming different new database files in an empty folder both pass the check (inferred) | filed `ISS-096` |
| 12 | `Flush`, `ClearCache`, `Drop`, `GetStatistics`, a password change and a commit took the write lock outside the helper (a raw `LockRecursionException` inside a read); `GetStatistics` flushed, counters' marks included, under the read lock | fixed: all through `EnterWrite`; `GetStatistics` takes the write lock |
| 13 | The refusal's message named the wrong cases (it fires for any write lock, and queries don't enumerate lazily) | fixed |
| 14 | The obsolete password service is still silent for a folder that doesn't exist | named: deliberate (it sets the password a later database is created with, as before) |
| 15 | Claims: 2.0.x reading the new forms, 1.x files' `""`, the spec's database-file rule and string keys, stale comments | corrected here, in PLAN-046 sections 2, 7.2 and 9, the release notes and the code |

Confirmed sound by the reviewers: the lock helpers release what they took; `globalWriteDepth` and the statement
log are touched only under the engine-wide write lock; another user's read-only lock file holds on Linux; a case
variant of the path shares the lock on Windows; the fast writer agrees with the rule on every path it takes.

**Tests added** (each red against the tree, the reviewers' probes for the same cases red as above):
`XodAutonumberTests` 2 (a rolled-back drop; a committed drop still resets); `XodStoredValueTests` 4 (a key
written back from a relationship, every format; an update keeping values; a `lazyLoad` insert; a type made
without a constructor); `XodStatementUndoTests` 7 (a child failing after its parent listed it; a row whose page
write fails; a child without a parent key property, by index and by scan; a child inserted again with its key; a
stale entry; ISS-095 pinned; a buffered context whose undo failed); `XodLockSafetyTests` 3 (maintenance inside a
read; an unproven exclusive hold; read-only media, run in a Linux container).

## 6. Verdicts

After the review and `TASK-065`:

| Change | Verdict |
| :--- | :--- |
| ISS-062 statement undo | Mitigation (fix filed as ISS-090): rows and parent entries the statement made, in-process only; not changes to existing rows (ISS-089), not across a crash |
| ISS-083 counters across a rollback | Clean with named debt: the marks wait for the next flush, so a crash before it can still reissue a number |
| ISS-060 stored values | Clean with named debt: scalar properties, every writer through the one rule and every reader through the reader's; an attribute can't hold a null (ISS-094); collection entries aren't covered (ISS-097) |
| ISS-084 lock file access, no recovery without a hold | Clean with named debt: an exclusive hold is proven, not assumed; with .NET's file locking switched off the caller's word is taken |
| ISS-085 one database per folder | Clean with named debt: the creation race of two new files (ISS-096); processes of 2.0.0 and 2.0.1 can still create a second file |
| ISS-086 lock helpers | Clean: every write-lock taker goes through the helper |
| ISS-087 password service path | Clean with named debt: the service can't pass `DatabaseOptions`; a folder that doesn't exist is still silent (deliberate) |
| ISS-088 parent unlink | Clean with named debt: one unlink; a child with no parent property isn't found (ISS-095) |
| ISS-091 failed undo | Mitigation within ISS-090's: the index is dropped and the context refuses writes, its close writing nothing; under `Buffered` what it had buffered is dropped |

**Gate** (`TASK-063`, the tree as committed in `4a0ce19`, 2026-11-12): `Xod.Tests` 277/277 on net8.0 and on
net9.0; conformance 14/14 on each; the independent reader `read.mjs --check` (six fixtures) and `--check-fresh`
ok; `XodBench.Tests` 26/26; the library builds with 0 warnings.

**Gate** (`TASK-065`, the tree as committed after the review's fixes, 2026-11-12): `Xod.Tests` 301/301 on net8.0
and on net9.0 (`TASK-064`'s tests among them); conformance 14/14 on each; `read.mjs --check` (six fixtures) and
`--check-fresh` ok; `XodBench.Tests` 26/26; the library builds with 0 warnings. The four changed test classes,
147/147, also pass in a Linux container (.NET 9 SDK image, no network), the read-only-media test on a read-only
mount. **Guards proven by breaking the code**: 14 breaks, each turning at least one of the new tests red (a
rollback keeping a drop's reset; a key write-back not rewritten; a kept value read the old way; a `lazyLoad`
insert under the old rule; a constructorless type's missing value; a row noted after its write; a parent entry
not noted; the unlink ignoring the row's parent; the scan delete without the unlink; a stale entry kept; the IO
service's close flushing its buffer; `Flush` and `GetStatistics` taking the lock outside the helper; an
exclusive hold not proven).

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
- **ISS-095**, **ISS-096**, **ISS-097** (filed by the review, Low): see section 5.
- Under `DurabilityMode.Buffered`, a context whose undo failed drops what it had buffered (the cause is ISS-090).
- Shared holds on an NFS client: a shared lock needs read access only, so it holds; whether NFS clients are a
  supported place for a database at all is not decided.
