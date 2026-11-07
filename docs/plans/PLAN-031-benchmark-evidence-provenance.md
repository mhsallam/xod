# PLAN-031: Benchmark Evidence — Stamped Runs, Committed Results, Computed Scorecard

- **Task ID**: `TASK-036`
- **Fixes Issue**: [`ISS-036`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement

The fair harness (PLAN-028) fixed how numbers are *produced*, but not how they are *kept*:

1. **Results were written to `bin/`**, which git ignores. Every published number therefore lived
   in a build folder: one `dotnet clean` from being gone, and invisible in a clone.
2. **No run recorded what produced it.** A result file held engine labels and timings. Which
   commit, which runtime, which machine, whether the tree was dirty — all unrecorded. This is how
   `XOD++ v1.2.3` and `XOD++ (XML)` runs from different code revisions ended up pooled in one
   folder and compared against each other.
3. **The dashboard's scorecard was hand-typed HTML.** Four cards of frozen numbers and verdicts
   (`"XOD #1 Fastest"`, `"1.43s vs 1.96s"`, `"1,700x"`, `"3.5x slower"`), written once and never
   regenerated, displayed above a banner promising fair methodology. It did not read the result
   set at all: the numbers stayed wrong for as long as nobody re-typed them, and a "winner" badge
   would stay on an engine that had lost.
4. **The committed dashboard was a 9-run smoke set** (4,000 rows), while the plans quoted 250 MB
   and 1 GB figures that existed only in `bin/`.

## Design & Refactoring Steps

1. `RunResult.Stamp` (`RunStamp`): version, commit, working-tree state, runtime, machine, OS, CPU,
   core count, capture time. `BenchStamp.Capture()` reads git through a process call and returns
   the literal `"unknown"` when git cannot be read — an empty string would read as a valid value.
2. Orchestrator stamps every result, prints the stamp in the header, and names the file
   `bench-v<version>-<commit>-<timestamp>.json`.
3. Results and dashboard are written into the repo (`benchmarks/results/`, `benchmarks/dashboard.html`),
   not beside the binaries.
4. `--dashboard` accepts a comma-separated list of result files, so a run kept as several
   invocations renders as one page.
5. `DashboardGenerator` computes the scorecard from the result list: medians per engine, best
   value marked per metric, win counts derived, ties counted for each engine. The hand-typed cards
   are removed. A provenance banner states version, commit, tree state, machine and run count, or
   says "unstamped data set" when older files lack a stamp.
6. Child workers are launched through `Environment.ProcessPath`, and through `dotnet <dll>` when
   the harness itself was started that way — the previous code spawned `dotnet` with no entry
   point, so every worker failed when the harness was run as `dotnet XodBench.dll`.

## Verification Plan

- `benchmarks/results/README.md` states the rule: a number is quotable only when its file is
  committed and its stamp is `clean`.
- A committed v1.2.5 run at 250 MB, 1 GB and 40k relational with a clean stamp.
- Dashboard regenerated from those files; the rendered HTML contains the computed scorecard and
  none of the hand-typed strings (checked by grep for `XOD #1 Fastest`, `1,700x`, `3.5x slower`).
- Pre-stamping files are kept under `benchmarks/results/archive/unstamped-2026-11-03/` and marked
  as not citable.

## Superseded for rendering

The computed dashboard described here is now built by the separate `xod-site` repository (`PLAN-034`). The stamping, the result files and the rule in `benchmarks/results/README.md` remain the source of truth; only the HTML generator is on its way out.
