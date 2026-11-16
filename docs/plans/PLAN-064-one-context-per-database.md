# PLAN-064: a database is open in one context at a time

- **Task ID**: `TASK-079`
- **Status**: Done (2026-11-16). Step 1: every context holds its database alone. Step 2: what only served contexts
  side by side is taken out (section 5), before the release, so no release ships it.
- **Related**: the README's "Where it fits" and the site's `docs/transactions` section "Processes" (the rule this
  enforces); [`PLAN-061`](PLAN-061-high-issues-from-the-overall-review.md), [`PLAN-062`](PLAN-062-commit-gate.md) and
  [`PLAN-063`](PLAN-063-deletes-at-the-commit.md) (the machinery for contexts side by side that step 2 takes out)

## 1. The cause

XOD's scope has always been one owner per database: the README says "One process owns the folder at a time ... If
several services must write the same data, use a server database", and the site says to keep one context per database
for the life of the application. The code didn't hold to it: by default (`LockDatabase = false`) any number of
contexts, in one process or several, opened one database with a shared hold and wrote it, each with its own page cache,
index logs, password and AES256 key, coordinated by nothing but the files.

Every defect of contexts side by side came from that: a password change under another context (`ISS-071`, `ISS-148`,
`ISS-155`, `ISS-170`), a crashed commit undoing another context's writes (`ISS-147`, `ISS-165`), commits and writes
interleaved (`ISS-164`), a dropped table re-made by another context (`ISS-166`), caches and index logs that never see
another context's writes (`ISS-141`, `ISS-167`), index files and pages another context holds or replaces (`ISS-115`,
`ISS-119`, `ISS-126`, `ISS-172`). `PLAN-061` to `PLAN-063` fixed them one at a time, each with a new lock file, wait or
refusal, so each reached users as a behaviour change, and more of the same were open.

So: **the defects of contexts side by side happen because the open allows what the design doesn't support. The change
makes them impossible by holding every database alone, from its open to its disposal: no second context can open it.**

## 2. The change (step 1)

- **Every open holds the database alone** (`IOService.OpenAndRecover`): the folder's lock file and the database file's,
  exclusively, through the gate, as `LockDatabase` did. Any other open, in this process or another, waits
  `LockRetryTimeoutMs` and is refused with `DatabaseLockedException`. XOD 2.0.2 holds the lock files shared, so it and
  this version keep each other out too.
- **`DatabaseOptions.LockDatabase` has no effect**, documented, not obsolete: code that sets it compiles without a
  warning.
- **A folder where no lock file can be created or opened** (a folder this user can't write, read-only media, the file's
  permissions) still opens for reading only, with no hold (`NoHoldReason`), as the shared open did; an open there that
  finds a write a crash left is refused, as before.
- **The folder check and the recovery run under the hold**, as `LockDatabase` ran them; the open's shared branch
  (recovery only when an exclusive try succeeded, the creation's hold alone handed back for a shared one) is no longer
  reached. `IOService`'s `lockDatabase` and `creating` parameters are gone.
- **The CLI** (`xod stats`, `xod query`, ...) opens as a context does, so it is refused while an application has the
  database open, as it was beside a `LockDatabase` context in 2.0.2.
- **A lock file a medium won't open for writing** (EROFS, an `IOException`, on a lock file that is there) is opened for
  reading, as one this user can only read (`IOService.OpenOneLockFile`). The shared open asked for reading only; the
  open alone asks for writing first, and on a read-only mount failed with the `IOException` (found by the Linux run).
- **.NET's own reading of "file locking off"** includes a browser (`OperatingSystem.IsBrowser()`, `SafeFileHandle.Unix.cs`,
  release/8.0 and 9.0); XOD's copy missed it, so every open there would have been refused (review, below).
- **`DatabaseLockedException`'s message** says a database is open in one context at a time, and how to wait for one.
- **`PasswordSwitch.RefuseBesideUnfinishedSwitch`** is gone: only the shared open called it. Comments that described
  shared opens are corrected.

## 3. Tests

- **New** (`XodOneContextTests`): a second context is refused with `LockDatabase` off and on; a database opens again
  once its context is disposed; a shared hold of the folder's lock file (as XOD 2.0.2 takes) keeps a new open out; an
  open beside another process's context (paused in its commit) is refused and leaves its journal; a reading use (as
  the CLI's) is refused too. Red against the code before the change: the four refusals (the two guards passed).
- **Removed: tests of contexts side by side** (22, and the file `XodJournalBesideTests.cs` with 5; 8 more removed at
  first were brought back, below). With every open alone, their second context is refused at its open, so the case
  they test can't be reached: commits over another context's writes, drops and rollbacks seen from another context,
  journals and writes beside another context, password changes and AES256 keys beside another context, a hold given up
  and taken back. The code they covered is what step 2 removes.
