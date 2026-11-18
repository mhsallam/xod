# PLAN-075: an update or delete by example of a keyless type reaches every page

- **Task ID**: `TASK-090`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-136`, and the third part of `ISS-168` (the same stop)
- **Related**: [`VERIFY-2026-11-18`](../VERIFY-2026-11-18-open-issues.md), [`PLAN-074`](PLAN-074-a-row-is-deleted-by-its-code.md)
  (its precondition), `ISS-188` (what a key with a part at its default names)

## 1. The cause

`Update(item, newItem)` and `Delete(item)` find the rows an example matches with `GetTree`. For a type without a key it
walked the table's pages and stopped at the first page that had a match, and its result (`XRowTree`) held a single page:
the update saved, and the delete removed rows from, that one page. With 60 equal rows over 5 pages, both touched 12
and returned true.

So: **an update or delete by example left matches on other pages because the rows it found could only be on one page.
The change makes the result carry each row with its own page, and the walk of a keyless type go through every page.**

It was held back once (2026-11-18): callers then deleted by example rows whose code they knew, and the first-page stop
was what bounded them. Since [`PLAN-074`](PLAN-074-a-row-is-deleted-by-its-code.md) every such row is deleted by its
code.

## 2. The change

- **`XRowTree`** holds the table and its rows as `XRow`s, each with the page it is on.
- **`GetTree`**, for a type without a key, goes through every page and keeps each page's matches; the key path gives its
  one row the same way.
- **The update by example** works row by row, saving each row's page; **the delete by example** deletes each row by its
  code, taken from its own page.
- **A type with a key keeps the first-page stop, as a mitigation.** An example that sets every key matches one row at
  most, so the stop costs nothing there. But an example with a key part left at its default (a composite key's `B = 0`)
  is matched by its other parts here, while the index looks the key up literally; walking every page made such an
  update with indexing off rewrite every row sharing the other parts (60 rows collapsed to one key, where the first page
  gave 12 and the index 1). Which rows such a key names is `ISS-188`; until it is decided, a keyed type behaves as it did.

## 3. Tests

`XodExampleMatchTests`, with and without indexing, small pages, two groups' rows interleaved over several pages:
- an update by example changes every matching row and no other, live and after a reopen; the rows it doesn't match
  keep their values: **red before** (8 of 40);
- a delete by example removes every matching row and no other, live and after a reopen, and a second delete finds
  none; a later insert works: **red before** (52 rows left of 20).

`XodCascadeDeleteTests`: over many pages, one parent's update by example and one order's cascade delete touch only
their own keyless children, 30 parents' equal children spread over the pages. It passes before this change too: it
guards the reason the fix was held.

## 4. Breaks

| Break | Caught by |
| :--- | :--- |
| K1 the walk stops at the first page with a match | 4 (both tests, both index modes) |
| K2 the update saves only the first match's page | **not caught**: the pages' documents are watched, so a change marks its page pending whether or not it is saved (`IOService.Save` says so) |
| K3 the delete takes every code from the first match's page | 2 (the delete, both modes) |
| K4 an update's dropped entries deleted by example again | 4 (the cascade tests, the one over many pages included) |
| K5 a keyed type walks every page | **not caught by a test**: the right result is `ISS-188`'s decision, so no test pins either; the review's probe measured it (60 rows rewritten, 12 with the stop) |

## 5. Review

An independent review, with probes: for a type without a key the cause is removed, in XML, JSON, Binary, with a
password, buffered, in a committed and a rolled-back transaction; rows that start or stop matching during the update
are fixed when it begins; a self-referencing type whose update deletes rows it matched leaves no ghost index entry or
page. 200 updates or deletes by example over 97 cached pages cost 10 to 25 % more than with the stop. It found the
first version walking every page for a keyed type too, which widened a defect nobody had filed: a composite key with
a part at its default, with indexing off, rewrote every row sharing the other parts (60 of 61 rows, 2 distinct keys
left). The index looks such a key up literally, the example matcher and the primary-key check treat the default part
as unset. Filed as `ISS-188`; the keyed stop kept (section 2). Also from it:

- **Three walks find the rows an example matches**: `SelectItemsByExample` (every page, the index for any property),
  `GetTree` (keys only, no index for another property), `GetRowByExampleObject` (stops at the first match). A keyless
  `Update` or `Delete` by an indexed property still walks every page.
- **In buffered mode** an update by example that fails part way keeps the rows it changed before (`ISS-090`); they can
  now be on any page.
- `Delete` and `Update` fire one pair of triggers per statement, however many rows they reach.

## 6. Verdict

**Mitigation for keyed types (`ISS-188` filed); clean for keyless types.** A keyless update or delete by example
reaches every matching row on every page; a keyed type keeps its first-page stop until `ISS-188` decides what a key
with a part at its default names. The debt: the three walks, the buffered undo (`ISS-090`).

## 7. How it was checked

- The tests red before; the breaks.
- The review, and its probes.
- The gate (`node tools/gate.mjs`), and the whole suite on Linux.
