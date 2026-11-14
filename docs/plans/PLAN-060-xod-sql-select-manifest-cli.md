# PLAN-060: XOD SQL, version 1 - SELECT in .NET and the CLI, and the schema manifest

- **Task ID**: `TASK-072`
- **Status**: Done (2026-11-14). Ships in 2.0.2 with everything else in the tree. On 2026-11-14 the owner
  had the tree published to GitHub and the site updated; there's no tag and no NuGet package until the owner
  says so.
- **Related**: [`PLAN-044`](PLAN-044-roadmap-sql-analytics-rust-browser.md) step 3 (build the query engine),
  [`PLAN-045`](PLAN-045-query-language-spec.md) (the language, D1-D8),
  [`PLAN-046`](PLAN-046-file-format-spec.md) section 10 (the manifest, E1-E3),
  [`PLAN-047`](PLAN-047-conformance-set.md) (the cases that judge it)

## 1. What the owner asked, and the scope

The owner (2026-11-14), quoting the docs' "no SQL is implemented yet": plan and implement SQL now; once
everything is tested and verified, all the tree's changes ship as 2.0.2, but nothing goes to NuGet until
the owner says so.

**Scope, decided by the owner on 2026-11-14:** `PLAN-045` D3 as planned, SELECT in .NET and the CLI:

- `db.Sql(...)` in the library;
- the schema manifest (`PLAN-046` section 10), written by the library at a type's first write (E2);
- `xod query` in the CLI, which reads tables through their manifest, without the application's classes.

Writes (`INSERT`, `UPDATE`, `DELETE`) are not in this plan. D3 puts them after the manifest has been in a
release.

The language is `PLAN-045` sections 4 to 9 as accepted (D8: sections 5 and 6 as written). The engine is C#,
as `PLAN-045` section 10 says ("C# now; Rust if step 2 wins"). The Rust trial of `PLAN-044` step 2 hasn't
run.

## 2. What is built

### 2.1 The API

```csharp
SqlResult r = db.Sql("SELECT Name, COUNT(o.Id) AS n FROM Customer c LEFT JOIN c.Orders o GROUP BY c.Name");
SqlResult r = db.Sql("SELECT * FROM Product WHERE Category = ?", "kitchen");                  // ? by position
SqlResult r = db.Sql("SELECT * FROM Product WHERE Id = @id", new Dictionary<string, object> { ["id"] = 3 });
IReadOnlyList<Product> p = db.Sql<Product>("SELECT * FROM Product WHERE Price > ?", 20m);    // by column name
```

- `SqlResult`: `Columns` (name and SQL type), `Rows` (`object[]` per row), `Warnings` (code and message).
- Values by SQL type: `INTEGER` `long`, `DOUBLE` `double`, `DECIMAL` `decimal`, `TEXT` `string`, `BOOLEAN`
  `bool`, `UUID` `Guid`, `TIMESTAMP` `DateTime`, `TIMESTAMPTZ` `DateTimeOffset`, `DATE` `DateOnly`, `TIME`
  `TimeOnly`, `INTERVAL` `TimeSpan`, `MAP` a read-only dictionary of text keys, `ARRAY` a read-only list,
  NULL `null`.
- Errors: `XodSqlException` with `Code` (`PLAN-045` section 9), `Line` and `Column` (for syntax errors).
- `EXPLAIN SELECT ...` returns one `TEXT` column, `plan`: how each table is read (a scan, or an index and the
  condition it answers).
- `DatabaseOptions.SqlSchema`: `Classes` (the default: a table whose class the program has is typed from
  the class, any other from its manifest) or `Recorded` (every table from its manifest, as `xod query` reads).
- `IXodContext` and `IXodEngine` are not changed (adding members breaks their implementers); `Sql` is on
  `XodContext`, for its own engines.

### 2.2 The engine (`src/Xod/Sql/`)

- **Lexer and parser**: the grammar of `PLAN-045` section 5, hand-written recursive descent, positions for
  `XQ001`. `[Name]` is a quoted name where a name may start, and a subscript (`Tags['k']`) after an operand.
