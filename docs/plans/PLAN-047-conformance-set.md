# PLAN-047: The XOD conformance set

- **Task ID**: `TASK-057` (roadmap step 1, third of three specs - `PLAN-044`)
- **Status**: First version built (2026-11-11), in [`conformance/`](../../conformance/README.md): 139 cases,
  four fixtures, the C# runner, and the independent file reader. It is in the CI gate. It found one new
  defect (ISS-056). G1-G3 decided as recommended (2026-11-11, section 9).
- **Related**: [`PLAN-045`](PLAN-045-query-language-spec.md) (XOD SQL; section 11 outlined this set),
  [`PLAN-046`](PLAN-046-file-format-spec.md) (the file format; section 14 asked for this set)

## 1. What this is

`PLAN-045` principle 3 says C#, Rust and the browser must return the same rows for the same query and
data, and that "the conformance set is the judge". This is that set. It has two halves:

- **Query cases:** a XOD SQL query and its expected columns and rows, or its error code. Every
  implementation of XOD SQL must pass them. No SQL engine exists yet, so today the C# library answers
  each case through its lambda API. That checks the expected answers and the library before any SQL
  exists.
- **File-format check:** an independent reader, written from `PLAN-046`, must read every fixture to the
  same content and find every index equal to its pages.

## 2. Layout

```
conformance/
  data/{xml,json,binary,password}/   the fixtures: one dataset, written by XOD 2.0.1
  data/stored-values/                the stored-values fixture (ISS-060), written by the library in the tree
  cases/<group>.json                 the cases (12 groups), generated from Xod.Conformance/Cases.cs
  format/read.mjs                    the independent reader (Node 18+, standard library only)
  format/content*.json               what each fixture must read to (2.0 fixtures, and the 1.2.5 one)
  format/column-types.json           column types the reader needs until the manifest exists
  Xod.Conformance/                   dataset, cases, runner, known-defect tests (xunit; also `dotnet run`)
```

The fixtures are marked `-text` in `conformance/.gitattributes`, so line-ending conversion can't change
their bytes.

## 3. The dataset

`Xod.Conformance/Dataset.cs` holds fixed values: no clock and no random numbers, so it is the same on
every machine and runtime. It is written four times: XML, JSON, Binary, and XML with the password
`conformance-password`. That's 9 tables and 101 rows, 490 KB in all.

| Table | Rows | What it exercises |
| :--- | :--- | :--- |
| `Customer` | 12 | unique key, indexed enum, nullable int and text, decimal with scale, UTC timestamp, attribute bool, SHA-256 hash, map, list, owned object (`Address`), children |
| `Order` | 18 | indexed text, date, nullable decimal, parent reference, children; three customers have none |
| `OrderLine` | 26 | parent, foreign-key reference to `Product`; every fifth order has no lines |
| `Product` | 15 | inserted out of Id order (stored order ≠ Id order), a NULL in an indexed column, nullable double |
| `Event` | 10 | every scalar type in `PLAN-045` section 4: byte, short, long beyond 2^53 and `long.MinValue`, `ulong.MaxValue`, float32 `0.1`, double `1e30`, char, `[Flags]` enum, Guid, UTC and unspecified timestamps, offsets incl. `+05:30`, date, time, interval with days, nullables, CDATA, an indexed decimal at 9 decimal places |
| `Integration` | 3 | an `AES256` column, one NULL |
| `Shop.Note`, `Archive.Note` | 3 + 2 | two tables with the same short name |

## 4. Cases

### 4.1 The file format

One file per group. Each case:

| Field | Meaning |
| :--- | :--- |
| `id` | stable, `<group>-NNN` |
| `sql` | the query |
| `columns` | name and SQL type of each result column |
| `ordered` | whether row order is part of the answer (only with `ORDER BY`, or stored order per D5) |
| `rows` | the expected rows; values encoded by type (4.2) |
| `error` | instead of columns and rows: the expected code (`PLAN-045` section 9) |
| `requires` | `password`: only the password fixture; `no-password`: only the others |
| `known` | an open issue that makes the C# library fail this case (4.4) |
| `csharp` | `false` when the C# library can't give the SQL answer; the expected rows still stand |
| `note` | what the case pins |

Groups and counts:
- `basic` 10, `where` 36, `null` 9, `order` 9, `aggregate` 13, `join` 15, `function` 18,
  `collection` 6, `type` 7, `name` 4, `crypto` 2, `error` 10.
