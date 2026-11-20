# PLAN-092: five small fixes: collections in an object property, fixed maps, a self reference's delete cascade, an undefined format, the index's reverse map

- **Task ID**: `TASK-108`
- **Status**: Done (2026-11-21)
- **Issues**: `ISS-201`, `ISS-213`, `ISS-215` (resolved); `ISS-156`, `ISS-111` (resolved in part); `ISS-216`, `ISS-217`
  filed
- **Related**: `ISS-184` (a declared collection a read can't give back), `ISS-187` (one rule for the rows a row owns),
  `ISS-200` (a self reference follows `Cascade = Update`)

## 1. ISS-201: no collection in an object property

**The cause.** A read takes a stored collection's item type from the property's declared type
(`XmlEngine.StoredItemType`). An `object` property names none, so every collection it held read back null: a list, an
array, a set, and a map, which the writer stored as a list of its pairs. The refusal of `TASK-097` looked at declared
types only.

**The change.** `ValueHelper.CollectionInObject` is the rule: any value that is a collection (not a string or a scalar).
`WriteProperty` refuses it for an `object` property, naming the property and the type held, before anything is written.

The first version refused only what a declared property can't be (a `HashSet<T>`, a subclass of a list). The review
found that a list, an array and a map held by an `object` property read back null too.

**The tests** (`XodObjectCollectionTests`): a set, a list subclass, a list, an array and a map are refused on insert and
nothing is written; an update to such a value is refused and the row is kept; a string and a number are still written.

## 2. ISS-213: fixed maps are read back

**The cause.** A read made a map in two ways: the property's own type, empty, filled as an `IDictionary`; or a
`Dictionary<K, V>` where the property can hold one. `ReadOnlyDictionary<K, V>` and the immutable and frozen
dictionaries fit neither, so since `TASK-106` they were refused when their type loaded.

**The change.** `ValueHelper.MakeMap` is the one maker, for the row reader and for SQL. `MapMakerOf(type)` decides, once
per type, how a map is made, and is the readability rule the class load asks (`UnreadableCollection`):
1. the type itself, made empty and filled;
2. a `Dictionary<K, V>`, where the property can hold one;
3. the type's public constructor whose one parameter takes the entries (`Dictionary<K, V>`, `IDictionary<K, V>`,
   `IReadOnlyDictionary<K, V>` or `IEnumerable<KeyValuePair<K, V>>`): `ReadOnlyDictionary<K, V>`;
4. a factory: `ImmutableDictionary.CreateRange` (also for `IImmutableDictionary<K, V>`),
   `ImmutableSortedDictionary.CreateRange`, `FrozenDictionary.ToFrozenDictionary`.

The first version took any one-parameter constructor that accepts a `Dictionary<K, V>`, so a class whose constructor
takes an `object` (a configuration, say) counted as readable. The review found it.

**Named debt (`ISS-216`).** A map's comparer isn't stored, for any map. `ImmutableSortedDictionary.CreateRange` uses the
default comparer (the .NET docs), which is culture-sensitive for `string`: a map written under an ordinal comparer can
hold two keys the default comparer finds equal, and every read of the row then throws.

**The tests** (`XodMapInterfaceTests`): each fixed map round-trips (`ReadOnlyDictionary`, `ImmutableDictionary`,
`ImmutableSortedDictionary`, `IImmutableDictionary`, `FrozenDictionary`), and SQL reads them into a class; a map class
with only an `int` constructor, and one whose constructor takes an `object`, are refused.

## 3. ISS-215: a self reference follows `Cascade = Delete`

**The cause.** `OwnedRows`, the one rule for the rows a row owns (`ISS-187`), skipped a `SelfForeign` reference whatever
it declared. `SelfForeign` is a reference by a foreign key to the row's own type, or between two types that name each
other.

**The change.** It is a reference like any other: it owns its row when it declares `Cascade = Delete`. A cycle ends at
the row already deleted. `OwnedRows` is also the rule an update takes for the rows it drops, so an update that sets
such a reference to nothing now deletes the row it named, as for a plain `[ForeignKey]` reference.

**A second defect, found by the review.** A reference's local key is written from the object it holds
(`ReferenceMap`), so the key and the stored element name one row. `SelfForeign` was left out: repointed by its key alone
while it held the object read, it stored the new key and the old row's element, and the delete, which follows the
element, took the old row. It now gets the same mapping as `Foreign`: the object's keys are written to the key.

**The tests** (`XodSelfCascadeDeleteTests`, with and without indexing): a chain; a cycle of two; an update that drops
the reference; a reference repointed by its key stores one row, and its delete takes that row; a plain reference keeps
its row.

## 4. ISS-156: an undefined format; no lock files without a database

