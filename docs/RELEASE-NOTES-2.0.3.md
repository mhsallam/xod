# Release notes: XOD 2.0.3 (2026-11-16)

What changed for users from 2.0.2, collected as each fix landed. Each item links the plan that records it. Before
upgrading, read [Upgrading from 2.0.x](https://xod.sabaeon.com/docs/upgrading) or the section below.

**The release, decided by the owner on 2026-11-16** (`TASK-078`, `TASK-079`): everything in the tree ships as
**2.0.3**, tagged `v2.0.3` and published to NuGet from the GitHub release by the `publish` workflow, which runs the
tests on both frameworks first and waits for the owner's approval. It includes a database open in one context at a
time ([`PLAN-064`](plans/PLAN-064-one-context-per-database.md)), which took out what the tree had added for contexts
side by side (a commit gate, a gate for holds, refusals beside another context's journal or password change) before
any release shipped it; the items below describe what ships. The site's `**Next release:**` labels became 2.0.3 the
same day.

## Upgrading from 2.0.2

- **A database is open in one context at a time** ([`PLAN-064`](plans/PLAN-064-one-context-per-database.md)). Keep
  one `XodContext` per database for the life of the application (a singleton, not one per request or per scope) and
  dispose it: a second open, in the same process or another, throws `DatabaseLockedException`, or waits
  `LockRetryTimeoutMs` for the first to close. A context never disposed keeps the database until the garbage collector
  finalizes it. The CLI (`xod stats`, `xod query`, ...) is refused while an application has the database open.
  `LockDatabase` no longer changes anything.
- **XOD 2.0.2 and this version keep each other out**: 2.0.2 holds the lock files shared, this version alone, so
  whichever opens second is refused. XOD 2.0.0 and 2.0.1 take no lock unless `LockDatabase` is set: don't run them
  beside this version.
- **The lock files `xod.lock` and `<database file>.lock` sit beside the database file and are never deleted**, as in
  2.0.2. Leave them in place. A folder where they can't be created or opened is opened for reading only, and nothing
  keeps a writer out beside such a reader (`ISS-173`).
- **Where the file system gives no lock** (an NFS client, where flock is a lock of the whole process), every open is
  refused, since nothing would keep a second context out. Not run on NFS: reasoned from flock(2). Setting
  `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` opens it, taken as saying nothing else opens the database (`ISS-104`).
- **A context that can only read the folder, with the lock files there, holds the database too**: a reader run by
  another user keeps the application's open out until it closes (reasoned from the code, not run).
- **Matching by example is exact now** (`ISS-139`): code that relied on `Query(example)` or `Update`/`Delete` by an
  item matching text case-insensitively has to match the case, or query with a lambda.

## New

## Behaviour changes

- **A database is open in one context at a time** ([`PLAN-064`](plans/PLAN-064-one-context-per-database.md)). Every
  context holds the database alone, from its open to its disposal; any other open, of this process or another, is
  refused with `DatabaseLockedException` (after `LockRetryTimeoutMs`). In 2.0.2 any number of contexts without
  `LockDatabase` opened one database and wrote it, each with its own caches, which the README and the site never
  supported ("One process owns the folder at a time") and which caused the defects of contexts side by side fixed or
  listed below. Section "Upgrading from 2.0.2" says what to change. The message of `DatabaseLockedException` says so.

- **A rollback writes nothing** ([`PLAN-063`](plans/PLAN-063-deletes-at-the-commit.md)). A transaction writes no data
  file before its commit, and now deletes none either: its deletes are made by its commit, after its journal. A
  rollback forgets them, with its writes, and touches no file; in 2.0.2 a transaction's delete renamed the file aside
  (`<file>.xdel`) at once, and its rollback renamed it back. A crash before the commit leaves no `.xdel`; one 2.0.2
  left is put back by the next open.
- **A password change is refused over anything a crash left** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-149`): a file a transaction set aside (`<file>.xdel`) and a password change's own leftovers, as well as a commit journal and an atomic write cut short (refused before). Open the database again, which puts them back, then change the password.
- **The AES256 key is made when a database gets a secret** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-150`). A new database opened with a password or `DatabaseOptions.PropertyEncryptionKey` records its key when it is created, protected by the application key if the creating context has one, else by the password; `Secure`, `ChangePassword` and `Loose` make it too when there is none and something protects it. A database with no key yet (one from 2.0.2 that has no `[Crypto(AES256)]` value, one created with neither, or one whose unused key a password removal dropped) makes it at its first such value. The key is written to the database file at once, outside any transaction.
- **A password change that couldn't replace a file** (another program had it open) **is finished by its context's next write**, or by the next open ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-148`); in 2.0.2 the files it hadn't replaced stayed under the old password.

## Version

The changes since 2.0.2 add no public type and remove none. One public method is marked obsolete
(`XmlExtensions.ElementMatch`, a compiler warning for code that calls it). The rest changes behaviour:
- a database open in one context at a time, a second open refused;
- matching by example that no longer ignores case;
- a password change refused where it ran before.

Code written for 2.0.2 compiles unchanged (with a warning where it calls `ElementMatch`), but a program can now see a
refusal it never saw, or miss a match it used to get. A minor version (2.1.0) would say that; this release is
**2.0.3** all the same (decided 2026-11-16): 2.0 has few installs so far, and a patch keeps the 2.0 line as one, as
2.0.2 itself was shipped (its minor-version changes released as a patch, `RELEASE-NOTES-2.0.2.md` section
"Version"). The section "Upgrading from 2.0.2" above is what makes up for the patch number.

At the release: 2.0.3 went into `src/Xod/Xod.csproj`, this file became `RELEASE-NOTES-2.0.3.md`, the site's
`**Next release:**` labels became 2.0.3, and the tag `v2.0.3` and the GitHub release started the `publish` workflow.

## Site pages

Brought in line with `PLAN-064` on 2026-11-16 and committed in `xod-site` (`40be5de`, not deployed; the site's tests and build passed, its links resolve): no commit gate or
`xod.gate.lock`, no refusals beside another context, `LockDatabase` without effect, a second open refused; the
upgrading page's section for 2.0.2 matches "Upgrading from 2.0.2" above. Drafted on 2026-11-16 in `xod-site` against the tree at `7e2a5e7`, committed there (`bf37b65`) and not deployed (a deploy waits
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

- **Contexts side by side no longer lose each other's writes** ([`PLAN-064`](plans/PLAN-064-one-context-per-database.md)): a second context can't open the database. In 2.0.2 two contexts without `LockDatabase` could: a password change left the other writing under the old password; a commit cut short and undone took back the other's writes; a commit or rollback wrote over them; a table one context's transaction dropped was made again by the other and its rows lost at the next open; each context's cache missed the other's writes (`ISS-071`, `ISS-147`, `ISS-148`, `ISS-155`, `ISS-164`, `ISS-165`, `ISS-166`, `ISS-167`, `ISS-141`).
- **A commit that fails on a file another program holds no longer stops its context writing** ([`PLAN-064`](plans/PLAN-064-one-context-per-database.md), `ISS-174`). A virus scanner or a backup tool holding a page made the commit fail, and its undo wrote back every file the transaction had changed, the held page too, failed on it, and refused every write until the database was opened again. Now the undo writes back only the files the commit changed: the commit throws, nothing of it stays, and the context carries on.
- **Encrypted XOD 1.x databases open with their password** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-151`), and an update no longer hashes the 1.x `[Crypto]` digests of its row again. `CryptoHelper.VerifyHash` and SQL `VERIFY_HASH` verify a value against a 1.x digest too. A digest is still hashed again where a row is written without the row it replaces (a child without a primary key, when its parent is updated: `ISS-171`). `MIGRATION-GUIDE` section 2.1 says what carries over, and that a bare `[Crypto]` is SHA256, not 1.x's MD5. SQL `VERIFY_HASH` of a null stored value is false (it threw for a column whose method isn't known).
- **An `InsertRange` can no longer leave a table unreadable** ([`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md), `ISS-129`). Outside a transaction, a page that filled was written in the background from a buffer another step could give back and reuse, so it could hold other pages' rows: the call succeeded and the table no longer opened (most often with `EnableIndexing = false`). A full page is now written with the rest of the batch. A batched insert is also faster: a check that ran for every row runs once per page.
- **Pages a crashed transaction set aside no longer come back in plaintext after `Secure`** (`ISS-149`): the change is refused until they are put back.
- **A key made inside a transaction is no longer taken back by its rollback** (or a commit undone after a crash), and a key whose save failed (the database file held open by another program) is no longer used unsaved (`ISS-150`).
- **Referenced objects are read fresh by every query** (`ISS-138`). A query used to hand back referenced objects (an order's customer) from a cache that lived as long as the context: after the customer was updated or deleted, the order still gave the old one, and every query got the same instance, so a change made to it without saving showed up in the next query. Now the rows of one query that reference one row share one object, and nothing is shared between queries: two queries give two instances.
- **Matching by example compares values the way the index does** (`ISS-139`, [`PLAN-061`](plans/PLAN-061-high-issues-from-the-overall-review.md)). `Query(example)`, `Query(examples)`, `Update` and `Delete` by an item, and the primary-key and unique checks with `EnableIndexing = false` compared text case-insensitively in the current culture. Now text compares exactly and numbers by value (`12.5m` finds a stored `12.50`), with or without indexing: `Name = "alice"` no longer finds "Alice"; without indexing, a key "abc" is accepted beside "ABC" (as it was with indexing) and a decimal key `12.5` is refused beside `12.50`. A `[Crypto(AES256)]` property now matches by its value; it never matched before. `XmlExtensions.ElementMatch` is marked obsolete: XOD no longer uses it.
