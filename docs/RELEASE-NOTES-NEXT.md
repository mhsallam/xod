# Release notes for the next release (draft)

What changes for users in the next release, collected as each fix lands. The version number and the
release itself are the owner's decision; nothing here is published. Each item links the plan that
records it. The last release: [2.0.2](RELEASE-NOTES-2.0.2.md).

**Prepared for release on 2026-11-16** (`TASK-078`): everything in the tree as of `7e2a5e7`, the commits since
`v2.0.2`. The version number is still open (section "Version"); `src/Xod/Xod.csproj` stays at 2.0.2 until it is
decided, as do the tag and the release.

## Upgrading from 2.0.2

- **Close every XOD 2.0.2 context of a database before opening it with this version, and don't run the two on one
  database at once.** 2.0.2 takes no commit gate, so its writes can land beside this version's commits, and its
  transactions still set the files they delete aside (`.xdel`) at once, so the loss `ISS-166` fixes can still
  happen through them.
- **Three lock files now sit beside the database file and are never deleted:** `xod.lock` (2.0.2), `xod.gate.lock`
  and `xod.commit.lock`. Leave them in place; a folder that can't hold them is opened for reading only.
- **New refusals to handle:** a write beside another context's commit that doesn't end within
  `TransactionTimeoutMs` throws `XodException`; a commit refused because another context wrote one of its files
  since the transaction first changed it throws `TransactionException` with nothing written, and should be
  retried; a write beside a commit journal a crash left throws `XodException` until the database is opened by a
  context that has it to itself. The behaviour changes below list each.
- **Matching by example is exact now** (`ISS-139`): code that relied on `Query(example)` or `Update`/`Delete` by an
  item matching text case-insensitively has to match the case, or query with a lambda.

## New

## Behaviour changes

- **No open beside a password change that didn't finish** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-148`). A password change that is made but couldn't yet replace a file (another program had it open) is finished by its context's next write, or by the next open that has the database to itself. Until then, another context's open now throws `XodException`, with either password; before, it opened with the old password and could write files the change never replaced.
- **A password change is refused over anything a crash left** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-149`): a file a transaction set aside (`<file>.xdel`) and a password change's own leftovers, as well as a commit journal and an atomic write cut short (refused before). Close every context and open the database again, which puts them back, then change the password.
- **The AES256 key is made with the database held alone** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-150`). A new database opened with a password or `DatabaseOptions.PropertyEncryptionKey` records its key when it is created, protected by the application key if the creating context has one, else by the password; `Secure`, `ChangePassword` and `Loose` make it too when there is none and something protects it. A database with no key yet (one from 2.0.2 that has no `[Crypto(AES256)]` value, one created with neither, or one whose unused key a password removal dropped) makes it at its first such value, and that value now throws `XodException` while another context has the database open: write it with the others closed, or set the password then.
- **A new lock file, `xod.gate.lock`**, beside `xod.lock`: held while a context takes the database alone or gives it back (PLAN-046 section 9). Never deleted, like the other lock files.
- **No write beside another context's commit journal** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-147`). A commit that a crash cut short, or that failed and couldn't be undone, leaves its journal (`transaction.xwal`), which the next open that has the database to itself undoes. Before, other contexts open meanwhile went on writing, and that undo took their writes back after they had been told the writes were made. Now a write beside such a journal throws `XodException` (where a commit beside it used to fail with `TransactionException` at `Commit`), until every context is closed and the database opened again; reads go on. A write beside another context's commit that is still under way waits for it, up to `TransactionTimeoutMs`. On a Unix network file system, or with .NET's file locking switched off on Unix, a commit under way can't be told from one that won't finish, so writes beside it are refused rather than waited for. Not yet covered: a buffered context's unflushed writes at its close beside a crashed journal, which the close refuses (`ISS-165`). (A commit that starts between another context's check and its write, `ISS-164`, is covered by the commit gate below.)
- **Writes and commits of two contexts are kept apart** ([`PLAN-062`](plans/PLAN-062-commit-gate.md), `ISS-164`). A new lock file, `xod.commit.lock`, beside `xod.lock` and never deleted: a commit holds it alone from before its journal until it is done, and every write holds it shared. A write now waits for another context's commit, and a commit for other contexts' writes, up to `TransactionTimeoutMs`; then the write throws `XodException`, or the commit `TransactionException` with nothing of it written. A commit is now refused (`TransactionException`, nothing written) when another context wrote one of its files (one it deletes included) after the transaction first changed it; this is decided per file, so two contexts changing different rows of one page conflict, and the later commit should be retried. A rollback no longer writes the files back as they were before the transaction: it used to take back what other contexts had written to them since. Don't open one database from XOD 2.0.2 (which takes no such lock) and from this version at once. Each statement that writes costs one more open and close of a file (about 40 µs on one machine).

