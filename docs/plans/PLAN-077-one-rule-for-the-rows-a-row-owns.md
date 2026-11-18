# PLAN-077: one rule for the rows a row owns

- **Task ID**: `TASK-092`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-187`
- **Related**: [`PLAN-074`](PLAN-074-a-row-is-deleted-by-its-code.md) (a row is deleted by its code, with one cascade),
  [`PLAN-076`](PLAN-076-an-update-with-keyless-children-and-unwritten-tables.md) (an update with keyless children),
  `ISS-181`, `ISS-190`, `ISS-191`, `ISS-193`, `ISS-194`

## 1. The cause

Two rules chose the rows a row owns:

- **The delete's cascade** (`CascadeDelete`) followed each property's `Cascade` flag, and ran only for a type that a
  flag set at load (`HasCascadeDeleteOrParentProperties`) marked as having a cascading or parent property.
- **The update** deleted the entries of a list that its new row dropped, by what the row stored: `complex` and
  `children` entries always, `reference` entries when the property cascades. It looked at lists alone.

They disagreed:

| What the row holds | The delete | The update |
| :--- | :--- | :--- |
| A single object without a key | Deleted its row | Kept the row it replaced or cleared: a new row at every update (3 rows, then 4, 5) |
| A cascading reference set to null | Deleted its row | Kept it, and the parent's delete could no longer reach it |
| A plain list of objects without a key | Kept the rows: the property doesn't cascade, and the type wasn't flagged | Deleted the dropped entries |
| A keyless child's own keyless list | Kept, when the update deleted the child | (the child is written again at every update, `ISS-191`): 2 rows left at each update |
| A `[Children]` list given `Cascade = CascadeOptions.Update` | Kept the children: the attribute replaced the default `Delete` | Deleted the dropped children |

The docs say children always go with their parent, whatever the cascade adds.

So: **rows were left because the delete and the update each had a rule of their own for the rows a row owns, and
each missed what the other covered. The change makes one function, `OwnedRows`, decide for a single entry and a list
alike, and both the delete's cascade and the update's drop take their rows from it; the cascade runs for every type
with a relation.**

The review of the first version found that neither input it trusted means "owned" (section 6): a stored `complex`
entry also marks a keyed row another row may link, and every property of a type without a key, `object` included, had
an implicit `Cascade = Delete`. So `OwnedRows` decides by the type of the row named, and the implicit cascade is gone.

## 2. The change

- **`OwnedRows(type, entity)`** (`XmlEngine.Mutations.cs`): of the rows a row's relations name, it owns a row of a
  type without a key (its write created that row for it alone, and nothing can link it by key), a child
  (`refType="children"`), and a row of a type with a key when the property declares `Cascade = Delete`. A single entry
  and a list entry alike. Each row's type is the property's declared type, or for an `object` the one its page holds;
  the update looked every dropped entry's type up by opening every table (`IOService.GetItemType`), though the
  property declares it.
- **`CascadeDelete`** deletes each owned row by its code, with its own trigger, as before.
- **`UpdateRows`** deletes the rows the old row owned and the new one doesn't name, by code.
- **The cascade's gate** is every type with a relation (`!IsStandalone`); the flag
  `HasCascadeDeleteOrParentProperties` is gone.
- **`PropertyService`** no longer gives a property whose type has no key an implicit `Cascade = Delete`: the row
  such a property names is owned by its type, and an `object` property may hold a keyed row other rows link.

Behaviour that changes, by design:

- deleting a parent deletes its plain list of objects without a key, with their `Delete` triggers;
- a `[Children]` list goes with its parent whatever its `Cascade` says;
- an update that replaces or clears an object without a key, or clears a reference that declares
  `Cascade = Delete`, deletes the old row, with its trigger: a declared cascade acts on an update too;
- an `object` property no longer takes a keyed row with it, on a delete or an update, unless it declares
  `Cascade = Delete`.

## 3. Tests

`XodOwnedRowsTests`, with and without indexing, live and after a reopen:

- an update that changes a single keyless object, one that changes only the title, and one by example: one row per
  event, the other event's equal object kept: **red before** (4 rows);
- an update that clears a single keyless object and a cascading reference: their rows deleted: **red before**;
- deleting a parent deletes its keyless list, another parent's equal kid kept: **red before**;
- a keyless child's own keyless list: no grain left after three updates, none after the delete: **red before**;
- a `[Children]` list of keyed notes with `Cascade = Update`: deleted with its parent: **red before**;
- guards: a plain list of keyed parts and a keyed reference without a cascade outlive their parent and the update
  that drops them; an update keeps the keyed children and the cascading reference its new row still names. They passed
  before too;
- from the review, each **red against the first version**: an `object` property cleared or deleted keeps the keyed
  row other rows link, and owns a keyless one; deleting a `List<object>` keeps its keyed item and deletes its keyless
  one; a keyed reference stored as 1.x wrote it (`refType="complex"`) is kept by a delete and an update while another
  row links it.

## 4. Breaks

| Break | Caught by |
| :--- | :--- |
| B1 a single entry owns nothing | 10 (the single object replaced, cleared, and through an `object` property; the cascade of a single reference) |
| B2 a row of a type without a key isn't owned | 24 (every keyless case, update and delete, and the earlier tests of dropped kids) |
| B3 children owned only when their property cascades | 2 (`[Children]` with `Cascade = Update`) |
| B4 every keyed row owned | 9 (the guards, the `object` and `List<object>` cases, the 1.x layout, a convention test) |
| B5 the cascade gated by a cascading or parent property, as before | 8 (the keyless lists, nested and of `object`; `[Children]` with `Cascade = Update`) |
| B6 the update deletes every owned row, named again or not | 2 (the kept children and reference) |
| B7 the implicit `Cascade = Delete` of a property whose type has no key put back | 2 (the `object` property) |

## 5. Found while probing

- **`ISS-181`** is wider than a `[ForeignKey]`: a plain keyed reference replaced with a new object throws the same
  way. One that declares `Cascade = Delete` now succeeds, but only because its old row is deleted before the update
  descends into it, so the descent finds nothing to rewrite: an effect of the order, not a fix.

## 6. Review

An independent review with probes of the first version, with and without indexing, after a reopen, in buffered mode,
in a committed and a rolled-back transaction, with an `UpdateFilter`, `InsertOrUpdate`, an update by example and
triggers. It found that the five reported cases were fixed, and that the change could delete a row another row still
names, in three ways the base didn't:

- **an `object` property cleared or repointed by an update deleted the keyed row it linked**, which other rows still
  named: the property had an implicit `Cascade = Delete`. If that row was the last on its page, a row still naming it
  became unreadable. `Delete` of the holder did the same before the change;
- **a `List<object>` holding a keyed item** stores it as written for the row alone (`ISS-190`), so deleting the list's
  row deleted it;
- **a single keyed object XOD 1.x wrote before its key was set** is stored the same way, so a delete or an update of
  its row deleted it while another row linked it.

All three are fixed (sections 1 and 2) and tested. Its other findings:

- the comments and the tests' summary claimed more than the code: corrected;
- `DropType` deletes a table's pages without a cascade, so dropping a parent type leaves the rows it owns, as before:
  filed as `ISS-193`;
- a keyed child that two parents' `[Children]` lists name is deleted with either parent, as before: filed as
  `ISS-194`;
- a failed update in buffered mode, or in a transaction committed after the failure, has already deleted the rows it
  drops (`ISS-089`): the base left them as orphans, now they are gone; neither leaves a row named that doesn't exist;
- a trigger that refuses an owned row's delete during an update leaves that row unnamed, as `ISS-191` describes; the
  owned rows of a deleted plain keyless list now fire `Delete` triggers;
- an `UpdateFilter` that skips the relation keeps its rows, one that includes it replaces them; `InsertOrUpdate`
  deletes the replaced row; a rollback restores every deleted row.

The change after the review wasn't reviewed again; its tests and breaks cover each of the three findings.

## 7. Verdict

**Clean with named debt.** One rule decides the rows a row owns, for the update and the delete, single entries and
lists; a keyed row is taken only through a child list or a declared cascade. The debt: the write still marks entries
by more than one rule (`ParseChildren` by the declared type, `ParseReference` by the value's), which `OwnedRows` no
longer trusts; `DropType` (`ISS-193`); a child two parents name (`ISS-194`); the repoint of `ISS-181`.

## 8. How it was checked

- The tests red before; the breaks.
- The review, and its probes.
- The gate (`node tools/gate.mjs`), and the whole suite on Linux.
