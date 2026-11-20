# PLAN-091: six small fixes: inherited attributes, the no-value rule, the password's owner, a map stored as a list, a self reference's cascade, a char's key

- **Task ID**: `TASK-107`
- **Status**: Done (2026-11-21)
- **Issues**: `ISS-122`, `ISS-199`, `ISS-205`, `ISS-214`, `ISS-200`, `ISS-183` (resolved); `ISS-215` filed
- **Related**: `ISS-188` (a key part at its default is no value), `ISS-152` (the file keys), `ISS-128` (a map declared by
  its interface), `ISS-189` (what an update writes into related rows)

## 1. ISS-122: an override takes its base property's attributes

**The cause.** `PropertyService` read a property's attributes with `PropertyInfo.GetCustomAttributes(true)`, which
ignores `inherit` for a property (the .NET docs for `MemberInfo.GetCustomAttributes(Boolean)`: the parameter "is ignored
for properties and events"). So a `[PrimaryKey]`, `[Property(AutoNumber = true)]`, `[UniqueKey]` or `[NotMapped]`
declared on a base class's `virtual` property did nothing on a derived type that overrides it.

**The change.** `PropertyService.AttributesOf(prop)` is the one reader. It walks from the property to each property it
overrides, and for each attribute type takes the nearest declaration: an override that declares a `[Property]` or a
`[ForeignKey]` of its own replaces the base property's, all of them, and inherits the rest. That is what .NET does for
an attribute that allows one (seen by running it on .NET 9; the docs don't say), made the rule for those that allow
several too. An attribute whose usage says it isn't inherited isn't (none of XOD's).

The first version used `Attribute.GetCustomAttributes(prop, true)`, which returns both levels of an attribute that
allows several. The review found an override declaring another `[ForeignKey]` then had two key maps, and the insert
failed.

**What it changes.** A derived type stores what its base properties declare: an overridden key is the key, an
overridden autonumber is numbered, an overridden `[NotMapped]` property is left out.

**The tests** (`XodInheritedAttributeTests`):
- an overridden key and autonumber, with and without indexing;
- an overridden unique key;
- an overridden `[NotMapped]` property of a type XOD can't store;
- a `[ForeignKey]` declared again on the override;
- an override's own `[ForeignKey]` replacing the base property's, and a further override inheriting the nearest;
- an override's own `[Property]` replacing the base property's.

## 2. ISS-199: one rule for a value that is no value

**The cause.** "Null or its type's default" was written in about ten places:
- the example matcher (`SetsProperty`, with typed fast paths);
- the primary-key and unique checks (by kind);
- `GetPrimaryValues`;
- the relation writers and the reference map;
- `GetPropertyValues`;
- the autonumber (its typed paths and `IsUnset`).

They agreed, but a fix to one wouldn't have reached the others.

**The change.** `ValueHelper.IsNoValue(value, type)` is the rule. `ValueHelper.SetsValue(prop, item)` reads a property
through its typed getter where it has one (no boxing), each typed case that rule for its type. Every place above asks
one of them, and so does the parent key a child is given (the review found it tested `!= null` only). The values a
writer leaves out of a row (`StoredValue`) are another question, and keep their own rule.

**The test** (`XodNoValueRuleTests`): for every typed case and a few others (a nullable `int` and `Guid`, an enum, a
`float`), over each spelling of the default (`0.00m`, `-0.0`, `NaN`, `""`, `" "`), the typed getter gives the rule's
answer. A refactor: the rule was the same before, so nothing was red.

## 3. ISS-205: the password the files are under has one owner

**The cause.** Four objects held it:
- the engine (`XmlEngine.password`);
- the password switch (`PasswordSwitch.Password`, which a change commits);
- the I/O service's password string;
- the I/O service's keys made from that string.

The I/O service's setter assigned its two one after the other. Encryption read the keys; the raw page cache and
serialization read the string. A change copied the switch's value into the engine and the I/O service by hand.

**The change.** The switch is the owner:
- The engine reads it there (the statistics, the AES256 keys).
- The I/O service asks it each time, through `KeysOfPassword`, which makes the keys again when the password is another
  one. The keys made last are reachable only through that class.
- `IOService.Load` takes the context's keys, not a password of its own.

The first version kept the keys in a field of the I/O service. Three reads of that field, made before anything had made
the keys, wrote a new database's files unencrypted. The whole suite caught it.

**The tests.** No wrong state was found before (the change runs under the engine's exclusive lock), so nothing was red.
The password tests (`XodPassword*`, `XodSecurity*`, `XodPropertyCrypto*`, `XodStrayFileTests`) guard it: a break that
keeps the password the I/O service was given fails 27 of them.

## 4. ISS-214: a map property whose value isn't a map

**The cause.** XOD 2.0.x took an `IDictionary<K, V>` property for a list and stored each pair as text
(`collType="generic"`). Since `TASK-106` the property is a map. Both row readers sent that value to the list reader,
which set a list on the map property (`ArgumentException`). SQL read it as an empty map.

**The change.** `StoredValue.IsStoredMap` is the one test of a stored map:
- Both row readers send every value of a map property that isn't null to `ReadMapOfRow`. A value that isn't a map
  throws `DatabaseFileException` naming the row, its type and the property. This includes a value with no mark at all,
  which the review found the readers took for a scalar.
- SQL reads it as NULL with its warning `XQ014`, as any value that isn't its column's type, by the class and by the
  recorded schema.

The message's way out is to delete the row and insert it again. The review found that an update of such a row reads it
too where its type has relations. 2.0.x couldn't read the value either: the pairs' text isn't a form that can be read
back.

**The tests** (`XodMapStoredAsListTests`):
- a row rewritten as 2.0.x stored it, read by the standalone reader and by the general one;
- SQL, by the class and by the recorded schema;
- a value with no mark;
- the row deleted and inserted again, for a type with relations;
- a map stored as a map still reads.

## 5. ISS-200: a self reference follows `Cascade = Update`

**The cause.** `UpdateRelatedRows` skipped a `SelfForeign` reference whatever it declared. `SelfForeign` is a reference
by a foreign key to a type that has a `[ForeignKey]` property of this type: the row's own type, or two types that name
each other. The skip was a rule kept from the descent `TASK-095` replaced. Since `TASK-095` a cycle is stopped by
`WrittenByThisUpdate`, so the skip guarded nothing.

**The change.** A `SelfForeign` reference is a reference like any other: written when it declares `Cascade = Update`.
`Cascade = Delete` on one is still not followed (`OwnedRows` skips it): filed as `ISS-215`, and said at the site.

**The tests** (`XodRelatedRowsUpdateTests`, with and without indexing):
- a manager edited through an employee is written;
- two rows that manage each other are each written once;
- without the cascade, the copy isn't written.

## 6. ISS-183: the Node reader keys a char by its code point

**The cause.** `read.mjs` mapped a manifest's `char` to `text`, and keyed it by its text. The format (`PLAN-046` section
8.3) and the library (`IndexKeys.KindOf`) key it by its code point. No conformance model indexed a char.

**The change.** The reader keeps `char` as its own type:
- Keys v2 key it as an integer: its UTF-16 code point plus 2^63.
- Keys v1 key it by its text. 2.0 did so, as for every type it didn't treat as a number or a date
  (`IndexService.NormalizeIndexValue` at v2.0.0; the review found the v1 path had no rule for it).
- The dataset indexes `Event.Code`, and `column-types.json` gives it as `char`.

The committed 2.0.1 fixtures hold no index on it; the fresh dataset (`--check-fresh`, in the gate) does.

**The test**: `read.mjs --check-fresh` red before (`no key rule for type "char"`), green after with 11 more index keys.
No fixture holds a v1 index of a char, so the v1 rule is checked only by reading 2.0's code.

## 7. Verification

- **Red first.** 13 of the first 20 tests failed against the unfixed code. The others guard what must not change, plus
  the attribute declared twice, which guards the change itself. The conformance check was red too. ISS-199 and ISS-205
  change no behaviour, so nothing could be red.
- **Breaks, all caught:**

  | Break | Tests red |
  |---|---|
  | Attributes read without inheritance | 5 |
  | Both levels of an attribute that allows several | 1 |
  | No walk to the overridden property | 7 |
  | The walk jumps to the root definition | 1 |
  | A typed case disagreeing with the rule (`-0.0`, blank text) | 1 each |
  | The I/O service keeps the password it was given | 27 |
  | Each row reader sending a list to the list reader | 1 each |
  | The standalone reader taking an unmarked value for a scalar | 1 |
  | SQL reading a list as a map (by the class, by the manifest) | 1 each |
  | A self reference left out of the cascade | 4 |
  | A self reference written without `Cascade = Update` | 2 |
  | The Node reader taking a char for text, or keying it by its text | 4 each |

  Not covered by a test: the parent key's `IsNoValue` (a parent can't be written with a key at its default), the v1
  char key, and an attribute that isn't inherited (XOD has none).
- **An independent review** found:
  - an override's own `[ForeignKey]` taken together with the base's;
  - the message's way out impossible for a type with relations;
  - `Cascade = Delete` on `SelfForeign` not followed;
  - a map value with no mark read differently by the library and SQL;
  - the v1 char key;
  - the parent key's `!= null`.

  All fixed and tested, filed (`ISS-215`), or recorded here. It found no path that writes a file unencrypted or under
  the wrong key, and confirmed the .NET claims against the docs. The rule for an attribute that allows several and is
  inherited was confirmed by running it, not from the docs.
- **The suites:** the whole suite on Windows (.NET 9) and on Linux (the .NET 9 SDK container); the gate.
