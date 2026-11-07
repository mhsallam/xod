# Speed experiments — 2026-11-04

Two experiments from `PLAN-036` were implemented, measured and **reverted**. Both were expected to win;
neither did. This report records the numbers and what they rule out, so the next attempt starts from
evidence instead of another plausible story.

Machine: the owner's Windows PC, same session, same build configuration. Tests were green (40/40) at
every step, including while each change was live.

## How the numbers were taken

The first experiment was judged with plain repeated runs and was inconclusive: `updateDeleteMs` swings
±25% between runs on this machine, which is wider than the effect we were looking for. The second
experiment therefore used an **interleaved A/B**: two complete harness builds (one against the modified
engine, one against baseline), alternating before/after in the same session so machine drift cannot
masquerade as an improvement. That protocol is the one to reuse.

---

## Experiment 1 — defer the per-row `.xtab` counter save

**Hypothesis (PLAN-036 §2, proposals 1b and 6).** Every inserted or deleted row opens the table file,
edits `<Page count="...">` and calls `IOService.Save`. At 2,600 deletes that is 2,600 saves; deferring
them to the batch end should cut mutation time substantially.

**Change.** Added `IOService.MarkDirty(XFile)`: inside a batch or `Buffered` durability it adds the
file to the existing dirty set and returns, skipping the cache re-registration, raw-page eviction and
cost recomputation that `Save` performs per row. Applied to the counter update on the delete path and
both `Save(tableFile)` calls on the insert path.

**Result (medians; 5 runs flat, 3 runs relational):**

| Metric | Before | After | Verdict |
| :--- | ---: | ---: | :--- |
| `updateDeleteMs` (250 MB XML) | 49.0 ms | 52.8 ms | inside noise |
| `bulkInsertMs` (250 MB XML) | 1,254 ms | 1,261 ms | no change |
| `cascadeDelete100Ms` (10k relational) | 83.7 ms | 79.9 ms | inside noise |

**Reverted.** The per-row counter save is not where the time goes. `Save` under `Buffered` does no disk
I/O, and its bookkeeping is cheap next to the row work.

---

## Experiment 2 — byte-level replace for pure-scalar updates

**Hypothesis (PLAN-036 §2, proposal 1a).** A single update today parses the old row into `XElement`
(`TryGetRowFromRawPage`), builds a second DOM tree from the POCO (`Write`), deep-compares the two
(`ElementEquals`), removes the old index entries by re-parsing that DOM, then serialises the new tree
back to UTF-8 for the splice. Replacing that with *find the span → format the row from the POCO →
compare bytes → splice* should make updates 2–3× faster.

**Change.**
- Split `SpliceRowInRawPage` into an `XElement` overload and a byte-level overload
  (`byte[] replacementBytes, bool isDelete`), so a caller can splice bytes it already has.
- Added `IOService.TryReplaceStandaloneRow(pageFileName, rowCode, item, meta, exceptionService, out changed)`.
  It guards on `StorageFormat.Xml`, no password and `meta.IsPureScalarStandalone`, finds the row with
  `RawPageBuffer.TryFindRow`, formats the replacement with the existing `FormatStandaloneRowUtf8`,
  byte-compares, and splices only when something changed.
- Wired it into the primary-key branch of `UpdateItem` ahead of the DOM path, using
  `RemoveRowElement(type, null, itemCode)` (the reverse-index `RemoveItemCode` path) and `IndexItem`
  for the index delta.

**Result — interleaved A/B, 4 pairs, all four after-builds slower than all four before-builds:**

| Variant | `updateDeleteMs` per run | Median |
| :--- | :--- | ---: |
| before (baseline engine) | 44.3, 44.9, 59.2, 50.9 | **47.9 ms** |
| after (byte-level path) | 53.1, 60.5, 66.6, 76.3 | **63.6 ms** |

**~33% slower, not faster. Reverted.** Bulk and point reads were unchanged, so the regression is
confined to the update path.

### Why it was slower — hypotheses to test before trying again

1. **The fast path may rarely be entered.** If `TryReplaceStandaloneRow` returns `false` (row not in
   the raw page, page not loadable, metadata guard), the code has already paid
   `ValidateUniqueAndPrimaryConstraints`, a raw-page lookup and a full row format — and then the DOM
   path repeats the validation. That would show up as a pure loss, which matches the data.
2. **`RemoveItemCode` may cost more than parsing the row.** The element-free index removal was chosen
   assuming the reverse index is O(1); if it rebuilds or scans, it trades a small parse for a larger
   walk.
3. **Formatting is not cheap.** `FormatStandaloneRowUtf8` writes every property including the 2 KB
   payload; the DOM path writes the same bytes through `XElement.ToString`, so there was no saving on
   the write side — only on the parse and compare, which may be small next to the splice and index work.
4. **The real cost may be elsewhere entirely** — lock acquisition, `EnsureIndexHydrated`, the splice's
   memory copy of a 512 KB page, or the page flush path.

