# PLAN-086: an update filter takes a relation and its keys together

- **Task ID**: `TASK-102`
- **Status**: Done (2026-11-20)
- **Issues**: `ISS-207`
- **Related**: [`PLAN-085`](PLAN-085-a-moved-child-leaves-the-parent-its-row-names.md) (where it was filed)

## 1. The cause

A relation is stored twice: as its local key columns (`BoxId`, `AuthorId`) and as the relation's element (a child's
parent element and the parent's list that names the child; a reference's element). Reads and deletes follow one or the
other. An `UpdateFilter` named properties one by one, so a filter that named one of the two wrote it alone:

- **Target the key alone:** the key moved, and the stored parent element and the old parent's list stayed. The child
  was listed by, and deleted with, a parent its key no longer named; a `[ForeignKey]` reference read the old row.
- **Skip the relation, or skip the key:** the other one was written anyway. The row then held a key and a relation that
  disagreed, and the next plain update settled it one way or the other.

So: **the filter decided per property what is one value stored in two places. The change makes a filter that names a
relation or any of its key columns name them all, for Target and for Skip.**

## 2. The change

`XmlEngine.WithRelationKeys(type, filter)`, applied once where every update with a filter enters (`UpdateItem`, the
one update entry; `InsertOrUpdate` reaches it through `Update`): each property with `[ParentKey]` or `[ForeignKey]`
local keys (declared or by convention) forms a group with them, and a filter that touches a group gets the whole group.
Groups that share a key column are joined. The caller's filter is never changed; a filter that touches no relation is
used as given.

## 3. Tests

`XodUpdateFilterRelationTests`, with and without indexing, **red before** (8 of 8):

- a child read on its own, its parent key changed, updated with `Target = [key]`: it leaves the old parent's list, the
  new one lists it, and deleting the old parent leaves it;
- a child with its key and its parent object both changed, updated with `Skip = [Box]` or `Skip = [BoxId]`: both stay
  as stored, and a plain update afterwards changes nothing about the parent;
- a book with `[ForeignKey(nameof(AuthorId))] Author`, `AuthorId` changed and updated with `Target = [AuthorId]`: the
  reference reads the new author.

Added after the review, which found them untested: the relation targeted alone writes its key too (the child moves);
a `[ForeignKey]` reference skipped leaves its key as stored. Red without the fix (4 of 4).

Break: the call to `WithRelationKeys` removed, all 12 fail.

## 4. Review

An independent review (the code and the problem, not this plan): clean. `UpdateItem` is the only update entry, every
public `Update` and `InsertOrUpdate` reaches it, and the filter in `XmlEngine.Examples` is a query's, not an update's.

## 5. Verdict

Clean. The filter's meaning is settled at the one entry; the write paths (`Write`, `ItemForRow`, `KeepLeftOutValues`)
are unchanged and see a filter that names whole relations.
