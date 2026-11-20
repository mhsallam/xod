# PLAN-088: a page a crash left unnamed is deleted at the next open; a relation's code that finds no row reads as gone

- **Task ID**: `TASK-104`
- **Status**: Done (2026-11-20)
- **Issues**: `ISS-209` (resolved), `ISS-210` (closed: by design, with one defect fixed); `ISS-211` filed
- **Related**: [`PLAN-087`](PLAN-087-a-page-is-on-disk-while-a-table-names-it.md) (where both were filed), `ISS-043`
  (the marker), `ISS-104`, `ISS-095`

## 1. ISS-209: the cause

A page reaches the disk before any table names it. It leaves only after the last table naming it is written without
it (`ISS-196`). A crash in between leaves a page no table names:

- a table's first page, written before its table file;
- a page a delete emptied, or a dropped table's page, after its table was written and before the page was deleted.

No read of its table reaches such a page. But nothing removed it, either.

So: **nothing recorded which pages were in such a window, so nothing could later tell a page a crash left from any
other page no table names. The writing context now records each page before its window opens. The next open deletes
only the pages a crash recorded that no table names.**

## 2. ISS-209: the change

- **The record** (`IOService.NotePageInFlight`). Each context already keeps an "indexes behind" marker (`ISS-043`). It
  is created and marked before the context's first page or table write. A page's name is appended to that marker, one
  line each, and flushed to the system before the page is written. That covers two points:
  - when a new page's name is chosen (`NewPageFileName`, the only source of new page names);
  - when a page's delete is queued to wait for its table's write (`DeleteUnlocked`).
- **The window's end** (`IOService.PagesSettled`). The records are cleared once no write statement is under way and
  nothing of the context waits. The engine decides this, in two places:
  - the outermost statement's end, after its writes;
  - a flush with no write under way (`FlushToDisk`, beside `IndexesCaughtUp`).

  These are not cleared:
  - a failed statement's records: its undo may still be putting files back;
  - a commit's records, which its journal may still undo. The next flush or transaction clears them.
- **The sweep** (`IOService.DeleteUnnamedPages`). The open (`XmlEngine`) runs it after the recovery, after the database
  file is read (its password and format are known), and before anything is written. It:
  - reads every table file;
  - deletes each page that a marker left by a dead context names and that no table names;
  - deletes the marker.

  The recovery (`DropIndexesLeftBehind`) drops the indexes for such a marker, as for any marker left behind, and keeps
  it for the sweep.
- **Kept, always:**
  - **A page no marker names.** A table file can be missing for reasons other than a crash: a copy, a sync or a restore
    not finished. Its pages then stay.
  - **Every page, when a table file can't be read.** Nobody can know which pages it names. The marker still goes, so
    no open is left distrusting every index for a page that only wastes space.
  - **A page another program holds.** It stays unnamed, as before.
- **No sweep:**
  - **No hold.** A context without a hold never records and never sweeps.
  - **No locks.** Where the file system takes no locks (`FileLockingSwitchedOff`), markers aren't kept for the sweep:
    a live context's marker can't be told from a dead one's there (`ISS-104`).
- **A conversion into another folder** (`XodMigrator.ConvertInto`) carries such a marker, and the converted database's
  first open sweeps. Converted in place, the marker just stays where it is.
- **The format stays compatible.** An older release reads a marker with page lines as "behind", as before: it drops the
  indexes and deletes the marker, and the pages stay unnamed.

## 3. ISS-210: not a defect, and one defect found under it

The issue proposed that a relation's stored row code that finds no row be an error, as a missing page became one in
`ISS-196`. The code says otherwise. XOD makes such codes itself, and the reads that leave them out give the right
answer:

- **`Drop<T>()`** deletes a table's rows without touching the rows that name them. A parent's list then names children
  that aren't there, and a child names a parent that isn't.
- **A delete** takes a child out of its parent's list (`RemoveFromParentElements`), and nothing else. Even that skips a
  child with no parent property (`ISS-095`). A reference to the deleted row, single or in a list, keeps its code, and
  `XodItemCacheTests` already required it to read as null.
- **XOD 2.0** left any child's entry in its parent's list after the child's delete.

So the list reader leaves the entry out, a reference with a `[ForeignKey]` is found again by its key, and one without
reads as null. The readers now say why, at the site, and tests pin the behaviour.

**One defect was found under it and fixed.** A reference of type `object` whose row was gone threw
`AnynomousTypeException` ("register the type"), and the row holding it couldn't be read. The type of such a reference
comes from the table that lists its page, and when no table lists the page, the type was taken as unknown. Now:

- **No table lists the page** (`IOService.PageTableName` returns none): the row is gone, and the reference reads as null.
- **A table lists the page, but its type isn't registered:** it still throws.

A stale code can come to name a different row only if a new code collides with it. Codes are drawn at random
(`ValueHelper.PickCode`, 36 bits) and aren't checked against existing ones, so this is improbable, not impossible. The
same is true of a new page's name against an existing page. Filed as `ISS-211`.

## 4. Tests

`XodUnnamedPageTests`. The tests marked † were red before the fix (6 of 6 first), or red against the version the reviews
rejected:

- † a table's first row, with the process killed before its table file's write: after the next open, no page is on
  disk, and the table takes a row;
- † every row deleted with no transaction (`deletemembers`, a new crash-writer mode), killed before the first emptied
  page's delete: after the next open, no page is on disk;
- † pages a dead context's marker names and no table names are deleted, in XML, JSON and Binary and under a password. A
  page no marker names, and every page a table names, stay, and the marker goes;
- † pages no crash named are kept, including while every table file is away. The tables, put back, read every row;
- † a long-lived context (`longlived`: 400 rows over many statements and pages, never flushed) killed after its
  statements: its table file missing at the next open costs no page;
- † a trigger's flush in the middle of a statement (`emptytables`, `triggerflush`, direct and buffered): the record of
  the page the statement chose stays, and the page goes after the crash;
- † a statement whose batch was written and which then failed, killed in its undo (`rangeundo`): the page goes;
- † a conversion into another folder after a crash: the converted database's first open drops the page;
- a table file the open can't read: every page stays, and the marker goes.

`XodDanglingRelationTests` (`ISS-210`):

- a parent whose children's table was dropped reads an empty list, then lists the children inserted again;
- a `[ForeignKey]` reference whose row was deleted reads as null, then as the row inserted again with its key;
- a plain list of rows with a key, one of them deleted, reads without it;
- † a reference of type `object` whose row was deleted, or whose table was dropped, reads as null.

Breaks, each caught:

| Break | Tests that failed |
|---|---|
| A new page not recorded | 2 |
| A queued delete not recorded | 1 |
| The marker dropped by the recovery | 7 |
| The records never cleared | 1 |
| The records cleared by the writers, as in the second version | 3 |
| The flush clearing the records in the middle of a statement | 2 |
| The marker not carried by a conversion | 1 |
| An unfinished sweep keeping its marker | 1 |
| Every page no table names swept, as in the first version | 5 (run with the second version) |
| A page a table names deleted | 4 |
| A table the open can't read skipped | 1 |
| The list reader throwing on a code that finds no row | 2 |
| The reference reader throwing on a code that finds no row | 2, one of them `XodItemCacheTests` |

## 5. Review

There were three independent reviews, each given the code and the problem, not this plan. Each probed in its own copy.

1. **The first version** deleted at the open every page no table named. The first review reproduced a table file missing
   for another reason (a copy not finished) costing its pages: the table, put back, named deleted pages. It also found
   that a reference of type `object` to a gone row threw (fixed, §3), and that a stale code could name a wrong row after
   a collision (`ISS-211`). Hence the record.
2. **The second version** cleared a page's record only when the indexes caught up. The second review reproduced a
   long-lived context, in direct mode and never flushed, recording every page it ever made: a crash and a missing table
   file then cost them. It also found:
   - the migrator recovered without sweeping;
   - a sweep that couldn't finish kept its marker, and every index untrusted, for good.

   Hence the window's end, the migrator's marker, and the marker deleted after every sweep.
3. **The third version** cleared the records in the writers, whenever nothing was pending. The third review reproduced
   two windows still open when that held:
   - a trigger's flush in the middle of a statement, before the page the statement chose was pending;
   - a statement's undo after its batch was written.

   Hence the engine's two points. It confirmed the migrator's marker is found whatever its name, and that no path
   deletes a page a table names. It corrected a claim: a reference can still reach an unnamed page's rows, and reads
   them as gone once the sweep deletes the page.

The final change was not reviewed a fourth time. Each finding above is fixed with a test that was seen red first.

**Named debt:**

- A statement that fails, and whose undo also fails, can leave a page no table names. A later close may clear its
  record: the page leaks, as before `ISS-209`, and no data is lost.
- After a power cut, a page may lack its record (no write is flushed to the device). The page leaks, as before.
- With locks local to one host (`ISS-104`), an open on another host can take a live context's marker for a dead one's.
  It can then delete a page that context is writing. That row of `ISS-104` is updated.

## 6. Verdict

- `ISS-209`: clean, with named debt (above).
- `ISS-210`: closed as by design, with the behaviour pinned by tests. The `object` reference defect under it is fixed.
- Filed: `ISS-211`.