## What this changes about the plan

- **Proposal 1 is withdrawn.** Both halves of it (deferred saves, byte-level update) measured neutral
  or worse. The DOM work in the update path is not the bottleneck at this scale.
- **Stop guessing; instrument.** Before the next optimisation anywhere in the mutation path, add
  temporary stopwatch counters inside `UpdateItem` (parse, format, compare, index, splice) behind an
  environment flag, run the harness once, and let the numbers choose the target. That is a one-hour job
  and it would have saved both of these experiments.
- **Split the metric.** `updateDeleteMs` mixes 100 updates with 100 deletes and varies ±25% run to run.
  Add `update100Ms` and `delete100Ms` beside it (keeping the existing metric for continuity) so a
  change to one half can be judged.
- **Unchanged priorities with the best expected value:** JSON row spans (`ISS-039`, proposal 2 — a
  ~4× win on JSON point reads, independent of the update path) and the graph-insert binding work
  (proposal 3, ~2×, mechanical). Both still look worth doing; neither depends on proposals 1b/6.

## The rule this session re-learned

A plausible mechanism is not evidence. The interleaved A/B is what turned "2–3× expected" into
"33% worse" in an afternoon, and it is cheap: build both variants once, alternate the runs, compare
medians. Use it before writing the plan, not after.

---

## Round 3 - larger pages, dirty-range flush, and where the mutation time actually goes

Prompted by the owner ("maybe 512 KB is too small to measure"), the page-size sweep was extended past
512 KB and then instrumented. The result changes the plan more than either of the first two experiments.

### The page-size curve (XML, 250 MB, 100k rows)

| page | bulk insert | 1,000 lookups | 20 queries | 100 upd + del | peak mem |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 512 KB | 1,186 ms | 84 ms | 19 ms | 44 ms | 414 MB |
| 2 MB | 775 ms | 49 ms | 22 ms | 68 ms | 419 MB |
| 5 MB | 709 ms | 15 ms | 18 ms | 73 ms | 420 MB |
| 8 MB | 693 ms | 5 ms | 16 ms | 80 ms | 423 MB |

JSON and Binary follow the same shape, more strongly: at 5 MB pages, JSON lookups fall from 1,111 ms
to 82 ms and Binary from 1,253 ms to 142 ms.

### Experiment 3 - dirty-range flush: no win, reverted

`FlushRawPageToDisk` wrote from `DirtyStartOffset` to the end of the page, so the mutation regression at
large pages looked like write amplification. Tracked a `(start, end)` dirty range instead and wrote only
that span. 40/40 tests green.

| pages | before (median of 3) | after (median of 3) |
| ---: | ---: | ---: |
| 8 MB | 58.9 ms | 62.3 ms |
| 5 MB | 57.7 ms | 62.8 ms |

No improvement; reverted. Scattered updates inside a page make the range nearly as large as the page,
so the flush still writes most of it.

### What the instrumentation showed

Temporary counters (since removed) around `EnsureRowIndex`, the expansion branch of the splice and the
splice itself, run once per page size:

| page | index rebuilds | bytes scanned | row expansions | splice writes |
| ---: | ---: | ---: | ---: | ---: |
| 512 KB | 486 | 255 MB | 5 | 200 |
| 2 MB | 123 | 257 MB | 5 | 200 |
| 8 MB | 35 | 289 MB | 5 | 200 |

Two things follow.

1. **Row expansions are not the cost** (5 in 200 mutations), so the "DOM work" theory is dead for this
   workload and the dirty-range fix had nothing to save.
2. **Each page is scanned once to build its row index, and the run scans the whole database once**
   (~255 MB) whatever the page size. That is the real fixed cost - and it interacts badly with the
   benchmark's warm-up.

### The measurement confound this exposes

`Runner` warms **100 lookups** before timing 1,000. A 250 MB database has ~200 pages at 512 KB but only
~50 at 8 MB. With 100 warm-ups, half the pages at 512 KB are still cold, so a timed lookup pays a
full-page scan to build that page's index; at 8 MB nearly every page is warm before the clock starts.

That means the 17x improvement in point lookups is **partly warm-up amortisation, not per-read cost**.
The same trap applies to the recorded v1.2.5 numbers.

### Next (before any more optimisation)

1. **Fix the warm-up**: touch every page before timing (or report cold and warm lookups separately).
   Until then, page-size and lookup numbers are not comparable.
2. **Re-run the curve** with that fixed warm-up at 250 MB and 1 GB, and pick defaults from it.
3. **Then** decide whether `ISS-039` still needs row spans, and whether the 8 MB mutation penalty
   (which is flush write amplification) is worth the point-read gain.

---

## Round 4 - the warm-up was the story, and the page-size curve collapses

