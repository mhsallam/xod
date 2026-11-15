# PLAN-061: the High issues from the overall review

- **Task ID**: `TASK-075`
- **Status**: In progress. `ISS-147` done as a mitigation with named debt (section 2); `ISS-138` and `ISS-139`
  resolved (section 3); `ISS-148`, `ISS-149` and `ISS-150` resolved (section 4); `ISS-129` and `ISS-151` next.
- **Related**: [`REVIEW-2026-11-14`](../REVIEW-2026-11-14-overall.md) (`TASK-073`, which filed them),
  [`PLAN-051`](PLAN-051-commit-failure-and-shared-access.md) section 7 (the journal's recorded debt)

## 1. What the owner asked, and the order

The owner (2026-11-15): fix the High issues the overall review filed, starting with `ISS-147`. The order, the
riskiest to data first:

1. `ISS-147`: a commit a crash cut short later undoes other contexts' acknowledged writes.
2. `ISS-138`, `ISS-139`: a referenced object served stale from the item cache; matching by example compares
   text another way than the index.
3. `ISS-148`, `ISS-149`, `ISS-150`: the password switch and what a crash leaves; the first data key.
4. `ISS-129`, `ISS-151`: a full page written in the background; XOD 1.x encrypted databases.

Each fix lands as its own commit, with its tests seen red first, each guard broken once, and an independent
review where the area is high-risk (all of these are: persistence, concurrency or crypto).

## 2. `ISS-147`: writes beside another context's commit journal

### The cause

A commit writes its journal (`transaction.xwal`, the before-images of the files it changes) before it writes
any file, and deletes it once it has. A journal left in the folder means its commit didn't finish, and the next
open that has the database to itself writes the before-images back (`RecoverWalJournalIfPresent`). Only an open
alone recovers, so while other contexts stay open the journal stays, and nothing stopped them writing: what they
wrote to the files the journal names was put back with it, after they had been told it was made. **The other
contexts couldn't tell a commit under way from one that won't finish, so they did neither of the right things
(wait, or refuse).**

### The change

- **The committing context holds its journal** from the moment it is staged until after it is deleted
  (`TransactionService.WriteWalJournal`: `FileAccess.Write`, `FileShare.Read | FileShare.Delete`; on Unix .NET
  takes `flock(LOCK_SH)`, which follows the file through the rename). A commit that failed and couldn't be undone
  lets go of it (`Commit`'s `finally`), so it reads as one that won't finish.
- **`XodTransaction.JournalIn`** tells the three states apart: none; a commit under way (a probe that shares
  nothing is refused for the sharing, `HeldByAnother`); one that won't finish (the probe opens, or the hold
  can't be seen).
- **`XmlEngine.NotBesideAnotherJournal`**, called from `ThrowIfWritesRefused` when a write operation starts and
  before each file write that passes the engine's hook (`BeforeFileMutate`): waits for a commit under way, up to
  `TransactionTimeoutMs`, and refuses beside one that won't finish. A context's own commit is exempt
  (`HoldsJournal`).
- **Index files are exempt** (found by the review: reads rebuild and save an index, and threw beside a crashed
  journal). A journal never names an index file (`CaptureBeforeMutate`), and its undo leaves every index
  untrusted and drops them all (`LeaveIndexesBehind`, then `DropIndexesLeftBehind`), so an index saved beside a
  journal undoes nothing acknowledged.
- **`IOService.FileLockingSwitchedOff` is true only off Windows**: only the Unix file handle reads
  `System.IO.DisableFileLocking` (`SafeFileHandle.Unix.cs`, `CanLockTheFile`, dotnet/runtime `release/9.0`); the
  Windows one passes `FileShare` to `CreateFile` whatever it says (`SafeFileHandle.Windows.cs`). So on Windows a
  held journal is still seen, and `ProveExclusive` still proves a hold, with the switch set.

### Tests, seen red first

`XodJournalBesideTests` (new) and one assertion in `XodCommitFailureTests`:

- a write beside a commit a crash cut short is refused, and the recovery leaves the database as before that
  commit, taking writes again (red before: the write went in and the recovery undid it);
- reads go on beside it (red against the first version of the fix: the index save threw);
- a write beside a commit under way waits for all of `TransactionTimeoutMs` and is refused as under way, and a
  write once the commit has ended goes in (red before: the write went in beside it);
- a commit that failed and wasn't undone lets go of its journal, and the other contexts refuse beside it (red
  before);
- a buffered context closed beside a crashed journal has its unflushed writes refused and the close throws
  (pins the debt below; it passed at once).

Each guard broken once, with the journal, commit-failure and transaction tests run against it; every break
turned at least one red, and the file was restored after each:

| Break | Tests red |
| :--- | :--- |
| B1 no look for another's journal | 5 |
| B2 a faulted commit keeps holding its journal | 1 |
| B3 the journal not held while its commit runs | 1 |
| B4 a context waits for its own commit's journal | 6 |
| B5 a held journal read as one that won't finish | 1 |
| B6 index saves looked at too | 1 |
| B7 a commit under way refused without waiting | 1 |

### How it was checked

- **Gate** (the final tree, 2026-11-15, Windows): engine and crash writer build with 0 warnings; `Xod.Tests`
  467/467 on net8.0 and 475/475 on net9.0; conformance 24/24 on each; the independent reader `--check` and
  `--check-fresh` ok; `XodBench.Tests` 26/26; no machine details in the tracked files.
- **Linux** (2026-11-15, `mcr.microsoft.com/dotnet/sdk:9.0`, a non-root user, the container's own file system,
  no network): the journal and commit-failure tests 15/15. The waiting test there shows the Unix side of the
  hold: `flock(LOCK_SH)` follows the journal through its rename and the probe's `LOCK_EX` is refused, or the
  write would have been refused as one that won't finish instead of waiting. NFS and SMB are not run (read from
  the runtime's source: refused, not waited for).

### The independent review, and what came of it

| Finding | Verdict | Disposition |
| :--- | :--- | :--- |
| 1. Reads that rebuild an index save it through the hook, and threw beside a journal | Confirmed (the read test, red) | Fixed: index files exempt |
| 2. A probe could open between the delete and the release of the hold | Rejected | The probe shares nothing, so it can't open while the hold (write access) is open; the file is deleted before the hold goes |
| 3. A look is not a hold: a commit can write its journal between the look and the write it guards; wider for writes on other threads | Confirmed | Named debt; the structural fix filed as `ISS-164`; the comments no longer say "nothing is written beside" |
| 4a. On a Unix network or unrecognised file system .NET takes no `LOCK_SH` for a write-opened file, so a live commit reads as one that won't finish | Confirmed (`CanLockTheFile`: nfs, smb, smb2, cifs, or a type it can't read) | Fails safe (refused, not let in); the comment and the message say so |
| 4b. The switched-off early return applied on Windows, where the switch changes nothing | Confirmed | Fixed at the owner of the reading (`FileLockingSwitchedOff`) |
| 5. A buffered context's acknowledged writes can't be flushed beside a crashed journal; the close throws | Confirmed (the buffered test) | Named debt, filed as `ISS-165` |
| 6. Second copies: the commit's own journal check refuses at once while other writes wait; four lists of what a crash leaves; `.xdel` set-asides a sibling hazard | Confirmed / unconfirmed (`.xdel`) | Named; the lists are `ISS-149`'s fix; `.xdel` filed as `ISS-166`, unconfirmed |
| 7. `JournalIn` misreads some probe failures, so a message can name the wrong cause | Confirmed | The comment says so; what is refused is right either way |
| 8. The wait holds this context's own locks | Confirmed | Named in the comment; bounded by `TransactionTimeoutMs` |

