# PLAN-042: Benchmark reporting refresh (site copy, harness, one-hour runs)

- **Task ID**: `TASK-053`
- **Status**: Implemented locally on 2026-11-06 - harness, site summary and copy changed; three
  capacity runs and one 45-minute soak run. Not committed, not deployed: the copy waits for the
  owner's approval. Followed by [`PLAN-043`](PLAN-043-stress-publishing-run.md): these runs ran
  uncommitted code (`dceb41e-dirty`), so the site now voids them; PLAN-043 is the run that replaces them.
- **Related**: [`PLAN-040`](PLAN-040-stress-and-endurance.md) (stress design and its pass criteria),
  [`PLAN-041`](PLAN-041-insert-rate-independent-of-table-size.md) (the insert fix the capacity
  section predates), `ISS-043` (crash window, open)

## What the site showed before this plan, against the raw files

### Endurance section (`xod-site/src/pages/benchmarks.astro`, `#endurance`)

Source: `benchmarks/results/stress/stress-soak-20261106_105217.jsonl`, the newest `soak` file.

| The site says | The file and the harness say |
| :--- | :--- |
| "Eight threads, four hours." | Planned 480 min; the last sample is at 324 min (5 h 22 min). There is no `end` row: the run was stopped from outside, not by the harness. |
| "eight workers reading and writing while a writer keeps committing" | 8 workers on one shared `XodContext`: 6 do `Find` by `Id`, 2 do `Find` + `Update` (`Price += 0.5`). There is no separate writer. |
| Writes "committed alongside the reads" | Durability is `Buffered` and `Soak()` never calls `Flush()`. The database size is 1,020.6 MB in all 323 samples, through 183.6M updates; the process ended without a clean close. No evidence that any update reached disk. |
| Reads over the database | Ids 1-200,000 only (`rowCap`), out of 2,000,000 seeded rows. In-memory lookups. |
| Read p99 per minute | p99 of the last 20,000 reads in the shared queue when the sample is taken: at ~200k reads/s that is the last ~0.1 s of each minute. |
| "Memory ... peak, settling to 2.5 GB" | `rssMb` is the working set. From about 12:56 UTC (minute ~304) reads fall to 94k-140k/s and the working set drops from 4.8 GB to 1.6 GB: the OS trimming it, which points to other work on the machine, not XOD releasing memory. |
| Read p99 353 us (last 30 samples), reads 130,592/s | Both come from the window above. By PLAN-040's own rule ("If another agent is building, the run is void") that window is void. |
| Header `rows: 20000000` | The soak seeds `SeedRows` (2M); the header logs the capacity default. |

PLAN-040's agreed pass criteria, applied to the published summary: last reads / first reads = 76%
(criterion 80%), last p99 / first p99 = 1.77x (criterion 1.5x). As published, the run fails its own
criteria, and the failure sits in the window that was not idle.

PLAN-040 also specified 1 writer, a 70/25/5 read/write/graph-insert mix, 5-10M rows, write p99, GC
counts and pauses, error count and load at start and end. The harness records none of these.

The earlier file `stress-soak-20261106_043811.jsonl` has a 185-minute gap between two samples (the
machine slept); the site correctly ignores it.

`stoppedBy` is read by `scripts/build-data.mjs` but the harness never writes it, so the "Stopped at the
four-hour mark by choice" sentence can never show.

### Capacity section (`#capacity`)

Source: `stress-capacity-20261106_162635.jsonl`, the run on the code before PLAN-041. The run on the
final code is `results/unpublished/stress-capacity-20261106_171342.jsonl` (not picked up by the build).

| The site says | Problem |
| :--- | :--- |
| "Insertion slowing from 41,459 to 3,842 rows a second ... is the next thing to improve." | Fixed by PLAN-041 (232k-239k rows/s at 9-10M on the final code). Stale. |
| "Cold reopen 3.3 ms" | `reopenMs` times `new XodContext(path)` only. The index loads lazily on the first query, and the files were just written, so the OS cache is warm. It is "open", not a cold reopen to a first answer. |
| "random-read p99 is 20,247 us at ten million rows against 1,416 at one" | p99 of 100 probes is roughly the second-slowest probe, and the probes after a reopen include the first index load. The new run shows 452 us at 10M. The caption's explanation ("reading away from the cache") is not measured. |
| Insert rate "first million, then at ten million" | `firstRate` is the first sample, wherever it lands: 1.25M rows in the published run (30 s samples), 330k in the new run (2 s samples). |
| "about 510 bytes a row" | Hard-coded. The new run's file is 4,963.9 MB at 10M rows: 520 bytes a row. |
| Peak memory "the practical ceiling at this size" (16.2 GB) | Working set, samples only. The new run peaks at 12.3 GB at the 10M checkpoint, which `build-data.mjs` does not read. |
| (nothing) | `closeMs` is not shown. The new code folds the index log on close: 1.4 s at 10M. A reader should see it. |

