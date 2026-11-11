# PLAN-045: XOD SQL - the query language (spec draft)

- **Task ID**: `TASK-057` (roadmap step 1, first of three specs - `PLAN-044`)
- **Status**: Spec accepted (2026-11-11): the owner took the recommendation on every decision, D1-D8
  (section 12). Nothing is built.
- **Related**: [`PLAN-044`](PLAN-044-roadmap-sql-analytics-rust-browser.md) (why SQL, the order, the
  browser), [`PLAN-046`](PLAN-046-file-format-spec.md) (the file-format spec, draft) and the conformance set (not written yet)

## 1. What this is

A SQL dialect for XOD databases: read-only `SELECT` first, writes designed here and built later. One
language for every place XOD runs - the .NET library, the command-line tool, a Rust core if step 2
earns it, and the browser.

**Principles**

1. **Real SQL, a subset.** People, BI tools and LLMs already write SQL. A query that is valid XOD SQL
   means what it means in standard SQL; when the standard leaves a choice open, this spec makes it and
   says so (section 8).
2. **Extensions only where the object model needs them**: walking a relationship (`o.Customer.Name`,
   `JOIN c.Orders`). Each one is listed in section 6.
3. **One answer everywhere.** C#, Rust and the browser must return the same rows for the same query and
   data. The conformance set (step 1, third spec) is the judge; semantics never depend on whether an
   index was used.
4. **The data stays readable.** Nothing here changes the page files people open, except what D1 adds.
5. **The C# API stays.** Lambdas remain the way .NET code queries objects; SQL is added beside them.

## 2. Where it runs

| Place | How | Notes |
| :--- | :--- | :--- |
| .NET | `db.Sql("SELECT ...", args)` returns rows; `db.Sql<T>(...)` maps each row to `T` by column name | parameters only, never string-built values |
| CLI | `xod query <db> "SELECT ..." [--format table\|csv\|json]` | beside `stats`, `convert`, `secure`, `loose` |
| Browser | `const db = await xod.open('shop'); await db.query("SELECT ...", args)` | `PLAN-044` step 4 |

A result is a list of columns (name and type) and a list of rows. Column names come from the select
list: the column's own name, or the alias after `AS`.

## 3. The first problem: the files don't carry types (D1)

Every value is stored as invariant text (`ValueHelper.FormatScalar`): `42`, `4.5`, `True`, an enum's
name, ISO-8601 dates. The column types live only in the C# class. The .NET library knows them; an engine
without the model - the CLI on someone else's database, Rust, the browser - sees only text, and can't
tell `"10"` (a number) from `"10"` (a code), or sort dates from strings correctly in every case.

**D1 - how a model-less engine learns the types.**

- **(a) A schema manifest in the files (recommended).** The .NET library writes each table's columns -
  name, XOD type, nullable, key, unique, indexed, relationship, `[Crypto]` - into the table's `.xtab`
  (or a small `.xsch` beside it), whenever it opens a type. Readable, like the rest. A database written
  by 2.0 before the manifest exists gets one the next time 2.0.x+ opens it with the model. Defined in the
  file-format spec.
- (b) Infer types from the values. No file change, but `"007"` becomes 7 and a column that is empty in
  the first pages has no type. Answers would depend on the data seen - against principle 3.
- (c) SQL only in .NET, where the model is. Gives up the CLI, Rust and the browser - the reasons for SQL
  in `PLAN-044`.

Everything below assumes (a).

## 4. Tables and columns

### Tables

Each stored class is a table. XOD keys its files by `Type.FullName` (`MyApp.Shop.Customer`).

- A table is named by its **short name** (`Customer`) when that is unique in the database.
- When two namespaces share a short name, the full name is required, quoted: `"MyApp.Shop.Customer"`.
  A short name that is ambiguous is an error that lists the candidates.
- Names are case-sensitive, like C#: `customer` is not `Customer`. (**D2**: case-insensitive matching,
  as most SQL engines do, is friendlier for hand-written queries; recommended: case-insensitive for
  table and column names, with an error when two names differ only by case.)

### Columns