- **Binder**: resolves tables (D2: any case; a clash or an ambiguous short name is `XQ003`), columns, paths
  and children collections; gives every expression its SQL type, so `'a' + 1` (`XQ005`) and a column neither
  grouped nor aggregated (`XQ006`) fail before any row is read.
- **Executor**: scans, joins (hash on equality keys, otherwise nested), filters, `GROUP BY`/`HAVING`,
  aggregates, `DISTINCT`, `ORDER BY` (stable, so ties keep stored order), `LIMIT`/`OFFSET`, subqueries
  (uncorrelated ones run once), paths as left joins on the relationship's keys, a complex object by its item
  code.
- **Reading**: every table the query names is read once, under that table's read lock, all of them held for
  the whole query, so no write lands in the middle of a query. Rows come from the same page walk as
  `Select<T>` (stored order, D5) and are decoded column by column, never into objects. As `Select<T>` does, a
  query sees the statements another thread's open transaction has made so far: a transaction doesn't hold
  the engine's lock between its statements.
- **Lock order (as built, after the review)**: a writer waiting for a table's write lock keeps new readers out
  (ReaderWriterLockSlim prefers writers), so two queries taking the same tables in opposite orders, each behind
  a waiting writer, deadlocked (review finding 1, reproduced by `XodSqlLockTests`). A query now waits for a
  table's lock only when the table sorts (ordinal, full name) after every table it holds. One that sorts
  earlier is taken only if it is free at once; otherwise the query gives every table back and starts again,
  taking the tables it found first, in order. Each new start adds a table, so a query starts at most once per
  table.