## Version

The changes since 2.0.2 add no public type and remove none. One public method is marked obsolete
(`XmlExtensions.ElementMatch`, a compiler warning for code that calls it). The rest changes behaviour:
- new lock files;
- writes and commits that now wait or are refused;
- matching by example that no longer ignores case;
- a password change refused where it ran before.

Code written for 2.0.2 compiles unchanged (with a warning where it calls `ElementMatch`), but a program can now see a
refusal it never saw, or miss a match it used to get. Two numbers fit:
- **2.1.0**, recommended: a minor version says the upgrade needs reading (the section above), where a patch says
  it doesn't.
- **2.0.3**: as 2.0.2 itself was shipped (its minor-version changes released as a patch, `RELEASE-NOTES-2.0.2.md`
  section "Version").

Not decided yet. At the release: the number goes into `src/Xod/Xod.csproj`, this file becomes
`RELEASE-NOTES-<version>.md` with the date, the site's `**Next release:**` labels become the number, and the tag
`v<version>` and the GitHub release start the `publish` workflow, which waits for approval.

## Site pages to update with the release

Drafted on 2026-11-16 in `xod-site` against the tree at `7e2a5e7`, committed there (`bf37b65`) and not deployed (a deploy waits
for the release). 16 pages under `src/pages/docs` and the docs menu (`src/content/docs-nav.ts`):
- **Labelled `**Next release:**`** beside what 2.0.2 does:
  - the lock files and the commit gate (`transactions`, `storage`, `limits`);
  - a transaction's deletes at its commit, the rollback that touches no file, the commit refused over another
    context's write, and the refusals beside a journal a crash left (`transactions`, `storage`, `exceptions`,
    `options`);
  - the password change and the AES256 key (`security`, `attributes`, `options`, `api`, `cli`, `exceptions`);
  - matching by example, the key checks without indexing, and referenced objects per query (`reading`, `models`,
    `writing`, `relationships`, `api`);
  - encrypted 1.x databases and 1.x digests (`migrating`, `security`, `reading`, `api`).
- **`upgrading` becomes "Upgrading from 2.0.x"**, with a section for 2.0.2 to the next release (before the first
  open, code, what can now throw, behaviour to check) above the 2.0.0/2.0.1 checklist.
- **Corrected, wrong in 2.0.2 too** (`migrating`): 1.x `MD5`/`SHA1` digests don't verify in 2.0.2 (it compares
  hexadecimal only), and a bare `[Crypto]` is `SHA256` in 2.0 where 1.x used `MD5`.