Every stored scalar property is a column, named as the property. `[NotMapped]` and read-only properties
don't exist in the files, so they aren't columns.

| C# type | SQL type | Literal |
| :--- | :--- | :--- |
| `bool` | `BOOLEAN` | `TRUE`, `FALSE` |
| `byte`, `short`, `int`, `long` (and unsigned) | `INTEGER` (64-bit; `ulong` above 2^63 is `DECIMAL`) | `42` |
| `float`, `double` | `DOUBLE` | `4.5`, `1e9` |
| `decimal` | `DECIMAL` (exact, 28 digits, as .NET) | `12.50` |
| `string`, `char` | `TEXT` | `'text'` |
| enum | `TEXT` (the member name, as stored) | `'Public'` |
| `Guid` | `UUID` | `UUID '8f14e45f-...'` or a `TEXT` literal compared with it |
| `DateTime` | `TIMESTAMP` | `TIMESTAMP '2026-11-08 09:30:00'` |
| `DateTimeOffset` | `TIMESTAMPTZ` | `TIMESTAMPTZ '2026-11-08 09:30:00+03:00'` |
| `DateOnly` | `DATE` | `DATE '2026-11-08'` |
| `TimeOnly` | `TIME` | `TIME '09:30'` |
| `TimeSpan` | `INTERVAL` | `INTERVAL '02:00:00'` |
| `Nullable<T>` | the type of `T`, nullable | `NULL` |
| `Dictionary<K, V>` | `MAP` | read with `m['key']` (section 6) |
| list or array of scalars | `ARRAY` | `ANY(tags) = 'x'` (section 6) |

- **Enums** compare by name, as stored. Ordering enums by their numeric value is not supported in v1.
- **`DateTime` kind**: stored in round-trip form, which keeps `Z` for UTC and an offset for local times;
  comparisons use the stored instant. (The file-format spec pins the exact forms.)
- **Encrypted columns** (`[Crypto(AES256)]`) read as plain text only when the database is opened with
  its password; otherwise they are `NULL` and a warning names them. **Hashed columns** (`SHA*`, `MD5`)
  read as their hash; `VERIFY_HASH(column, 'plain')` returns `BOOLEAN`.

### Relationships

Reference properties aren't columns; they are paths (section 6). A child's key property
(`CustomerId`) is an ordinary column, so plain SQL joins work too:

```sql
SELECT c.Name, o.Status
FROM Customer c
JOIN "Order" o ON o.CustomerId = c.Id
```

(`Order` is a reserved word, hence the quotes; section 8.)

## 5. SELECT, version 1

```
query        := select [ ORDER BY order_item { , order_item } ] [ LIMIT n [ OFFSET m ] ]
select       := SELECT [ DISTINCT ] item { , item }
                FROM source { join }
                [ WHERE expr ]
                [ GROUP BY expr { , expr } ]
                [ HAVING expr ]
item         := * | alias.* | expr [ [ AS ] name ]
source       := table [ [ AS ] alias ] | ( query ) [ AS ] alias
join         := [ INNER | LEFT [ OUTER ] ] JOIN source ON expr
              | [ INNER | LEFT [ OUTER ] ] JOIN alias.collection [ [ AS ] alias ]     -- section 6
              | CROSS JOIN source
order_item   := expr [ ASC | DESC ] [ NULLS FIRST | NULLS LAST ]
```

**Expressions**

- Literals (section 4), column references (`Name`, `c.Name`), parameters (`@name` or `?`).
- Arithmetic `+ - * / %`; text concatenation `||`.
- Comparison `= <> != < <= > >=`; `IS [NOT] NULL`; `[NOT] BETWEEN a AND b`; `[NOT] IN (list)`;
  `[NOT] IN (subquery)`; `[NOT] EXISTS (subquery)`; `[NOT] LIKE pattern [ESCAPE c]`.
- `AND`, `OR`, `NOT`; `CASE WHEN ... THEN ... ELSE ... END`; `CAST(x AS type)`; `COALESCE`, `NULLIF`.
- Scalar subqueries in the select list and `WHERE`.

**Functions, version 1**