`Runner` warmed 100 random lookups before timing 1,000. XOD builds a page's row index lazily, so every
timed lookup that landed on a cold page paid a full-page scan. The warm-up now probes the whole key
range (up to 4,096 evenly spaced ids) for **every engine**, so no engine starts cold and the comparison
stays fair. Timed lookups are still random.

### XML, 250 MB, with the covering warm-up

| page | bulk | 1,000 lookups | 20 queries | 100 upd + del |
| ---: | ---: | ---: | ---: | ---: |
| 512 KB | 906-1,125 ms | **5-6 ms** | 20 ms | 44-52 ms |
| 1 MB | 894-957 ms | 5-7 ms | 17-19 ms | 44-46 ms |
| 2 MB | 810-832 ms | 5 ms | 29-35 ms | 48-52 ms |
| 4 MB | 804-844 ms | 5-6 ms | 19-32 ms | 50 ms |
| 8 MB | 708-772 ms | 5 ms | 19-21 ms | 53-54 ms |

**The 84 ms -> 5 ms "gain" from larger pages was entirely a warm-up artifact.** With every page warm,
point lookups cost ~5 us at any page size. Page size still buys bulk-insert time (1,125 -> 708 ms) and
costs a little on mutations; it does nothing for lookups.

### The same workload, all three engines (512 KB, fair warm-up)

| engine | bulk | 1,000 lookups | 20 queries | 100 upd + del |
| :--- | ---: | ---: | ---: | ---: |
| XOD++ (XML) | 906-1,125 ms | **5-6 ms** | 19-20 ms | 44-52 ms |
| SQLite 3 (WAL) | 1,757-1,770 ms | 6-7 ms | 40-43 ms | 4.3-4.5 ms |
| LiteDB | 2,522-2,564 ms | 41-47 ms | 112-130 ms | 17.8 ms |

XOD's warm point lookups are **on par with SQLite**, not 13x slower. The recorded v1.2.5 figure
(88.9 ms) was measured with the short warm-up. The real remaining gap on this workload is mutations
(~10x) and, at 10k, the relational writes.

### What this means

- `ISS-039` as filed ("random PK lookups") was **a benchmark artifact**, not an engine defect. The
  row-span work in `PLAN-036` proposal 2 is not justified by this metric.
- The recorded result set is not comparable with the harness as it now stands: every `pkLookup1000Ms`
  figure predates the warm-up fix. Re-run and re-stamp the set before quoting any lookup number, and
  before the site refreshes.
- Page-size default stays at 512 KB: it is the best balance once lookups stop dominating the story.
- The next honest optimisation targets are the ones the corrected data still shows: mutations (~10x),
  graph insert (~6.8x) and cascade (~6.5x), all on the write path.

---

## Round 5 - the relational load gap is a document-format page-parse problem

**Provisional: measured while another session was building on the same machine.** Absolute numbers
are unusable (JSON relational load came out 3x the recorded value, cascade varied 530-3,426 ms), so
nothing here is a record. The comparison is still informative because each rep contains all page
sizes, and the ratio is taken inside the rep - machine drift cancels.

### Paired test, JSON at 10k, 7 reps

Ratio of each run to the 512 KB run in the same rep:

| page | ratios per rep | median |
| ---: | :--- | ---: |
| 64 KB | 0.49, 0.57, 0.61, 0.64, 0.72, 0.80, 0.88 | **0.64** |
| 2 MB | 0.66, 0.76, 0.79, 0.82, 0.93, 0.97, 3.07 | 0.82 |
| 512 KB | reference | 1.00 |

64 KB pages make the JSON relational load **~36% faster** in every one of seven paired reps, and the
cascade ~30% faster. 2 MB is inconsistent and no better than 512 KB overall.

### Control: the same test on XML

| page | load | cascade | graph insert |
| ---: | ---: | ---: | ---: |
| 64 KB | 181 / 166 ms | 142 / 122 ms | 5,280 / 5,395 ms |
| 512 KB | 182 / 175 ms | 75 / 76 ms | 4,484 / 4,495 ms |

No page-size effect on the XML load path, and small pages are **worse** for XML cascades and graph
inserts. That is the pattern the hypothesis predicts: XML slices rows out of page bytes and never
parses a page; JSON parses a whole page per row, so a smaller page is cheaper per read.

### What this means

- The document-format relational gap is at least partly **page granularity**, not the graph walk.
- A per-format page-size default is a legitimate lever: something in the 64-128 KB range for JSON and
  Binary, 512 KB for XML. It needs a clean machine to choose the number, because smaller pages also
  make indexed queries 25x slower on document formats (PLAN-035).
- The code fix (row spans) would remove the trade-off rather than move it, but the page-size default
  is a zero-risk experiment that can be done first.

**Next:** when the machine is idle, run the full matrix (64/128/256/512 KB x flat and relational x
JSON/Binary, plus XML as control), record it with a clean stamp, and pick per-format defaults from the
data.