- **Checked:** each claim against the code at `7e2a5e7` (one corrected: `BeginTransaction()` doesn't wait for
  another context's commit); the site's tests and build pass; every new link resolves to a heading. The pages were
  not looked at in a browser.

Left as they are: the intro of `migrating` ("2.0 opens 1.x databases") is qualified under "Things to check" only;
`ISS-170` and `ISS-168` (open, and older than this release) aren't on the pages. At the release, the labels become
the version number, and the landing page (`index.astro`) is unchanged.

## Fixed

- **A table a transaction dropped or emptied is no longer lost to another context** ([`PLAN-063`](plans/PLAN-063-deletes-at-the-commit.md), `ISS-166`). A transaction's delete renamed the file aside (`<file>.xdel`) at once, so other contexts saw the table gone and could make a new one at its name; the rollback then failed and the context refused writes, and the next open dropped the old table's rows. Now a transaction's deletes are made at its commit, like its writes: until then every other context sees the files as they were, and a rollback touches no file (it no longer fails, or waits for another context's commit, over a file its transaction deleted). A commit is refused, as for any file it changed, when another context wrote a file it deletes. A crash before the commit leaves no `.xdel`; one an older version left is still put back by the next open.
- **A commit or rollback no longer loses another context's write** ([`PLAN-062`](plans/PLAN-062-commit-gate.md), `ISS-164`): a commit cut short and undone took back a write another context made beside it; a commit overwrote one made after its transaction changed the same file; a rollback wrote the files back over one. A transaction's commit refused because a file couldn't be read now throws `TransactionException` (it threw `IOException`).
- **Encrypted XOD 1.x databases open with their password** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-151`), and an update no longer hashes the 1.x `[Crypto]` digests of its row again. `CryptoHelper.VerifyHash` and SQL `VERIFY_HASH` verify a value against a 1.x digest too. A digest is still hashed again where a row is written without the row it replaces (a child without a primary key, when its parent is updated: `ISS-171`). `MIGRATION-GUIDE` section 2.1 says what carries over, and that a bare `[Crypto]` is SHA256, not 1.x's MD5. SQL `VERIFY_HASH` of a null stored value is false (it threw for a column whose method isn't known).
- **An `InsertRange` can no longer leave a table unreadable** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-129`). Outside a transaction, a page that filled was written in the background from a buffer another step could give back and reuse, so it could hold other pages' rows: the call succeeded and the table no longer opened (most often with `EnableIndexing = false`). A full page is now written with the rest of the batch. A batched insert is also faster: a check that ran for every row runs once per page.
- **A password change can no longer be overtaken by another's** (the review of `ISS-148`). A context that held the database alone for a password change, an AES256 key or a new database gave that hold up and took its shared hold back, holding nothing for an instant; another context could change the password in that instant, and the first then wrote under the old one. Now no other context can hold the database alone in between.
- **Pages a crashed transaction set aside no longer come back in plaintext after `Secure`** (`ISS-149`): the change is refused until they are put back.
- **Two contexts writing their first `[Crypto(AES256)]` values no longer lose one's key** (`ISS-150`), a key made inside a transaction is no longer taken back by its rollback (or a commit undone after a crash) once another context has written values under it, and a key whose save failed (the database file held open by another program) is no longer used unsaved.
- **Referenced objects are read fresh by every query** (`ISS-138`). A query used to hand back referenced objects (an order's customer) from a cache that lived as long as the context: after the customer was updated or deleted, the order still gave the old one, and every query got the same instance, so a change made to it without saving showed up in the next query. Now the rows of one query that reference one row share one object, and nothing is shared between queries: two queries give two instances.
- **Matching by example compares values the way the index does** (`ISS-139`, [`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md)). `Query(example)`, `Query(examples)`, `Update` and `Delete` by an item, and the primary-key and unique checks with `EnableIndexing = false` compared text case-insensitively in the current culture. Now text compares exactly and numbers by value (`12.5m` finds a stored `12.50`), with or without indexing: `Name = "alice"` no longer finds "Alice"; without indexing, a key "abc" is accepted beside "ABC" (as it was with indexing) and a decimal key `12.5` is refused beside `12.50`. A `[Crypto(AES256)]` property now matches by its value; it never matched before. `XmlExtensions.ElementMatch` is marked obsolete: XOD no longer uses it.
