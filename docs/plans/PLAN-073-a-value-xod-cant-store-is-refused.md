# PLAN-073: a value XOD can't store is refused, not lost

- **Task ID**: `TASK-088`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-142`
- **Related**: [`VERIFY-2026-11-18`](../VERIFY-2026-11-18-open-issues.md), [`PLAN-046`](PLAN-046-file-format-spec.md)
  section 7.5 (the scalars the format stores)

## 1. The cause

`ValueHelper.IsScalarType` ended with "or any value type", so a property of `Int128`, `Half`, `BigInteger`, a tuple or
a struct of the caller's was taken as a scalar: written as its text, then read back as its default, as `ParseScalar`
knows only the scalars of section 7.5. An `nint` is a primitive type, so it reached `Convert.ChangeType`, which threw on
every read.

So: **values were lost because the list of what the format stores had a catch-all, and nothing refused a property
outside it. The change makes the list exactly section 7.5 and refuses any other value type when its class loads.**

## 2. The change

- **`IsScalarType`** is the scalars of section 7.5 and their nullable forms: the primitive types but `nint` and `nuint`,
  enums, `string`, `decimal`, `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`.
- **The writer refuses a value it has no branch for** (`WriteProperty`, the owner of how a value is stored): a value
  type outside the list used to fall through every branch and be written as nothing. It throws now, and the statement
  writes nothing: an `object` property holding such a value is refused there.
- **A class with a stored property of any other value type is refused when it loads** (`PropertyService.LoadType`), with
  an `XodException` naming the property and its type, before anything is written. The check follows the writer's
  branches (`ValueHelper.UnstorableValueType`): a struct outside the list, a collection struct too (`ImmutableArray<T>`,
  `ArraySegment<T>`, which the readers can't make); an array's elements; a map's keys and values, which are written as
  text, read from the `IDictionary<K, V>` it implements, so a subclass of `Dictionary<K, V>` too; a generic collection's
  first type argument, its items. A read-only or `[NotMapped]` property is never stored, so it is left alone. The
  writer and the check share the one test of a map (`ValueHelper.IsMapType`).
- **A class is registered once it has loaded**: one refused part way through its load stayed registered, so the next
  lookup of its table by name (the statistics, a table's type) found it with no properties and went on.
- The manifest's type list (`SqlType.StoredTypeOf`) said such a value was stored by its text; its comment now says the
  last line is for a column a table written before this change may hold, which a class can no longer load.

### What changes for callers

A class that has such a property can't be used until the property is `[NotMapped]`, read-only or of a stored type. Its
values were never read back, so no data that could be read is lost. A database that holds such a table needs the class
changed to read it, and anything that looks its table up by name (`GetStatistics`) is refused until then, as for any
class refused when it loads.

## 3. Tests

`XodStoredValueTests`:
- `Int128`, `Half`, `BigInteger`, a struct, a nullable struct, `nint`, a tuple, a `List<Half>`, a struct array and a
  `Dictionary<string, Int128>`: each refused by its first insert, naming the property, with no file written for its
  table: **red before**, every case;
- after the review, the same for a subclass of `Dictionary<string, struct>`, `ImmutableArray<int>` and
  `ArraySegment<int>`; an `object` property holding an `Int128` refused by the write, with nothing written, and one
  holding a string stored; a read-only `Int128` property left alone; a refused class refused again at the second
  lookup of its table by name (`GetStatistics` twice): each red against the first version of this change;
- a `[NotMapped]` struct is left alone.

## 4. Breaks

| Break | Caught by |
| :--- | :--- |
| F1 no refusal when the class loads | 7 (the collections, the maps, the collection structs, the second lookup; a struct property is refused by the write too) |
| F2 any value type is a scalar again | 16 |
| F3 `nint` is a scalar | 1 |
| F4 a generic collection's items aren't looked into | 1 (the list) |
| F5 the writer drops a value it has no branch for | 1 (the `object` property) |
| F6 a read-only property is refused | 1 (the read-only property) |
| F7 a map isn't looked into | 2 (both maps) |
| F8 a collection struct is looked into, not refused | 2 (`ImmutableArray`, `ArraySegment`) |
| F9 a class is registered before it has loaded | 1 (the second lookup) |

## 5. Review

One review of both changes ([`PLAN-072`](PLAN-072-a-char-reads-back-as-written.md) section 5), with probes. It found the
first version a second copy of the writer's dispatch, which disagreed with it four ways: an `object` property holding a
struct was now dropped where it used to fail loudly; a read-only property, never stored, was refused; a subclass of
`Dictionary<K, struct>` still lost its values; `ImmutableArray<int>` was now taken as a collection, and its default
made the insert and then the close throw. And a class refused part way through its load stayed registered. Fixed as
section 2 says: the writer refuses what it can't store, and the check follows its branches. Also from it:

- **`[NotMapped]` on a base class's property isn't seen on an override**, so the refusal's advice doesn't work there
  unless the override is marked too: the attribute lookup (`ISS-122`), which this makes bite.
- **A collection class that isn't generic itself** (`class PointList : List<PointValue>`) is stored as a class, and
  its items read back empty: filed as `ISS-184`.
- The list of stored types has three copies that must agree: `IsScalarType`, the manifest's types
  (`SqlType.StoredTypeOf`, which agrees) and the refusal's message.

## 6. Verdict

**Clean, with named debt.** The writer refuses what it can't store, and a class is refused before anything of it is
written, by a check that follows the writer's branches. The debt: the attribute lookup on an override (`ISS-122`), a
collection class that isn't generic (`ISS-184`), the three lists that must agree.

## 7. How it was checked

- The tests red before; the breaks.
- The review.
- The gate (`node tools/gate.mjs`), and the whole suite on Linux.
