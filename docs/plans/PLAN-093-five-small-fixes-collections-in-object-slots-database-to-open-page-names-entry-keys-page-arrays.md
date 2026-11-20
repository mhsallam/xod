# PLAN-093: five small fixes: collections in object slots, whether there is a database to open, page names, list entry keys, page arrays

- **Task ID**: `TASK-109`
- **Status**: Done (2026-11-21)
- **Issues**: `ISS-217`, `ISS-204`, `ISS-212` (resolved); `ISS-156`, `ISS-211` (resolved in part); `ISS-218` filed
- **Related**: `ISS-201` (an `object` property holding a collection), `ISS-105` (a cached page's array is a reader's),
  `ISS-209` (a page a crash left unnamed), `ISS-210` (a relation's code that finds no row)

## 1. ISS-217: a collection where an object is held

**The cause.** `ISS-201` refused a collection in an `object` property: no read gives one back from an object. Two more
places hold an object and asked nothing:
- an item of a `List<object>`, written as a row of its own type, which failed with a raw `XmlException` (a generic type's
  name holds a backtick) naming nothing;
- a map's value, stored as text, which read back null (found by the review).

**The change.** Each asks `ValueHelper.CollectionInObject`, the one rule, and refuses with one message
(`PropertyService.ObjectCollectionMessage`) naming the property. The list is checked before any item is written.

**The tests** (`XodObjectCollectionTests`): a set in a `List<object>` is refused naming the property and nothing is
written, the class item beside it neither; a set as a map's value is refused; a class in a `List<object>` is still
written.

**Filed (`ISS-218`).** A string or a number in a `List<object>` reads back as a bare `object`, with no error. Found by the
review, reproduced.

## 2. ISS-156: one answer to whether there is a database to open

**The cause.** Three places decided it:
- the engine, before the hold, by every leftover the recovery looks at (`WriteLeft`), a cut-short `.tmp` included, which
  the recovery only deletes: the open held, recovered, was refused, and left its lock files;
- the migrator, by the database file alone: it refused a database a crash had set aside (`data.xod.xdel`), which a
  context opens and recovers;
- the recovery itself, which decides by the files' contents.

**The change.** `IOService.HoldsDatabase` is the one answer for the context and the migrator: the database file, or what
a crash left that the recovery gives it back from (`DatabaseFiles.GivesBackDatabaseFile`, by the names the recovery
uses): the transaction journal, the database file set aside, or a staged database file beside its record. Not a
cut-short write, a table's or page's leftovers, or a staged file or record alone. The migrator checks the file again
after its recovery under the hold: what was counted may give nothing back.

The first version counted every leftover but cut-short writes. A break of the migrator's second check that no test
caught showed it: a table's file set aside counted as a database.

**Still open.** The check before the hold reads names, not contents: a journal or a record whose contents give nothing
back still leaves the lock files of a refused open, and lock files are never deleted (a deletion races another open on
Unix). A database file set aside under a name other than `data.xod` is refused through its folder (`DatabaseFiles.Resolve`
picks `data.xod`) while its file path opens. The obsolete `XodSecurityService` decides by the file alone.

**The tests** (`XodApiDefectTests`): a folder holding only `data.xod.tmp`, `T.xtab.xdel`, `data.xod.xpwlog` or
`data.xod.xpwnew` is refused with no lock file; the migrator converts a database set aside, refuses a folder holding only
a cut-short write, and refuses a staged file beside a record that names no commit.

## 3. ISS-211: a page name already on disk is drawn again

**The cause.** A page's name is drawn from a counter seeded at random per process: unique within a process, not across
processes, and never checked against the folder. A name another process had drawn made two tables share a page file.

**The change.** `IOService.NewPageFileName`, the one place a page is named, draws again while the name is on disk
(`File.Exists`: a page this context has queued to delete counts, since its delete would take the new page; the review
found the first version skipped those). The draws come from a field a test can replace.

**Still open.** A new row's code isn't checked against its page's rows: the raw insert path formats the row with its
code before it takes the page, so a check belongs to a change of that path. A stale reference's key isn't checked
against its row's either.

**The test** (`XodPageNameTests`, with and without indexing): the first draw names a file already in the folder; the page
takes the second, and the file is left as it was.

## 4. ISS-204: a list entry's key, written as stored

**The cause.** A child's entry in its parent's list carries the child's key as attributes, written by `XAttribute`'s own
conversion (`PT1H` for an hour) and compared with the key's `ToString` (`01:00:00`). For a `TimeSpan`, a `bool` or a
`DateTime` key no entry matched, so a child never took over the entry its key had left, and got a second one beside it.

**The change.** `EntryKeyText` is the key as the row stores it (`ValueHelper.FormatScalar`); the three writers of an entry
use it. `EntryKeyMatches` takes that form or `XAttribute`'s, so the entries an earlier version left, the ones a takeover
is for, still match.

**By design: the staleness.** An update of the child alone doesn't write its entry, so a key changed since the child was
listed isn't there. The key is read only for an entry that names no row, which reads as gone anyway (`ISS-210`); writing
the entry at each key change would cost a parent write for nothing (`XodChildEntryTests` pins that an update of the child
alone leaves it). A reference's and a parent's key attributes are still written in `XAttribute`'s form; nothing compares
them.

**The tests** (`XodChildEntryKeyTests`, with and without indexing): a child dropped with its table and inserted again
takes over its entry, written now or in the earlier form, and the entry holds the stored form.

## 5. ISS-212: a page read takes an array of its size

**The cause.** Every page load took at least 528 KB from a pool. A page in the cache never gives its array back (a reader
may hold it, `ISS-105`), so the pool refilled only from buffers that never entered the cache, and the floor only cost
memory. The pool's flag, `IsPooled`, was kept off a cached page by convention alone.

**The change.** The pool, `IsPooled` and `ReleasePooledBuffer` are gone. A page read from disk takes an array of its own
size. A new page, and a page the raw writer appends to, take a full page's array: the growth step goes to that size at
once (the first version doubled from the page's own size, which moved a page appended row by row four times on its way
to full: found by the review).

**The tests** (`XodPageArrayTests`): a page of five rows read from disk has an array of the file's size; a page appended
to 1500 times moves once.

## 6. Verification

- **Red first.** 10 of the first 12 new tests failed against the unfixed code (the page-name test with its injected seam, added
  first with no other change, so it failed by the shared page); the other two guard what must not change.
- **Breaks, all caught:**

  | Break | Tests red |
  |---|---|
  | A list of object not checked | 1 |
  | A map's value not checked | 1 |
  | A cut-short write taken for a database | 5 |
  | A table's file set aside taken for a database | 1 |
  | A staged file without a record taken for a database | 1 |
  | The migrator deciding by the file alone | 1 |
  | The migrator converting what the recovery gave nothing back from | 1 |
  | A page name on disk taken | 2 |
  | An entry key written by `XAttribute` | 2 |
  | An entry key matched in the stored form only | 2 |
  | A page loaded into a 528 KB floor | 1 |
  | A page grown by doubling from its own size | 1 |

  Not covered by a test: a page name queued for delete (a clash of 1 in 2^36).
- **An independent review** found the map's value, the growth step and its comment, the leftovers whose contents give
  nothing back, the page queued to delete, `Resolve` and `XodSecurityService`, the scalar items of a `List<object>`, and a
  stale test comment. All fixed and tested, filed (`ISS-218`), or recorded here. It confirmed the `XAttribute` conversion
  of a `TimeSpan` by running it (the docs say only "per the W3C specifications"), and that `GC.AllocateUninitializedArray`
  arrays are read only below what was written.
- **The suites:** the whole suite on Windows (.NET 8 and 9) and on Linux (the .NET 9 SDK container); the gate.
