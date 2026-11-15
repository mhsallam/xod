# Release notes for the next release (draft)

What changes for users in the next release, collected as each fix lands. The version number and the
release itself are the owner's decision; nothing here is published. Each item links the plan that
records it. The last release: [2.0.2](RELEASE-NOTES-2.0.2.md).

## New

## Behaviour changes

- **No write beside another context's commit journal** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-147`). A commit that a crash cut short, or that failed and couldn't be undone, leaves its journal (`transaction.xwal`), which the next open that has the database to itself undoes. Before, other contexts open meanwhile went on writing, and that undo took their writes back after they had been told the writes were made. Now a write beside such a journal throws `XodException` (where a commit beside it used to fail with `TransactionException` at `Commit`), until every context is closed and the database opened again; reads go on. A write beside another context's commit that is still under way waits for it, up to `TransactionTimeoutMs`. On a Unix network file system, or with .NET's file locking switched off on Unix, a commit under way can't be told from one that won't finish, so writes beside it are refused rather than waited for. Not yet covered: a commit that starts between another context's check and its write (`ISS-164`), and a buffered context's unflushed writes at its close beside a crashed journal, which the close refuses (`ISS-165`).

## Fixed

- **Referenced objects are read fresh by every query** (`ISS-138`). A query used to hand back referenced objects (an order's customer) from a cache that lived as long as the context: after the customer was updated or deleted, the order still gave the old one, and every query got the same instance, so a change made to it without saving showed up in the next query. Now the rows of one query that reference one row share one object, and nothing is shared between queries: two queries give two instances.
- **Matching by example compares values the way the index does** (`ISS-139`, [`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md)). `Query(example)`, `Query(examples)`, `Update` and `Delete` by an item, and the primary-key and unique checks with `EnableIndexing = false` compared text case-insensitively in the current culture. Now text compares exactly and numbers by value (`12.5m` finds a stored `12.50`), with or without indexing: `Name = "alice"` no longer finds "Alice"; without indexing, a key "abc" is accepted beside "ABC" (as it was with indexing) and a decimal key `12.5` is refused beside `12.50`. A `[Crypto(AES256)]` property now matches by its value; it never matched before. `XmlExtensions.ElementMatch` is marked obsolete: XOD no longer uses it.
