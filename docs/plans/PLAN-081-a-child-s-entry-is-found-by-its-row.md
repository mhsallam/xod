# PLAN-081: a child's entry in its parent's list is found by its row

- **Task ID**: `TASK-096`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-135`
- **Related**: [`PLAN-079`](PLAN-079-a-key-part-at-its-default-is-no-value.md) (the key check after the write), `TASK-063`,
  `ISS-202` to `ISS-204` (filed)

## 1. The cause

A child inserted on its own (`db.Insert(new Note { Box = box })`) is added to its parent's `[Children]` list. The
write looked for the parent's entry by the child's key values (`XmlEngine.Relations.ParseReference`):

- a child type without a key has no key values, so the first entry matched, and was repointed to the new child: the
  parent listed the new child in place of the first, though both rows existed (`ISS-135`);
- a keyed child whose key was taken matched the listed child's entry and repointed it. Since `PLAN-079` the key check
  runs after the write, so this happened before the check refused the insert; in a transaction or buffered mode the
  statement's undo then took the entry out, and the parent no longer listed its child. This came with `PLAN-079`'s
  commit, not released.

So: **an entry was found by key values, which a child without a key doesn't have and a refused child shares with a
listed one; and the list was asked who owns a key, which is the key check's to say. The change finds a child's entry
by the row's code, and lets a key take over only an entry that names no row.**

## 2. The change

- The entry for a row is the one that names its code.
- When none does, a keyed child may take over an entry with its key that names no row (a child deleted before its
  unlink, which 2.0 and earlier left; `TASK-063`): a labelled cleanup of what earlier versions left. The takeover is in
  the statement's log, so its undo takes it out.
- Otherwise the row gets an entry of its own. Whether its key is taken is the key check's to decide, after the write;
  when it refuses, the statement's undo takes the entry out. The first version left an entry with the key that names
  a row standing for the new one: the review found that an entry keeps the key its child had when listed, so a child
  given a key another had been changed from was left out of its parent's list.

The same change fixes a child without a key updated on its own, which took the first entry the same way, and arrays of
children.

## 3. Tests

`XodChildEntryTests`, with and without indexing:

- two children without a key inserted on their own beside one listed: the parent lists all three, live and after a
  reopen: **red before**;
- a keyed child refused for its key, in a transaction and in buffered mode: the listed child keeps its entry: **red**
  against `PLAN-079`'s commit, passing before it;
- from the review: a child's key changed by an update, then a new child given the old key: both listed: **red**
  against the first version.

Each rule broken once and caught: an entry found by key first, a key taking a live entry, a dangling entry not taken
over (`XodStatementUndoTests`), a live entry with the key standing for the new row. An independent review with
probes; its finding fixed and tested, the rest filed.

## 4. Left open

- `ISS-202`: an update of a child alone takes its old parent from the object it is given, which a child read on its
  own doesn't hold: a child moved to another parent is listed by both, and deleting the old parent deletes it.
- `ISS-203`: a parent with two `[Children]` lists of one type: a child inserted or updated on its own is listed in the
  first.
- `ISS-204`: an entry's key attributes are written when it is listed and never again, and compared by `ToString`,
  which differs from their stored form for a `bool`, a `TimeSpan`, and a number in a culture with a decimal comma.
