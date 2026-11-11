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

| Issue | Where |
| :--- | :--- |
| ISS-053 indexed decimal rounding | case `where-033` |
| ISS-056 indexed enum lookup | cases `where-001`, `where-011` |
| ISS-050 constant AES key | `KnownDefectTests` |
| ISS-051 control character in an XML page | `KnownDefectTests` |
| ISS-052 deleted Id reused | `KnownDefectTests` |
| ISS-054 indexed double overflow | `KnownDefectTests` |

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

**New defect: ISS-056.** `Query` on an indexed enum returns no rows. C# compiles `c.Tier == Tier.Gold`
as `(int)c.Tier == 2`. The index planner drops the conversion on the property side and looks up the key
for the number `2`, but the index stores `Gold`.

## 6. Changing it

- **A case:** edit `Cases.cs`, then `dotnet run --project conformance/Xod.Conformance -f net9.0 -- cases`.
  A test fails while `cases/` doesn't match `Cases.cs` and the oracle.
- **The dataset or the file format, on purpose:** `-- data` rewrites the fixtures with the library in
  the tree. Then run `node conformance/format/read.mjs --write` and record why here. Rewriting fixtures
  is the one way to change what every implementation is judged against, so it is never part of a fix.
- **Expected content of the format check:** `--write` rewrites `content.json` from the XML fixture and
  `content-v1.2.5.json`; `--check` then requires the other fixtures to match.

## 7. The gate

`.github/workflows/tests.yml`:
- `dotnet test conformance/Xod.Conformance` on Windows and Linux, .NET 8 and 9;
- `node conformance/format/read.mjs --check` on .NET 9 runners.

Each guard was broken on purpose and seen red on 2026-11-11:
- a hand-edited row in `cases/basic.json` failed the drift test and all four fixture runs;
- a `known` marker on a passing case failed with "looks fixed";
- a one-word change in `format/content.json` failed `--check` for the four 2.0 fixtures.

The CI steps haven't run yet: nothing is pushed.

## 8. Not covered yet

- **Writes** (`PLAN-045` D3): cases come when writes are designed in detail.
- **Parameters** (`@name`, `?`), `EXPLAIN`, `CURRENT_DATE`/`CURRENT_TIMESTAMP`, and `SELECT *`. The order
  of `*`'s columns waits for the manifest.
- **The manifest** (`PLAN-046` section 10): nothing writes it yet. `column-types.json` stands in, and
  goes away when the manifest is built.
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
