# PLAN-040: Stress and endurance tests (capacity, soak, crash torture)

- **Task ID**: `TASK-051`
- **Status**: Capacity profile run on 2026-11-06 (result below). A soak (`stress-soak-20261106_105217`) was
  published on the site before it met this plan: no commits, no end row, a busy last half hour. Both
  profiles were reworked and re-run under [`PLAN-042`](PLAN-042-benchmark-reporting-refresh.md).
- **Related**: `TEST-PLAN-2.0` layer 8 (volume), `XodVolumeTests`, `PLAN-039` (crash harness)

## 1. Why

The benchmark suite answers "how fast is one operation". It does not answer the two questions a
serious user actually asks:

1. **How much can it hold?** How far can the database grow before something breaks, and does it stay
   usable as it grows?
2. **Does it stay healthy under load?** Not a 4-second burst - hours of sustained mixed traffic, with
   throughput, latency and memory tracked over time, and repeated crashes in the middle of it.

"100 updates + 100 deletes" is an honest micro-benchmark and a terrible headline. These tests are the
headline.

## 2. Machine

The machine a run used is not published (the owner's rule, 2026-11-07): no CPU model, thread count,
memory, OS build or runtime patch level. The harness keeps it in a private, git-ignored record beside
each result (`Machine.RecordPath`, `PLAN-044`). What bounds the runs here is the free disk on the test
volume: 84.8 GB on 2026-11-05.

That bounds the capacity run: with ~1 KB rows, **1 GB of database per million rows**, so 50 million
rows is roughly 50 GB plus index and journal headroom. The run stops on a disk threshold, not on a
schedule.

## 3. Profiles

### A. Smoke (5-10 minutes) - validates the harness, not the engine

- 1,000,000 rows in batches, mixed reads and writes, one crash.
- Purpose: prove the harness records the right things before spending hours on it.
- Disk: ~1 GB.

### B. Capacity (30-90 minutes) - "how much can it hold"

- Grow the database in batches until `--max-rows` or the disk threshold is reached. Suggested first
  run: **20,000,000 rows** (~20 GB) with 256-byte payloads, then a second profile with 4 KB payloads to
  hit 1 GB faster and test larger pages.
- Every 30 seconds: rows written, rows/sec for the interval, RSS, database size, page count, index
  size, and a **probe**: 100 random key lookups + 1 indexed query, with p50/p99.
- Checkpoints: at 1M, 5M, 10M, 20M rows - close, reopen cold, time the open, run the probe again.
  That is where a database feels slow in real life: opening it.
- Output: `results/stress-capacity-<stamp>.jsonl` plus a summary JSON.

### C. Soak / load over time (4-24 hours) - "does it degrade"

- Steady state: 70% reads, 25% single-row writes, 5% graph inserts, 8 reader threads and 1 writer
  thread, on a fixed 5-10M row database.
- Sampling every 60 seconds: throughput per second, p50/p99 read latency, p99 write latency, RSS, GC
  gen0/1/2 counts and pause total, database size, WAL size, error count.
- A crash injection every hour (random kill of a writer child, then recovery and a full integrity
  check) - so the soak also proves that repeated crashes do not corrupt or leak.
- Pass criteria, agreed up front:
  - throughput in the last hour within **80% of the first hour** (no downward drift),
  - RSS growth over 24 h **< 15%** after the first hour of warm-up,
  - p99 read latency in the last hour within **1.5×** of the first hour,
  - **zero** integrity failures after crash recovery, and recovery time reported per crash.
- Output: `results/stress-soak-<stamp>.jsonl` with one line per minute, plus a summary.

### D. Crash torture (1-2 hours) - "kill it 100 times"

- Sustained mixed load with a random kill every 30-60 seconds, 100 iterations.
- After each recovery: counts, sampled keys, index query results, and the graph shape of a sample row.
- Anything short of "all checks pass every time" fails the run.
- Reuses the `Xod.CrashWriter` pattern and `XOD_CRASH_AFTER_ROWS`; no new engine hooks needed.

## 4. What gets added to the harness

`benchmarks/XodBench` gains a `--suite stress` mode with `--profile smoke|capacity|soak|torture`,
`--minutes`, `--max-rows`, `--payload`, `--disk-cap-gb`, and `--out`. It writes **JSON Lines** (one
sample per interval) so the data can be charted later without re-running anything, and it prints a
one-line progress summary to stdout for the session log.

No new measurement code in the engine: the harness uses the public API only.

## 5. What could invalidate a run, and how we avoid it

- **Other load on the machine.** The owner has other sessions; the plan is to run when the machine is
  idle and to record load average at start and end. If another agent is building, the run is void.
- **Disk throttling / thermal drift.** The soak measures within-run drift, which cancels the machine
  being slower by 20% than last week - but not a mid-run thermal collapse, so CPU package temperature
  is sampled too.
- **SQLite as a control.** Capacity and torture runs are XOD-only. The soak runs a **4-hour SQLite
  comparison** once, so "stays flat for 24 h" has a reference point rather than being a lonely number.

## 6. Deliverables

1. Harness modes, committed, with a `--dry-run` that prints the plan without touching disk.
2. JSONL results committed under `benchmarks/results/stress/`, with the same stamp discipline as the
   benchmark runs.
3. A summary table per profile in this plan, filled after the run.
4. If the numbers hold up: a site section, "Endurance" - rows over time and throughput over time,
   which is a far better story than a bar chart of 100 updates.

## 7. Schedule

Run in this order, each gated on the previous:

1. **Smoke** (10 min) - harness correctness.
2. **Capacity** at 20M rows (60-90 min) - the headline number.
3. **Crash torture** (1-2 h).
4. **Soak 8 h**, then **24 h** if the 8 h run is clean.

The owner picks the window; the machine must be otherwise idle for the duration, especially for the
soak.

## Result: capacity profile (2026-11-06)

`--profile capacity --max-rows 10000000 --payload 256`, one process, on the development machine.

| rows | size on disk | cold reopen | probe p50 | probe p99 | insert rate |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000,000 | 509.8 MB | **1.6 ms** | 298 us | 1.4 ms | ~41,000 rows/s (at 1.25M) |
| 5,000,000 | 2,553 MB | **2.0 ms** | 210 us | 19.8 ms | ~7,100 rows/s (at 5.8M) |
| 10,000,000 | 5,107 MB | **3.3 ms** | 244 us | 20.2 ms | ~4,000 rows/s (at 9.7M) |

- **Ten million rows in 4.99 GB** - about 510 bytes per row at 256-char payloads.
- ~~**Reopening is nearly free**: 3.3 ms for a 5 GB database, growing roughly one millisecond per 3 GB.~~
  **Correction (PLAN-042):** "cold reopen" timed `new XodContext(path)` only. The index loads on the
  first query, which the next probe paid. Reopen to a first answer, measured since: 354 ms at 1M rows,
  1.4 s at 5M, 3.6 s at 10M (opening alone 2-6 ms). The OS file cache was warm in both.
- **Insertion degrades as the file grows**: 41k rows/s near the start, 4k rows/s at ten million.
  Measured cause (`PLAN-041`): the whole `.xidx` index (1.1 GB at 10M rows) was rewritten after every
  10,000-row batch, and finding the tail page walked every page. Fixed in `TASK-052`: the same command
  now inserts 10M rows in 52 s at 232k-239k rows/s near ten million (`PLAN-041`, result not yet on
  the site).
- **Memory is the real ceiling at this scale**: the process reached 16.4 GB RSS on a 5 GB database.
  The page cache budget is 4 GB, so the rest is parsed pages. At that rate a 20M-row run would need
  roughly 33 GB of memory before it reached the disk floor (`ISS-046`).
- Probe p99 around 20 ms at 5M and 10M, against 1.4 ms at 1M: random reads across a file that does
  not fit in cache pay for parsing the page they land on. **Correction (PLAN-042):** the explanation was
  not measured. With 100 probes, p99 is the second-slowest probe, and after a reopen one of the 100 paid
  for loading the index. With 1,000 probes taken after the first answer: p99 1.9 ms at 10M.
