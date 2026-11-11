# PLAN-050: A password change that is all or nothing (ISS-065 to ISS-068)

- **Task ID**: `TASK-060`
- **Status**: Fixed in the tree (2026-11-12), not released: no version change, nothing pushed.
- **Related**: [`PLAN-046`](PLAN-046-file-format-spec.md) sections 2 and 9 (the files, updated),
  [`PLAN-049`](PLAN-049-property-encryption.md) (the AES256 key a password change also protects),
  [`KNOWN-ISSUES.md`](../KNOWN-ISSUES.md)

## 1. What was wrong, and why

| Issue | Defect | Cause |
| :--- | :--- | :--- |
| ISS-065 | A password change that failed part-way (a file open elsewhere, a crash) left a database neither password opens. | `Secure`, `Loose` and `ChangePassword` rewrote the files one at a time in place, with no commit point and no record, so nothing could tell afterwards how far a change got. `ChangePassword` was `Loose` then `Secure`: every file was in plain on disk between the two. A change inside a transaction let the rollback put files back under the old password. |
| ISS-066 | With `DatabaseOptions.LockDatabase`, `Secure` and `ChangePassword` failed (`IOException`). | The rewrite's temp name for `<name>.xod` was `<name>.xod.lock`: the lock file, held open. |
| ISS-067 | Under a password with buffered durability, `Flush` didn't put the index on disk. | Under a password the index service saves an index through the buffered files; every flush (six copies of it) flushed the buffered files first, so the index stayed queued. |
| ISS-068 | Opening a folder could pick `pw.xod.tmp` (or a `.lock`, `.xpwnew`) as the database file, and create a new database under that name. | The engine took the first match of `*.xod*`. |

**Cause sentence (ISS-065).** Files ended under two passwords because the switch rewrote them in place with
no commit point and nothing recorded how far it got. The change makes that impossible: the switch stages
every file, commits with a record, then renames; the open finishes or discards a switch by its record; and
every other path that could write those files around a switch (a later write in the same context, another
context's open, a transaction journal or atomic write waiting for recovery, a transaction's rollback) waits
on the record or is refused.

## 2. The design

- **One writer** for the files' password: `XodSecurityService.SwitchFiles`, behind `Secure`, `Loose` and
  `ChangePassword` (each checks, then switches). The engine has one path for the three
  (`XmlEngine.SwitchPassword`): refuse inside a transaction, finish a pending switch, check, flush, protect
  the AES256 key (PLAN-049), switch, follow the password the files are under, remove stale key protections.
- **The switch** (PLAN-046 section 9 has the format):
  1. refuse while `transaction.xwal` or a `.tmp` exists (the next open would put those back, under the
     password they were written with);
  2. create `<database file>.xpwlog` holding only its header and keep it open with no sharing;
  3. write each database file under the new password to `<file>.xpwnew`, flushed to the device;
  4. rewrite the record as the commit (header, file names, `commit`), flushed;
  5. rename each `.xpwnew` over its file; close and delete the record.
- **Recovery at open** (`RecoverSwitch`, first of `IOService.RecoverInterruptedWrites`, outside its catch):
  a record held open is a switch in progress, and the open fails; a committed record is finished; an
  uncommitted one, and every `.xpwnew` no record names, is discarded. A rename that fails stops the open.
- **A switch that commits but can't replace a file** (open elsewhere) stands: the context takes the new
  password and throws to say so. Its next file write finishes the switch first (the engine's
  `BeforeFileMutate` hook, which every file write of `IOService` and `IndexService` calls first), and so does
  its next password change; otherwise the next open does. Until then, reading that file in this context
  fails. The one write outside the hook, the AES256 key's (`PropertyKeyService` through `IOService.SaveNow`,
  outside it so that a rollback can't take a key away), reads the database file first, which fails until that
  file is switched; so it can't write a file a staged copy would later replace.
- **Cleanup of a switch that fails before its commit** deletes the record first, then the staged files. A
  record that may hold the commit and can't be deleted is treated as a commit (pending, as above).
- **One flush routine** (`XmlEngine.FlushToDisk`): buffered files, indexes, buffered files again (the index
  files queued under a password). Pages go before their index, which the open's staleness check relies on.
  Used by `Flush`, `ClearCache`, `ClearCaches`, `GetStatistics`, the password switch and dispose.
- **The database file of a folder** is the first `.xod`/`.xodj`/`.xodb` in ordinal order.
- **Fault injection** for the tests (`FaultInjection.PasswordStep`, inert unless set): `XOD_CRASH_AT_STEP`,
  `XOD_FAIL_AT_STEP` (one `IOException`), `XOD_PAUSE_AT_STEP`, at named steps (`staged:2`, `committed`,
  `switching:pw.xod`, `switched:1`), run through the crash writer, the same on every OS.

## 3. Tests

`XodPasswordChangeTests` (23). Each was red on the code before its fix, or proven by breaking the code it
guards (section 4).