### The verdict

**Mitigation, with named debt (the fix filed as `ISS-164`).** The defect the review reproduced (a crash's journal
undoing writes another context made after it) can't happen from a write that starts after the journal is in
place: it is refused or waits. It can still happen inside the window between the look and the write, and a
write made before the other commit's journal is overwritten by that commit. Those need writes from two contexts
kept apart, which `ISS-164` records. Also left: `ISS-165`, `ISS-166`, and the behaviour change below.

### What changes for users

A write beside another context's journal fails with `XodException` when the write starts (or before a file
write), where before it went in, or, for a commit beside it, failed with `TransactionException` at `Commit`. A
write beside a commit under way now waits for it, up to `TransactionTimeoutMs`.

## 3. `ISS-138` and `ISS-139`: objects read from an old cache, and rows matched by text

### The causes

- **`ISS-138`.** The objects a read builds for the rows it references were kept in a cache as long as the
  context, keyed by type and row code. Nothing owned its freshness: each write had to clear what it changed, and
  the clears skipped standalone types (the updates and deletes of a customer); another context's writes never
  reached it; and every reader got the same instance. **The cache outlived the read, so its entries had to be
  invalidated, and the invalidation was a duty of every writer.** A row code is also unique only within its page
  (codes come from a counter that starts anywhere in each process), so two rows of two pages could share an entry.
