# PLAN-080: what a parent's update writes into the rows it names

- **Task ID**: `TASK-095`
- **Status**: Done (2026-11-18)
- **Issues**: `ISS-189`, `ISS-181`
- **Related**: [`PLAN-077`](PLAN-077-one-rule-for-the-rows-a-row-owns.md) (the rows a row owns),
  [`PLAN-076`](PLAN-076-an-update-with-keyless-children-and-unwritten-tables.md), `ISS-157` (the site's cascade text)

## 1. The cause

After writing a parent's row, the update descended into its relations, by a rule of its own:

- only when the parent's own row changed, so an edit to a related object alone wasn't written (`ISS-189`);
- into every list of keyed items, writing each item's values whatever the relation declared;
- for a single reference that changed, it called `UpdateItem(type, oldRef, newRef)`: it took the change as an edit of
  the row the old reference named and rewrote that row as the new object, which threw `ReservedPrimaryKeyException`
  when the new object was another row (`ISS-181`).

`CascadeOptions.Update` was never read, though the docs say it adds updates. A cycle of relations was stopped only
after a row had been written twice: the check compared the caller's track with its ancestors, not the row about to be
written, so the second copy of a row, read with the first, overwrote the first's new values.

So: **an update wrote related rows by where it happened to descend, not by what the relation declares. The change
makes one function, `UpdateRelatedRows`, write the rows a parent owns or cascades to, after every write of the
parent, and nothing else.**

## 2. The change

By what each relation declares, the rule an ORM's cascade follows. (A database's `ON UPDATE CASCADE` carries a
changed key to the rows that name it; it never writes their other values.)

- **A parent owns its children** (`[Children]`): its update writes each child's values.
- **A relation that declares `Cascade = Update`** (or `All`) writes its rows' values too, single or a list.
- **Any other reference** only links or repoints to its rows, which the parent's own write does, and never writes
  their values: a copy the parent holds may be older than the row.
- **A row of a type without a key** is written by the parent's write, as before (`ISS-179`, `ISS-191`).
- **A row is written once per update** (`WrittenByThisUpdate`): the update keeps a record of the rows it wrote, by
  type and key, before and after. A row reached again, through a cycle, by a second path or as a second copy in a
  list, is skipped before its trigger fires, so it is written from the first copy reached. The first version walked
  the track's ancestors, which only a cycle passes through again: the review found a row reached by two paths written
  twice, its edit undone by the older copy. An update begun inside a trigger keeps a record of its own.

Behaviour that changes, by design: a plain list of keyed objects no longer has their values written by its parent's
update. Declare `[Property(Cascade = CascadeOptions.Update)]` on the list to keep that, or update the objects
themselves. An insert links a keyed row that exists and never writes its values, whatever the relation declares, as
before.

## 3. Tests

`XodRelatedRowsUpdateTests`, with and without indexing:

- a reference pointed at another row, and at a new object: **red before** (`ReservedPrimaryKeyException`);
- a stale copy held by a plain reference and a plain list doesn't overwrite a row renamed meanwhile: **red before**;
- a relation that cascades updates writes its rows when only they changed: **red before** (`ISS-189`);
- a cycle of two rows that cascade updates to each other ends, both written once: **red before**;
- a row reached by two paths and as two copies in a list, the later copies older: written once, with the edit:
  **red against the first version** (the ancestor walk);
- a guard: a `[Children]` list's child edited alone is written. It was before too: the parent's row compared as
  changed for a `[Children]` list, so the old descent ran.

`XodUpdateChildrenTests`'s guard for a keyed list's values now declares `Cascade = Update`.

Each rule broken once and caught: children not written, the cascade ignored, every reference written, the walk run
only when the row changed, a row written twice in a cycle, the ancestor walk in place of the record.

An independent review found the cause removed and no second copy on the update path. With indexing on and off, an
update of a parent with 400 children took no longer than before: every `[Children]` child was written before too.

## 4. Left open

- `ISS-200`: `Cascade = Update` declared on a relation to the row's own type by a foreign key (`SelfForeign`) is
  not followed (by reading).
