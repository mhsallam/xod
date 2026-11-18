# PLAN-084: every file written whole goes through its `.tmp`

- **Task ID**: `TASK-099`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-134`
- **Related**: `ISS-043` (the mark of indexes behind), `ISS-158` (a ban on direct file writes)

## 1. The cause

A file written whole is written beside its name as `.tmp`, then moved over it, so a crash leaves the old file or the
new one (`IOService.WriteRawBytesAtomic`). Two things broke that:

- **A new page was written straight to its name.** A crash partway left part of a page, which the next open read and
  failed on (`DatabaseFileException`), so the table couldn't be read.
- **The next open moved a `.tmp` with no file beside it into the file's place.** Nothing says such a `.tmp` was
  written to its end, so a table file cut short was put in place the same way. Every writer moves its `.tmp` over its
  file, which is never missing in between: a `.tmp` left belongs to a write that hadn't finished.

So: **two writers and the recovery each decided what a crash leaves. The change makes every file written whole take
one order, written beside its name and then moved, and the recovery delete every `.tmp` left, so a file is never put
in place unless its write finished.**

## 2. The change

- A new page is written through `WriteRawBytesAtomic`, as an existing one is.
- `RecoverInterruptedWrites` deletes every `.tmp` of the database. A file never moved into place is as if its write
  hadn't started: its statement didn't finish, and a commit's journal undoes the rest.
- A new index file goes through its `.tmp` too. A crash partway through one was already handled: the index is written
  before the pages, under the mark of `ISS-043`, which drops it at the next open. This change only keeps one write
  order for every file written whole.
- A test step, `file-partly-written`, sits between the two halves of a file written whole, and after the index file's
  header. A step can name a file by its extension (`file-partly-written:*.xpag`), for files named at random.

## 3. Tests

`XodNewFileCrashTests`:

- a table's first row, with the writer killed while part of its new page, table file or index file is on disk: the
  next open reads the table, and the row can be inserted again. **Red before** for the page and the table file
  (`DatabaseFileException`), passing for the index file;
- a `.tmp` cut short with no file beside it is deleted at the next open, not put in its place: **red before**.

Breaks: a new page written straight to its name, caught; a lone `.tmp` moved into place, caught; a new index written
straight to its name, **not caught**, as expected, since `ISS-043`'s mark already covers it.

## 4. Left open

- `ISS-158`: nothing yet stops a new direct write of a data file. The library's direct writes left are journals and
  markers, written under their own protocols.