Feeding the new run through today's `build-data.mjs` would print: "in 1 minutes" (43 s between first and
last sample; the true wall time is 52 s), 4.68 GB (last sample at 9.58M rows, not the 4.85 GB at 10M),
9.5 GB peak (misses 12.3 GB), and "162,437 -> 239,263/s" (reads as a rising rate).

### Landing page (`xod-site/src/pages/index.astro`)

- Line 55: "Eight threads read at over 140,000 rows a second while a writer keeps committing." Six
  threads read (about 197k/s on average); nothing commits.
- Line 75 "Crash-safe writes when power is cut" and line 130 "ACID + crash recovery": `ISS-043` (High,
  open) is a crash window in which rows written by a batch are on the pages but missing from the index.
  Benchmarks also run without fsync. No published run measures a power cut.

## What was done

The review above led to these changes:

### Harness (`benchmarks/XodBench`)

- `StressRunner.cs`, soak: 8 reader threads (Find by primary key over the hot rows) and one writer
  thread (Find, `Price += 0.5`, Update) that commits with `Flush()` every `--flush-ms` (1 s). At the
  end: flush, close, reopen, first answer, then a check that every row is there, that the hot rows'
  prices equal the seed values plus 0.5 per counted update, and that the unique key holds. One writer
  only (`--writers 0|1`): two writers on one row could lose an update and break the check.
- Every read, update and commit is timed into a per-thread `LatencyRecorder` (log-linear buckets,
  at most 1/32 high); each sample reports the interval's count, p50, p99, p99.9 and max.
- Each sample also records private memory, GC heap, GC counts and pause share, the process's CPU
  share, the whole machine's CPU share, "other" CPU (machine minus process) and free memory.
- The header carries the commit (`-dirty` when tracked files differ), CPU, RAM, AC power, runtime,
  the affinity, and the workload as run (no more `rows: 20000000` on a soak).
- The harness always writes an `end` row: `planned`, `interrupted` (Ctrl+C, through a cancellation
  token) or `error` (a worker's exception stops the run and is recorded).
- Capacity samples every `--sample-rows` (250,000), so a rate means the same at any speed; each
  checkpoint reports `closeMs`, `reopenMs` (open only) and `firstAnswerMs` (open plus the first
  query, which loads the index), then 1,000 probes (`--probe`, was 100). `--checkpoints` is settable.
- `--cpus performance` (the default) pins the run to the most performant cores of a hybrid CPU, read
  from `GetSystemCpuSetInformation` (EfficiencyClass).
- Tests: `benchmarks/XodBench.Tests` (9), seen red first (they did not compile against the old
  harness). Two guards proven by breaking the code: switching the commit off fails
  `Writes_are_committed_during_the_run_and_verified_after_a_reopen`; sampling one reader's recorder
  fails `Every_read_in_an_interval_is_in_that_interval_s_percentiles`. The first version of that test
  could not fail (count and total came from one snapshot) and was rewritten to compare against the end
  row. Added to `.github/workflows/tests.yml` (net9.0 jobs).

### Site (`xod-site`)

- `scripts/stress-summary.mjs` computes every stress number the page prints, from the files. A soak
  counts only with an end row saying `planned`, at least 80% of samples on a quiet machine (other
  programs under 10% of the CPU, at least 4 GB free) and half of each comparison window quiet; busy
  samples are drawn but not counted. The pass criteria are PLAN-040's, scaled to the run: after a
  5-minute warm-up, minutes 5-15 against the last 10 minutes; reads and commits within 80%, read p99
  within 1.5x, private memory within +15%, and the data check passed.
- Capacity: rates over the first and the last million rows, by time; size, bytes per row and peak
  memory from the final state, checkpoints included; the newest build's runs as a median with its range.
- Files without the new header are not summarized, so the old soak can no longer reach the page.
- `npm test` (10 tests, red first: the module did not exist) runs as the first step of `npm run build`.
- `benchmarks.astro`: both sections rewritten from the data (title, threads, minutes, the verdict,
  a criteria table, the verification sentence, busy-sample marks, reopen to first answer, close time,
  the dips explained). `index.astro`: the "eight threads … 140,000 rows a second" claim now appears
  only when the soak passes, with its own numbers.

### Measurement caveat found on the way

Windows' CPU-time accounting on this machine reports 3.4 s for a 5.0 s single-thread busy loop (with
or without opting out of power throttling; no VM running). CPU shares in the files are therefore used
only to tell quiet samples from busy ones, never quoted as absolute numbers.