- **Brought back after the review**, as their scenarios are still reachable: the five tests of an index file replaced
  under a context and the three of keys moved after it (`ISS-042`, `ISS-113`), now with the file replaced from outside
  (`OutsideReplace`: rebuilt in a copy of the folder and copied over, as a sync or restore tool would, or a writer
  beside a context with no hold, `ISS-173`); and a staged journal a crash left, now for one context (the next open
  applies none of it and deletes it), a guard only: with one context nothing writes between the crash and that open.
- **Rewritten**, where a second context was incidental: the 16-thread transaction test and a new database's file check
  dispose the first context before opening again; the lock-file test opens one context at a time; two of the crypto
  tests write from one context; the opens paused before their hold (`ISS-096`) are now refused for the other's hold,
  and the one-database-per-folder check is asserted once the other database is closed; the read-only-lock-file open
  (`ISS-084`) is refused and recovers nothing; the crash writer's file scan skips the lock files, now held without
  sharing.

## 4. Breaks

Each restored and the source compared with its diff before the run; the whole suite each time.

| Break | Caught by |
| :--- | :--- |
| S1 every open shared again | the five refusals of `XodOneContextTests` and three older tests (13 failed) |
| S2 no reading-only fallback where no lock file can be created | the read-only folder and no-hold marker tests |
| S3 the fallback recovers a write a crash left | `An_open_that_cannot_take_a_hold_recovers_nothing_and_is_refused_while_something_is_pending` |
| S4 no folder check under the hold | `An_open_the_folder_refuses_recovers_nothing` |
| S5 the hold kept after a failed open | `A_locked_open_whose_recovery_fails_lets_go_of_the_database` |
| S6 the replaced-index-file handling does nothing | three of the restored tests |
| S7 the database file put back into a transaction's image | **not caught**: with one context, the values written under a key in a transaction roll back with it, so the case the exclusion guards (another context's values under a key taken back) can't be reached; the test's comment says so, and the exclusion joins section 5 |
| S8 no read access for a lock file a medium won't open for writing | not on Windows (the test needs a read-only mount); on Linux the test failed before the fix and passes with it |

Step 2 (the source compared with its diff after the four, unchanged):

| Break | Caught by |
| :--- | :--- |
| T1 an open makes `xod.gate.lock` again | `A_database_s_folder_holds_only_the_hold_s_lock_files` (red before step 2: the folder held the gate files) and the password-change tests' check of what is left in the folder (20 failed) |
| T2 a context without a hold counts as holding the database (`HasDatabaseToItself` always true) | **not caught**: every caller of `WithDatabaseToItself` (a password change, the AES256 key) is a write, which the engine refuses first for a context with no hold (`XmlEngine.WritesRefused`, `NoHoldReason`); the check is the key maker's own, kept so the owner refuses whoever calls. The tests can't reach the internal type |
| T3 a commit writes over a journal there | **not caught**: a journal at a commit is one this context's failed commit left, and that failure already refuses every write (`transactionFault`) until the next open undoes it; kept as the journal owner's own check |
| T4 a missing folder gives no reason | **not caught**: an open without its folder fails on the missing database file before anything writes; the reason makes that context's state say "no hold" instead of counting as holding (the review's finding, section 6) |

The fix of `ISS-174` (section 6), each part broken once. The undo writes back a file its commit reported changed, or
one not on disk as the transaction found it; each layer covers the other, so a report site is broken with the
comparison off too:

| Break | Caught by |
| :--- | :--- |
| F1 nothing reported as changed | **not caught alone**: the comparison finds every changed file the tests make; with it off too (F8), nine tests |
| F7 no comparison | **not caught alone**: the reports cover every write the tests make; with them off too (F8), nine tests |
| F8 neither | nine tests of a failed commit's undo |
| F9 a file that can't be read counts as changed | `A_commit_that_fails_on_a_page_another_program_holds_..._carries_on(share: None)` |
| F2 the undo writes every before-image back again | the held-page test, both share modes (red before the fix) |
| F3 a whole file written goes unreported (comparison off) | six tests of a failed commit's undo |
| F4 a page renamed into place goes unreported (comparison off) | **not caught**: no commit in the tests replaces a page by a rename; a commit writes a page in place, or creates it, and renames only when the file's length on disk isn't the one the context last wrote (a file replaced from outside). The comparison covers it; the report sits after the rename, as after the other writes |
| F5 a commit's delete goes unreported (comparison off) | `A_failed_commit_puts_back_the_pages_it_deleted` (new: a drop and the table made again, failed after its files) and an index test |
| F6 a page written in place goes unreported (comparison off) | three tests of a commit undone |

