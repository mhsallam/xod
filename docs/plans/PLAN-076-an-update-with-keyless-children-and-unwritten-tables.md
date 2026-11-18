# PLAN-076: an update of a parent with keyless children doesn't throw, and a table not written yet is seen

- **Task ID**: `TASK-091`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-179`, `ISS-192`
- **Related**: [`VERIFY-2026-11-18`](../VERIFY-2026-11-18-open-issues.md), [`PLAN-074`](PLAN-074-a-row-is-deleted-by-its-code.md)
  (an update deletes the entries it drops by their codes), `ISS-187`, `ISS-189`, `ISS-190`, `ISS-191`, `ISS-171`

## 1. The cause

An update of a parent writes the parent's row again (`Write`). For a list of children the write decides per child
type: a child with a key is linked to its stored row (`ParseChildren`, `SetChildItem`), and a child without one is
written as a new row holding its values. The entries the new list drops are then deleted by their codes. After that
the update descended into every child of a changed list with `Update(childType, child)`, an update by key. A child
without a key can't be updated by key, so every update by key of a parent whose list held one threw
`MissingPrimaryKeyValueException`, and the statement was undone: for a plain list and for a `[Children]` list. An
update by example (`Update(item, item)`) didn't throw only because it passes the same list object as old and new, and
the descent skips an unchanged list.

So: **an update of a parent threw for a list of keyless children because it updated every child by key after the
write, and a child without a key can't be updated by key and needs no update: the write made it a new row holding its
values. The change makes the descent skip a list whose child type has no key, by the rule a plain list links by.**

The review found a second defect the throw had hidden (`ISS-192`): in buffered mode, before a flush, the update kept
every row its new list dropped. Each dropped entry's type is found from its item code (`IOService.GetPageType`), which
listed the table files on disk; a table not flushed yet has none, so the type was null and the entry skipped. A cascade
through an `object` property or a `List<object>` finds its rows' type the same way and kept them too, and so did both in
a transaction that made the tables, as its files are written at the commit. The context's own list of its tables,
written or not, is `IOService.TableFileNames`; `GetPageType` now looks there. The review of that change found a third
copy of the disk listing, `RegisteredTypes`, which `DropAll` drops by: it kept a table not written yet. It looks there
too.

## 2. The change

- **`ListLinksStoredRows(childType)`** (`XmlEngine.Relations.cs`): a list's child type has a key. A plain list links a
  child to its stored row by it (`ParseChildren`), and the update descends by it. A `[Children]` list decides per child
  by its key values (`SetChildItem`), which comes to the same for a type without a key.
- **`UpdateRows`** descends into a changed list only when its child type has a key; a keyed child the write created is
  updated by key too, as before.
- **`IOService.GetPageType`** and **`XmlEngine.RegisteredTypes`** take the tables from `TableFileNames()`, the tables
  this context sees, those not written yet included. `GetStatistics` keeps its own listing by design: it flushes first
  and measures the files on disk.

## 3. Tests

`XodUpdateChildrenTests`, with and without indexing, live and after a reopen:
- a parent read with its plain list of keyless kids, a kid changed and one added, updated by key: the list is the new
  one, the dropped kid's row is gone, the other parents' equal kids stay: **red before**
  (`MissingPrimaryKeyValueException`);
- the same for a `[Children]` list of notes with a parent key: each note keeps its parent key: **red before**;
- a guard: a list of keyed parts, one relabelled and one added: the relabelled part keeps its row with its new label,
  the new part gets a row. It passed before too;
- in buffered mode, before a flush: an update that drops a kid deletes it, with its trigger, and a cascade through a
  `List<object>` deletes its rows; after the flush and a reopen too: **red before** (no row deleted);
- the same in a transaction that made the tables, committed: red with the old lookup (break G1);
- `DropAll` drops a table not written yet, before a flush and in a transaction: **red before**.

`XodCascadeDeleteTests`: an update that drops a kid deletes only that row, other parents' equal kids stay (held back
from [`PLAN-074`](PLAN-074-a-row-is-deleted-by-its-code.md), which it couldn't reach): **red before**.

## 4. Breaks

| Break | Caught by |
| :--- | :--- |
| L1 the update descends into every child (the old code) | 6 (the keyless tests and the drop test, both index modes) |
| L2 the update descends into no child | 2 (the keyed guard) |
| L3 the rule says no list links a stored row | 6 (the keyed guard; the cascade cycle and the row a cascade took, whose keyed rows were written again) |
| G1 a page's table found among the files on disk only | 6 (the buffered tests and the transaction test, both index modes) |
| G2 the registered types from the files on disk only | 2 (`DropAll`, before a flush and in a transaction) |

## 5. Found while probing

With the old descent put back, both behave the same, so neither comes from this change:
- **`ISS-189`**: a keyed child whose values alone changed isn't written by its parent's update: the parent's row holds
  only the child's code and key, so it doesn't change, and nothing descends. With an entry added beside it, the same
  child's new values are written.
- **`ISS-190`**: a parent whose `List<object>` holds an item with a key can't be updated: the write creates the item
  again (`ParseChildren` decides by the declared type, `object`, which has no key) and throws
  `ReservedPrimaryKeyException`.

## 6. Review

An independent review with probes, in every format, with a password, in a committed and a rolled-back transaction,
with an `UpdateFilter`, arrays, `InsertOrUpdate` and an update by example: the throw is removed at its cause (every
keyless case threw before and is right after, after a reopen too). It found the buffered defect above (`ISS-192`,
fixed here), and paths the throw had kept closed, now reached:

- **A keyless child with a plain keyless list of its own leaves its old grandchildren at every update** (2, 4, 6 rows,
  the parent still showing 2): the dropped child is deleted by its code, and the delete's cascade follows only
  `Cascade = Delete` properties. The update's rule and the delete's disagree: `ISS-187`, widened. With
  `Cascade = Delete` on the inner list nothing is left.
- **Every update writes every keyless child again** (`ISS-191`): an `Insert` and a `Delete` trigger per child even
  when nothing changed; a trigger that refuses a child's `Insert` leaves the child out, its old row deleted, and one
  that refuses the `Delete` leaves the old row; a keyless child with a `[UniqueKey]` throws
  `ReservedUniqueKeyException`, the copy written before the old row is deleted (that one threw before too).
- Its comments claimed more than the code (a "linked row only", "the one rule for both"): corrected.
- Unchanged by this change: `ISS-188` (a composite key with a part at `0`, rewritten through the descent's update by
  example), `ISS-190`, `ISS-187` (a plain keyless list left when its parent is deleted).
- `ISS-171`'s keyless child is reachable now; its note is corrected, the 1.x digest not probed.

A second review, of the lookup's change alone: the cause is removed (a table on disk was already read through the
cache, so only a table file not written yet was missed); a table rolled back or dropped is gone from the list; the
locks nest safely. It found `RegisteredTypes` (fixed above) and, already there and unchanged: two tables whose names
differ only by case, a file of another program named `*.xtab` (its parse throws), and the cost of the lookup, which
opens every table per dropped entry (3,000 dropped kids took about 0.4 s either way); a map from page to table owned by
`IOService` would be its structural fix. The `RegisteredTypes` change took the owner's list the review had checked; it
wasn't reviewed again.

## 7. Verdict

**Clean with named debt.** An update of a parent whose list holds keyless children works, and the rows it drops are
deleted before a flush and in a transaction; `DropAll` drops a table not written yet. The debt: the descent still
guesses what the write did and updates keyed children again by example (`ISS-188`, `ISS-189`); an update writes every
keyless child again (`ISS-191`), and its rule for dropped entries isn't the cascade's (`ISS-187`, now reached by nested
keyless lists at every update); the lookup's cost per dropped entry.

## 8. How it was checked

- The tests red before; the breaks.
- The review, and its probes.
- The gate (`node tools/gate.mjs`), and the whole suite on Linux.
