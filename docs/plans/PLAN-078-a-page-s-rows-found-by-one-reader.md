# PLAN-078: a page's rows found by one reader, and a row that can't be read is an error

- **Task ID**: `TASK-093`
- **Status**: Done (2026-11-18)
- **Issues**: `ISS-143`, `ISS-144`
- **Related**: [`REVIEW-2026-11-14`](../REVIEW-2026-11-14-overall.md), [`VERIFY-2026-11-18`](../VERIFY-2026-11-18-open-issues.md),
  `ISS-195`, `ISS-196`, `ISS-197`, `ISS-198` (filed)

## 1. The cause

The readers of a page's bytes found its rows by the byte patterns `<Row code="` and `</Row>`, eleven of them. A
`[Markup]` value holding either (written as CDATA), a property named `Row` (its element is `<Row>` inside every
row) or a class named `Row` ended a row early or started a forged one. Each reader then swallowed what it couldn't
parse (`catch { }`), so a row cut short read as absent or as nulls, `Last` returned a row made of a value's text, and
an update wrote the loss back.

So: **rows were lost because eleven readers each found rows by a pattern a value can hold, and hid the parse that
failed. The change makes one reader, `PageRows`, find every row by element depth, and lets a row it can't read fail
as a `DatabaseFileException` naming the page.**

## 2. The change

- **`PageRows`** (new): the one reader of where a page's rows are. It follows element depth and skips CDATA,
  comments, processing instructions and quoted attribute values. A page it can't follow throws
  `DatabaseFileException("The page 'X' is damaged: …")`.
- **`RawPageBuffer`**: the row map is built by `PageRows` only, and every raw reader takes its rows from the map
  (`TryFindRow`, `RowsInOrder`, `TryLastRow`, `RowsMapped`). Two rows with one code (a page XOD 1.x wrote can hold
  them: its codes were random) are both rows of the page, as the document read has them, and the code names the later
  one, as the document read's lookup does. The map kept the later one alone, so a read of the rows dropped the
  earlier.
- **`IOService.RawPages`**: `Last`, `Select(skip, take)`, the count and the row lookups use the map; the 64 KB tail
  reads and the pattern fallbacks are gone. A row whose XML doesn't parse is a damaged page (`ParseRow`). The
  swallowed failures are gone from `TryGetRowFromRawPage`, `GetRawData` and the page load.
- **The fast read of a row of scalars** (`TryParseEntityFromUtf8Row`) reads only what XOD writes: elements,
  attributes and escaped text. A row holding CDATA, a comment or a processing instruction, a value it doesn't parse,
  a close tag that doesn't name its element, or a row that ends early goes to the XML read, which reads it or names
  the damage. It used to leave a bad value at its default, read CDATA as text and end a tag at a quoted `>`.
- **Reads side by side** (`ParallelReads.For`): every read runs, and the failure with the lowest index is thrown as
  itself, as a loop meets it. A damaged page read beside three or more others came out as an `AggregateException`.
  The page preloads, the query's parallel read and the index's block reads use it.
- **`IndexService.Persistence`**: a table file it can't read means the index is rebuilt, not trusted.

### Found by the review: a page's bytes given back while a reader held them

A reader takes a page from the cache and reads it outside the cache's lock. Another reader beside it can drop that
page from the cache (a read that opens the page as a document, or an eviction). The drop gave the page's array back
to the pool and set its bytes to null, so the first reader threw, or read whatever page took the array next. This
predates the change; the change made it likelier, since `Last` now loads the page through the cache. A page that
leaves the cache now keeps its bytes, and the GC takes them once no reader holds the page. Only a buffer never put in
the cache goes back to the pool. A writer that moves a page to a larger array still gives the old one back: writes
hold the engine-wide write lock, which no reader holds beside them. The cost: a page dropped from the cache is a new
array at its next load, not one from the pool. Over 40 cycles of opening, reading 14 pages and closing, the review
measured 942 MB allocated where 654 MB were, and 23 full collections where 14 were; the time and the memory left
after the run were the same within noise.

## 3. Tests

`XodRowScannerTests` (21) and `XodPageBufferLifetimeTests` (1):

- `[Markup]` values holding the row markers, a property named `Row` (a type of scalars and one read as XML), a
  structurally damaged row, a misnamed close tag (as XML and as scalars), a cut page, a bad number, valid XML the fast
  read declines (a comment, CDATA with and without `</`, a quoted `>`, a processing instruction), two rows with one
  code read whole, and a damaged page read beside others: 19 of 21 **red against HEAD**; the two that pass there are
  a guard (a class named `Row`).
- The page buffer: a reader's page dropped by a document read, then another page loaded: **red** with the old drop.
  The eviction follows the same rule; it needs 65,536 cached pages, so no test reaches it.
- Each rule broken once and caught: the fast read taking CDATA, a tag ending at the first `>`, a close tag taken
  whatever it names, a repeated code overwriting the earlier row, a parallel read wrapping its failure, `TagEnd`
  ignoring quotes, a document read giving the page's bytes back.
- An independent review of the change, and a second of the fixes it asked for. Its findings are taken up here or
  filed below.
- The reviewer's parallel probe (six readers, `Select`, `Count`, `Last`, `Find`): 3 to 13 throws per run before, none
  in 1,782 rounds after.

## 4. Cost

A page's first read follows element depth through every tag. On one machine, with 120,000 rows over 55 pages:

| Read | Before | After |
| :--- | ---: | ---: |
| Every page, first read in a context | 62 ms | 122–137 ms |
| `Select(60000, 100)`, first read | 0.46 ms | 1.1 ms |
| `Last`, repeated | 0.001 ms | 0.03 ms |

Counts, lookups by key, queries and repeated reads are unchanged within noise.

## 5. Left open

- `ISS-195`: pages written by hand. `PageRows` refuses valid XML that neither XOD nor 1.x writes between rows (a
  comment, a processing instruction, `<Row>` with another attribute first or single quotes), which the document read
  takes, and an insert into such a page throws; the fast read is looser than the XML read in three places.
- `ISS-196`: a missing page file, or an index entry whose row isn't on its page, still reads as no row.
- `ISS-197`: a page from XOD 1.x with two rows of one code: both read now, but a lookup by code and the index name
  only the later.
- `ISS-198`: the index's snapshot writer runs side by side and wraps a failure in an `AggregateException`.
- A bad number in a row read as XML throws `FormatException`, which doesn't name the page.