## 5. Step 2: what only served contexts side by side

Done (2026-11-16). With step 1, none of this was reached by two contexts of this version. It hadn't shipped (2.0.2 has
none of it), so no user sees it. Taken out:

- the commit gate (`IOService.CommitGate`, `xod.commit.lock`), the per-file check of a commit against other contexts'
  writes, and the write's wait for another context's commit (`PLAN-062`);
- the gate (`xod.gate.lock`), the shared hold given up and taken back around a password change, an AES256 key or a new
  database (`WithDatabaseToItself`, `TakeBackSharedHold`, `ShareHoldOnceCreated`), and `NoHoldReason`'s "lost its hold";
- the refusal of writes beside another context's journal (`ISS-147`'s mitigation) and of an open beside an unfinished
  password change (`ISS-148`);
- the commit's own hold on its journal and the probe of a journal another context holds (`ISS-147`'s), and the
  refusal of a password change beside another context's journal.

So `IOService.CommitGate.cs`, `XodCommitGateTests.cs` and the crash writer's modes for a second context are gone; the
three tests of a transaction's own deletes there moved to `XodTransactionDeletesTests`. `WithDatabaseToItself` stays,
as a check: it runs its action when the context holds the database (every context with a hold), and refuses one with
no hold. The open with no folder now says so (`NoHoldReason`). `PropertyKeyService.MakeDataKeyUnlocked` no longer
takes the hold. A commit is: the journal staged and moved into place, the files written, the journal deleted.

A failed commit's undo now writes back only the files its commit changed (`ISS-174`, found by the review of step 2):
the writes report each file as they change it (`IOService.FileChangedOnDisk`, the one feed, wired by the engine to the
transaction), and the undo writes back those, and any other file of the transaction that isn't on disk as it found it
(`XodTransaction.NotAsBefore`), so a write that ever fails to report is still undone rather than left as half a commit
(the second review). A file that can't be read and wasn't reported is left: its write failed before changing it. The
recovery at the open still writes back every before-image of a journal: it can't know how far the commit got. The raw
page flush's transaction flag, always false (no transaction is open when pages are written), is gone.

Taken out as leftovers too (the review): `ThrowIfWritesRefused`'s unused parameter, the crash writer's switches no test
set any more (`XOD_TEST_TX_TIMEOUT`, `XOD_TEST_BATCH`, `XOD_TEST_LOCK`), the skipped `ISS-167` test (a second context's
write, which can't happen), and comments that described other contexts.

Kept, though written for contexts side by side: the transaction's exclusion of the database file from its image (break
S7): the AES256 key's one writer is `IOService.SaveNow`, outside every transaction, so a rollback has nothing of the
database file to put back; the comment says it no longer keeps a value readable.

Not removed, though written for contexts side by side: the index service's handling of an index file replaced under a
context (`ISS-042`), which a file tool or a writer beside a context with no hold (`ISS-173`) still reaches (the review
below).

Kept: everything a crash, a failure or another program (a backup tool, a virus scanner) can still cause, which a
single context meets too: the journal and its recovery, the deletes at the commit (`PLAN-063`: a rollback touches no
file), the index markers, and the handling of a file another program holds.

## 6. Review

An independent review of step 1 (the code and the problem, not this plan), read-only, with probes run in a copy:

| Finding | Disposition |
| :--- | :--- |
| On a local file system with .NET's locking on, the cause is removed: one construction path, one exclusive hold, the migrator through the same holder | No change |
| A context with no hold (no lock file can be created or opened) opens for reading beside a writer that opens later, and reads stale data (reproduced with a denied folder, then allowed) | Named debt, `ISS-173`: the same before; it can't write. The index service's handling of a replaced file stays (section 5) |
| Where the file system takes no lock (`DisableFileLocking` on Unix, NFS `local_lock`, SMB on older Linux), two contexts can both hold "alone", even in one process; XOD 2.0.0 and 2.0.1 take no lock without `LockDatabase` | Named debt, `ISS-104` extended (a process-wide registry of held databases would make the in-process half impossible); 2.0.0/2.0.1 in the upgrade notes |
| A context never disposed keeps every other open out until the garbage collector runs (reproduced); scoped or per-request contexts, which worked by default, now fail | Documented in the release notes; the exception's message says how to wait |
| A user who can only read the folder, with the lock files there, takes the hold alone and keeps the writer out | Documented: the rule is one context at a time, readers included |
| Deleted tests whose scenario one context still reaches: the staged journal, and the index file replaced under a context | Brought back (section 3) |
| `Securing_a_database_makes_its_key` no longer shows `Secure` making the key; `A_key_made_inside_a_transaction_outlives_its_rollback` doesn't go red with the exclusion removed | Comment of the first corrected (it can't be seen through an encrypted file); the second confirmed by break S7 and said in its comment |
| A missing folder at the open (`noFolderAtOpen`) counts as "to itself" with no hold; unreachable today (the engine creates the folder or the open fails) | Fixed in step 2: the open with no folder has a `NoHoldReason`, so it counts as holding nothing |
| A refused open changes nothing on disk but the lock files it creates | No change |
| Dead code and comments describing shared opens | Comments corrected; the code is section 5 |
| **Step 2** (a second review, of the step 2 diff, read-only, probes in a copy): a commit that fails on a page another program holds without sharing faults its context, where step 1 refused it before the journal (the deleted comparison read every file first); with a holder that shares reading, the same in 2.0.2. Cause: the undo writes back files the commit never changed | Fixed, `ISS-174` (sections 4, 5): the undo writes back only what the commit changed; the deleted test's scenario is a new test, red before |
| Step 2: leftovers (an unused parameter, crash-writer switches no test sets, a skipped test that can't run, comments describing other contexts) | Taken out (section 5) |
| Step 2: "a journal at its name is always whole" claims more than the code for a power cut (nothing is flushed to the device) | Comment qualified |
| Step 2: with `DisableFileLocking`, the removed journal probe was the last refusal of a write beside another process's journal | Added to `ISS-104` |
| Step 2: the key maker's re-read of the database file, and `IOService.Reopen`, answer a second context that can't open now | Kept as the owner's guard; comments say so |
| The fix of `ISS-174` (a second review, of the fix): the cause removed; but a write that fails to report, or a rename that reports a failure it didn't have, would leave half a commit silently, where the old undo faulted | Fixed: the undo also writes back any file not as the transaction found it (breaks F1, F7, F8) |
| The fix: a file the commit created, held by another program, still fails the undo and faults the context | Named: not silent, the next open recovers; the same before |
| The fix: the raw page flush's transaction flag is always false, and claims atomic writes inside a transaction that never happen | Removed |
| The fix: the claims about `File.Move` and `FileStream` leaving a file unchanged when they throw | Confirmed in the .NET 9 source for the open; the rename's, by Win32 and POSIX behaviour, not stated in so many words |
| The remaining comments of other contexts (`PasswordSwitch`, `XodSecurityService`, `PropertyKeyService`) | Corrected; the password record's message now names the program holding it |
| .NET's reading of "file locking off" includes a browser, which XOD's copy missed, so every open there is refused | Fixed, cited (`SafeFileHandle.Unix.cs`, release/8.0 and 9.0, fetched) |
| On an NFS client flock is a process-owned fcntl lock, so the hold's in-process proof succeeds and every open is refused (where the default shared open used to work) | Documented as a limit in the release notes and `ISS-104`; reasoned from flock(2), not run on NFS |

## 7. Verdict

**Step 1: clean, with named debt.** The cause, opens that let several contexts write one database, is removed where the
file system takes locks: every open holds the database alone. Debt: a context with no hold beside a writer
(`ISS-173`); file systems that take no lock, and XOD 2.0.0/2.0.1, which the hold can't see (`ISS-104`).

**Step 2: clean.** The machinery for contexts side by side is gone rather than left unreached: one lock path (the two
lock files, held alone), one commit path (journal, files, journal deleted). What stays guards what one context still
meets: a crash, a failure, another program. Three of its checks can't be reached through the public API (breaks T2 to
T4) and are kept at their owners. Its review found that a failed commit's undo wrote back files it never changed
(`ISS-174`, older than step 2 and widened by it): the cause is removed, the undo writes back only what the commit
changed.

## 8. How it was checked

- The new tests red against the code before the change (the four refusals; the two guards passed), and the Linux
  read-only-media test red before the EROFS fix.
- The breaks above, each restored and the source compared with its diff before the run.
- The gate (`.github/workflows/tests.yml`'s steps, run locally, and the machine-details check): green, net8.0 515 passed and net9.0 523 (one skipped each, the `ISS-167` test), the
  conformance tests, the format reader, the stress harness.
- The whole suite on Linux (the .NET 9 SDK container, no network, a read-only mount for the read-only test): 523
  passed, 1 skipped.

Step 2, with the fix of `ISS-174`:
- The held-page test red against the code before the fix (both share modes); the breaks T1 to T4 and F1 to F9 above.
- Two independent reviews, read-only, with probes in copies: of the step 2 diff (section 6, `ISS-174` found), then of
  the fix (the cause removed; its findings fixed or named, section 6).
- The gate: green, net8.0 512 passed and net9.0 520 (none skipped: the `ISS-167` test is gone), the conformance tests,
  the format reader, the stress harness, the machine-details check.
- The whole suite on Linux, as above: 520 passed.
