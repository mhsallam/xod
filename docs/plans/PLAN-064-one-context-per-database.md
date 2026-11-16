# PLAN-064: a database is open in one context at a time

- **Task ID**: `TASK-079`
- **Status**: Step 1 done (2026-11-16): every context holds its database alone. Step 2 (removing what only served
  contexts side by side) proposed, section 5.
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

## 5. Step 2: what only served contexts side by side

Not yet done. With step 1, none of this is reached by two contexts of this version; it waits for a second context that
can't open. It hasn't shipped (2.0.2 has none of it), so taking it out before the release means no user ever sees it:

- the commit gate (`IOService.CommitGate`, `xod.commit.lock`), the per-file check of a commit against other contexts'
  writes, and the write's wait for another context's commit (`PLAN-062`);
- the gate (`xod.gate.lock`), the shared hold given up and taken back around a password change, an AES256 key or a new
  database (`WithDatabaseToItself`, `TakeBackSharedHold`, `ShareHoldOnceCreated`), and `NoHoldReason`'s "lost its hold";
- the refusal of writes beside another context's journal (`ISS-147`'s mitigation) and of an open beside an unfinished
  password change (`ISS-148`);
- the transaction's exclusion of the database file from its image (break S7 above).

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
| A missing folder at the open (`noFolderAtOpen`) counts as "to itself" with no hold; unreachable today (the engine creates the folder or the open fails) | Named, section 5 |
| A refused open changes nothing on disk but the lock files it creates | No change |
| Dead code and comments describing shared opens | Comments corrected; the code is section 5 |
| .NET's reading of "file locking off" includes a browser, which XOD's copy missed, so every open there is refused | Fixed, cited (`SafeFileHandle.Unix.cs`, release/8.0 and 9.0, fetched) |
| On an NFS client flock is a process-owned fcntl lock, so the hold's in-process proof succeeds and every open is refused (where the default shared open used to work) | Documented as a limit in the release notes and `ISS-104`; reasoned from flock(2), not run on NFS |

## 7. Verdict

**Step 1: clean, with named debt.** The cause, opens that let several contexts write one database, is removed where the
file system takes locks: every open holds the database alone. Debt: a context with no hold beside a writer
(`ISS-173`); file systems that take no lock, and XOD 2.0.0/2.0.1, which the hold can't see (`ISS-104`); the code that
only served contexts side by side, still there and no longer reached (section 5).

## 8. How it was checked

- The new tests red against the code before the change (the four refusals; the two guards passed), and the Linux
  read-only-media test red before the EROFS fix.
- The breaks above, each restored and the source compared with its diff before the run.
- The gate (`.github/workflows/tests.yml`'s steps, run locally, and the machine-details check): green, net8.0 515 passed and net9.0 523 (one skipped each, the `ISS-167` test), the
  conformance tests, the format reader, the stress harness.
- The whole suite on Linux (the .NET 9 SDK container, no network, a read-only mount for the read-only test): 523
  passed, 1 skipped.