| Test | Guards |
| :--- | :--- |
| A change that can't read a file (Xml, Json, Binary), and `Secure`/`Loose` the same, change nothing | staging; one writer |
| A crash at `staged:1`, `staged:3` (old password), `committed`, `switched:1`, `switched:3` (new) leaves one password; nothing on disk in plain | the commit point; recovery |
| A commit record cut short is not a commit | the commit line |
| A change that can't replace `pw.xod` or a table file finishes at the next open | recovery |
| A second change after one that didn't finish finishes it first (`pw.xod` and a table file) | the engine's and the service's finish |
| Writes after a change that didn't finish are kept | the write fence |
| Opening the database during a change is refused, and the change completes | the held record |
| A change over an unfinished commit (`transaction.xwal`), or an unfinished write (`.tmp`), is refused | the refusal |
| A change inside a transaction is refused | the refusal |
| A locked database can be secured | ISS-066 |
| `Flush` under a password with buffered durability writes the index file | ISS-067 |
| A change with buffered durability keeps rows not yet flushed | the flush before the switch |
| Opening a folder picks the database file | ISS-068 |

## 4. How it was checked

- **Red first.** The first round's 13 tests were red on the code before (`PLAN-049`'s tree); the review's
  six (section 5) were red on the first round's fix.
- **Each guard broken, one at a time** (a script that restores each file and checks it byte for byte),
  each turning tests red: no staging (in-place writes); no recovery at open; any record counts as a
  commit; orphan staged files kept; no transaction refusal; no refusal over unfinished writes; the write
  fence removed; the engine's finish removed; the pending mark not set; the record neither held nor
  respected (both halves; either alone still stops the second open); the index not flushed through the
  buffered files; no flush before the switch; the folder open back to `*.xod*`.
- **Not proven by a test:** the record deleted before the staged files after a failed commit, and the
  check that every staged file is there when the switch renames (each needs a delete to fail).
- **The gate** (2026-11-12, on the final tree): `Xod.Tests` 153/153 on net8 and net9 (130 before, 23 new);
  `Xod.Conformance` 14/14 on each; `read.mjs --check` and `--check-fresh` ok (the reader now refuses a folder
  with a password change record, as it does a transaction journal); `XodBench.Tests` 26/26; the library
  builds with no warnings. No public API change (`api-surface.txt` unchanged).
- **Second copies removed:** the list of a database's files (the switch, the migrator, the folder open) is
  `Helpers.DatabaseFiles`; the flush is `FlushToDisk`.

## 5. Independent review

A fresh agent reviewed the first round (staging, commit record, recovery at open, transaction refusal)
read-only and reproduced its serious findings with probes:

1. **H1, a leftover transaction journal undid a finished switch** (reproduced). Fixed by refusing over an
   unfinished commit or write.
2. **H2, writes after a switch that didn't finish were lost at the next open**: the stale staged copy
   replaced the file they wrote (reproduced with the index file). Fixed by the write fence.
3. **H3, another context's open deleted a live switch's staged files**, and the switch took the missing
   files as already switched: the database then opened with neither password (reproduced, and again here
   with the guard broken). Fixed by the held record and the switch's check that every copy is there.
4. **M1, `Flush` under a password with buffered durability left the index queued** (reproduced, before this
   change too). ISS-067, fixed.
5. **L1-L5**: the switch takes every database file in the folder (the folder is the database: tables are
   named by type, so a second database there already shares them); the cleanup order (fixed); the class
   comment claimed crash safety beyond a process crash (corrected); folder open (ISS-068, fixed);
   `XodSecurityService` is public, a second way in (ISS-069, open).

The reviewer also confirmed the transaction refusal by breaking it, and flagged that the first round's
"can't replace a file" tests depended on Windows sharing rules; they now inject the failure.

## 6. Verdicts

| Issue | Verdict |
| :--- | :--- |
| ISS-065 | **Clean, with named debt** (section 7): all or nothing across a process crash, for one context; another context opening during a change is refused. |
| ISS-066 | **Clean**: no temp name of the lock file. |
| ISS-067 | **Clean**: one flush routine. |
| ISS-068 | **Clean**. |

## 7. Remaining debt

- **A power cut** can lose a rename or resurrect a deleted record: the library flushes file contents, not
  directory entries (as everywhere in XOD).
- **Linux is not run here.** The held record and the tests that hold a file with no sharing rely on .NET's
  advisory locks between .NET processes on Unix; CI runs Ubuntu.
- **A context open before another changes the password** keeps the old password and writes under it
  (ISS-071, open; `DatabaseOptions.LockDatabase` prevents it).
- **A commit whose write fails leaves its journal** while the context goes on (ISS-070, open): the next open
  undoes that commit, and with it any later write to the same files.
- **`XodSecurityService` is public** (ISS-069, open): calling it directly skips the engine's flush,
  transaction refusal, AES256 key and caches. Making it internal is an API change.
- **A switch that fails before its commit** may leave a protection of the AES256 key by the password it
  tried, inside the database file under the password that still opens it; the next finished change removes
  it.
- **Reads in a context whose switch didn't finish** fail until a write, a password change or a reopen
  finishes it.