## Results

### Capacity, three runs of one build (`47ed673a6a89`), 2026-11-06

`--profile capacity --max-rows 10000000`, 256-character payloads, 10,000 rows per `InsertRange`,
Buffered, pinned to the performance cores, on the harness after the review (full GC between close
and reopen, misses counted). No run was left out.

| | median | range of three |
| :--- | ---: | ---: |
| 10M rows, wall time | 57.1 s | 56.5-58.5 s |
| insert rate, first million | 219,298 rows/s | 217,865-222,222 |
| insert rate, last million | 215,983 rows/s | 204,499-219,780 |
| reopen to first answer at 10M | 2.9 s | 2.8-2.9 s |
| open alone at 10M | 5.6 ms | 4.9-5.7 ms |
| close at 10M (flush and close) | 1.2 s | 1.14-1.22 s |
| probe p99 at 10M (1,000 lookups) | 899 us | 586-1,315 us |
| peak private memory | 13.8 GB | 13.7-13.8 GB |
| size on disk | 4.85 GB, 521 bytes a row | |

The median-time run's slowest 250k-row sample was 65,136 rows/s at 8.75M. An earlier set of three on
the pre-review harness (`stress-capacity-20261106_1819*`-`_1821*`) gave the same rates but a 17.1 GB
peak: the closed context was still on the heap at each checkpoint. Those files stay in the results
folder; the page groups runs by build and shows only the newest.

### Soak, 45 minutes (`stress-soak-20261106_182403.jsonl`), 2026-11-06

`--profile soak --seconds 2700`: 2,000,000 rows seeded (8.5 s), 8 readers and 1 writer on one
`XodContext` over the first 200,000 rows, a commit every second, Buffered, pinned to the performance
cores, a sample every 30 s. All 90 samples were taken on a quiet machine. It ended `planned`, code 0.

| criterion (`PLAN-042/1`) | minutes 5-15 | minutes 35-45 | allowed | result |
| :--- | ---: | ---: | ---: | :--- |
| reads per second | 397,675 | 412,581 | 80% at least | pass (104%) |
| commits per second | 0.98 | 0.99 | 80% at least | pass |
| read p99 (mean of samples), us | 127.1 | 122.7 | 1.5x at most | pass |
| private memory, MB (every sample) | 3,794 | 3,892 | +15% at most | pass (+2.6%) |
| data checked after a reopen | | | | pass |
| updates per second (not a criterion in `/1`) | 4,608 | 6,007 | | shown |

- Over the run: 409,038 reads a second, p50 5.2 us and p99 123 us in a typical sample; 5,604 updates a
  second in 0.98 commits a second; 1.10 billion reads and 15,128,226 updates in 2,658 commits.
- Tails: the slowest read took 856 ms and the slowest commit 554 ms (a typical sample's slowest commit:
  30 ms). `Flush` takes the engine-wide write lock (`XmlEngine.Flush`), so a long commit stalls every
  reader. GC pauses took 20.7% of the time.
- After the run: flushed in 7.2 ms, closed in 270 ms, reopened in 2 ms, first answer 898 ms after
  opening; all 2,000,000 rows present, the hot rows' prices equal to the seed values plus every one of
  the 15,128,226 updates, the unique key still enforced.
