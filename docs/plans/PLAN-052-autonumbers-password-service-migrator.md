# PLAN-052: Autonumber settings and updates, one password writer, a migrator that is all or nothing (ISS-047, ISS-048, ISS-059, ISS-069, ISS-072, ISS-075, ISS-080 to ISS-082)

- **Task ID**: `TASK-062`
- **Status**: Fixed in the tree (2026-11-12), not released: no version change, nothing pushed.
- **Related**: [`PLAN-048`](PLAN-048-format-defect-fixes.md) section 7 (ISS-059 filed),
  [`PLAN-050`](PLAN-050-password-change.md) section 7 (ISS-069 and ISS-072 filed),
  [`PLAN-051`](PLAN-051-commit-failure-and-shared-access.md) (the lock file and recovery this builds on),
  [`PLAN-046`](PLAN-046-file-format-spec.md) sections 2, 6 and 9, [`KNOWN-ISSUES.md`](../KNOWN-ISSUES.md)

## 1. What was wrong, and why

| Issue | Defect | Cause |
| :--- | :--- | :--- |
| ISS-047 | `[Property(IdentitySeed = 1000)]` didn't compile; auto-numbering always counted up by one. | The properties were typed `dynamic`, which C# refuses as a named attribute argument (CS0655). Two copies of the settings were kept (`IntSeed`/`IntIncrement` for int and long, `IdentitySeed` for the rest), read where each number was handed out. |
| ISS-048 | `DatabaseOptions.EnableCompression` had no effect. | Nothing has ever read it. |
| ISS-059 | An update could change an autonumber value past the counter: lowering the highest value lost its record, and raising one above a counter already started wasn't seen, so a later insert could be given the same value. | Only inserts and deletes told the counter and the table's mark about values; an update rewrote the row without either. |
| ISS-075 | An update whose new item left an autonumber unset (0) stored 0: the row lost its number, and a key autonumber lost its key (found writing ISS-059's tests). | The row was written from the new item as given. |
| ISS-069 | The public `XodSecurityService` was a second way to change the files' password; through it, encrypted property values became unreadable and an open context kept writing under the old password. | It rewrote the files itself, skipping the engine's flush, its transaction refusal, the AES256 key's protection (PLAN-049) and the refusal while another context has the database open (PLAN-051). |
| ISS-072 | The migrator wrote its target file by file (a crash left a partial target), mixed its files into a target folder that held others, copied a source another context had open or a crash had left mid-commit, and overwrote the only copy file by file when converting in place. | It took no hold on the source, ran no recovery, and wrote with `File.WriteAllBytes` straight into the target. |
| ISS-080 | A type refused when it loaded (bad attribute settings), first used by a read, leaked the engine's read lock: the next transaction threw `LockRecursionException`, other threads' writes blocked, `Dispose` threw (found by the review, section 5; predates this plan). | `EnterTableReadLock` took the lock and then loaded the type; the caller's `try` began only after it returned. |
| ISS-081 | An update with an `UpdateFilter` dropped values stored as attributes (`ValuePosition.Attribute`) that the filter left out: an attribute-positioned autonumber key became 0 (found by the review; predates). | The filter's merge copied the old row's elements, never its attributes, in two copies (the fast and the tree update paths). |
| ISS-082 | A filtered update indexed, and checked unique keys against, the new item's values for properties the filter left out, though the stored row kept the old ones: the row's unique value dropped out of the index (no longer found or enforced), and an update could be refused over a value it wouldn't store (found while fixing ISS-081; predates). | The checks and the index were fed the new item, while the row stored was a merge of the new item and the old row. |

## 2. The design

### Autonumber settings (ISS-047, ISS-048)

- `PropertyAttribute.IdentitySeed` and `IdentityIncrement` are typed `object`, which C# accepts in an attribute:
  `[Property(AutoNumber = true, IdentitySeed = 1000, IdentityIncrement = 5)]`, or a `long` constant beyond int.
- One reader, `PropertyService.ReadIdentitySettings`, runs once every autonumber of the type is known (the `Id`
  convention included) and stores `PropertyInfoItem.Seed` and `Increment` (decimal, internal). The settings must
  be whole numbers, the seed 1 or more (0 is the value that means "unset") and within the whole numbers the
  property's type holds exactly, the increment 1 or more; a Guid autonumber takes neither. Otherwise the type
  is refused when it loads, with `AutonumberSeedException` (new, public), not when a number is handed out.
- A float or double holds whole numbers exactly only up to 2^24 or 2^53 (`AutonumberService.WholeRange`): a
  number past that isn't handed out (`OverflowException`, as an int's checked cast refuses one past its
  range), rather than stored as a number already handed out.
- `EnableCompression` is `[Obsolete]`, pointing at `StorageFormat.Binary`. Removing it is a breaking change, so
  it waits for a major version (ISS-048 stays open as mitigated).

