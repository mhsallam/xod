# PLAN-028: Fair Benchmark Harness v2 (Flat + Multi-Level Relational + 1 GB + Concurrency)

- **Task ID**: `TASK-033`
- **Fixes Issue**: [`ISS-033`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Why
Harness v1 (`PLAN-021/024/027`) produced numbers that were not apples-to-apples:

| v1 problem | Effect |
|---|---|
| XOD kept one warm context for steps 3-7; SQLite/LiteDB reopened the DB per step | Open cost billed only to competitors (e.g. LiteDB `Count+Last` 86 ms was mostly open) |
| Single run, integer ms, no warm-up, no median | Run-to-run noise (we saw 1,060-1,474 ms for the same build) presented as a single number; best runs were cherry-picked |
| Durability differed (XOD `Buffered` no fsync, SQLite WAL `NORMAL`, LiteDB checkpoint) | Write numbers not comparable |
| SQLite update was server-side `UPDATE ... SET Price = Price + 10` | No hydration, unlike XOD/LiteDB read-modify-write |
| Separate executables per engine, fixed run order | Order/thermal bias, different code paths for timing |
| Flat single-table model only; no concurrency; max 250 MB | Did not exercise relationships, contention, or scale |

## Design rules (v2)
1. **One executable, one runner, one timing code path.** Engines implement the same adapter interface; the runner times every adapter call identically (`Stopwatch`, double ms).
2. **Process isolation.** Each (engine, suite, scale, iteration) runs in a fresh child process. Engine order is rotated per iteration.
3. **Warm/cold parity.** Every engine keeps one open handle for the warm phase (XOD context / SQLite connection / LiteDatabase). Cold open (open + 1 insert + commit) is timed separately for all engines.
4. **Repetition.** N iterations per configuration; report median, min and max. Micro operations (PK lookups) run 100 warm-up ops, then 1,000 timed ops.
5. **Equal semantics.**
   - Bulk insert: data generated before timing; one batch/transaction per engine.
   - Mutations: read-modify-write (hydrate, change, write) in one batch/transaction for all engines.
   - Pagination: insertion/PK order for all engines.
   - Every result is checked (counts, ids, graph shape) so a "fast" wrong answer fails the run.
6. **Durability disclosed.** XOD never fsyncs (`FileStream.Flush(false)`), so SQLite runs `journal_mode=WAL, synchronous=OFF` (no fsync, the closest match). LiteDB 5 has no fsync switch, so its write numbers include fsync cost. The dashboard shows this caveat.
7. **Same machine, same disk, same temp root** for all engines.

## Suites
- **Flat** (`BenchRow`: Id PK autonumber, Sku unique, CategoryId indexed, Price, Payload ~2.4 KB varied text)
  - 250 MB = 100,000 rows, 1 GB = 400,000 rows.
  - Ops: bulk insert, cold open + 1 insert, 1,000 random PK lookups, Count + Last, 20 indexed queries (x500 rows), deep page (skip 90% / take 25), 100 updates + 100 deletes, disk size, peak working set.
- **Relational** (4 levels: `Customer` 1-N `Order` 1-N `OrderLine` N-1 `Product`), normalized in all three engines:
  - XOD: `[Children]` / `[ParentKey]` / `[ForeignKey]`, graph insert, `include: "*"` graph load, cascade delete.
  - SQLite: 4 tables, FK indexes, `ON DELETE CASCADE`, graph load = 3 queries.
  - LiteDB: 4 collections with indexed FK fields, manual graph assembly (what `DbRef`/`Include` does internally).
  - Ops: graph insert, 200 random full-graph loads, indexed shallow query on `Order.Status`, 200 order status updates, 100 cascade deletes of customers.
- **Concurrency** (on a 100k-row flat DB): fixed 5 s window, `R` reader threads (random PK lookups) + 1 writer thread (read-modify-write update, each committed individually), `R` in {1, 4, 8}. Reports reads/s, writes/s, p50/p99 read latency.
  - XOD uses one shared context in `DurabilityMode.Immediate`; SQLite uses one connection per thread (WAL); LiteDB uses one shared `LiteDatabase`.

## Deliverables
- `benchmarks/XodBench/` (harness + orchestrator), `benchmarks/results/*.json`.
- Filterable dashboard (suite, scale, engine, metric group, log/linear, min-max range) comparing **XOD++ v1.2.3 vs SQLite vs LiteDB** only.

Recorded: every result is now stamped and committed (`PLAN-031`), and the dashboard is computed from those files rather than hand-typed.
