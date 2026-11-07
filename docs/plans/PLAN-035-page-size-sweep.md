# PLAN-035: Page-Size Sweep (32 KB → 512 KB)

- **Task ID**: `TASK-040`
- **Issue**: none (investigation)
- **Status**: Implemented — conclusions recorded, no default changed

## Question

The benchmark pins every XOD database at **512 KB pages**. That is tuned for bulk load, and it
looked like the wrong shape for point reads: a cold lookup has to bring a whole page through the
cache to extract one 2 KB row. Would a smaller page make point reads faster?

## How it was measured

`benchmarks/XodBench` now honours an `XOD_PAGE_SIZE` environment variable (kilobytes), so the real
harness — same data, same operations, same machine — can run the sweep instead of a side program.
Each row below is one 250 MB flat run (100,000 rows) on XOD, 512 KB unless stated.

## Results

**XML**

| page | bulk insert | 1,000 lookups | 20 indexed queries | deep page | 100 upd + 100 del | size | peak mem |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 32 KB | 5,769 ms | 105 ms | 93 ms | 1.44 ms | 99 ms | 256.7 MB | 540 MB |
| 64 KB | 3,098 ms | 112 ms | 61 ms | 1.09 ms | 64 ms | 255.2 MB | 492 MB |
| 128 KB | 1,869 ms | 111 ms | 48 ms | 0.23 ms | 51 ms | 254.4 MB | 455 MB |
| 256 KB | 1,437 ms | 118 ms | 34 ms | 0.17 ms | 49 ms | 254.0 MB | 415 MB |
| **512 KB** | **1,186 ms** | **84 ms** | **19 ms** | 0.18 ms | **44 ms** | **253.8 MB** | **414 MB** |

512 KB wins or ties everywhere for XML. The intuition was wrong because XML slices a row out of the
page bytes: it never parses the page, so page size only decides how many page files exist and how
often the index has to open one. Fewer, larger pages win.

**JSON**

| page | bulk | 1,000 lookups | 20 indexed queries | 100 upd + 100 del | size | peak mem |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 32 KB | 22,856 ms | 1,348 ms | 912 ms | 482 ms | 335.7 MB | 595 MB |
| 64 KB | 5,868 ms | **654 ms** | 229 ms | **128 ms** | 330.5 MB | 693 MB |
| 128 KB | 3,762 ms | 840 ms | 208 ms | 106 ms | 327.9 MB | 752 MB |
| **512 KB** | **2,414 ms** | 1,249 ms | **9 ms** | 188 ms | **325.9 MB** | 996 MB |

**Binary**

| page | bulk | 1,000 lookups | 20 indexed queries | 100 upd + 100 del | size | peak mem |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 32 KB | 36,124 ms | 3,361 ms | 1,268 ms | 628 ms | 73.7 MB | 570 MB |
| 64 KB | 9,840 ms | 4,005 ms | 812 ms | 473 ms | 72.0 MB | 601 MB |
| 128 KB | 6,003 ms | **795 ms** | 236 ms | 442 ms | 71.0 MB | 676 MB |
| **512 KB** | **4,525 ms** | 1,366 ms | **8 ms** | **206 ms** | **70.6 MB** | 773 MB |

## Conclusions

1. **The default stays 512 KB.** For the benchmark's mix — bulk load, indexed queries, updates,
   footprint — it is the best or tied setting on every format.
2. **Page size is a real workload knob, with a sharp trade-off.** On JSON and Binary, 64–128 KB pages
   cut point lookups by roughly 1.6–1.9× (JSON 1,249 → 654 ms; Binary 1,366 → 795 ms) but make
   indexed queries 25–100× slower (JSON 9 → 229 ms) and bulk load 2–4× slower. There is no single
   setting that wins both; a workload that is mostly "fetch by id" on a document format should choose
   small pages deliberately, and be told what it costs.
3. **This does not close the point-read gap.** Even at its best, JSON needs 654 ms for 1,000 lookups
   where XML needs 84 ms. The remaining cost is parsing the page per row, which is `ISS-039`
   (row-level access); the sweep only softens it.
4. **No code change beyond the knob.** The default is unchanged, so no existing result is invalidated.

## Follow-up

- Document the trade-off in `benchmarks/results/README.md` next to the run instructions, and consider
  surfacing it in the migration guide ("choose 64–128 KB for lookup-heavy document workloads").
- The real speed work is `ISS-039`: a row span index per JSON page and an offset/block scheme for
  Binary, so a point read stops costing a whole-page parse.

---

## Follow-up 2026-11-04: the sweep stopped too early

The original sweep tried 32 KB - 512 KB and concluded 512 KB was the right default. That conclusion only
held inside the range tested. The owner suggested 5 MB; measured on the same harness, 250 MB, XML:

| page | bulk insert | 1,000 lookups | 20 queries | 100 upd + 100 del | peak mem |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 512 KB | 1,186 ms | 84 ms | 19 ms | 44 ms | 414 MB |
| 1 MB | 815 ms | 91 ms | 18 ms | 64 ms | 419 MB |
| 2 MB | 775 ms | 49 ms | 22 ms | 68 ms | 419 MB |
| 4 MB | 681 ms | 23 ms | 18 ms | 66 ms | 417 MB |
| 5 MB | 709 ms | 15 ms | 18 ms | 73 ms | 420 MB |
| 8 MB | 693 ms | **5 ms** | 16 ms | 80 ms | 423 MB |
| 16 MB | 652 ms | 6 ms | 23 ms | 88 ms | 433 MB |

