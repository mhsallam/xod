# Benchmark results

One file per orchestrator invocation. Every run inside carries a `Stamp` object:

| field | meaning |
| --- | --- |
| `LibraryVersion` | assembly version of the `Xod` project that was benchmarked |
| `GitCommit` | `git rev-parse --short HEAD` of the repo that built it |
| `GitWorkingTree` | `clean` / `dirty` at the moment the run started |
| `DotnetRuntime` | runtime version the harness ran on |
| `Machine`, `Os`, `Cpu`, `CpuCount` | where the numbers came from. `Machine` is recorded as `local`: the hostname identifies the owner's PC and is not needed to reproduce a run |
| `CapturedAtUtc` | when the invocation started |

## Naming

```
bench-v<version>-<commit>-<yyyyMMdd_HHmmss>.json
```

The commit is the tree that was measured. A file whose stamp says `dirty` measured a tree that
is not in git: its numbers are a work-in-progress reading, not a release result.

## The one rule

The site publishes no machine identity (CPU, threads, memory, OS build, runtime patch level). Since 2026-11-07 result files do not carry it either: XodBench writes it to a git-ignored record beside each result (`.machine/<result>.json`, `Machine.RecordPath`) for reproducing a run, keeping only the mains-power reading and the .NET major version in the result. Result files from before that date still carry it and are not published. The site's data build refuses machine identity (`xod-site/scripts/public-data.mjs`).

A number is quotable only when its result file is committed and its stamp is `clean`. Anything
else is a working note. That rule exists because the numbers published for v1.2.2 through v1.2.5
could not be traced back to a revision, and two of them (deep-page and footprint ratios) were
computed from a data set that no longer exists.

For the stress runs (`stress/`) the rule is enforced, not only stated: the harness stamps a tree with
any change outside `benchmarks/results/` as `-dirty` (untracked files included), `--publishable`
refuses to start such a run, and the site (`xod-site/scripts/stress-summary.mjs`) voids one (PLAN-043).

## Rendering

The dashboard is the `xod-site` repository (Astro), built from these files. This repo renders nothing.

## Lookup warm-up

`Runner` touches the whole key range before timing the point-lookup step: up to 4,096 evenly spaced
ids, for every engine, so no engine starts cold. XOD builds a page's row index lazily, and without
the warm-up a 250 MB database at 512 KB pages was half cold when the clock started - every cold hit
paid a full-page scan. That made the recorded lookup numbers look 15x worse than the same build
measures with a fair warm-up.

Any file recorded before 2026-11-04 is in `archive/pre-warmup-2026-11-04/` and its lookup numbers
are **not comparable** with the current set.

## Page size

The harness runs XOD at 512 KB pages by default. `XOD_PAGE_SIZE=<kilobytes>` overrides it, which is
how the page-size sweep in `docs/plans/PLAN-035-page-size-sweep.md` was run:

```powershell
$env:XOD_PAGE_SIZE = 64
dotnet benchmarks/XodBench/bin/Release/net9.0/XodBench.dll --worker --engine xod-json --suite flat --scale 250mb --dir C:\temp\pg64
```

512 KB is the default. With a fair warm-up, page size has **no effect on point lookups** (5-6 ms at
every size from 512 KB to 8 MB); larger pages buy bulk-insert time and cost a little on mutations.
Small pages (64-128 KB) make indexed queries 25-100x slower. See `PLAN-035` for the full curve and
the two corrections it needed.

## Producing a full run

```powershell
dotnet benchmarks/XodBench/bin/Release/net9.0/XodBench.dll --suites flat,relational,concurrency --scales 250mb,10k --iterations 3
dotnet benchmarks/XodBench/bin/Release/net9.0/XodBench.dll --suites flat --scales 1gb --iterations 1
dotnet benchmarks/XodBench/bin/Release/net9.0/XodBench.dll --suites relational --scales 40k --iterations 1
```

Three invocations, three files — hence the comma-separated list when rendering. The 1 GB and
40k scales run one iteration each because the Binary and JSON formats take minutes per run at
that size; the iteration count is visible in each file, so a reader can see which values are
single-sample.

## Known gaps in the current data set

- **Relational 40k was recorded twice.** The v1.2.5 file from `87532ba` has three engines because
  JSON and Binary failed the cascade-delete validation there (`ISS-037`, 764 orphan `OrderLine`
  rows); the harness refuses a run rather than record a wrong answer. After the fix the scale was
  re-recorded at `55111fe` with all five engines, so a data set assembled from both files is a
  mixed revision set and the dashboard says so.
- **The cascade fix changes cost, not just correctness.** At 40k the JSON and Binary cascade of 100
  customers takes ~2.4 s / ~1.8 s because each cold child page is loaded and saved individually.
- **1 GB runs are single-iteration**, because JSON and Binary take 74-100 s per indexed-query
  step at that size (`ISS-038`). The `Iteration` field in the file is the record of that.

## `archive/unstamped-2026-11-03/`

Thirteen files produced on the night of 2026-11-03/04, before stamping existed. The code revision
is not recorded in them, the engine labels are whatever the harness called the engine at that
moment (`XOD++ v1.2.3` and `XOD++ (XML)` both appear), and several are smoke-scale runs that
produced the dashboard at the time.

They are kept because the numbers quoted in PLAN-021, PLAN-024 and PLAN-030 came from them, and
deleting them would hide that. They are **not** evidence for any release, and must not be cited
as such: the first run that can be tied to a revision is the one recorded after this directory
was created.
