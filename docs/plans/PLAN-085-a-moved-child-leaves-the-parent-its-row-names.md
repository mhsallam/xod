# PLAN-085: a child's parent is the one its row and its list say

- **Task ID**: `TASK-100`
- **Status**: Done (2026-11-18)
- **Issues**: `ISS-202`, `ISS-203`
- **Related**: [`PLAN-081`](PLAN-081-a-child-s-entry-is-found-by-its-row.md) (where both were found), `ISS-089`,
  `ISS-194`, `ISS-207`, `ISS-208` (filed)

## 1. The cause

A child row names its parent (`<Box refType="parent">page.row</Box>`), and its parent lists it. Three decisions about
that relation were made from the wrong source:

- **The old parent of an update was taken from the object the update was given** (`XmlEngine.UpdateRows`). A child read
  on its own holds no parent object, and a child without a key is updated by a replacement that holds the new parent,
  so a child moved to another parent was never taken out of the old one's list: both parents listed it, and deleting
  the old parent deleted it (`ISS-202`).
- **A row took as its parent any row of the parent's type that wrote it** (`ParseReference`). The relation a row was
  written through was a field of the parent's track, set by its children's writes and never reset. So a row the parent
  wrote afterwards through a `Cascade = Update` reference became its child, with its key rewritten; and a child with
  two parent properties of one type got the first one's parent for both, its second key overwritten at insert (found
  by the review).
- **A child written on its own was listed in its parent's first list of its type** (`ISS-203`), whichever parent
  property that list pairs with.

So: **the parent was read from whatever object or track was at hand, not from the stored row and the relation. The
change reads the old parent from the stored row, by the rule the delete already used, and makes a row a parent's child
only through the `[Children]` list it is written through, in the list that pairs with its parent property.**

## 2. The change

- `StoredParentCode(type, row, parentProp)`: the parent a stored child row belongs to: the parent its element names,
  else the one its stored parent key values find. The one rule, for the delete's unlink and the update's.
- An update compares the parent the old row belongs to with the parent the new row belongs to; when they differ, the
  child's entry leaves the old parent's list (`RemoveChildEntry`, the one place an entry leaves a list). The object is
  no longer asked.
- `ReadWriteTrack.Via`: the `[Children]` list of its parent a row is written through, set on the row's own track when it
  is made (`Create` from a children list, `UpdateItem` from `UpdateRelatedRows` for a children list), and null for any
  other relation. It replaces `RootProperty`, a field of the parent's track.
- `ChildParentProperty(list, parentType, childType)`: the parent property a `[Children]` list pairs with (the one it
  names, else the child's first parent property of that type). It answers only for a children list. A row takes the
  parent of its track only for the parent property its list pairs with; a child written on its own is listed in the
  list that pairs with the parent property being written.

An unlink is a change to a row that existed, so a statement that fails after it doesn't put it back in buffered mode,
as for every such change (`ISS-089`).

## 3. Tests

`XodChildMoveTests`, with and without indexing:

- a keyed child read on its own and moved by its parent key, or by its parent object and key: the old parent no longer
  lists it, the new one does, and deleting the old parent leaves it: **red before**;
- a child without a key moved by `Update(old, replacement)`: the same: **red before**;
- a child another parent features through a `Cascade = Update` reference keeps its own parent and its key: **red
  before**, and red against this change's first version;
- a child with two parent properties of one type, inserted on its own and in a list: each parent property keeps its
  key, and each parent lists it in its own list: **red before**;
- a child whose stored row names no parent (its element taken out of the page) leaves the parent its key finds;
- a child updated in place stays listed once.

Six breaks, each caught: the old parent not unlinked; a child unlinked from a parent it keeps; the old parent not found
by its stored key; a parent taken by its type alone; a child listed in its parent's first list of its type; any
relation, not only a children list, making a parent. An independent review with probes found the second and third causes above; it also
found `ISS-207` and `ISS-208`.

## 4. Left open

- `ISS-194`: a parent can list a child whose row names no parent and whose key finds none; the update can't find that
  parent either.
- `ISS-207`: an `UpdateFilter` treats the parent key and the parent relation as two values.
- `ISS-208`: a child already listed by two parents (written before this change) stays so until it moves again.