Point lookups improve **17x** between 512 KB and 8 MB (84 ms -> 5 ms, faster than SQLite's 6.6 ms on the
same table), bulk load improves ~40%, indexed queries are unchanged, and the cost is mutations
(44 ms -> 80 ms) plus a little memory.

**JSON and Binary at 5 MB** show the same shape, and larger:

| format | metric | 512 KB | 5 MB |
| :--- | :--- | ---: | ---: |
| JSON | 1,000 lookups | 1,111 ms | **82 ms** |
| JSON | bulk insert | 2,414 ms | 1,946 ms |
| JSON | updates/deletes | 188 ms | 647 ms |
| JSON | peak memory | 996 MB | 1,408 MB |
| Binary | 1,000 lookups | 1,253 ms | **142 ms** |
| Binary | bulk insert | 4,525 ms | 4,227 ms |
| Binary | updates/deletes | 206 ms | 954 ms |
| Binary | peak memory | 773 MB | 1,201 MB |

Why: with ~50 pages instead of ~200-1,700, every page is resident, so a point read stops paying a cold
page load (XML) or a whole-page parse per row (JSON/Binary) - `ISS-039` is largely a page-count problem,
not only a row-span problem.

Why mutations get worse: `FlushRawPageToDisk` writes from `DirtyStartOffset` to the end of the page
(`IOService.RawPages.cs`, `FlushRawPageToDisk`). One updated row near the start of an 8 MB page means an
8 MB write.

**Corrected conclusion:** page size is the biggest lever found so far, and the default must be revisited
per format. Next: track a dirty *range* rather than a start offset, then re-run the matrix at 250 MB and
1 GB and pick defaults from the data.

---

## Second follow-up (same day): that correction was itself an artifact

The 17x lookup gain reported above came from the harness warming only 100 lookups: at 512 KB a 250 MB
database has ~200 pages, so half the timed lookups paid a cold full-page scan, while an 8 MB database
was fully warm before the clock started.

`Runner` now probes the whole key range (up to 4,096 evenly spaced ids) for every engine before timing.
With that fix, XML point lookups are **5-6 ms at every page size** (512 KB through 8 MB), and the same
warm-up puts SQLite at 6-7 ms and LiteDB at 41-47 ms on the same table. Page size does not affect
lookups; it only buys bulk-insert time and costs a little on mutations.

**Final conclusion:** keep the 512 KB default. The larger-page tables in this file are real measurements
of a real effect, but the effect they appeared to have on lookups was the warm-up, not the pages.

---

## Final matrix (2026-11-05, idle machine, 28 runs)

Run after the other agent's build finished, two reps per cell, order rotated between reps. Medians.

**JSON, flat 250 MB**

| page | bulk insert | 1,000 lookups | 20 queries | 100 upd + del | size |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 64 KB | 6,122 ms | 2.6 ms | 9.0 ms | **105 ms** | 330.5 MB |
| 128 KB | 3,898 ms | 2.4 ms | 8.9 ms | **96.9 ms** | 327.9 MB |
| 256 KB | 2,863 ms | 2.5 ms | 8.2 ms | 106.7 ms | 326.6 MB |
| 512 KB | **2,552 ms** | 2.6 ms | **8.2 ms** | 176.7 ms | **325.9 MB** |

**JSON, relational 10k**

| page | graph insert | load 200 graphs | cascade delete |
| ---: | ---: | ---: | ---: |
| 64 KB | 7,273 ms | **1,966 ms** | **498 ms** |
| 128 KB | 6,955 ms | 2,213 ms | 617 ms |
| 256 KB | 6,703 ms | 2,406 ms | 718 ms |
| 512 KB | **6,462 ms** | 2,529 ms | 849 ms |

**Binary, relational 10k**

| page | graph insert | load 200 graphs | cascade delete |
| ---: | ---: | ---: | ---: |
| 128 KB | 6,479 ms | **1,695 ms** | **539 ms** |
| 512 KB | **5,693 ms** | 2,663 ms | 655 ms |

**XML control**

| page | bulk | 1,000 lookups | 100 upd + del | load graphs | cascade |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 64 KB | 3,338 ms | 4.0 ms | 62.3 ms | 171 ms | 227 ms |
| 512 KB | **1,174 ms** | 4.6 ms | **43.3 ms** | 173 ms | **75 ms** |

### What the matrix decides

- **Default stays 512 KB for every format.** It wins bulk load on all three (2.4-2.8x over 64 KB),
  graph insert, footprint and XML mutations. Point lookups are insensitive to page size.
- **A per-workload knob, not a per-format default.** JSON and Binary pay a whole-page parse per row on
  the relational read path, so 64-128 KB pages buy 22-41% there (JSON load 2,529 -> 1,966 ms, cascade
  849 -> 498 ms; Binary load 2,663 -> 1,695 ms) - at the cost of roughly doubling bulk-insert time.
  That is a documented trade-off for a relational-heavy document workload, not something the default
  should choose for everyone.
- **Smaller pages do not close the relational gap.** Even at 64 KB, JSON loads 200 graphs in 1,966 ms
  where SQLite needs 12 ms and XML needs 171 ms. The remaining cost is the per-row page parse, not the
  page count - the case for row-level access on the document read path is now made by these numbers
  rather than by a benchmark artifact.
