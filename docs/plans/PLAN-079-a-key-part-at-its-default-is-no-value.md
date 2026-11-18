# PLAN-079: a key part at its default is no value

- **Task ID**: `TASK-094`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-188`
- **Related**: [`PLAN-075`](PLAN-075-a-keyless-update-or-delete-reaches-every-page.md),
  [`RELEASE-TRIAGE-2026-11-18`](../RELEASE-TRIAGE-2026-11-18.md) (the decision)

## 1. The cause

A key part at its type's default (`0`, an empty `Guid`, null) meant two things. The index looked it up literally, a
`0` included. The example matcher (`SetsProperty`) and the primary-key check took it as unset. So with a composite key
`(A, B)` and indexing off, `Update(new T { A = 1, B = 0 })` rewrote every row with `A = 1`, where indexing on changed
one; and a row stored with a part at `0` was never kept unique.

Decided 2026-11-18: a key part at its default is no value, the rule every example already takes.

So: **a key named different rows with and without indexing because the index and the example matcher disagreed on
what a default part means. The change applies the matcher's rule everywhere a key is taken: one function,
`KeyPartsSet`, says whether an object sets none, some or all of its key; a new row is checked against it once its key
is final; and one lookup by key, `TryLookupByKey`, answers only for a full key.**

## 2. The change

- **`KeyPartsSet(type, item)`** (`XmlEngine.Validation.cs`): `NoKey`, `None`, `Some` or `All`, by `SetsProperty`.
- **A row is written only with every key part set** (`ValidateUniqueAndPrimaryConstraints`):
  `MissingPrimaryKeyValueException` otherwise. A new row is checked once its key is final (`ValidateNewRow`): after an
  autonumber is given its value, and after the row's write sets the parts its relations give (`[ParentKey]`,
  `[ForeignKey]`). The check ran before that write, so such a row was never kept unique, and the first version of
  this change refused it (found by the review). What the write did for the row's relations is taken back by the
  statement's undo when the check refuses.
- **One lookup by key** (`TryLookupByKey`): it answers only for an example that sets every key part. The update's and
  the delete's fast paths, `GetRow`, `GetTree` and a child's link call it. The delete's fast path looked a part at its
  default up literally, so an example without a key deleted a row stored with a key part at `0`, with indexing on and
  not off.
- **An update by key needs every part** (`Update(item)`).
- **An example that sets some key parts and not others is refused** (`RefusePartKey`): `InsertOrUpdate`, an update by
  example and a delete by example. One that sets no key part names no row of a keyed type, as before.

Behaviour that changes, by design:

- a row whose key isn't an autonumber can't be inserted with a key part at its default (`[PrimaryKey] int Code = 0`
  was stored, and never kept unique). So a key part can't be `0`, `false`, an enum's first member, an empty `Guid`,
  or an empty or null string;
- a row with a key part from a relation is kept unique;
- a row 2.0.x stored with a key part at its default can't be updated or deleted by its key; `DeleteWhere` removes
  it.

## 3. Tests

`XodDefaultKeyPartTests`, with and without indexing:

- an insert with a key part at its default, one of an `InsertRange` included, writes nothing; an autonumber is given
  its value first: **red before**;
- an update, delete or `InsertOrUpdate` by a key with a part at its default, an update by an example with part of a
  key, and an update that writes a part at its default are refused, every row as it was: **red before** (indexing off
  rewrote every row the other part matched);
- a guard: a full key and an example without a key part work as before;
- from the review: a key part from a `[ParentKey]` and from two `[ForeignKey]`s is set before the check, and a second
  row with the same key is refused: **red before** (stored twice), and red against the first version (refused
  outright); a row stored with a key part at `0` is named by no example, with and without indexing: **red before**
  with indexing.

Each rule broken once and caught: the write's check, the update's check, the example's check, a part at its default
counted as set, the check run before the write, and a lookup by key taking a part at its default. An independent
review checked the change.

## 4. Left open

- `ISS-199`: what a default is, for a key part, is written in several places (`SetsProperty`, `GetPrimaryValues`, the
  checks by kind in `ValidateUniqueAndPrimaryConstraints`, the relation writers). They agree today.
