# PLAN-051: A failed commit undone at once; a database a password change has to itself (ISS-070, ISS-071, ISS-073, ISS-074, ISS-076 to ISS-079)

- **Task ID**: `TASK-061`
- **Status**: Fixed in the tree (2026-11-12), not released: no version change, nothing pushed.
- **Related**: [`PLAN-050`](PLAN-050-password-change.md) (the password switch this builds on),
  [`PLAN-046`](PLAN-046-file-format-spec.md) sections 2 and 9 (the lock file and recovery, updated),
  [`KNOWN-ISSUES.md`](../KNOWN-ISSUES.md)

## 1. What was wrong, and why

| Issue | Defect | Cause |
| :--- | :--- | :--- |
| ISS-070 | A commit whose write failed part-way left its journal while the context went on; the next open undid that commit and any later write to the same files. A journal the open couldn't write back was deleted, whatever the error. | Nothing undid a failed commit until the next open, and the context kept showing it. The open's recovery sat in a `catch` that swallowed every error and deleted the journal. |
| ISS-071 | Without `LockDatabase`, a context open before another changed the password went on writing under the old one. | No context knew whether another had the database open. |
| ISS-073 | An open undid a commit another context had under way: its journal looked like one a crash left (found writing ISS-071's tests; red on the code before). | The open recovered whatever it found, with no way to tell a live journal from a dead one. |
| ISS-074 | An open that failed after taking hold of the database (a wrong password) kept holding it: with `LockDatabase`, later opens got `DatabaseLockedException` until the garbage collector ran. | The engine's constructor didn't release what it had taken when it threw. |
| ISS-076 | With buffered durability, a rollback (and, after the first round of this plan, a failed commit) came back at the next flush; the transaction's before-images also missed what was buffered before it (found by the review, section 5; the rollback case predates this plan). | Forgetting what the context held dropped its caches but not its pending writes, and a transaction began with writes still in the buffer. |
| ISS-077 | A commit whose journal couldn't be written left the context showing the commit while every later write stayed in memory for good and was lost (found by the review; predates). | The journal was written outside the part of the commit that is undone, and its batch was never ended. |
| ISS-078 | A commit wrote its journal over another's (a concurrent commit's, or one a crash left while others had the database open) and deleted it after; and the open made a journal never renamed into place the journal (found by the review; predates). | The journal's name is the database's, and nothing checked whether one was there; the `.tmp` step treated a journal's like any other file's. |
| ISS-079 | `ClearCache` inside a transaction dropped the transaction's pages waiting in its batch and its index changes, so the commit wrote part of it (found while fixing ISS-076; predates). | The cache drop cleared pending page writes along with the cache, and the indexes, whatever was in progress. |

**Cause sentences.** ISS-070: a failed commit wasn't undone where it failed. The commit now undoes itself at
once, from the snapshots it holds, under the engine's lock; if that fails, the context refuses every write
until the next open undoes it from the journal. ISS-071 and ISS-073: no context knew whether others had the
database open. Every context now holds the lock file, shared (or with `LockDatabase` exclusively); a password
change needs it exclusively, and an open recovers only when it can have it exclusively. ISS-076 and ISS-079:
what the context held was dropped in three places, none of which owned its pending writes. One routine now
forgets everything, pending writes included, after a rollback or a failed commit; a transaction starts with
nothing pending; dropping the cache never drops a pending write. ISS-077: part of the commit sat outside its
undo. Every failure point of a commit now takes the one undo. ISS-078: the journal's name is shared and
nothing checked it. A commit is now refused while another's journal is there, and deletes only its own.

## 2. The design

- **One routine puts files back** (`XodTransaction.RestoreBeforeImages`): files the transaction created
  deleted, snapshots written back through `.tmp`, and (after a commit wrote them) every index file deleted.
  A rollback, a failed commit and the open's recovery use it.
