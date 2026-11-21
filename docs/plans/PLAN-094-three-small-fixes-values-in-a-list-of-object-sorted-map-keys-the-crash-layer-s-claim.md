# PLAN-094: three small fixes: values in a list of object, sorted map keys, the crash layer's claim

- **Task ID**: `TASK-110`
- **Status**: Done (2026-11-21)
- **Issues**: `ISS-218`, `ISS-157` (resolved); `ISS-216` (resolved in part); `ISS-219`, `ISS-220` filed; `ISS-197` looked at
  and left open
- **Related**: `ISS-217` (a collection in a list of object), `ISS-213` (the one map maker), `ISS-090` (a statement across
  a crash)

## 1. ISS-218: a list of object holds rows of a class

**The cause.** A list of `object` writes each item as a row of its runtime type, and reads back rows of a class. A string
read back as a bare `object`; a number or a struct as its default. Nothing said what such a list can hold.

**The change.** `ValueHelper.ReadsBackAsListRow` is that rule: a row of a class, not a string, not a value of any value
type, not a collection. `ParseChildren` asks it for every item before any is written, and refuses one that fails, naming
the property (`PropertyService.ObjectListScalarMessage`, or `ObjectCollectionMessage` for a collection).

The first version refused the scalars alone (`IsScalarType`): a `Half`, an `Int128`, a `BigInteger`, an `IntPtr` and a
struct still read back as their default. The review found it.

**Not changed.** A database that already holds such items reads them back as before. An anonymous type, a class, still
fails with a raw `IOException`.

**The tests** (`XodObjectCollectionTests`): a string, a number beside a class item, a `Half`, a `BigInteger`, a struct and
a `KeyValuePair` in a list of object are refused and nothing is written; a class item is still written.

## 2. ISS-216: sorted maps with string keys

**The cause.** A map's comparer isn't stored, and a read made a sorted map with the default comparer, which follows the
culture (`Comparer<string>.Default`, culture-sensitive per the .NET docs for `String.CompareTo`, which name the soft hyphen
as ignored). Two keys it finds equal, written under an ordinal comparer, were one key: a `SortedDictionary` or
`SortedList` kept one, and an `ImmutableSortedDictionary` threw at every read.

**The change.** `SortedDictionary<string, V>`, `SortedList<string, V>` and `SortedList` are made by their constructor
taking a comparer, given `StringComparer.Ordinal` (`ValueHelper.OrdinalSortedMapOf`); `ImmutableSortedDictionary<string, V>`
by `CreateRange(IComparer<TKey>, ...)` with it. Ordinal equality is the finest: keys distinct under any comparer when the
map was written stay distinct. The one maker serves the row reader and SQL.

**What it changes.** These maps read back in ordinal order (`Banana`, `apple`, `cherry`), the same on every machine; they
read back in the reading culture's order before.

The first version made any class with a constructor taking a comparer with the ordinal one, so a user's case-insensitive
map read back case-sensitive. The review found it; a class of the user's keeps its own constructor.

**Still open.** The comparer isn't stored: a `Dictionary<string, V>` written under `OrdinalIgnoreCase` reads back
case-sensitive, and a user's sorted map class keeps its default comparer, which follows the reading culture.

**The tests** (`XodMapInterfaceTests`): the three maps keep `"ab"` and `"a­b"` written under an ordinal comparer,
read under the invariant culture, which finds them equal (the test checks that first); a user's case-insensitive map
keeps its comparer; a sorted map reads back in ordinal order.

## 3. ISS-157: layer 4 says what its test runs

**The cause.** `TEST-PLAN-2.0` layer 4 said a killed process leaves an all-or-nothing database. `XodCrashSafetyTests`
kills a process inside one `InsertRange`, in direct mode with no transaction, whose rows reach the disk only at its end.

**The change.** Layer 4 says that, and names the tests that kill a transaction at its commit (`XodSharedAccessTests`,
`XodMigratorSafetyTests`, `XodCommitFailureTests`). The test's summary and the crash writer's comment said "one
transaction" too; both are corrected. The first rewording claimed a transaction the test doesn't run; the review found it.

## 4. ISS-197: left open

A 1.x database is opened in place, and its database file (`<Database />`) records nothing that says its pages were
checked. Giving a row that shares its code a code of its own needs a one-time pass over every page at an open that holds
the database alone, and a mark in the database file that it is done: a format change, not a small fix. The design is in
its row.

## 5. Filed

- `ISS-219`: an `object` property holding a string or a number makes every read of its row throw, unless
  `[GenericTypeProperty]` names its type.
- `ISS-220`: a `Dictionary<string, object>` holding a number reads it back null, and a `SortedDictionary<object, V>`
  makes every read of its row throw.

Both found by the review, reproduced.

## 6. Verification

- **Red first.** Both first tests failed against the unfixed code: no exception for a string in a list of object, and
  `ImmutableSortedDictionary.CreateRange` throwing on the keys.
- **Breaks, all caught:**

  | Break | Tests red |
  |---|---|
  | A scalar in a list of object written | 1 |
  | Scalars alone refused (the first version) | 1 |
  | Strings let through | 1 |
  | A sorted map made by its parameterless constructor | 1 |
  | An immutable sorted map by the default comparer | 1 |
  | Any class with a comparer constructor made ordinal (the first version) | 2 |
  | No sorted map made ordinal | 2 |

- **An independent review** found the user's map class, the values outside the scalars, the crash layer's new
  mismatch, the read-back order and stale comments. All fixed and tested, or recorded here. It confirmed the .NET claims
  against the docs (`ImmutableSortedDictionary.CreateRange` and its overloads, the `SortedDictionary` constructors,
  `Comparer<T>.Default`, `String.CompareTo`). The culture test fails, rather than skips, on a machine without ICU.
- **The suites:** the whole suite on Windows (.NET 8 and 9) and on Linux (the .NET 9 SDK container); the gate.