- **A query inside a read or write on the same thread** (a query's predicate; an `InsertRange` reading a
  caller's lazy items) starts with that table already held, which it can't give back (second review, finding
  1, reproduced). The engine's lock helpers now keep a per-thread record of the table locks a thread holds
  (`heldTableLocks`; not the lock's `IsReadLockHeld`, which its documentation reserves for debugging). SQL
  counts those tables as held: it enters one of them again without waiting, and waits only above them all. A
  table below them that isn't free at once is refused with an `XodException`, as a write inside a read is.
- **One lock per table name in any case** (second review, finding 2): SQL names a table by its file, which a
  case-insensitive file system may spell differently from the class's full name its writers use. The order
  is by name, ordinal ignoring case.
- **Indexes** (the class known): conditions on one table of the form `column = v`, `IN`, ranges and
  `BETWEEN` on a key or indexed column go to the index planner `Query<T>` uses, as a typed expression; only
  when the value converts exactly to the property's type, and not for `float`/`double`. As built: unique
  columns too, and no ranges on text or enums (the index's order isn't SQL's there). The rows it names
  are read in stored order and the whole `WHERE` is evaluated on them anyway, so an index never changes an
  answer (`PLAN-045` principle 3).

### 2.3 One decoder per source of the schema

- **With the class**: each column is read with the library's own `StoredScalar` (the rule the index and
  the row readers share: `nil`, an empty text, a value left out) and the collection readers, then turned
  into its SQL value. SQL and lambdas read a value the same way.
- **From the manifest** (`xod query`, `SqlSchema.Recorded`): the stored text is parsed by the manifest's
  type, with the same rules: `nil` is NULL, an empty text is `''` for a plain text column and otherwise
  counts as left out, a value left out reads as the manifest's `default` (section 3).
- The schema the binder sees is the same in both cases: the class's manifest, built by the code that writes
  it, or the stored one. A table with neither is read with every column as `TEXT`, with warning `XQ013`.
- **Encrypted columns** (`AES256`) in both: plaintext only when a secret the caller gave opens them (the
  password, or `DatabaseOptions.PropertyEncryptionKey`); otherwise NULL, with warning `XQ008` naming the
  column. A value 2.0 encrypted with its built-in constant (ISS-050) opens with no secret, so SQL reads it as
  NULL; the lambda API still decrypts it, for compatibility.

### 2.4 The manifest (E1-E3)

- `<Schema version="1" type element writer>` in the table file, with `<Column>` and `<Relation>` as
  `PLAN-046` section 10.2 lists them, in the class's property order (the order of `SELECT *`).
- **When**: the table file a write creates is created with it; at the first write of a type in a context
  (`Insert`, `InsertRange`, `InsertOrUpdate`, `Update`, `Delete`), the manifests of its table and of the tables
  its relations can write are compared with the class's and replaced when they differ (the `writer` attribute
  ignored). The change goes through the table file's normal save, so a transaction commits or rolls it back
  with its rows. A read never writes. `Drop` writes none.
- **As built**: every write operation checks, inside its table lock (`EnterTableWriteLock`, which every row
  write takes), not only a context's first. A manifest found to match is remembered by its element, so a
  check after that costs a lookup, and a table file read again (after a rollback) is checked again. The
  closure of tables is computed once per class.
- **Checked independently**: `read.mjs` compares the manifest of the fixtures the tree writes with
  `column-types.json` (the types it was given by hand) and reads them with it.

### 2.5 The CLI

`xod query <db> "SELECT ..." [--format table|csv|json] [--password <pwd>]`. It opens the database with
`SqlSchema.Recorded` and `InitialCreate = false`, and calls no writer: it writes no rows or schema and
creates no database. There is no read-only open, so the open itself still holds `xod.lock` and, as every open
does, puts back a commit a crash cut short. `json` writes the values as the conformance
set encodes them (`PLAN-047` section 4.2). Warnings go to stderr; an error is `XQnnn: message` on stderr and
exit code 1.

## 3. Choices the spec leaves open, made here

Built so. `PLAN-046` section 10.2 gives the manifest's; `PLAN-045` sections 8 and 9 give the reserved words and
the new codes, and point here for the rest. The rows below the line were chosen while building (2026-11-14).

| Topic | Choice | Why |
| :--- | :--- | :--- |
| Manifest `default` | the value a read gives a column its row leaves out (`StoredValue.ReadBackOfMissing`: what a new instance holds, since `[DefaultValue]` has no effect, ISS-118), when that isn't null or the type's default | a row written before its property existed lacks it, and the reader gives it that value |
| Encrypted columns with an application key | the key opens them, as the password does | PLAN-049 added the key after `PLAN-045` was written; it is the caller's secret |
| `LENGTH`, `SUBSTRING`, `POSITION` | count characters (Unicode code points) | standard SQL counts characters; C#'s `Length` counts UTF-16 units |
| `TRIM(s)` | removes spaces, as the standard; `TRIM([LEADING\|TRAILING\|BOTH] [chars] FROM s)` too | |
| Division by zero | `INTEGER` and `DECIMAL`: error `XQ010`; `DOUBLE`: IEEE (`Infinity`, `NaN`) | as C# for each type |
| `CAST(DECIMAL or DOUBLE AS INTEGER)` | rounds half away from zero | as G1 for `ROUND` |
| A text literal beside another type | converted to that type (`Placed < '2026-04-01'`); `XQ005` when it doesn't parse | as standard SQL treats an untyped literal |
| `x \|\| y` with a non-text side | converted to text | as PostgreSQL |
| `CURRENT_TIMESTAMP`, `CURRENT_DATE` | `TIMESTAMPTZ` with the machine's offset, and its local date; fixed for one query | |
| `EXTRACT` | `YEAR`, `QUARTER`, `MONTH`, `DAY`, `HOUR`, `MINUTE`, `SECOND`, `DOW` (0 = Sunday), `DOY`; of a `TIMESTAMPTZ`, its own clock | there is no session time zone |
| `DATE_TRUNC` of a `DATE` | a `DATE` | |
| `ORDER BY` with `SELECT DISTINCT` | must name selected columns (`XQ006`) | otherwise the order is undefined |
| Output names | a column's own name, a path's last name, an aggregate or function's name in lower case, otherwise `?column?` | |
| Ambiguous column | `XQ003` (now "ambiguous name": tables and columns) | |
| New codes | `XQ010` division by zero, `XQ011` a scalar subquery gave more than one row, `XQ012` a parameter without a value or of a type SQL has no column for; warnings `XQ013` a table or column read as text (no schema lists it), `XQ014` a stored value that doesn't parse as its column's type, read as NULL (E3) | |
| Writes and version-2 syntax | `XQ001`, with a message saying they aren't in this version | |
| *Chosen while building:* | | |
| Reserved words | PostgreSQL's reserved words plus `ILIKE`, `LIMIT`, `OFFSET`, not SQL-92's | SQL-92 reserves `Year`, `Day` and `Value`, which accepted cases use as column names |
| A word after `.` | always a name, reserved or not (`l.Order.Customer.Name`) | nothing else can follow a `.`; `Order` is a common class name |
| `EXTRACT` result | `INTEGER`, except `SECOND`: `DECIMAL` with its fraction | a second keeps its fraction, as in PostgreSQL |
| A text parameter | takes the type it meets, as a text literal does (`XQ005` when it doesn't parse) | a BI tool sends every `?` as text |
| A parameter's type | checked before it is converted: a .NET value no SQL type holds is `XQ012` | otherwise it became its `ToString()` text |
| More positional values than `?` | `XQ012` | a value without a place is a mistake in the call |
| `Sql<T>` | each column to the writable property of its name, in any case; two columns to one property, or a class with two properties of the name, `XQ003`; a value converted only exactly (a NULL a property can't hold, a fraction for an `int`) `XQ005`; a column without a property left out | a silent loss would give wrong objects |
| `ORDER BY` name | an output column's name first, then an ordinal, then an expression over the input | as PostgreSQL |
| `GROUP BY` name | an input column first, then an output name or ordinal | as PostgreSQL |
| Index use | `=`, ranges and `IN` with constants on a key, unique or indexed column of a table typed from its class; no ranges on text, enums or floating point; rows still come in stored order, page by page | the index's order isn't the stored order, and its text order isn't SQL's |
| `TIMESTAMP` comparisons | the clock time, `Kind` ignored (`PLAN-045` section 4 corrected) | as C# and the index keys (`PLAN-046` section 8.3): otherwise an index could change an answer |
| `LIKE`'s `_` | one code point | as `LENGTH` and `SUBSTRING` count (review finding 8) |
| `CAST` of a `TIMESTAMPTZ` | `AS TIMESTAMP`: its UTC time, `Kind` UTC (the instant kept); `AS DATE`: the date on its own clock, as `EXTRACT` | a `TIMESTAMP` can hold the instant; a date can't, and `EXTRACT(DAY ...)` already reads its own clock |
| A map's null value | NULL when the value type is nullable, otherwise its type's default (`valueNullable`, `PLAN-046` section 10.2) | as the library reads it (review finding 5) |

## 4. Tests, written first and seen red

1. **The conformance set through SQL** (`conformance/Xod.Conformance`): every case's `sql` runs through
   `db.Sql` and must give the expected columns, types and rows, or the expected error code:
   - with the classes, over the four committed fixtures (2.0.1's files, no manifest);
   - from the manifest only (`SqlSchema.Recorded`), over the four fixtures written by the tree into a
     scratch folder, so the manifest the library writes is judged by the same answers.
   - The 10 error cases and `crypto-002` run for the first time. All red before the engine exists.
2. **New cases** for what the set doesn't cover yet (`PLAN-047` section 8): parameters, `SELECT *` and
   `alias.*`, `EXPLAIN`, `CURRENT_DATE` with an injected clock, the new codes.
3. **The manifest** (`tests/Xod.Tests`): written by the first write and not by a read; a 2.0.1 table gets it
   at its first write; a changed class replaces it; a rolled-back transaction leaves the file as it was;
   kept by `XodMigrator` and a password change; `Drop` writes none.
4. **The decoder against the reader**: for every column of the stored-values fixture (nil, empty, left out,
   value collections), the manifest decoder gives what the class decoder gives.
5. **The CLI**: each format over a fixture; errors and exit codes.
6. **`read.mjs --check-fresh`** fails when a fresh fixture's manifest disagrees with `column-types.json`;
   proven by breaking the writer.

## 5. Done means

- every case above green in the full gate (its seven steps), .NET 8 and 9;
- an independent review (parsing, persistence) and its findings resolved;
- `RELEASE-NOTES-NEXT.md`, this plan's verdict, `PLAN-045`/`PLAN-046`/`PLAN-047` where they change, and the
  site pages that say there is no SQL (`limits.md` "Ad-hoc SQL and reporting", `index.md`, `cli.md`,
  `reading.md`), with new behaviour labelled `**Next release:**`.

## 6. Not in this plan

- Writes (D3), version-2 syntax (window functions, `WITH`, set operations, `RIGHT`/`FULL` joins).
- The analytics suite in `XodBench` (`PLAN-044` step 3): scans and group-bys at 1M and 10M rows. Until then
  no speed claim is made for SQL.
- The browser and the Rust core (`PLAN-044` steps 2 and 4).

## 7. As built (2026-11-14)

### 7.1 Tests

- Section 4's six groups are in `tests/Xod.Tests` (`XodSqlTests`, `XodSchemaManifestTests`, `XodCliQueryTests`,
  `XodSqlLockTests`, `XodSqlKeyTypesTests`) and `conformance/Xod.Conformance` (`SqlConformanceTests`), each seen
  red first. Guards were proven by breaking the code they guard:
  - the manifest written at every write and at a table file's creation (both had to be off for the "no
    manifest" check to fail, as designed: either path writes it);
  - the index range rules, `Decrypt`, the `XQ014` catch, and unlisted columns;
  - the manifest decoder's `default` (`Sql_reads_each_stored_form_as_the_reader_does`, which takes out the
    elements a row written before its property existed lacks: the stored-values fixture alone never reaches
    `default`, since the writers write every non-default value);
  - `read.mjs --check-fresh` on a manifest switched off and on a wrong column type.
- **Different from section 4:**
  - `CURRENT_DATE` isn't given an injected clock: the test brackets the query between two readings of the
    machine clock and checks one value for every row, which is deterministic.
  - `Drop` writes none (`Drop_writes_none_...`). `DropType` takes the exclusive lock, not
    `EnterTableWriteLock`, so it never reaches `RecordSchemas`. Proven on 2026-11-14, once
    `XmlEngine.Mutations.cs` held no other session's uncommitted work: with `DropType` calling `RecordSchemas`,
    the test is red (the other table's file is rewritten); with the file restored (identical to HEAD), the
    manifest tests pass, 12 of 12 on .NET 9.
- `xod query`'s tests run on .NET 9 only, because the tool targets .NET 9. A first version made the tool
  multi-target, which broke `dotnet run --project src/Xod.Cli -- ...`; that was put back before the gate.

### 7.2 The independent review

A fresh reviewer was given the code and the problem, not these conclusions. Each finding was checked against
the code, and reproduced by a test where it could be (red first):

| # | Finding | Checked | Resolution |
| :--- | :--- | :--- | :--- |
| 1 | High: two queries taking the same tables in opposite orders, each behind a waiting writer, deadlock | reproduced: `XodSqlLockTests` deadlocked | fixed: one lock order (section 2.2) |
| 2 | Medium: in a grouped query, an ungrouped column read by a subquery in the select list, `HAVING` or `ORDER BY` took the group's first row instead of failing with `XQ006` | reproduced, 5 cases | fixed: the binder records each column of an outer query a subquery reads (`OuterUses`), and the grouping check covers them |
| 3 | Medium: `TIMESTAMP` compares clock time, not the instant `PLAN-045` section 4 said | confirmed: the spec sentence was the error | `PLAN-045` corrected: comparing instants would let an index (keys v2: ticks, `Kind` ignored) change an answer, and SQL would disagree with lambdas |
| 4 | Low-medium: the manifest's `default` is a per-process snapshot for an initializer that isn't a constant | confirmed by reading; the writers are safe | filed `ISS-161`, with `ISS-118` |
| 5 | Low: the decoders differ on a map's null value, a non-canonical enum, a struct the library can't store | the map case reproduced | the map fixed (`valueNullable`); the rest filed `ISS-162` (only hand-edited text reaches it) |
| 6 | Low: "the database is only read" claims more than the code | confirmed | wording corrected: the help text, this plan, the release notes, the site |
| 7 | Low: redirected CLI output loses non-ASCII text; a third copy of the value encoding | reproduced: `?????` | UTF-8 for every command's redirected output; the copy filed in `ISS-162` |
| 8 | Nits: `LIKE`'s `_` counts UTF-16 units; `SUBSTRING` wraps; `INTERVAL` times NaN and Unicode digits throw .NET exceptions; `ROUND(x, -400)` gives NaN; `CAST` of a `TIMESTAMPTZ` uses two clocks | the first five reproduced | fixed; the `CAST` rule kept and written down (section 3) |
| Q2 | A path whose keys compare but hash apart (`DOUBLE` against `INTEGER`) found nothing | reproduced | fixed: such a path follows the link its row stores |

Its Q1 also found "one query sees one state" claimed more than the code gives (another thread's open
transaction is visible, as to `Select<T>`): corrected in section 2.2.

**A second review**, of the lock-order fix alone (concurrency is a high-risk area), with the code and the
problem. It confirmed the restart gives every lock back, starts each attempt clean, and ends. It also confirmed
the ReaderWriterLockSlim behaviour relied on, citing the Microsoft Learn pages for the class and for
`TryEnterReadLock`. Its findings:

| # | Finding | Checked | Resolution |
| :--- | :--- | :--- | :--- |
| 1 | Real defect: SQL run inside a read or write on the same thread (a predicate, an `InsertRange`'s lazy items) holds a table the order didn't see, so the circle could still close | reproduced: deadlocked | fixed: a per-thread record in the engine's lock helpers (section 2.2); the query is refused instead of waiting out of order |
| 2 | Risk: a table's lock was named by its file in SQL and by its class's full name in its writers; on a case-insensitive file system they could differ | reproduced: two casings, two locks | fixed: one lock per name in any case |
| 3 | Nits: restart cost (bounded, one re-bind per table); the test had no check that every lock is free afterwards | confirmed | both lock tests now check every lock is free; the cost stays, as designed |

Lambda reads nested the same way (a predicate that reads another table) could already deadlock without SQL,
with two such callers in opposite orders: filed as `ISS-163`.

## Verdict

Built, tested and reviewed on 2026-11-14; ships in 2.0.2. Not released: no tag and no NuGet package until
the owner says so. Per change:

- **`db.Sql` (`src/Xod/Sql/`, `XmlEngine.Sql.cs`): clean with named debt.** The conformance set passes
  through SQL in all four fixtures, typed from the classes and from the manifest alone (section 7.1, `PLAN-047`
  section 5.1). Every review finding that gave a wrong answer or an unhandled exception is fixed, each with a
  test seen red first. Debt: `ISS-162`, hand-edited enum or struct text read differently by the two decoders.
  No speed claim is made: the analytics benchmarks are `PLAN-044` step 3.
- **Locks (`SqlSource.Lock`, the engine's per-thread record, one lock per table name in any case): clean
  with named debt.**
  - The circle the first review found, and the nested one the second found, are reproduced and gone.
  - The fix for the nested case had no third review.
  - Debt: `ISS-163`, lambda reads nested the same way, which predate SQL.
  - Designed behaviour: a SQL query inside a read on the same thread is refused, not made to wait, when it
    needs a busy table that sorts below the ones the thread holds.
- **The schema manifest: clean with named debt.**
  - It is written when a table file is created and checked at every write.
  - A read never writes it, and a rollback takes it back.
  - `read.mjs` checks it independently.
  - Debt: `ISS-161`, the `default` of an initializer that isn't a constant.
  - `Drop` writing none is tested, and proven by breaking `DropType` (section 7.1).
- **`xod query`, and UTF-8 for redirected output: clean with named debt.** Debt: `ISS-162`'s copy of the
  value encoding, and `ISS-156`'s password on the command line, which predates this.
- **Docs:**
  - `PLAN-045`: section 4's `TIMESTAMP` rule corrected; sections 8 and 9 (reserved words, `XQ010` to `XQ014`).
  - `PLAN-046` section 10.
  - `PLAN-047` sections 5.1, 7 and 8.
  - The release notes.
  - The site pages `limits`, `index`, `reading`, `cli`, `api`, `options`, `exceptions` and `storage`, labelled
    `**Next release:**`.

**Not verified:**
- CI hasn't run: nothing is pushed.
- The full gate ran again on 2026-11-14 on this task's commit, every step green. Before the push, that commit
  and the two under it were rebuilt to take machine details out of two docs (`PLAN-056`, `ISS-106`): a change
  to text only, so the gate wasn't run again on the rebuilt commits.
- The cross-process manifest rewrites of `ISS-161` are reasoned from the code, not reproduced.