| Group | Functions |
| :--- | :--- |
| Aggregate | `COUNT(*)`, `COUNT(x)`, `COUNT(DISTINCT x)`, `SUM`, `AVG`, `MIN`, `MAX` |
| Text | `LOWER`, `UPPER`, `LENGTH`, `TRIM`, `SUBSTRING(s, start, len)`, `REPLACE`, `POSITION(a IN b)` |
| Numbers | `ABS`, `ROUND(x, n)`, `FLOOR`, `CEIL` |
| Dates | `EXTRACT(part FROM x)`, `DATE_TRUNC('day' \| 'month' \| ..., x)`, `CURRENT_DATE`, `CURRENT_TIMESTAMP` |
| XOD | `VERIFY_HASH(col, plain)` |

Not in version 1, planned for version 2 (`PLAN-044` step 6): window functions (`OVER`), `WITH` (CTEs),
`UNION`/`INTERSECT`/`EXCEPT`, `RIGHT` and `FULL` joins, `GROUPING SETS`, regular expressions.

## 6. Object-model extensions

Each extension is something a XOD database has and a plain SQL table doesn't.

### To-one paths

A reference property can be followed with dots, as in C#. Each step is an implicit `LEFT JOIN` on the
relationship's key, so a missing reference gives `NULL`, not a lost row:

```sql
SELECT o.Id, o.Customer.Name
FROM "Order" o
WHERE o.Customer.Name LIKE 'N%'
```

### Children collections

`JOIN alias.collection` joins a parent to its children through the relationship XOD already knows,
without naming keys:

```sql
SELECT c.Name, COUNT(o.Id) AS orders, SUM(l.Amount) AS total
FROM Customer c
LEFT JOIN c.Orders o
LEFT JOIN o.Lines l
GROUP BY c.Name
ORDER BY total DESC
```

A collection is also a source for a subquery: `(SELECT COUNT(*) FROM c.Orders)`.

### Maps and scalar lists

- `Tags['room']` reads a dictionary entry (`NULL` when the key is missing).
- `'x' = ANY(Labels)` tests a list of scalars; `CARDINALITY(Labels)` is its length.

## 7. Writes (designed now, built after SELECT) (D3)

Writes must keep every rule the C# library enforces: keys and auto-numbering, `[UniqueKey]`,
`[Required]`, children cascading, `[Crypto]` hashing and encryption, the journal. With D1(a) the manifest
carries those rules, so a model-less engine can enforce them.

```sql
INSERT INTO Customer (Name) VALUES ('Nadia');          -- Id assigned
UPDATE Product SET Price = Price * 1.1 WHERE Category = 'kitchen';
DELETE FROM Basket WHERE Id = 7;                        -- its children too, as in C#
```

- One statement is one transaction, as one C# call is. `BEGIN; ...; COMMIT;` groups them.
- `INSERT` of a whole object graph (a customer with its orders) stays a C# feature; SQL inserts rows.
- `CREATE`, `ALTER` and `DROP TABLE` aren't supported: the classes are the schema. (`DROP TABLE` may be
  added to match `db.Drop<T>()`.)

**D3 - when writes ship.** Recommended: SELECT first, in .NET and the CLI; writes after the manifest
has been in a release and the conformance set covers them. Writes in the browser stay out until sync
(`PLAN-044`).

## 8. Choices the standard leaves open

| Topic | XOD SQL | Why |
| :--- | :--- | :--- |
| Text comparison | ordinal and case-sensitive (`=`, `<`, `ORDER BY`) | matches C# `==` and `string.CompareOrdinal`, so SQL and lambdas agree |
| `LIKE` | case-sensitive; `ILIKE` for case-insensitive (**D4**) | same reason; `ILIKE` is a common extension |
| `NULL` | three-valued logic, as the standard | `WHERE x = NULL` matches nothing; use `IS NULL` |
| `NULL` in `ORDER BY` | `NULLS LAST` ascending, `NULLS FIRST` descending, unless stated | one fixed rule, written down |
| Order without `ORDER BY` | stored order: the order `db.Select<T>()` returns (**D5**) | C# already reads in that order; recommended as a documented guarantee, defined exactly in the file-format spec |
| Integer division | `7 / 2 = 3`; `7 / 2.0 = 3.5` | as C# |
| `SUM` of `INTEGER` | `INTEGER`; overflow is an error, not a wrap | silent wrap gives wrong totals |
| `AVG` | `DOUBLE`, or `DECIMAL` for a `DECIMAL` column | exact money stays exact |
| Reserved words | the SQL-92 list plus `ILIKE`, `LIMIT`, `OFFSET`; quote with `"..."` | `Order`, `User`, `Group` are common class names |
| Identifier quotes | `"Name"`; `[Name]` also accepted (**D6**) | `[...]` is what .NET developers know from SQL Server |
| Parameters | `@name` and `?` | `@name` reads well from C#; `?` is what BI tools send |

