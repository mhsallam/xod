# PLAN-037: Index hydration on first write (cold open + one insert)

- **Task ID**: `TASK-042`
- **Issue**: `ISS-040` (new)
- **Status**: Proposed — design agreed, not implemented
- **Predecessor**: `PLAN-035` (page-size matrix), `REPORT-2026-11-04-speed-experiments.md`

## 1. The problem, measured

The benchmark card **"Open, then write a record"** is the closest of SQLite's ten wins to flip:

| 250 MB, 100k rows | XOD | SQLite | needs |
| :--- | ---: | ---: | ---: |
| open + 1 insert + commit | 17.86 ms | 2.671 ms | **6.7x** |

An isolated program against the same 250 MB XML database (buffered durability, 512 KB pages, same
options as the harness) shows where those milliseconds go:

| step | median |
| :--- | ---: |
| `new XodContext(...)` (open) | **0.44 ms** |
| first `Insert` | **50.6 ms** |
| first `Flush` | 2.0 ms |
| second `Insert` | 0.05 ms |
| second `Flush` | 5.4 ms |

Opening is already lazy and nearly free. The cost is the **first write**, and only the first.

## 2. Root cause (instrumented, 2026-11-05)

Temporary counters inside `IndexService.Persistence.cs` (index load, block hydration, full snapshot),
run against the same 250 MB database and removed afterwards:

| measurement | value |
| :--- | ---: |
| index load, per open (`TryLoadFromDisk`) | **~24-33 ms** |
| block hydration during the write | **0 blocks, 0 ms** |
| full index snapshot (at seed time, not the insert) | 25.5 ms |

So the plan's first draft was wrong about the mechanism. **Nothing is hydrated.** The first write
pays two things:

1. **`TryLoadFromDisk` reads and parses the whole `.xidx` file** — for 100k rows and three indexed
   properties that is tens of megabytes, ~24-33 ms on this machine, and it happens on the first
   mutation of the type because `EnsureIndexHydrated` runs there.
2. A full index snapshot (25 ms) happens when the seeding session closes, so it is part of the seed,
   not of the measured first insert. A change to force append-instead-of-snapshot was tried and made
   the first insert no better (54 ms against 32 ms), so it was reverted.

The cost that matters is the **whole-file index read**. The fix is to make the sidecar readable in
parts — the bucketized v3 below — not to hydrate less.

## 3. Goal and constraints

**Goal:** cold open + one insert + commit **≤ 3.0 ms** on the 250 MB XML data set, so the card can flip.

Constraints:

- No public API change, no format change for pages or user data — `.xidx` is a **derived sidecar**
  and can be rebuilt by scanning pages, which is the safety valve this design relies on.
- XML, JSON and Binary must all pass `XodIntegrityTests`, `XodCacheTests` and the stress suite.
- Guardrails: `pkLookup1000Ms`, `indexedQuery20Ms`, `updateDeleteMs` and `bulkInsertMs` may not regress
  by more than 5% on the same-machine interleaved A/B.
- Encrypted databases (`Password` set) fall back to the current full-load path. Out of scope to
  optimize; must not break.

## 4. Options considered

| # | Option | Verdict |
| :--- | :--- | :--- |
| 1 | Do nothing; document that a cold first write pays index load | Rejected: it is a real user-facing cost (desktop app opens, saves one record), not only a benchmark artefact |
| 2 | Warm the index in the background on open | Rejected: the first write still waits for it, and it burns CPU on read-only opens |
| 3 | Binary-search the raw block bytes per key | Rejected: variable-length records need an offset index, which is the bucketing work below without the read-locality benefit |
| 4 | **Bucketized `.xidx` v3: hydrate only the bucket a key hashes to** | **Chosen** |
| 5 | Full B-tree sidecar | Rejected for now: much larger change for the same cold-start win |

## 5. Design — `.xidx` v3, bucketized

### 5.1 File layout

```
magic "XIDX3\0"  (8 bytes)
int32  bucketCount            (fixed 2048, power of two)
int32  bucketCount * (int64 offset, int32 length)      // directory
per bucket: int32 entryCount, then entries sorted by normalized key:
            (string normKey, int32 codeCount, string[] itemCodes)
delta log (unchanged semantics): op, propName, normVal, itemCode
```

Buckets are keyed by `hash(normKey) & (bucketCount - 1)`. One property's keys are spread across
buckets; each bucket holds a small sorted list (~50 entries for 100k keys / 2048 buckets).

### 5.2 Read path

- Point lookup / uniqueness check / delete: seek to the directory entry, read that bucket only,
  materialise ~50 entries, then consult the in-memory delta overlay.
- At flush, dirty buckets are rewritten; a full compaction still exists for `MaxDeltaBeforeCompaction`.
- The hot path never touches an `UnhydratedIndexBlock` larger than a bucket.

### 5.3 Migration and rebuild

- Reader accepts v2 and v3. A v2 file is **rebuilt into v3** on first open (scan the pages, which is
  the existing fallback), once, and documented as a one-time cost.
- Any structural doubt — bad magic, truncated directory, bucket offset out of range, `.xpag` newer
  than `.xidx` (the existing freshness check) — deletes the sidecar and rebuilds from pages. The
  database itself is never at risk.

