# PLAN-074: a row is deleted by its code, with one cascade

- **Task ID**: `TASK-089`
- **Status**: Done (2026-11-18)
- **Issues**: `ISS-130`, `ISS-180`, `ISS-185`, `ISS-186`
- **Related**: [`VERIFY-2026-11-18`](../VERIFY-2026-11-18-open-issues.md), [`PLAN-070`](PLAN-070-delete-where-and-drop-all-are-one-write.md)
  (`DeleteWhere` as one write), `ISS-136` (the walk of every page, which waited for this)

## 1. The cause

A stored row was deleted three ways, and cascaded two:

- **A delete by key** (indexing on) deleted each row by its item code (`DeleteByItemCode`), and its cascade looked only
  for list entries, so a single `[Property(Cascade = Delete)]` reference was left behind, and it fired no trigger for
  the rows it deleted.
- **A delete by a scan** (indexing off, or an example) deleted each row on the page it found it on, read the row's
  object graph, and deleted each cascaded child **by example**: a child without a key equal to another parent's
  child was deleted there too (`ISS-185`).
- **An update** deleted the list entries it dropped by example, though each entry holds the row's code: a parent's
  update writes its keyless children again under new codes, so the example matched the new copies too, and other
  parents' equal children (`ISS-180`: one `Update(parent, parent)` left 2 of 6 children).
- **`DeleteWhere`** deleted each match by example, so the first of several equal rows without a key took them all and
  the rest found none: the count and the triggers were short (`ISS-186`).

So: **rows were lost, or left, because callers that know a row's code deleted it by example, and the cascade had two
copies with two trigger rules. The change deletes every row by its code, through one function, with one cascade.**

## 2. The change

- **`DeleteByItemCode` is the one delete of a stored row**: the rows a key finds, the rows a scan finds (their codes
  taken from `GetTree`), the rows a cascade reaches, an update's dropped entries, `DeleteWhere`'s matches and the
  statement undo. It removes the row and its index keys, records the removal in the table, cascades, and takes the row
  out of its parent's list unless the row goes with its parent. A row that is no longer there is not deleted again and
  fires nothing, as when an earlier cascade of the same statement took it.
- **One cascade (`CascadeDelete`)**: each row a deleted row's `[Property(Cascade = Delete)]` properties name is deleted
  by the code the row stores, a single reference (`<Addr refType="reference">page.row</Addr>`) as well as each entry of
  a list or an array. The row of an `object` property, or of a list or array of `object`, is deleted as the type its
  page holds. An entry written without a code is found by the key it carries. A cascade that comes back to a row it
  has deleted stops there: two rows whose cascades named each other used to recurse until the stack overflowed.
- **One trigger rule for the rows a statement reaches**: each row a cascade or an update's dropped entry deletes fires
  its own `BeforeAction` and `AfterAction`, with the object read from its row, and a `BeforeAction` that refuses keeps
  that row; each `DeleteWhere` match fires its own, given the match. A `Delete` fires one pair for the statement,
  with the caller's item, as before.

### What changes for callers

- A delete by key now deletes a single cascade reference, and fires the triggers of the rows its cascade deletes, as a
  delete found by a scan did.
- The rows a cascade deletes are deleted after their parent's row: a child's trigger no longer finds its parent stored
  (with indexing off it used to).
- `DeleteWhere` returns the number of rows it deleted, and fires one pair of triggers per row, for equal rows too.

## 3. Tests

`XodCascadeDeleteTests`, each with indexing on and off:
- a delete by key deletes the order's cascade reference and its own lines, no other order's, and fires the triggers of
  each row it deletes, the same both ways; the same after a reopen: **red before** (indexing on: no reference deleted,
  one trigger; off: the other orders' equal lines deleted);
- a cascaded row whose trigger refuses is kept: **red before**;
- `Update(parent, parent)` keeps the parent's keyless children and every other parent's: **red before** (6 became 2);
- `DeleteWhere` over 20 equal rows returns 20 and fires 20 pairs of triggers: **red before** (1);
- a row an earlier cascade of the same `DeleteWhere` took is not deleted or triggered again: **red before**;
- a cascade through a `List<object>` and an `object` property deletes each row as its type: its key can be inserted
  again, before and after a reopen, and the trigger names its type: red against the first version of this change;
- a cascade cycle deletes each row once (the old code overflowed the stack).

An update that drops one keyless child, which should delete that row only, can't run until `ISS-179` (an update of a
changed list of keyless children throws first); it lands with that fix.

## 4. Breaks

| Break | Caught by |
| :--- | :--- |
| B0 the whole engine file as it was | 10 (every test of the first version) |
| B1 a single reference isn't cascaded | 2 |
| B2 a cascaded row fires no trigger | 6 |
| B3 a dropped entry deleted by example | 2 |
| B4 `DeleteWhere` deletes by example | 4 |
| B5 a refusing trigger is ignored | 2 |
| B6 a row already gone still fires its trigger | 2 |
| B7 the scan fires each row a second trigger | 1 (indexing off; with indexing on a delete by key never scans) |
| B8 an `object` list's rows deleted as `System.Object` | 2 |

## 5. Review

An independent review, with probes in every storage format (XML, XML with a password, JSON, Binary, Binary with a
password), with indexing on and off: the change removes the four defects, and every delete of a known row goes by its
code. It found a cascade through a `List<object>` or `object[]` deleting each row as `System.Object`, which has no
table: its index keys stayed, so its key couldn't be inserted again, and its table kept an emptied page. Fixed (the
type is the page's when the declared type is `object`), with its test. Also from it:

- **An update chooses the entries it drops by a rule of its own**, not the cascade's: a single keyless class property
  gets a new row at every update and the old one is never deleted; a cascade reference set to null is left; a plain
  keyless list drops its rows on an update but keeps them when its parent is deleted. Filed as `ISS-187`.
- **In buffered mode a child trigger that throws leaves a delete half done**: a delete by key fires the triggers of the
  rows its cascade deletes now, so it meets the buffered mode's missing undo (`ISS-090`), which a delete by a scan
  already met. Direct mode and a transaction take it back.
- **`Delete` fires one pair of triggers per statement**, with the caller's item, however many rows its example
  matches, while every other row fires its own. With `ISS-136`'s walk of every page an example can match more rows,
  still with one pair.
- **`IXodEngine.DeleteWhere`'s default** (an engine of your own that doesn't implement it) deletes each match by
  example, as before: it has no codes. XOD's engines implement it.

## 6. Verdict

**Clean, with named debt.** Every stored row is deleted by its code, through one function, with one cascade; nothing
that knows a row's code deletes it by example. The debt: the update's own rule for what it drops (`ISS-187`), the
buffered mode's undo (`ISS-090`), one trigger per `Delete` statement, the interface's default `DeleteWhere`.

## 7. How it was checked

- The tests red before; the breaks.
- The review, and its probes in every storage format.
- The gate (`node tools/gate.mjs`), and the whole suite on Linux.