- This run used the harness from before the review: no `build` id, no criteria in the header, and reads
  that answered nothing were not counted (the writer's were: 0).

## Independent review

A fresh reviewer read the harness, the summary, the page and the engine's locking (read-only, no runs).
Its findings were checked against the files before acting on them; all held.

| # | Finding | Done |
| :--- | :--- | :--- |
| 1 | Capacity had no void rules, and "the median of three runs" was one run's figures (best close and first answer, worst first-million rate) | fixed: a capacity run counts only when it ended `planned`, code 0, at its row target, with no wrong answers; every number is its own median with its range; the curves are labelled as the median-time run; runs are grouped by `build`, a hash of the assemblies that ran, not a `-dirty` commit |
| 2 | "Quiet" counted the run's own memory as a busy machine, and unknown as quiet | fixed: free memory is counted besides the run's own working set; unknown is not quiet; the memory criterion uses every sample, so a leak fails instead of voiding |
| 3 | The writer was not judged: commits are a timer heartbeat; "commit p99" of ~30 values is their max | fixed: updates per second is a criterion in the header from `PLAN-042/2`; the slowest commit is shown as such. The soak below predates it and shows the update rate beside its criteria, not judged |
| 4 | A gap in the timeline (sleep, stall) still counted | fixed: a gap over 1.5 sample intervals, or wall clock and run clock disagreeing, voids the run. `SleepGuard`'s comment no longer claims it prevents sleep or explains the 5-hour run |
| 5 | A run still going was published as "stopped from outside", under a full headline | fixed: a soak inside its planned time is skipped; no end row reads "still going, crashed or was killed"; a void run shows its reasons and the chart only |
| 6 | "Set before the run" was not enforced: the rules lived in site code | fixed for new runs: the harness writes the rules into the header. The soak below is judged by `PLAN-042/1` and the page says it predates recording them |
| 7 | The end check was one sum, could not fail in any test, and a failed check exited 0 | fixed: an id-weighted sum as well (catches an update on the wrong row), `Update`'s result counted, exit code 3, and a test that drops every 50th update and sees the check fail |
| 8 | Reads and probes never checked that they found the row | fixed: misses are counted for readers, the writer, probes and the first answer; any miss voids the run |
| 9 | "Peak memory 17.1 GB" counted the context just closed (heap 12.5 GB to 17.2 GB with no full GC) | fixed: a full collection between close and reopen at each checkpoint; capacity re-run. The "stops being a fair test" sentence is gone; the page states the numbers |
| 10 | Copy the data contradicts: dips "once each time the index doubles", "opening ... at every size", "fold", "within the hour", "committed" raw files, 0-10M ticks | fixed: no cause is given for the dips (they coincide with full GCs, which also happen without a dip); the opening range comes from the data; raw files are named, not called committed; ticks follow the data |
| 11 | No provenance on the stress sections | fixed: build (or commit) printed in both |
| 12 | Chart averages included busy samples; thinning dropped them | fixed: averages over quiet samples; busy samples always kept |
| 13 | Crashes on missing fields or no checkpoint; nominal intervals; no minimum length; payload length; checkpoint row label; mistyped options ignored | fixed (guards, measured intervals, 10-minute minimum, 5 quiet samples per window, 267-character payload in the header, actual rows, unknown options exit 2) |
| 14 | The CI gate never runs (xod2 has no remote); harness tests depend on real time | named below |

Second copies removed: update and commit counts come from the recorders only; the words, cadence and
landing sentence live in `xod-site/src/lib/stress.ts` for both pages; the page uses `verify.ok`, not
its own sum. The reviewer confirmed the dependency claims against Microsoft's and Node's docs
(`SYSTEM_CPU_SET_INFORMATION` layout and `EfficiencyClass`, `GetSystemTimes`, `GC.GetTotalPauseDuration`,
`SetThreadExecutionState`, `node --test` globs in v22). `Volatile.Read` over plain increments is exact on
x64 only.

## Not done (named)

- **The xod2 CI gate never runs**: the repository has no remote, so `.github/workflows/tests.yml` (now
  including `XodBench.Tests`) is a gate on paper. Locally: `dotnet test benchmarks/XodBench.Tests`.
- **Harness tests use real time** (3-second soaks, a 2-second cancel, a 90% band on read counts).
- **"Quiet" is measured machine-wide**, not on the pinned performance cores, with Windows' CPU
  accounting (which under-reads). A per-processor reading would close it.
- **Power and temperature** are recorded once (AC power at the start), not per sample.
- **Landing-page numbers outside the stress sections** are typed in ("four microseconds", "a hundred
  thousand rows in about a second", "a gigabyte of rows in about a gigabyte of files"); they were not
  part of this review.
- **The soak below was run by the harness before the review fixes**: its file has no `build`, no
  recorded criteria, and no count of reads that answered nothing (the writer's misses were counted).

## Verdicts

| change | verdict |
| :--- | :--- |
| Soak and capacity harness (`StressRunner`, `LatencyRecorder`, `Machine`/`Meter`, `Program`) | clean, with named debt (quiet measured machine-wide with Windows' CPU accounting; harness tests use real time; the CI gate does not run) |
| Site summary (`stress-summary.mjs`) and data selection (`build-data.mjs`) | clean, with named debt (files from before the rules were recorded are judged by `PLAN-042/1`, and the page says so) |
| Page copy (`benchmarks.astro`, `index.astro`, `src/lib/stress.ts`) | clean: every number and claim in the stress sections comes from the files. Awaiting the owner's approval; not committed, not deployed |
| The soak result | counts under `PLAN-042/1`; run on the pre-review harness (see above). A 45-minute re-run on the current harness would carry the update-rate criterion, a build id and read-miss counts |
| Corrections to `PLAN-040` | made where the claims were written (reopen, probe p99, soak status) |
