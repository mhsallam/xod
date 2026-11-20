# PLAN-090: four small fixes: stray files, maps declared by interface, a snapshot's failure, a `Guid?` key

- **Task ID**: `TASK-106`
- **Status**: Done (2026-11-20)
- **Issues**: `ISS-182`, `ISS-128`, `ISS-198`, `ISS-123` (resolved); `ISS-213`, `ISS-214` filed
- **Related**: `ISS-134`, `ISS-148`, `ISS-153`, `ISS-166` (the recovery of what a crash leaves), `ISS-184` (a
  collection XOD can't read back is refused), `ISS-137` and `ISS-144` (a parallel loop's failure thrown as itself),
  `ISS-117`

## 1. ISS-182: what a crash leaves is the database's by its name

**The cause.** The open recovers what a crash left, and a password change is refused over it. Each kind of leftover
was recognised by its extension alone:

| Leftover | What the open did with a stray naming another database file |
|---|---|
| `<file>.tmp` (`DatabaseFiles.IsOfDatabase` took any `.xod`) | deleted `other.xod.tmp` |
| `<file>.xdel` | put `other.xod.xdel` back as `other.xod`: a second database file, which every later open refused |
| `*.xpwnew` | deleted `other.xod.xpwnew` |
| `*.xpwlog` | replayed a committed `other.xod.xpwlog` into `other.xod` |
| `*.xwal` | read any, then undid or deleted it |

The first version of this change fixed the first two only; the review found the others.

**The change.** `DatabaseFiles` is the one owner of "this database's":
- `IsOfDatabase(file, databaseFile)`: tables, pages and indexes, and a database file when it is there, or when it is
  the path itself (a new database's first write, cut short before the file was there). The file system decides, not a
  comparison of names. A folder holds one database file (`Resolve`, checked again under the hold), so the one there is
  this one, however the path spells it.

  The second review found the first version compared names by a rule guessed from the platform (ignoring case on
  Windows and macOS). Wherever a volume's rule differed (a case-insensitive Linux volume, a Unicode form macOS folds, a
  short name), that would have left the database file out of a password change's or a conversion's files, and missed
  its own leftovers.
- `IsJournal`: `transaction.xwal`, the journal's one name since 2.0.0 (checked at v2.0.0, v2.0.2, v2.0.3, v2.1.0).
- `IsPasswordRecord`: `<database file>.xpwlog`, the one record a switch writes.
- `IsStaged`, `IsAtomicWrite`, `IsSetAside`: `.xpwnew`, `.tmp` and `.xdel` of a file `IsOfDatabase` (or the journal)
  takes. So on Linux a stray `DATA.xod.xdel` beside `data.xod` names no file there, and is left.
- `RequireDatabaseFile`: the recovery and its check take the database's file, and refuse a folder, whose parent they
  would read.
- The recovery lists every file and filters by these, as the check of what is left does. A glob's case rule is the
  platform's, so on Linux the recovery missed a `cut-short.xpag.TMP` that the check reported, and every password change
  was refused.

Every reader of leftovers asks these:
- the open's check for writes left (`IOService.WriteLeft`), which a password change also asks;
- the recovery: `RecoverInterruptedWrites`, `PutBackSetAsideFiles`, `PasswordSwitch.RecoverSwitch`,
  `XodTransaction.RecoverWalJournalIfPresent`;
- the password change's list of files;
- the migrator.

**What is left.** The index markers (`*.xidxdirty`) are still matched by extension. A stray one drops this database's
indexes, which are rebuilt from the pages. If it names pages, the next open's sweep (`ISS-209`) also deletes those that
no table names, as for a marker this database's crash left: a folder mid-copy whose table file hasn't arrived loses
them. That is `ISS-209`'s design, which trusts a dead marker beside the database.

**The tests** (`XodStrayFileTests`):
- Each stray is left alone, and a second open works:
  - a `.tmp`;
  - an `.xdel`;
  - a committed password change's record and staged file;
  - another `.xwal`;
  - on Linux, a case-variant name.
- A password change goes ahead beside a stray `.tmp`, beside another `.xwal`, and after a leftover with its suffix in
  capitals (red on Linux with the glob put back).
- The database's own `.tmp` is still recovered.
- A folder passed for the database's file is refused.
- A password change through a path spelled in another case goes through, and the old password opens nothing. This
  passes here with names compared exactly too: the engine writes the database file another way as well, so no test on
  these file systems shows the case the second review read in the code.

## 2. ISS-128: a map declared by its interface

**The cause.** The map test (`ValueHelper.IsMapType`) took the non-generic `IDictionary` or a `Dictionary<,>`.
`IDictionary<K, V>` and `IReadOnlyDictionary<K, V>` are neither, so a property declared as either counted as a list of
its first type argument. Since `ISS-184` it is refused at registration; before that, each pair was stored as text and a
read threw. Four places looked up a map's key and value types, by `IDictionary<,>` only. The writer read entries through
the non-generic `IDictionary` only. SQL had its own rule for the type to make.