### 5.4 What stays the same

- `pendingDeltas` and the append-delta log are kept; only hydration granularity changes.
- `reverseHydratedTypes` / `codeToIndexEntries` are unchanged.
- `RemoveRowElement`, `IndexItem` and the query planner keep their signatures.

## 6. Phases

1. **Format v3** — writer, reader, directory, rebuild path, behind `XOD_INDEX_V3=1` while it bakes.
   Tests: round-trip, 2048-bucket spread, rebuild from a deleted sidecar, v2 file converted, encrypted
   DB untouched.
2. **Bucket-level hydration** — replace `EnsurePropertyHydratedUnlocked` on the write path with
   `EnsureBucketHydrated(key, normVal)`; keep the full-hydrate path for range-like operations and as
   the fallback when a bucket is unreadable.
3. **Measure** — interleaved A/B against a baseline build, five pairs, on `coldOpenInsertMs` plus the
   guardrail metrics, at 250 MB and 1 GB. Record with a clean stamp.
4. **Decide** — flip the flag to default if the A/B wins and guardrails hold; otherwise revert to v2
   and record the negative result in the experiment log.
5. **Clean up** — delete the v2 writer once v3 has a release behind it, keep the v2 reader for one
   minor version.

## 7. Tests (new, `XodIndexFormatTests`)

1. v3 round-trip: insert 10k rows, close, reopen, every key found.
2. Uniqueness is enforced without full hydration: insert a duplicate `Sku` after a cold open → throws.
3. Delete after cold open removes the key; count and re-insert work.
4. Rebuild: delete the `.xidx`, reopen, all keys found, write path correct.
5. Corrupt directory: truncate the file, reopen, rebuild, no exception escapes.
6. v2 → v3 conversion: ship a v2 fixture, open, assert v3 on disk afterwards.
7. Crash between rebuild and write: simulated by deleting the sidecar mid-sequence; the next open
   still answers correctly.
8. Encrypted database: the v3 path is skipped and the old behavior is used.

## 8. Risks and mitigations

| Risk | Mitigation |
| :--- | :--- |
| Uniqueness enforced only within a bucket | All entries for the same normalized key hash to the same bucket by construction; a differential test compares v3 results against the hydrated v2 path over 10k random keys |
| Bucket skew | Fixed 2048 buckets with a good string hash; test asserts max bucket ≤ 4× mean on the benchmark data |
| Crash during bucket rewrite | Write to `.xidx.tmp` and rename (the existing atomic-write helper), never in place |
| Migration cost on big databases | Rebuild is a page scan already implemented for the "index file missing" case; measure at 1 GB |
| Two formats in the wild | v3 writer only after the A/B; v2 reader kept for one minor release |
| Encrypted databases | Explicit fallback, covered by a test |

## 9. Measurement protocol

Same discipline that killed three earlier proposals:

- Two prebuilt harness binaries (baseline and v3), **interleaved** runs, five pairs per metric.
- Report the median per variant and the per-pair ratio; a win must be consistent across pairs, not an
  average dragged by one run.
- Success: `coldOpenInsertMs` ≤ 3.0 ms, guardrails within 5%, 40/40 tests, no change in
  `XodIntegrityTests`.

## 10. Out of scope

- Range scans over indexes (not supported today; bucketing preserves equality semantics only).
- Changing page format, durability, or the mutation paths that failed in
  `REPORT-2026-11-04-speed-experiments.md`.
- Making the *second* write faster; it is already 0.05 ms.

---

## 11. First shipped step (2026-11-05): deferred block reads and a cheap freshness check

Two changes, no format change, both behind the existing behaviour:

1. **`UnhydratedIndexBlock` can point at the file.** `TryLoadFromDisk` now walks the property headers
   and skips the block payloads instead of `File.ReadAllBytes` on the whole `.xidx`; a block is read
   from disk only when it is hydrated. Encrypted databases keep the in-memory path.
2. **The freshness check reads page names as text.** It used to open the `.xtab` through the file
   service and enumerate its DOM, which measured **9-21 ms** on a 100k-row table and was most of the
   load time. It now scans the table document for `file="..."` and stats the first two and last two
   page files, which measures **0.0 ms** and keeps the same check.

Measured with the interleaved A/B protocol, four pairs, both harness builds prebuilt:

| `coldOpenInsertMs` | runs | median |
| :--- | :--- | ---: |
| before | 18.90, 19.78, 20.52, 20.81 | **20.15 ms** |
| after | 14.96, 15.16, 16.10, 16.84 | **15.63 ms** |

**22% faster, every after-run below every before-run.** `pkLookup1000Ms` (4-5 ms) and
`updateDeleteMs` (44-56 ms, noisy) unchanged within their bands; tests 40/40.

That does not flip the card yet (SQLite 2.671 ms). The remaining ~15 ms is in the first
insert-and-commit path, not in the index load: the index is now read lazily and the freshness check is
free, so the next measurement should instrument `EnsureIndexHydrated`'s remaining work, the page
append and the commit flush separately before any further change.