## 9. Errors

Errors carry a stable code, a message and, for syntax errors, a line and column. The codes are part of
the conformance set, so every implementation reports the same failure for the same mistake.

| Code | When |
| :--- | :--- |
| `XQ001` syntax | the query doesn't parse |
| `XQ002` unknown-table | no table by that name |
| `XQ003` ambiguous-table | a short name matches two namespaces |
| `XQ004` unknown-column | no such column or path |
| `XQ005` type | an operation the types don't allow (`'a' + 1`) |
| `XQ006` aggregate | a column neither grouped nor aggregated |
| `XQ007` overflow | an `INTEGER` result out of range |
| `XQ008` locked | an encrypted column read without the password (warning, not an error, for `SELECT`) |
| `XQ009` read-only | a write on a read-only database (a packed `.xodx`, the browser) |

## 10. How it is built (outline, decided in step 3)

- One parser and planner per core (C# now; Rust if step 2 wins), checked against the same conformance
  set. Not a translation to LINQ: LINQ can't express everything here (outer-join paths, `NULLS LAST`)
  and would tie semantics to the C# engine.
- The planner uses the existing indexes for `=`, ranges and `IN` on key and indexed columns, and the
  per-page index ranges in `.xtab` to skip pages; everything else scans. Plans never change answers.
- `EXPLAIN SELECT ...` shows the plan: which index, which pages, estimated rows.

## 11. Conformance (the third step-1 spec, outline)

- A fixed dataset in all three storage formats, plus the 1.2.5 fixture: customers, orders, lines,
  products, events with every type in section 4, nulls, a namespace clash, encrypted and hashed columns.
- Each case: a query, the expected columns and rows (JSON), whether row order is part of the answer
  (only with `ORDER BY` or D5), or the expected error code.
- C#, Rust and WASM run the same cases; a release needs all of them green.

## 12. Decisions for the owner

Decided 2026-11-11: the owner accepted all eight recommendations.

| | Decision | Decided |
| :--- | :--- | :--- |
| **D1** | How a model-less engine learns column types | a schema manifest written by the .NET library into the files |
| **D2** | Case of table and column names | case-insensitive, error on a clash |
| **D3** | When writes ship | after SELECT, once the manifest is in a release |
| **D4** | `LIKE` case | case-sensitive, plus `ILIKE` |
| **D5** | Order without `ORDER BY` | stored order, guaranteed |
| **D6** | `[Name]` quoting | accept it beside `"Name"` |
| **D7** | The name | "XOD SQL" (plain; no new brand to learn) |
| **D8** | Version 1 scope | sections 5 and 6 as written; window functions and CTEs in version 2 |

## 13. Next

1. ~~The owner's decisions on D1-D8~~ - all eight recommendations accepted, 2026-11-11.
2. The file-format spec (pages, `.xtab`, `.xidx`, the journal, and the manifest from D1), exact enough
   for a second implementation to read today's files: drafted in [`PLAN-046`](PLAN-046-file-format-spec.md)
   (2026-11-11), the manifest's placement and rules awaiting E1-E3.
3. The conformance set: the dataset and the first hundred cases, runnable against the C# library's
   lambda API today for the parts it can already answer, so the cases are checked before any SQL exists.

## Verdict

Spec accepted; nothing built. Every statement about today's XOD is from the code (`ValueHelper`, `PropertyService`, the 1.2.5
fixture) as of 2026-11-11; everything else is a proposal.