- 139 in all. 129 expect rows and 10 expect an error.

### 4.2 Values

| SQL type | JSON |
| :--- | :--- |
| `INTEGER` | a number; a string of digits beyond ±2^53, so no JSON reader loses it |
| `DOUBLE` | a number (shortest round-trip); `"NaN"`, `"Infinity"`, `"-Infinity"` as strings |
| `DECIMAL` | a string, with the scale the value has (`"12.50"`); `ulong` above 2^63 is `DECIMAL` |
| `TEXT` | a string; enums as their stored name, `[Flags]` as `"Read, Write"` |
| `BOOLEAN` | `true` / `false` |
| `UUID` | lowercase `8-4-4-4-12` |
| `TIMESTAMP` | the stored round-trip text: `Z` for UTC values, no suffix for unspecified |
| `TIMESTAMPTZ` | round-trip text with its offset |
| `DATE`, `TIME`, `INTERVAL` | `2026-11-11`, `09:30:00.0000000`, `[-][d.]hh:mm:ss[.fffffff]` |
| `MAP` | an object, keys in ordinal order |
| `ARRAY` | an array |
| NULL | `null` |

**Passing a case:**
- An unordered answer compares as a sorted list of rows. An ordered one compares row by row.
- `DECIMAL` values compare by number, so `"0.10"` equals `"0.1"`. The C# runner compares text, which is
  stricter.
- Column names compare as written in the case. A column takes its own name (`Name`, even when the query
  says `name`) or its alias.
- An error case passes when the error code matches.

### 4.3 Where the expected answers come from

Each case is written once, in `Cases.cs`, as SQL beside its meaning in C#: a function over a source of
rows. The same function runs against two sources:

- **The oracle**: the dataset's objects in memory, with LINQ to Objects. It produces the expected rows
  that are written to `cases/`.
- **XOD**: `db.Select<T>()` for whole tables, and `db.Query<T>(predicate)` for filters, so XOD's own
  filtering and indexes answer them.

The C# is written with SQL's meaning, not C#'s:
- NULL never equals anything;
- text compares ordinally;
- NULLs sort last ascending and first descending;
- `COUNT(o.Id)` across joins counts joined rows.

**What this proves, and what it doesn't:**
- It proves the library reads, filters, indexes and loads relationships to the expected answers.
- It doesn't parse SQL. **The SQL text and the C# must mean the same thing, and that is a review
  check:** the two sit side by side in `Cases.cs`. The trickiest expected answers were checked by hand
  on 2026-11-11 (ordinal `DESC` with NULLs, counts across two left joins, decimal averages).

### 4.4 Known defects

A case marked `known` must fail on the C# library. If it passes, the runner fails and asks for the
marker to be removed and the issue closed, so the marker can't outlive the defect. `KnownDefectTests.cs`
does the same for defects a query can't show:

| Issue | Where | Now |
| :--- | :--- | :--- |
| ISS-053 indexed decimal rounding | case `where-033` | fixed; marker removed |
| ISS-056 indexed enum lookup | cases `where-001`, `where-011` | fixed; markers removed |
| ISS-050 constant AES key | `KnownDefectTests` | fixed ([`PLAN-049`](PLAN-049-property-encryption.md)); an ordinary test, with ISS-058 tests over the committed fixtures |
| ISS-051 control character in an XML page | `KnownDefectTests` | fixed; an ordinary test |
| ISS-052 deleted Id reused | `KnownDefectTests` | fixed; an ordinary test |
| ISS-054 indexed double overflow | `KnownDefectTests` | fixed; an ordinary test |

The fixes are in [`PLAN-048`](PLAN-048-format-defect-fixes.md) (2026-11-11, not released). Each marker
reported "looks fixed" first, as designed, and was then removed.

## 5. Results, 2026-11-11

Library 2.0.1 on the `main` working tree, .NET 8 and 9:

| Fixture | Pass | Known failures | Not for the library or this fixture |
| :--- | :--- | :--- | :--- |
| xml | 124 | 3 | 12 |
| json | 124 | 3 | 12 |
| binary | 124 | 3 | 12 |
| password | 125 | 3 | 11 |

"Not for the library" covers the 10 error cases, and `crypto-002`: without the password, SQL must
return NULL, but the C# library decrypts with its built-in key (ISS-050).