- **One routine forgets what the context holds** (`XmlEngine.ForgetEverythingHeld`): pending writes
  (`IOService.DiscardPendingWrites`, the one place they are dropped), caches, indexes, autonumbers, the AES256
  key. A rollback and a failed commit run it even when the restore failed. Dropping the cache
  (`ClearCurrentCache`, `ClearCache`) never drops a pending write; inside a transaction `ClearCache` keeps the
  indexes too.
- **A transaction starts with nothing pending**: `BeginTransaction` flushes the context first, so its
  before-images hold what was buffered, and forgetting every pending write at a rollback forgets only the
  transaction's. The engine knows whether the transaction's batch is still open (`transactionBatchOpen`): a
  rollback, or a commit that failed before writing, ends it unwritten.
- **A commit or a rollback runs whole under the engine's lock**, the undo of a failed commit included, so no
  other thread's write lands between the failure and the undo.
- **A commit that fails anywhere** (its journal, its files, its indexes, the journal's deletion) is undone at
  once and the original exception rethrown. Before its journal is in place nothing was written, so nothing is
  put back (another context may have written those files since). If the undo fails, the context is faulted
  and refuses writes; the journal stays for the next open.
- **The journal is never another commit's**: a commit is refused while a journal (or a staged one) is in the
  folder, the staged file is created new and renamed without overwriting, and a commit deletes only its own.
- **The open's recovery** fails the open when a journal can't be written back, and keeps the journal; a
  journal that doesn't parse is deleted; one naming a file outside the folder is refused. A journal never
  renamed into place (`transaction.xwal.tmp`) is deleted: its commit hadn't started.
- **Writes are refused in one place** (`XmlEngine.ThrowIfWritesRefused`), checked when a write operation
  starts, so a refused write changes nothing in memory either, and again before every file write: a faulted
  context, or one without a hold on the database.
- **The lock file** `<database file>.lock` is held by every context: shared (read access, others may read and
  write; on Unix .NET takes a shared advisory lock) or, with `LockDatabase`, exclusive. Either kind fails
  while the other is held. **It is never deleted**, by any context: on Unix a context that opened it just
  before a deletion would hold a file no longer in the folder, and the next would create another.
- **A context without a hold** refuses writes (`IOService.NoHoldReason`): one whose folder can't be written
  and has no lock file opens for reading only; one that lost its hold (another context took the database in
  the instant a password change gave it back) must be disposed.
- **A password change** takes the lock file exclusively for the whole change (`IOService.WithDatabaseToItself`)
  and is refused, changing nothing, while another context has it, or when this context holds no lock.
- **Recovery at open** runs only with the lock file held exclusively: with `LockDatabase` always; otherwise
  only when something is pending (a journal, a password change, a `.tmp` of a database file) and no other
  context has the database open. In a folder that can't be written it runs without a hold, since nothing
  there can be written by anyone: it finds nothing to change, or stops the open.
- **A failed open releases the lock file, and so does a close whose last flush fails.**

## 3. Tests

Each red on the code before (the tree of `PLAN-050`, or this plan's first round for the review's findings),
with the crash writer's modes `txcommit`, `txfail`, `txwrite-commit`, `txwrite-rollback`, `pwopen`,
`pwopen-locked`, the options `XOD_TEST_DURABILITY`, `XOD_TEST_LOCK`, `XOD_TEST_BATCH`, `XOD_TEST_SINGLE`, and
the fault steps `commit-written`, `commit-indexed`, `undo`, `journal-writing`, `journal-staged`,
`recover-restoring`, `retaking-hold`, `closing`.

`XodSharedAccessTests` (11):

| Test | Guards |
| :--- | :--- |
| A commit that fails part-way is undone at once: the context sees the database as before, what it writes next is kept | the undo |
| A commit that fails and can't be undone refuses writes until reopened; the open then undoes it | the fault |
| A recovery that fails keeps the journal and stops the open; the next open recovers | recovery errors |
| A locked open whose recovery fails lets go of the database | release on a failed open |
| An open during another context's commit leaves the commit alone | recovery only alone |
| A password change is refused while another context has the database open, and works once it closes | the exclusive change |
| A database locked by one context can't be opened by another, with or without `LockDatabase` | the lock file |
| A failed open lets go of the database | release on a failed open |
| The lock file stays, and doesn't block a later `LockDatabase` open | never deleted |
| A context that lost its hold refuses writes | the lost hold |
| A database in a folder that can't be written opens for reading, and refuses writes with the reason | read-only folders, refusal at the start |

`XodCommitFailureTests` (10):

| Test | Guards |
| :--- | :--- |
| A failed commit with buffered writes is not written later | flush at begin, the discard |
| A rollback with buffered writes is not written later | flush at begin |
| A commit whose journal can't be written is undone | the journal inside the undo |
| A commit that fails after its indexes drops them (reinserting its keys works) | the index drop |
| A commit that fails after its indexes leaves no index file | the index drop, on the files |
| A commit that fails after buffering its indexes doesn't write them later | the discard of pending writes |
| A close that fails lets go of the database | the close |
| Clearing the cache inside a transaction keeps its changes | the cache drop |
| A commit is refused while another commit's journal is there; the dead journal is kept and undone by the next open alone | the journal never another's |
| A journal never renamed into place is not applied over later writes | the journal's `.tmp` |

Two older tests changed with the design: `XodStressTests.Exclusive_Database_Lock_Prevents_Concurrent_Context_Writers`
now expects the lock file to stay, and the crash tests' writer takes several variables.

## 4. How it was checked

- **Gate** (the final tree, 2026-11-12): `Xod.Tests` 174/174 on net8.0 and on net9.0; conformance 14/14 on
  each; the independent reader `read.mjs --check` and `--check-fresh` ok; `XodBench.Tests` 26/26; the library
  builds with 0 warnings.
- **Guards proven by breaking the code** (each break turned at least one test red, then the code was restored
  and the crash writer rebuilt): the undo removed; caches kept after it; the fault check removed; recovery
  errors swallowed and the journal deleted; recovery at open with others open; the password change without
  the exclusive hold; the shared hold not taken; the constructor not releasing; a failed locked recovery
  keeping the hold; no flush at `BeginTransaction`; pending writes kept after a rollback; a journal failure not
  undone; the undo keeping the indexes; `Dispose` keeping the hold when its flush fails; a lost hold not
  recorded; the read-only folder failing the open; writes refused only at the file; the lock file deleted by
  an exclusive holder; a journal written over another; a journal's `.tmp` promoted; `ClearCache` in a
  transaction dropping its pending pages; and its indexes.
- **Not proven by a test**: that a commit refused before its journal puts nothing back (no test has another
  context write between); that the whole commit and its undo hold the engine's lock (no fault step sits in
  the gap any more; it is held by construction, `XodTransaction.exclusive`).