### Updates and autonumbers (ISS-059, ISS-075, ISS-081, ISS-082)

- **The item an update writes to a row** (`XmlEngine.ItemForRow`, both update paths): the new item, or a shallow
  copy of it carrying the row's own values where the update leaves them as they are: an autonumber the new item
  leaves unset (the row keeps its number), and a key, unique, indexed or autonumber value the filter leaves out
  (the row keeps it). The unique and key checks, the index and the counter all see that item, so they see the
  row as it will be stored. The caller's new item is never changed: the first fix of ISS-075 wrote the row's
  value into it, and a new item reused across updates, or an update by example over several rows, then gave
  duplicates or `ReservedPrimaryKeyException` (review finding A).
- **The counter and the mark** (`RecordAutonumberChanges`, after the checks): a value the update changes is
  seen by the counter, and the old one is recorded in the table's mark, as a delete records it, so neither is
  handed out again, in this context or after reopening.
- **The filter's merge** (`KeepLeftOutValues`): one copy for both update paths, keeping the old row's elements
  and attributes that the filter leaves out.
- **"Unset"** is one rule, `AutonumberService.IsUnset` (the type's default); the insert's typed paths compare
  with 0 and `Guid.Empty`, the same rule.

### The read lock (ISS-080)

`EnterTableReadLock` loads the type (`IsStandaloneTable`) before it takes the lock, as the write path already
did, so a refusal leaves no lock held. It was the only lock site with work between the lock and its `try`.

### One password writer (ISS-069)

- The switch itself (staged files, the commit record, renames, recovery) moved to an internal class,
  `Services.PasswordSwitch`, that only the engine uses (`XmlEngine.SwitchPassword`).
- The public `XodSecurityService` and `IXodSecurityService` are `[Obsolete]`. Their `Secure`, `Loose` and
  `ChangePassword` open a `XodContext` on `Path` and call it: the one way the password changes, with everything
  it does. For a database file that isn't there, only the object's `Password` changes, as before. Removing the
  types waits for a major version.

### The migrator (ISS-072)

- **The source is held alone**: every database file's lock file in the folder is opened exclusively
  (`IOService.OpenLockFile`, now the one place a lock file is opened, by a context or the migrator), so a
  conversion is refused with `DatabaseLockedException` while a context has the database open, and no context
  opens it meanwhile. A folder that can't be written is read without a hold, as a context reads it.
- **Recovered first**: `IOService.RecoverInterruptedWrites`, now static and shared with the context's open,
  finishes or undoes what a crash left (a commit, a password change), as an open would.
- **Into another folder**, which must be new or empty: the files are written to a folder beside it,
  `.<target>.converting-<id>`, on the same volume, and moved into place whole. A failure deletes that folder;
  a crash leaves it (it can be deleted) and no target.
- **In place** (the target is the source folder, however written): each file is replaced whole through
  `IOService.WriteRawBytesAtomic` (now also the page flush's writer), so a crash leaves a database that opens,
  each file in the old format or the new (a file's format is read from the file).
- Refused: a target folder that holds anything, and a folder with no database file.

## 3. Tests

- `XodAutonumberTests` (69 with the earlier ones): a seed and increment used, in every encoding and without an
  index; a long seed; settings refused when the type loads (increment 0, a fraction, text, out of range, a seed
  of 0 or less, a double seed past 2^53, a seed on a Guid); a float that stops at 2^24 instead of repeating a
  number; an update that raises, or lowers, an autonumber or an autonumber key; an unset key kept; one new item
  used for several updates (a key and a non-key autonumber), with the new item unchanged; an update by example
  over two rows; a filter that leaves an autonumber out; attribute values kept under both filter behaviours; a
  unique value a filter leaves out stays indexed, enforced, and isn't checked from the new item; a type refused
  on its first read leaves the context usable.
- `XodSecurityServiceTests` (5): a change and a removal through the public service keep encrypted values
  readable (a removal with nothing else to protect the key is refused, as the context refuses it); a change
  refused while a context has the database open; a wrong current password refused; a service for a database
  not created yet only records the password.
- `XodMigratorSafetyTests` (9): a conversion cut short (the crash writer killed after the first file) leaves no
  target, and runs again; a target that isn't empty refused; an empty target used; a source a context has open
  refused, into another folder and in place; an open during a conversion refused (the writer paused); a source
  a crash left mid-commit recovered before it is converted; an in-place conversion cut short after one and
  after three files opens with every row; the source folder named with a trailing separator converted in
  place; a folder without a database file refused.
- `XodApiSurfaceTests`: the snapshot records `[obsolete]` and a type written `dynamic` (which compiles to
  `object`, so a change between them showed no difference).

## 4. How it was checked

- **Red first**: the seed tests didn't compile on the code before (CS0655); every other new test failed on the
  code before for the reason it names (the migrator's mid-commit test: 50 customers copied where the open
  gives 25; the refused read: `LockRecursionException`).
- **Guards proven by breaking the code** (each break turned at least one test red, then the code was restored;
  the crash writer rebuilt after each migrator break): the seed ignored; an increment of 0 accepted; a seed out
  of range accepted; a fraction accepted; an update not seen by the counter; the old value not recorded in the
  mark; an unset value not kept; each update path skipping the autonumbers; the kept value written into the
  new item; the filter ignored for the counter; attributes not kept (each behaviour); left-out keyed values not
  kept; the checks fed the new item; a seed of 0 accepted; the double limit not applied; a number past the
  limit handed out; a Guid seed accepted; the type loaded after the read lock; `[obsolete]` and `dynamic` not
  in the snapshot; the public service bypassing the context; the service creating a missing database; the
  engine skipping the file switch; the migrator writing the target in place; a non-empty target accepted; the
  source not held; the source not recovered; the same folder not recognised; a folder without a database
  accepted; an empty target not replaced.
- **Not proven by a test**: that an in-place conversion replaces each file atomically (the crash points the
  tests use fall between files; a torn file needs a crash inside a write, which no step reaches). It rests on
  `WriteRawBytesAtomic`, the page flush's writer.
- **Gate**: section 6.

## 5. Independent review

**Round 1** (the autonumber work, before ISS-069 and ISS-072): the reviewer reproduced the baseline (211/211)
and found:

- **A (does not fully fix ISS-075)**: the first fix wrote the row's value into the caller's new item; a reused
  new item or an update by example then gave duplicates or `ReservedPrimaryKeyException`. Fixed: `ItemForRow`.
- **B**: a seed of 0 or less handed out 0, the unset value; a float or double seed past exact range handed out
  one number twice. Fixed: the seed rules and `WholeRange`.
- **C (ISS-080, predates)**: the read lock leaked when a type was refused on its first read. Fixed.
- **ISS-081 (predates)**: an update filter dropped attribute values, an autonumber key among them. Fixed; while
  fixing it, ISS-082 was found and fixed.
- Named debt it raised, resolved here: "unset" decided in two places (one rule now); the filter ignored by the
  counter (fixed); the header not naming the update's batch crash window or the rollback reissue (named, and
  the rollback filed as ISS-083); the wrong claim that the settings could never be set (a subclass of the
  attribute could; corrected); a Guid seed silently ignored (refused now); the snapshot blind to `dynamic`
  (fixed).
- Its compatibility checks: binary compatible (a consumer built against the old dll ran against the new one);
  a source break for code that reads `IdentitySeed` as an int (CS0266); `[Obsolete]` gives CS0618, which fails
  a consumer built with warnings as errors. These go in the release notes; with a new public type, a new
  capability and a source break, the reviewer's view is that the release is a minor version, not a patch (the
  owner's decision).

**Round 2** (ISS-069, ISS-072 and the round-1 fixes): pending.

## 6. Verdicts

Provisional, until round 2 of the review reports:

| Change | Verdict |
| :--- | :--- |
| ISS-047 seed and increment | Clean: one reader, when the type loads |
| ISS-048 `EnableCompression` | Mitigation: `[Obsolete]`; removal waits for a major version (ISS-048 open) |
| ISS-059 counter and mark on update | Clean with named debt: the update's mark write has a delete's batch crash window |
| ISS-075 unset autonumber kept | Clean: the row's item is a copy; the caller's new item is never changed |
| ISS-080 read lock | Clean: the type loads before the lock |
| ISS-081 filter merge | Clean: one merge, elements and attributes |
| ISS-082 checks and index under a filter | Clean: they see the row as stored |
| ISS-069 public password service | Clean with named debt: the obsolete types stay until a major version |
| ISS-072 migrator | Clean with named debt: a crash leaves the staging folder; the source folder is recognised by its path; nothing is flushed to the device (PLAN-046 section 9) |

**Gate** (the tree as committed, 2026-11-12): `Xod.Tests` 239/239 on net8.0 and on net9.0; conformance 14/14
on each; the independent reader `read.mjs --check` and `--check-fresh` ok; `XodBench.Tests` 26/26; the
library builds with 0 warnings.

## 7. Remaining debt

- **ISS-083** (filed, Low): a rollback resets the counters, so a number handed out inside a rolled-back
  transaction is handed out again.
- ISS-048 stays open as mitigated: `EnableCompression` goes in a major version.
- `XodSecurityService` and `IXodSecurityService` go in a major version.
- A conversion cut short by a crash leaves its staging folder beside the target; nothing deletes it.
- The migrator recognises the source folder by its path: reached through a link, it is refused as a target
  that isn't empty rather than converted in place.