**The file reader** reads the four 2.0 fixtures to the same 101 rows: 140 index keys, all matching their
pages. It also reads the 1.2.5 fixture: 260 rows, 310 index keys.

**After the fixes** ([`PLAN-048`](PLAN-048-format-defect-fixes.md), 2026-11-11): no case is marked
known; 10/10 on .NET 8 and 9. The fixtures are unchanged, so they still hold 2.0's v1 index keys, which
the library now rebuilds rather than trusts; the reader checks them as v1.

**New defect: ISS-056.** `Query` on an indexed enum returns no rows. C# compiles `c.Tier == Tier.Gold`
as `(int)c.Tier == 2`. The index planner drops the conversion on the property side and looks up the key
for the number `2`, but the index stores `Gold`.

### 5.1 Through SQL, 2026-11-14

XOD SQL version 1 ([`PLAN-060`](PLAN-060-xod-sql-select-manifest-cli.md)) runs every case's `sql` through
`db.Sql` (`SqlConformanceTests`), comparing the columns, their types and the rows, or the error code:

| Fixture | Typed from the classes (committed 2.0.1 files) | From the manifest alone (written by the tree) |
| :--- | :--- | :--- |
| xml, json, binary, password | 138 pass, 0 fail, 1 not for the fixture | 138 pass, 0 fail, 1 not for the fixture |

The 10 error cases and `crypto-002` now run: SQL reads an encrypted column only with a secret the caller
gave, so its NULL holds without the password. The same tests run the stored-values fixture's forms through
both decoders, with row 2 of each table missing the values a row written before its property existed lacks,
and compare each row with what `Select<T>` reads.

## 6. Changing it

- **A case:** edit `Cases.cs`, then `dotnet run --project conformance/Xod.Conformance -f net9.0 -- cases`.
  A test fails while `cases/` doesn't match `Cases.cs` and the oracle.
- **The dataset or the file format, on purpose:** `-- data` rewrites the fixtures with the library in
  the tree. Then run `node conformance/format/read.mjs --write` and record why here. Rewriting fixtures
  is the one way to change what every implementation is judged against, so it is never part of a fix.
- **Expected content of the format check:** `--write` rewrites `content.json` from the XML fixture and
  `content-v1.2.5.json`; `--check` then requires the other fixtures to match.

### 6.1 Changes made

- **2026-11-12, the stored-values fixture added** (`TASK-063`, [`PLAN-053`](PLAN-053-statement-undo-counters-stored-values-locks.md)):
  ISS-060 gave the format two forms 2.0 never wrote, an element or attribute with no text (an empty string)
  and an element marked `nil="true"` (null), so every reader must read them (PLAN-046 sections 7.2 and 8.3).
  The 2.0 fixtures can't hold them (2.0 wrote neither) and stay as they are. `data/stored-values` holds two
  small tables, one per row writer, written by the library in the tree with `-- stored-values` (its classes
  and rows in `Xod.Conformance/StoredValuesFixture.cs`); `format/content-stored-values.json` is what it must
  read to, and `--check-fresh` checks it as the tree writes it today. The reader's index check now keys an
  element with no text as `""` and a nil element as nothing, as the library does. Each of four breaks of
  the reader (the nil mark ignored, empty text read as missing, the index check skipping `""` or keying a
  nil element) failed `--check` on this fixture.
- **2026-11-13, value collections added to the stored-values fixture** (`TASK-071`,
  [`PLAN-059`](PLAN-059-one-database-per-folder-recorded-format-value-collections.md)): ISS-097 gave a map's
  entry and a list's item the `nil="true"` mark, stored an empty map, and named a nullable item by its
  underlying type (PLAN-046 section 7.2). A third table, `BlankCollections`, holds a map with a null, an empty
  and a set value, lists with a null and an empty item (one of `int?`), and an empty map and list. The
  fixture was written again with `-- stored-values`, so the two older tables' files were written again too
  (their pages have new codes); their expected content didn't change. The reader now reads a marked entry or
  item as null and one with no text as `""` (it read an empty list item as null). Each of two breaks of the
  reader (the mark ignored, an empty item read as missing) failed `--check` on this fixture. Every hold now
  covers the folder too (`xod.lock`, PLAN-046 section 9), so both writers delete the lock files their context
  held once it is closed (`Repo.DropLockFiles`); the `data` writer left the database file's lock file in the
  folder before (the committed fixtures have none).