## 5. Independent review

A read-only reviewer (2026-11-12) checked the first round with probes on copies, on Windows and in Linux
containers (.NET 8 and 9), and 10 mutation runs. It found, each verified here and fixed with a test:

1. **High**: buffered durability: a failed commit, and a rollback (predates), written back at the next flush
   (ISS-076).
2. **High**: a journal that can't be written leaves the batch open: the commit shown, later writes lost
   (predates; ISS-077).
3. A context that can't take back its shared hold after a password change carries on with no hold: the next
   change runs alone without exclusivity, and an opener may undo its live journal. Now it refuses writes.
4. Opening in a folder that can't be written failed (the lock file couldn't be created), against the format
   spec's "read-only folders keep working". Now it opens for reading.
5. A deleted lock file splits the locks on Linux (a `LockDatabase` close deleted it). Now it is never deleted.
6. The undo wasn't under the engine's lock throughout (inferred). Now the whole commit is.
7. Deferred recovery understated: a commit wrote over a dead journal and deleted it, two commits clobbered each
   other's journals, a journal's `.tmp` was promoted (ISS-078, fixed); the shared open neither recovers nor reads
   snapshots, and one stuck `.tmp` makes plain opens take the exclusive hold (debt, section 7, narrowed: only a
   database file's `.tmp` counts now).
8. `IOService.Dispose` wasn't exception-safe (fixed).
9. On Windows an open racing the lock file's deletion fails with `UnauthorizedAccessException` (gone with 5).
10. Processes of 2.0.0/2.0.1 take no hold (release notes, section 7).

It also found the index-drop guard untested (a test now; section 4) and listed the dependency facts: Windows
`FileShare` behaves as the code assumes (docs and probes); on Unix .NET takes `LOCK_SH` for a handle without
write access and `LOCK_EX` for `FileShare.None`, non-blocking, and any error other than `EWOULDBLOCK` leaves the
file unlocked silently; `System.IO.DisableFileLocking` switches locking off (runtime source,
`SafeFileHandle.Unix.cs`, release/8.0 and 9.0).

## 6. Verdicts

- **Failed-commit undo (ISS-070, ISS-076, ISS-077)**: clean. Every failure point of a commit takes one path;
  the context and the disk agree, buffered or not.
- **Recovery errors at open**: clean.
- **Fault state and refused writes**: clean, one decision checked at the start of every write and before every
  file write.
- **The journal never another's (ISS-078)**: clean among contexts of this version.
- **Cache drop (ISS-079)**: clean.
- **Shared lock file (ISS-071, ISS-073)**: a mitigation. It holds among contexts of this version that keep
  their hold, on a file system with locking; outside that (section 7) contexts without `LockDatabase` are
  uncoordinated (ISS-042), and `ISS-069` is still a way round it.
- **Releasing the hold (ISS-074)**: clean for the open and the close.

## 7. Remaining debt

- **What a crash leaves while other contexts have the database open** waits until an open finds the database
  to itself. Meanwhile the open contexts read the half-done commit (the library doesn't read a journal's
  snapshots in place of the files, as PLAN-046 asks of an outside reader: it can't tell a live journal from
  one a crash left); their commits are refused; their writes outside a transaction go through and are undone
  by that later recovery. Contexts without `LockDatabase` are otherwise uncoordinated (ISS-042).
- **A `.tmp` of a database file that the open can't tidy** (a file open elsewhere) stays, and every open
  without `LockDatabase` then takes the lock file exclusively for an instant, so another open at that instant
  fails at the default `LockRetryTimeoutMs` of 0.
- **Two openers at the same instant after a crash**: one recovers with the lock file held exclusively, and
  the other's open fails with `DatabaseLockedException` unless `LockRetryTimeoutMs` lets it wait.
- **After a password change**, an open in the instant the change gives back the database makes the changing
  context lose its hold: it refuses writes and must be disposed.
- **No coordination without file locking**: with .NET's file locking switched off
  (`System.IO.DisableFileLocking`), or on a file system that doesn't lock, every hold succeeds and nothing is
  coordinated. Deleting the lock file while contexts are open (a person, a tool) does the same on Unix.
- **Processes of XOD 2.0.0/2.0.1 take no hold**: a newer version's open may undo their live commit, and a
  password change goes ahead while they are open. Don't mix them on one database.
- **A context that is never disposed** holds the lock file until it is finalized, which blocks password
  changes and `LockDatabase` opens.
- **Reads in a faulted context** see the disk as the failed commit left it, until the database is reopened.
- **Behaviour changes** (release notes, `RELEASE-NOTES-NEXT.md`): a `LockDatabase` context and contexts
  without it no longer share a database; a context opened without a writable folder or lock file is
  read-only; the lock file stays in the folder.
- **Linux is not run by the gate here**: the reviewer's probes in Linux containers matched Windows for the
  holds; NFS and SMB are unverified.
