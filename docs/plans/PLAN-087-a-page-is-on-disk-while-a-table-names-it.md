# PLAN-087: a page is on disk while a table names it

- **Task ID**: `TASK-103`
- **Status**: Done (2026-11-20)
- **Issues**: `ISS-196`
- **Related**: [`PLAN-078`](PLAN-078-a-page-s-rows-found-by-one-reader.md) (where it was found), `ISS-134`
  ([`PLAN-084`](PLAN-084-every-file-written-whole-goes-through-its-tmp.md)), `ISS-144`, `ISS-166`, `ISS-125`

## 1. The cause

A table file names its pages. Every read walked them and skipped a page whose file wasn't there (`FileExists ...
continue`, nine places), and every read that took row codes from an index dropped a code whose row it didn't find
(six places). So a table read as fewer rows than it holds, fewer than `Count` said, and a lookup by key found nothing,
with no error.

The skips were there because XOD's own writes could leave such a table:

- **A statement wrote its table before its new page.** Each writer (a statement's end, a batch's or a commit's, a
  buffer's flush) wrote the document files, tables among them, then the raw pages. A crash between the two left the table
  naming a page that was never written (seen with `ISS-134`'s crash test: the table named the page, the page wasn't
  there).
- **A page a delete emptied, and a dropped table's pages, were deleted at once**, outside a transaction, and the table
  that stopped naming them was written later, at the statement's end or the buffer's flush. In buffered mode that could
  be much later, and a crash before it left the table naming deleted pages.
- **A failed statement's undo put its files back in no particular order**, so a crash during it could leave a table
  naming a page the undo had deleted already, or not put back yet.

So: **nothing kept the order between a table and its pages, and the reads compensated by skipping. The change makes
the order an invariant of the one writer: a page reaches the disk before any table on disk names it, and leaves only
after the last table naming it is written without it. With that held, a missing page or row is damage, and a read
says so.**

## 2. The change

- **One order for every writer** (`IOService.WriteInOrderUnlocked`, used by `WriteSaved`, `FlushPending` for a batch's
  end, a commit and a buffer's flush): pages first, then the tables and the other files, then the deletes, tables before
  pages. A crash between two writes leaves at most a page no table names, which no read reaches.
- **Every data-file delete waits for the writes it goes with**, as a transaction's already did (`ISS-166`): a delete made
  while a batch is open waits for that batch's end (forgotten by a rollback), any other for the buffer's flush or the
  statement's end. The file is gone for the context meanwhile (`DeletedHere`). A delete is made only once every write
  has succeeded; when one fails it waits with them, or is forgotten with them. A file deleted and then made again under
  the same name (a table dropped and made anew) is written, not deleted after. An index file is still deleted at once:
  no table names it. A failed batch's deletes wait with its writes in the buffer (`EndBatchUnwritten`).
- **The undo's order** (`StatementSavepoint.Undo`): the pages the statement found first, then the tables and other
  files, then the files it made, pages last; once a file can't be put back, the later steps are left as the statement
  wrote them.
- **One rule for a page a table names** (`IOService.RequirePage`): there, as the context holds it (cached, or on disk
  and not deleted here), or a `DatabaseFileException` naming the table file and the page. It replaces the nine skips:
  `Select`, `Count` (a page whose count isn't recorded), the stored-order walk, `TableRows`, the lookup and the tree by
  example, the next autonumber read from the pages, the statistics, and the index built from the pages.
- **One rule for a row an index names** (`IndexedRawRow`, `IndexedRow`): on its page, or a `DatabaseFileException`
  saying the index file is out of date and how to rebuild it (delete it; it is built from the pages at the next open).
  It replaces the drops in `Query<T>` (both paths), the lookup by example, `GetRow` and `GetTree` by key, and the lookup
  by example when the index covers it. An update or a delete by key on such an index reaches `GetTree` and throws too.
  Not rebuilt silently: an index that is wrong is a fault to see.
- A fault-injection step before each data-file delete, `file-deleting`.

## 3. Tests

`XodMissingPageTests`:

- the disk is checked before every file write and every delete (through `FaultInjection.InProcess`), and at the end:
  no table on disk names a page that isn't there. Direct statements (a first row, new pages, a delete that empties a page,
  `DeleteWhere`, a drop); a batch and a cascade (`InsertRange`, `DeleteWhere`, a parent with forty children, its delete,
  `DropAll`); buffered writes and their flush; a transaction's commit. **Red before** (4 of 4: the table named the new
  page before the page's write);
- a table dropped and made again inside a transaction, and in the buffer: its file is never deleted after its write;
- a statement that fails after emptying a page leaves the page and its rows;
- a page file deleted outside XOD: `Select`, a query, a lookup by example, the statistics, `Count`, the next autonumber,
  a lookup and an update by key without an index, and the index built from the pages each throw a
  `DatabaseFileException` naming the page. **Red before**;
- a row taken off its page behind the index: a lookup by key, a query, a lookup by example, an update and a delete each
  throw; with the index file deleted, it is built again and the row is gone. **Red before**.

`XodStatementUndoOrderTests`: an undo of a statement that made a page, deleted another and rewrote the table, checked
before each file it puts back: **red against the unordered undo**.

`XodStatementUndoTests.A_buffered_context_whose_undo_failed_closes_without_writing` made the buffered undo fail by
holding open a page the undo deleted on disk. A buffered undo no longer touches the disk (the delete waits for the
flush), so it now fails at its own step, `undo-statement`, through the in-process hook. The tests that set that hook run
one at a time (`InProcessHookCollection`).

Breaks, each caught: the writes in the old order (4 tests); deletes not deferred (4); tables deleted after pages (3); a
file made again deleted (1); a written file not cancelling its delete (2); a missing page skipped (4); an index row
dropped (1); the undo unordered (1); the undo going on after a failure (1).

## 4. Review

An independent review (the code and the problem, not this plan), with probes against this tree and against HEAD:

- **A regression in the first version, fixed.** A file was taken as "made again", and its delete skipped, when it was
  merely held in the cache. An insert makes its table in memory before its row is built, so `Drop<T>()` followed by an
  insert of `T` that failed (a child refused), then a flush or a commit, kept the old table while its pages went: the
  reopened table named deleted pages. HEAD didn't do this. Now only a write counts: a file written cancels the delete
  waiting for it, in either set, and a delete of a file still waiting for another writer waits too (made after it if
  that write is discarded). `A_drop_followed_by_a_failed_insert_deletes_the_table_with_its_pages` (buffered and in a
  transaction) was red, and `A_table_dropped_in_the_buffer_and_written_again_by_a_batch_stays` guards the case the
  cache check had covered.
- **The undo, fixed.** It went on after a file it couldn't put back, so a table could be put back after the page it names
  had failed. It now stops before the next step (`XodStatementUndoOrderTests`, red without it).
- **Claims, corrected.** The comments said a missing page could only be lost outside XOD and that no table could name a
  missing page; the stale-index message said the index is rebuilt at the next open (it is rebuilt when next needed). A
  context that holds nothing and reads beside a writer (`ISS-173`) can see a table change under it: the message now
  says so instead of calling the database damaged.
- `NothingPending` counts the deletes still waiting; the unused `IOService.GetItemPage`, which still skipped a missing
  page, is removed.
- **Filed:** `ISS-209` (a crash between a new page's write and its table's leaves a page no table names; nothing removes
  it), `ISS-210` (a relation's stored row code that finds no row reads as no row: the same silent drop for relations). Both were taken up in [`PLAN-088`](PLAN-088-a-page-no-table-names-is-deleted-at-the-open.md): `ISS-209` resolved; `ISS-210` closed, since XOD makes such codes itself and leaving them out is the right answer.
- **Unconfirmed:** the Windows delete behaviour the writer's comment cites (`DeleteFileW`) was kept from the earlier code
  and not checked against the documentation in this task.

## 5. Verdict

Clean, with named debt (`ISS-209`, `ISS-210`). The order is held by the one writer and the undo, the reads check it in
one place each, and the compensations are gone.