**The change** (`ValueHelper`, each the one owner):
- `IsMapType` also takes a type that is or implements `IDictionary<,>` or `IReadOnlyDictionary<,>`.
- `MapArguments` looks up a map's key and value types, cached per type (weakly, so a type of an assembly that unloads
  isn't held). The row reader, the schema manifest, SQL's
  conversion and the check of what a map can store all ask it.
- `MapToRead` gives the type a read makes:
  - the property's own type, where it can be made as an `IDictionary`;
  - else a `Dictionary<K, V>`, where the property can hold one (an interface);
  - else none.

  The row reader and SQL's conversion both use it. A map type with none is refused at registration
  (`UnreadableCollection`), and the message now says which maps can be read back.
- `MapEntries` reads a value's entries through `IDictionary` where it is one, and otherwise through its generic
  interface's `KeyValuePair`s. The writer and SQL's conversion use it.

**What it changes.** A map class with no parameterless constructor that no `Dictionary<K, V>` fits is now refused when
its type loads: `ReadOnlyDictionary<K, V>`, `ImmutableDictionary<K, V>`, `FrozenDictionary<K, V>`. In 2.1.0 such a
property registered, read back null when it was null, and threw when it held a value. Reading them is filed as
`ISS-213`.

Rows a 2.0.x release wrote for an `IDictionary<K, V>` property are stored as a list, and still don't read back (they
didn't in 2.0.x either): filed as `ISS-214`. 2.1.0 wrote none.

**The tests** (`XodMapInterfaceTests`):
- both interfaces read back as written, a null value included;
- a dictionary that is only an `IReadOnlyDictionary<K, V>` is written as a map;
- SQL reads the column as a map, by the class and by the recorded schema;
- a `ReadOnlyDictionary<K, V>` property is refused when its type loads, and the message says which maps can be read.

## 3. ISS-198: a snapshot's failure thrown as itself

**The cause.** A full index snapshot serialized its indexed properties' blocks with `Parallel.For` when there were
two or more. A failure there came out as an `AggregateException` around it, and with one property as itself. The
caller's handling depended on how many properties a type indexes, as `ISS-137` (writes) and `ISS-144` (reads) found.

**The change.** The blocks are serialized through `ParallelReads.For`: every block runs, and the lowest property's
failure is thrown as itself, as a loop meets it. Nothing is written before the blocks are done. A fault step
(`IndexService.IndexBlockStep`) marks each block.

**The test** (`XodIndexSnapshotFailureTests`): with two indexed properties, a failure in one block reaches the caller
with no `AggregateException` in its chain.

## 4. ISS-123: a `Guid? Id` by convention

**The cause.** A property named `Id`, with no `[PrimaryKey]` in its type, is the key, and an autonumber when it is a
number or a `Guid`. The number test unwraps `Nullable`, but the `Guid` one compared the declared type. So `int? Id` was
an autonumber and `Guid? Id` wasn't, and rows inserted with a null `Id` all kept a null key. A declared
`[Property(AutoNumber = true)] Guid?` already was one. The rule was written in four places.

**The change.** `ValueHelper.IsAutonumberType` (a number or a `Guid`, nullable or not) and `IsGuid` are the one test,
for the convention, the identity settings and the autonumbering. An `[Property(AutoNumber = true)]` on another type is
still refused only at the first insert, as before.

**The test** (`XodNullableGuidIdTests`): two rows inserted with a null `Id` get two new keys, and are found by them.

**What it changes.** A `Guid? Id` left null on insert now gets a new `Guid`, as `int? Id` gets a number. Rows already
stored with a null key keep it. The table's recorded schema gains `autonumber="true"` at its next write.

## 5. Verification

- The tests above, red first against the unfixed code: 8 of the first 9 (the ninth, the database's own `.tmp`, guards
  what must not change). The review's cases were added with the change that fixes them, and each was seen red by
  breaking it.
- Breaks, all caught:

  | Break | Tests red |
  |---|---|
  | A database file by extension alone | 3 |
  | The map test without the generic interfaces | 3 |
  | `IReadOnlyDictionary<,>` not a map interface | 3 |
  | Entries through `IDictionary` only | 1 |
  | `Parallel.For` back | 1 |
  | No unwrap of the key's type | 1 |
  | The password change's files by suffix | 1 |
  | The journal by suffix | 1 |
  | A folder taken for the database file | 1 |
  | Every map class registers | 1 |
  | The recovery's glob put back (on Linux) | 1 |

  Not caught: the database file compared by its name exactly. The engine writes the database file another way as
  well, so on these file systems no test shows the file left out.

- Two independent reviews:
  - the first found the three other leftovers still matched by suffix, the case-variant name on Linux, the
    unrecorded refusal of `ReadOnlyDictionary`-like maps, SQL's own copy of the map rule, the file/folder parameter
    and the four copies of the autonumber rule;
  - the second found the database file compared by a guessed name rule (now decided by the file system), the
    recovery's globs disagreeing with the check on Linux, a last copy of the `Guid` test, the strong cache, and the
    claim about stray markers. All fixed, or recorded here.
- `ParallelWrites` keeps its own rule for writes (`ISS-137`: one failure thrown as itself, several together). It is a
  different rule from `ParallelReads`, on purpose.
- The gate (`node tools/gate.mjs`) green; the whole suite on Linux (the .NET 9 SDK container).