**The cause.** `StorageSerializerFactory.Get` took any value it didn't know for XML, so `(StorageFormat)7` was recorded
as the database's format and written as XML. And an open refused because there is no database
(`InitialCreate = false`) took the hold first, leaving `xod.lock` and `data.xod.lock` in a folder that holds none.

**The change.**
- `Get` names `Xml` and throws `ArgumentOutOfRangeException` for anything else. The context and the migrator ask it
  before they make or hold anything, so a refused format leaves no folder and no file.
- The engine refuses before the hold when there is no database file, none to create, and nothing a crash left of one
  (`IOService.WriteLeft`, the recovery's own test). Where a crash left something, the open holds and recovers as before.

**What it changes.** An open with `InitialCreate = false` of a database not there yet is refused at once, not after
waiting for another context's hold (`LockRetryTimeoutMs`). A folder this user can't list throws
`UnauthorizedAccessException`, not `DatabaseFileException`.

**Still open in `ISS-156`.** A folder holding only a leftover `data.xod.tmp`: the open holds to recover it, the recovery
deletes it, and the refused open leaves its lock files. The migrator decides whether there is a database by the file
alone, so a database set aside as `.xdel`, which a context recovers, is refused by the migrator. The rest of the row
(the CLI password, the options that do nothing, a hex-looking `[Crypto]` plaintext) is untouched.

**The tests** (`XodApiDefectTests`): the migrator refuses format 7, into a target and in place, and changes nothing; the
CLI's `convert --format 7` returns 1 (`XodCliConvertTests`, .NET 9 only, as the tool is); a context with format 7 throws and leaves no file, and no folder where there was
none; a missing database with `InitialCreate = false` throws `DatabaseFileException` and leaves no file; a database set
aside as `data.xod.xdel` still opens.

## 5. ISS-111: the reverse map; the encrypted snapshot

**The causes.**
- `ForgetTypeUnlocked` forgot a type's trees and kept its entries in the reverse map (`codeToIndexEntries`, which row
  holds which keys), so a later build added them again beside the stale ones.
- `IndexRowElement`, the rebuild from the pages, registered each row in the reverse map before the map was built for the
  type; building it then added the row again. Every row of a rebuild was in the map twice. Found while testing the first.
- The encrypted snapshot writer reset the log but left the file in `undeletedIndexFiles`, so the next flush deleted the
  file it had just written.

**The change.**
- The forget removes the type's entries from the reverse map.
- `IndexRowElement` registers only once the map is built for the type, as every other writer of it does.
- Both snapshot writers end in `SnapshotWrittenUnlocked`: the log starts over, and the file is no longer one to delete.

**The tests** (`XodIndexDebtTests`, at the owner, reading the private map): a row has its two entries after a build,
and again after a forget and a rebuild; a row indexed once the map is built has two. The encrypted snapshot is changed by
reading: no test reached `undeletedIndexFiles` with encryption on through the public API (a held file, a rolled-back
transaction), and the review found no wrong answer it causes. Part (1) of the row is untouched.

## 6. Filed

- `ISS-216`: a map's comparer isn't stored (section 2).
- `ISS-217`: a `List<object>` holding a collection fails with a raw `XmlException` and writes nothing.

## 7. Verification

- **Red first.** 14 of the first 23 tests failed against the unfixed code. The others guard what must not change.
- **Breaks:**

  | Break | Tests red |
  |---|---|
  | A list, an array or a map in an object property written | 3 |
  | No map made by a constructor | caught |
  | No map made by a factory | caught |
  | A constructor taking an `object` counted | 1 |
  | SQL refusing a fixed map | caught |
  | A self reference left out of the delete cascade | caught |
  | A self reference's key not written from its object | 2 |
  | An undefined format taken for XML | caught |
  | The folder made before the format is checked | 1 |
  | A hold taken where there is no database | caught |
  | No hold where only a leftover is | caught |
  | A forgotten type's reverse entries kept | 1 |
  | A rebuild registering before the map is built | caught |
  | A rebuild never registering | caught |
  | The migrator not checking first | **not caught**: its first file's conversion refuses too |

- **An independent review** found the incomplete `object` property fix, the update's delete, the stale element of a
  self reference, the loose constructor, the folder made before the format check, the migrator's own test, the `.tmp`
  case, the untested encrypted snapshot, and stale text (a misplaced summary, a comment naming a removed method, a
  release note). All fixed and tested, filed, or recorded here. It confirmed the .NET claims against the docs
  (`ImmutableDictionary.CreateRange`, `ImmutableSortedDictionary.CreateRange` and its default comparer,
  `FrozenDictionary.ToFrozenDictionary`, `ConditionalWeakTable`).
- **The suites:** the whole suite on Windows (.NET 9) and on Linux (the .NET 9 SDK container); the gate.
