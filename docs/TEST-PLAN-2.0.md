# TEST-PLAN-2.0: what has to be true before the `2.0` tag

Working name: **XOD 2.0** (the "++" name is dropped at release). Current version `1.2.5`.
This document is the release gate, not a wish list: a layer is either green in the committed suite
or it is listed as a gap, and the tag waits for every layer.

## Why this exists

The suite had 28 tests and all of them were green while a benchmark run of 40,000 customers
returned **764 run-away `OrderLine` rows**. The tests were not wrong; they never asked the question.
A storage engine's release gate is not "the tests pass", it is "the layers that can hide a data
bug all ran".

## Layers

| # | Layer | What it proves | State |
| :-- | :--- | :--- | :--- |
| 1 | API basics (`XodTests`: CRUD, keys, relationships, crypto, triggers, CLI) | the documented surface works | **green** (14 tests) |
| 2 | Concurrency and churn (`XodStressTests`: 32 threads, soak, fuzz, locks, queued transactions, `.xwal` recovery) | it survives contention and repeated cycling | **green** (12 tests) |
| 3 | Referential integrity (`XodIntegrityTests`: cascade, orphans, idempotent delete, cold reopen) | a deleted parent takes its children with it, on every format | **green** (9 cases, `ISS-037` fixed) |
| 4 | Crash safety | a killed process mid-write leaves an all-or-nothing database | **green** (`XodCrashSafetyTests`: deterministic `XOD_CRASH_AFTER_ROWS` kills at four points inside a 100-row batch; every one rolls the batch back completely, no orphans, and the database accepts writes afterwards) |
| 5 | Differential vs a reference | the same operation script gives the same answers as SQLite | **green** (`XodDifferentialTests`: insert 500, update 50 by key, delete 25, index query, count and sum, cold reopen - compared against SQLite on the same script, identical; plus a unique-key violation on both) |
| 6 | Migration from 1.x | a real 1.x database opens, migrates and matches | **green** (`XodMigrationTests`: committed v1.2.5 fixture opens in 2.0, and converts to JSON and Binary with identical rows, graph shape and unique key) |
| 7 | Format equivalence | XML, JSON and Binary return identical results for the same data | **green** (`XodFormatEquivalenceTests`: one script - insert, read by id/index/page/include, update, cascade, cold reopen - serialised and compared across the three formats) |
| 8 | Volume | >= 1 GB / >= 1 M rows, page splits, reopen, deletes | **green** (`XodVolumeTests`, run with `XOD_VOLUME_TEST=1`: 1,000,000 rows of 1 KB payload - over 1 GB on disk - 20 indexed queries exact, 200,000 deletes, cold reopen with counts and spot checks; 31 s) |
| 9 | Public API surface | the 2.0 surface is documented and breaking changes are listed | **green** (`XodApiSurfaceTests`: 605-line snapshot of every exported type and member in `fixtures/api-surface.txt`; any change fails the suite until regenerated with `XOD_UPDATE_API=1`) |
| 10 | Platform matrix | `net8.0` **and** `net9.0`; Windows and Linux | **green** - 54/54 on Windows and 54/54 on Linux (Ubuntu 24.04 container, .NET 8 and 9 runtimes) for both frameworks; `.github/workflows/tests.yml` keeps it running on `ubuntu-latest` and `windows-latest` |

## Exit criteria

1. Layers 1–3 fully green, no skipped cases.
2. Layers 4–6 implemented and green; 7 and 8 green at their stated sizes.
3. No open issue at **Critical** or **High** severity (`ISS-037`, `ISS-038` today).
4. A committed benchmark run for the release commit, clean stamp, dashboard regenerated from it.
5. `MIGRATION-GUIDE.md` proven against a real 1.x database, not only by reading the code.
6. The rename is complete in-tree: no "++" in README, docs, solution, CLI output or package metadata;
   the assembly still exposes namespace `Xod`.

## Blocking defects

| Issue | Severity | What blocks the tag |
| :--- | :--- | :--- |
| — | — | `ISS-037` (cascade integrity) and `ISS-038` (1 GB query cost) are fixed. `ISS-039` (random PK lookups at 1 GB) is Medium and does not block the tag. |

## Rename and branch sequencing

Nothing here runs until the owner says so in the session where it happens; pushing and branch
rewrites are outward-facing.

**Step 0 — owner decisions (blocking):**
- Which repo is canonical for 2.0: replace `master` of `github.com/mhsallam/xod`, or keep `xod2` as its
  own repo and point the old one's README at it.
- NuGet id and visibility for the 2.0 package (`Xod.Core` already exists at `0.1.0-beta` for the 1.x line).
- Whether the 2.0 history is preserved as-is (merge with unrelated histories) or squashed.

**Step 1 — in-tree rename (safe, local, no remote):** README and docs, `XodPlusPlus.sln` → `Xod.sln`,
CLI banner strings, package metadata, license headers. Keep the `Xod` namespace and assembly name, so
consumers see a version bump rather than an API rename.

**Step 2 — legacy line:** in the repo that holds 1.x, tag the last 1.x commit `v1-final`, branch
`legacy-v1` at it, and let `master` carry 2.0. The 1.x code stays readable and buildable; nothing is
deleted.

**Step 3 — move 2.0 onto `master`:** merge or replace, per Step 0, then verify the full gate on the
resulting tree — not on the tree it came from, because the branch operation itself can lose files.

**Step 4 — the tag:** annotated `2.0.0`, message carries the measured numbers and the commit of the
benchmark set. Only after criteria 1–6 are green.

## Current blocker count

4 of 10 layers green; the remaining gaps are crash-kill, differential, migration-from-1.x, format equality, volume assertions, API surface and the platform matrix.
The honest answer to "is it ready for 2.0" is **no**, and the shortest path is `ISS-037` → layers 4–6.