- **`ISS-139`.** Five places matched a stored row against an example by writing the example as XML and comparing
  the text, case-insensitively in the current culture (`XmlExtensions.ElementMatch`), while the index compares
  exact keys (`IndexKeys`). **Two owners decided whether two values are equal**, so a call gave other rows with
  an index than without, and two rules decided which properties of an example take part.

### The change

- **A read's objects live as long as the read** (`ItemsCacheService`). One cache per outermost read on a thread,
  begun and ended by the engine's read entries (`EnterTableReadLock`/`ExitTableReadLock`, the SQL query); nested
  reads share it; any write ends it for the rest of the read it is made in (`WriteStarted`, from
  `EnterExclusive` and `EnterTableWriteLock`); outside a read nothing is kept. So no write has anything to
  invalidate, and the row-by-row clears are gone. Keyed by the stored reference (`page.row`). Not disposed with
  the context: a close can come from inside a read, which still ends after it; the `ThreadLocal`'s finalizer
  frees its slots (the runtime's `ThreadLocal.cs`, release/9.0, and the `ThreadLocal<T>.Dispose` note).
- **One test of a row against an example** (`XmlEngine.Examples`): a scalar matches when the value a read gives
  back from the row and the example's have one index key, by the index's own functions
  (`IndexService.GetNormalizedPropertyValue`, `NormalizeIndexValue`); any other property by the form a shallow
  write gives it, compared exactly; one rule for which properties take part (`SetsProperty`, the default of text
  being the empty string as before). An example that sets nothing matches nothing, as it did (a Delete by an
  example whose key was left unset takes no row). Every by-example path uses it.
- **One reader of a stored scalar** (`StoredValue.ReadBack`) for the read, the index and the match: the index
  parsed an empty text where a read leaves it out (found by the review).
- `XmlExtensions.ElementMatch` (public) keeps its behaviour and is marked obsolete; its one implementation takes
  the comparison.

### Tests, seen red first

`XodItemCacheTests` and `XodExampleMatchTests` (new), run against the unchanged tree (red) and this one:

- the same context's update and delete of a referenced row seen through the reference; each read hands out its
  own objects; a write inside a read seen by the rest of it; two rows of two pages with one row code are two
  objects (red: the second got the first's object); a SQL query shares nothing with the reads after it;
- by example: text exact, numbers by value, primary and unique keys as the index compares them, Update and
  Delete by example, an encrypted property by its value, indexed and unindexed alike (red);
- guards that passed before and must stay: the rows of one read share their referenced object; an example that
  sets nothing matches nothing; a reference in an example doesn't take part.
- Skipped: another context's update seen by a context that read the row before (`ISS-167`, the page cache).

Each guard broken once (nine breaks, each turning at least one test red; the files restored after each): the
cache living as long as the context; a write leaving the read sharing; keyed by row code; a SQL query never
ending its read; text keys ignoring case; scalars matched by text; an example that sets nothing matching every
row; an empty string taking part; a reference still taking part.

### The independent review

A read-only reviewer with probes, on the old and new code: `ISS-138` clean with named debt; `ISS-139` had a
regression, fixed. Its findings:

| Finding | Disposition |
| :--- | :--- |
| An example that sets only a reference matched every row (the shallow write leaves references out, and the match skipped them) | Fixed: what the written form leaves out doesn't take part, as before; tested |
| The comment claimed references and collections match by their form | Corrected; the collection behaviour (predates) filed as `ISS-168` |
| A second copy of "the value a read gives back" (the index's) | Fixed: `StoredValue.ReadBack`, and the index's key functions in the match |
| A standalone write on another thread lands inside a read (predates; now one read's window) | Filed as `ISS-169` |
| Behaviour changes without a note (exact text, AES256 matching) | In `RELEASE-NOTES-NEXT.md` |
| An empty `try {}` left in `DisposeResources` | Removed |
| Polymorphic references are never shared within a read (identity, not freshness); a keyless Update/Delete by example takes one page's matches (predates) | Named; the second in `ISS-168` |

### The verdict

- **`ISS-138`: clean, with named debt** (`ISS-167`, `ISS-169`). Nothing outlives a read, so no write can leave
  it stale.
- **`ISS-139`: clean, with named debt** (`ISS-168`). One owner of equality for every match.

Measured on one machine (a scratch benchmark, 1,000 customers and 10,000 orders): reading every order with its
customer and 5,000 reads by key took no longer than before; the reviewer's unindexed scans by example were no
slower, except over an encrypted property, which decrypts each row (it never matched before).

### How it was checked

- **Linux** (2026-11-15, `mcr.microsoft.com/dotnet/sdk:9.0`, a non-root user, the container's own file system,
  no network): the item-cache, example, trusted-index and SQL tests, 90 passed and the `ISS-167` test skipped.
- **Gate** (the final tree, 2026-11-15, Windows): engine and crash writer build with 0 warnings; `Xod.Tests`
  490 on net8.0 and 498 on net9.0, the `ISS-167` test skipped on each; conformance 24/24 on each; the
  independent reader `--check` and `--check-fresh` ok; `XodBench.Tests` 26/26; no machine details in the tracked
  files. The API snapshot changed by one line: `ElementMatch` marked obsolete.

## 4. `ISS-148`, `ISS-149` and `ISS-150`: the password switch, what a crash leaves, the first data key

### The causes

- **`ISS-148`.** A switch committed that couldn't replace a file (open elsewhere) was pending in its context's
  memory only (`PasswordSwitch.switchPending`), and an open beside other contexts recovers nothing, so it ignored
  the commit record on disk. **The state that decides whether an open is safe lived in one context's memory, and
  the other opens didn't read the record that holds it.** The review of this fix found a second way in: a context
  that gives its shared hold up to hold the database alone and then takes it back holds nothing in between, and
  taking the hold back checked nothing, so another context could change the password in that instant (reproduced
  with the crash writer paused at `retaking-hold`). The first data key made the gap common: every first
  `[Crypto(AES256)]` value of a database with no key now holds the database alone for a moment.
- **`ISS-149`.** The switch refused only over `*.xwal` and `*.tmp`, while the open recovers a set-aside file
  (`.xdel`) too. **Two lists of what a crash leaves**, so the switch converted the database and the open then put
  back a plaintext page.
- **`ISS-150`.** The key was made at the first value by reading the database file, deriving and saving it, beside
  any other context doing the same. **A read-modify-write of a shared record with no one owning the moment**: the
  last save won. The review found a second: a key made inside a transaction was in its image of the database
  file, so a rollback (or a commit undone after a crash) took the key back after another context had written values
  under it (reproduced). And `SaveNow`, the key's write, passed no mutation hook and left the file waiting to be
  written again by the next flush, outside the hold the key was made under.

### The change

- **The commit record decides** (`PasswordSwitch.RefuseBesideUnfinishedSwitch`, one reader of the record,
  `CommittedFiles`): an open beside other contexts refuses a switch committed and not finished; an open alone
  finishes it, as before. The record is read sharing its deletion, so the context finishing the switch isn't
  refused by a reader. Finishing is serialised within a context.
- **The gate** (`xod.gate.lock`, `IOService.GateName`): every hold alone is taken through it, the gate and both lock
  files without waiting for any of them while holding another (a retry loop around the whole), and a context that
  gives its shared hold up and takes it back (a password change, a key made, a new database) holds the gate from
  before it lets go until it holds again. So no other context can hold the database alone in between. PLAN-046
  section 9 records the file. XOD 2.0.2 and earlier don't take it.
- **One list of what a crash leaves** (`IOService.WriteLeft`): the switch refuses over anything on it, checked before
  the key's protections are written (`PasswordSwitch.CheckCanSwitch`); the recovery picks atomic writes and
  set-asides by the same predicates (and so leaves another program's `.tmp` alone, which it used to rename).
- **The key is made only with the database held alone**: at creation when the context has a secret, after a
  password change when there is none (`PasswordChanged`), or at the first value through `WithDatabaseToItself`,
  refused while another context has the database open. Read afresh under the hold. One maker
  (`NewDataKeyUnlocked`) refuses unless the database is held alone (`HasDatabaseToItself`, which no longer counts
  "no hold" as alone). A transaction keeps no image of the database file (`TransactionService.CaptureBeforeMutate`:
  nothing but the key writes it once it is created). `SaveNow` passes the mutation hook (a pending switch is
  finished first) and leaves nothing waiting.

### Tests, seen red first

In `XodPasswordChangeTests` and `XodPropertyCryptoTests`, run against the unchanged tree (red) and this one:

- another context doesn't open, with either password, beside a change that didn't finish (red: it opened with the
  old password and created a table);
- no change runs while another context takes its hold back (red: the other's change ran in the gap, and the first
  then wrote under the old password);
- a password change over a file a transaction set aside is refused (red: `Secure` succeeded, and the page came back
  in plaintext);
- a new database records its key when created; the first value of a database without a key needs it to itself; a
  key made inside a transaction outlives its rollback (red each); a key whose save failed is not used (Windows only:
  red against the reviewed version, by its break below);
- guards for the new behaviour, which the old code passed as well (its two writes came one after the other): two
  contexts of a new database write their first values with one key; securing a database makes its key.

Each guard broken once (twelve breaks; the files restored after each). Ten turn at least one test red: a shared open
ignoring an unfinished switch; a committed record read as not committed; the switch's old list; no key at creation;
none after a password change; the first value making its key beside others; the maker not checking its hold; a swap
without the gate; a transaction keeping an image of the database file; a key added to the cached database file
before its write. Two turn none, and stay as defence: the engine checking for what a crash left only after the
key's protections are written (red before the key service wrote a copy; now `ISS-147`'s journal check refuses that
write, and otherwise it leaves a protection for a password never applied, as a switch failing later does); `SaveNow`
leaving the file waiting (nothing else queues the database file now that the key service writes a copy).

### The independent review

A read-only reviewer with probes, on the old code and the first version of this change:

| Finding | Disposition |
| :--- | :--- |
| Taking the shared hold back after holding the database alone checked nothing: another context changed the password in between (reproduced) | Fixed: the gate; tested |
| A key made inside a transaction was taken back by its rollback after another context used it (reproduced) | Fixed: no transaction image of the database file; `SaveNow` leaves nothing waiting; tested |
| Two `ISS-150` tests and the Secure test pass on the old code | Kept as guards, labelled; their breaks turn them red |
| "The one list" and "can't differ" claimed more than two hand-kept lists | Fixed: shared predicates; the comments say what is shared |
| `HasDatabaseToItself` counted "no hold" as alone | Fixed |
| Two threads finishing a pending switch at once; a reader of the record refusing its deletion on Windows | Fixed |
| The first value of a keyless database refused beside others; the key protected by the creator's secret | Behaviour changes, in `RELEASE-NOTES-NEXT.md` |

The same reviewer then reviewed the fixes, with probes on the new code: the gap and the rollback closed (each
reproduced as fixed). Its findings:

| Finding | Disposition |
| :--- | :--- |
| A key whose save failed (the database file held open) stayed in the cached database file, and the next value used it unsaved: lost after a crash (reproduced; the mechanism predates) | Fixed: the key service changes a copy, which `SaveNow` caches only once written; a key held in memory is forgotten when its save fails; tested (Windows: a file held open fails its replacement only there) |
| A commit record this context can't read was read as gone, so an open went on beside it and a recovery deleted it with its staged files | Fixed: only a missing record is gone; an unreadable one refuses |
| `HasDatabaseToItself` asked the file system whether the folder exists, which reads "no" on any error | Fixed: fixed at the open |
| A swap's take of the gate collides with another context's instant on it, and is refused | Filed as `ISS-170` (refuses, never corrupts) |
| PLAN-046 said nothing is waited for while the gate is held; the shared hold taken back is | Corrected |
| Skipping the database file in transactions loses nothing; `SaveNow` through the hook is right for creation, rollback and `ISS-147` | No change |

### The verdict

- **`ISS-148`: clean, with named debt**: contexts of XOD 2.0.2 and earlier take no gate; a swap can be refused for
  an instant's collision on the gate (`ISS-170`).
- **`ISS-149`: clean.** The switch refuses over what the open recovers, by the same predicates.
- **`ISS-150`: clean, with named debt**: `HasDatabaseToItself` reads fields that aren't synchronised; every caller is
  serialised by the engine's locks today. The first value of a database with no key is refused beside other
  contexts (a behaviour change, by design).

### How it was checked

- **Gate** (the final tree, 2026-11-15, Windows): engine and crash writer build with 0 warnings; `Xod.Tests` 500 on
  net8.0 and 508 on net9.0, the `ISS-167` test skipped on each; conformance 24/24 on each; the independent reader
  `--check` and `--check-fresh` ok; `XodBench.Tests` 26/26; no machine details in the tracked files. The test run
  takes no longer with a key made at every new database with a secret.
- **Linux** (2026-11-15, `mcr.microsoft.com/dotnet/sdk:9.0`, a non-root user, the container's own file system, no
  network; the commit `4c2931f`): the password change, property encryption, shared access, crash safety, transaction,
  lock, migrator and journal tests, 136 passed. The read-only-media test ran first with its folders passed as empty
  strings, which point both at one writable folder, and failed for that; run again with a folder mounted read-only
  (`XOD_RO_RW`, `XOD_RO_RO`), the lock tests passed 6/6, the gate left uncreated where the folder can't be written.