## 7. The gate

`.github/workflows/tests.yml`:
- `dotnet test conformance/Xod.Conformance` on Windows and Linux, .NET 8 and 9;
- `node conformance/format/read.mjs --check` on .NET 9 runners.
- since [`PLAN-049`](PLAN-049-property-encryption.md): both checks decrypt every AES256 value (2.0's under
  its keys, new ones under the database's key; `Dataset.PropertyKey` protects it when there is no
  password) and compare them with the dataset's;
- since [`PLAN-048`](PLAN-048-format-defect-fixes.md): `-- data` into a scratch folder, then
  `read.mjs --check-fresh`, so the files the tree writes today (keys v2) are read by the independent
  reader too.

Each guard was broken on purpose and seen red on 2026-11-11:
- a hand-edited row in `cases/basic.json` failed the drift test and all four fixture runs;
- a `known` marker on a passing case failed with "looks fixed";
- a one-word change in `format/content.json` failed `--check` for the four 2.0 fixtures.

Since [`PLAN-060`](PLAN-060-xod-sql-select-manifest-cli.md) (2026-11-14), in the same test project, so no new
CI step: `SqlConformanceTests` (section 5.1). `--check-fresh` also checks each fresh table's manifest. A
manifest writer switched off failed `--check-fresh`, and so did a manifest giving one column the wrong type.

The CI steps haven't run yet: nothing is pushed.

## 8. Not covered yet

- **Writes** (`PLAN-045` D3): cases come when writes are designed in detail.
- **Parameters** (`@name`, `?`), `EXPLAIN`, `CURRENT_DATE`/`CURRENT_TIMESTAMP`, and `SELECT *`. The order
  of `*`'s columns waits for the manifest. Since PLAN-060 the C# library's unit tests cover them
  (`tests/Xod.Tests/XodSqlTests.cs`); they aren't cases yet, so a second implementation isn't held to them.
- **The manifest** (`PLAN-046` section 10): written since PLAN-060. `read.mjs --check-fresh` reads each fresh
  table's manifest and fails when it disagrees with `column-types.json`. The committed fixtures were written
  by 2.0.1 and have none, so `column-types.json` stays for them.
- **Local `DateTime`:** its stored offset is the writing machine's, so expected answers would depend on
  where they were produced.
- **Semantics `PLAN-045` leaves open, avoided in the data:** `LENGTH` of text outside the BMP,
  `CAST(DECIMAL AS INTEGER)`, `ROUND` at an exact tie.
- **SQL cases over the 1.2.5 fixture:** the format check reads it; no query cases use it yet.
- **ISS-051:** it can't sit in a fixture, because the page wouldn't read.

## 9. Decisions for the owner

These cases pin three answers `PLAN-045` didn't state. Decided 2026-11-11: the owner accepted all three,
and they are now in `PLAN-045` section 8.

| | Choice | Decided | Why |
| :--- | :--- | :--- | :--- |
| **G1** | `ROUND(x, n)` at a tie | half away from zero (`ROUND(2.5) = 3`) | what PostgreSQL and SQL Server do; C#'s default is banker's rounding |
| **G2** | a `float` (float32) column in SQL | read as float32, then widened: `0.1f` is `0.10000000149011612` and `Ratio = 0.1` matches nothing | what C# returns; parsing `"0.1"` straight to DOUBLE would give answers C# can't match |
| **G3** | a UTC `DateTime` in results | keeps its `Z` (`2025-02-04T09:11:00.0000000Z`) | the stored instant is unambiguous; dropping `Z` would make UTC and unspecified values look alike |


## 10. Next

1. ~~The owner's decisions on G1-G3~~ - all three accepted, 2026-11-11.
2. Step 1 is then complete: language, file format and conformance set. `PLAN-044` step 2 (measure a
   Rust core against the C# one) and step 3 (build SQL in C#) can start; each is judged by this set.
3. Fixes for ISS-050 to ISS-056, in the owner's order. Each fix turns its known marker into an ordinary
   pass.

## Verdict

Built and green in the local gate; CI is wired in but not run, since nothing is pushed. The expected
answers are as good as the C# that defines them: reviewed by hand for the hard cases, and checked
against XOD in four encodings. One new defect was found (ISS-056).
