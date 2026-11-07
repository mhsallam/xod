# Strategy note: how much is left in .NET, and whether xod-rs is the next move

- **Date**: 2026-11-05
- **Status**: advisory, no code change
- **Evidence**: the recorded set at `028cd2d` plus the phase instrumentation in `PLAN-038` §7

## 1. Where the remaining headroom actually is

From the current recorded set (XOD XML vs SQLite, 250 MB unless noted):

| Area | Current ratio | What the measurement says the cost is |
| :--- | ---: | :--- |
| Bulk load | **0.58x (XOD ahead)** | already won; little left |
| Point lookups | **0.68x (ahead)** | steady state is ~4.5 us; nothing to chase |
| Indexed queries | **0.45x (ahead)** | already won |
| Deep paging | **~0.01x (ahead)** | structural; nothing to do |
| Footprint / memory | ahead | — |
| Cold open + one insert | 1.5x slower than SQLite, 5x than LiteDB | 22% removed this session (index load); the rest is first-write plumbing |
| Single-row mutations | **10-12x slower** (30x at 1 GB) | DOM per row + page rewrite; three attempts failed to move it |
| Graph insert | **6.5x slower** | 95%+ of the time is `Write` — one `XElement` per property per entity |
| Graph load (XML) | 12.7x slower | per-row hydration and page locality |
| Graph load (JSON/Binary) | **230-450x slower** | a whole-page parse per row |
| Cascade delete | 7x slower | per-child delete bookkeeping |

So yes: there is a **large, well-located headroom** on the write and relational paths — but every item on
that list is a subsystem redesign, not a knob:

1. **A streaming writer for child entities.** The graph insert already measures at 95% DOM construction.
   The raw-write path (`FormatStandaloneRowUtf8`) already writes pure-scalar rows without a DOM and is
   what makes flat bulk insert fast. Extending that idea to entities with parent/child links is the
   single highest-value .NET change left: expected 2-3x on graph insert, contained to one writer.
2. **Row-level access for document formats.** Removes the 230-450x relational load gap on JSON/Binary and
   the page-parse cost behind it. Larger, riskier, but the prize is the biggest on the board.
3. **Append-only mutation log for single-row updates.** The 10-30x update gap is DOM-per-row plus a page
   rewrite; a row-delta log with periodic compaction is the standard answer. This is a storage-model
   change, not a tuning change.

Realistic ceiling in .NET, with all three done well: graph insert ~2-3x better, document relational loads
~4-5x, updates ~5-10x. That would put XOD ahead on most of the board and competitive on the rest.

## 2. What a Rust core would and would not buy

**It would buy:**

- No GC pauses and precise memory control; the write path could avoid per-entity allocation entirely.
- Zero-copy parsing for XML/JSON/Binary, and a page format designed around row spans from day one.
- `serde`-class serialization speed, `mmap`, and better primitives for concurrent page access.
- A single native library with a stable C ABI, usable from .NET, Go, Python, Node.

**It would not buy (for a Rust *core behind FFI*):**

- The .NET object layer. XOD's proposition is "your POCOs, no mapping layer". A Rust core behind FFI
  needs a mapping layer in C# — the exact cost the product claims to remove — unless the API moves to
  something less .NET-native, which changes the audience.
- A finish line. ACID, journaling, crash recovery, migrations, three formats, the 2.0 gate, and the
  benchmark harness all have to exist again. That is months, against a current engine that already
  **beats SQLite on bulk load, point lookups, indexed queries, deep paging, footprint and memory**.
- The 2.0 release. NuGet, GitHub and the site are ready; a rewrite postpones the product indefinitely.

## 3. Recommendation

**Ship XOD 2.0 on .NET first.** The engine is honest, measured, and ahead on the metrics that matter for
the pitch. A rewrite before a first public release would replace a product with a promise.

**Then treat xod-rs as a research track, not a rewrite.** A narrow spike, not a port:

1. Scope: page store + journal + row-span reader, one format (Binary), no object mapping.
2. Interface: a C ABI exercised from the existing harness through a minimal C# shim, so the same
   `XodBench` numbers decide whether it is worth continuing.
3. Success criterion: 3x or better on graph insert *and* document relational loads, with the flat suite
   at parity. If it does not clear that, the answer is no and the .NET path continues.
4. Time-box: two weeks of focused work. Anything longer without that evidence is a bet, not a plan.

**In the meantime, while other sessions occupy the machine, do the .NET writer work** (item 1 above): it
is the same insight a Rust core would exploit — stop building a DOM per entity — and it can be done
without a language change.

## 4. What I would not do

- Do not run benchmarks while akik/portfolio sessions are building; every number in this session that was
  taken under load had to be thrown away.
- Do not port the whole engine speculatively; the spike exists to answer "is this worth it" cheaply.
- Do not chase the update metric with another micro-change: three attempts failed, and the cost is
  structural (page rewrite per row), so it needs the append-only log design or nothing.

---

## 5. Clarification (same day): a complete engine in Rust, not a core behind FFI

That is a different bet, and a legitimate one. A whole engine in Rust means `struct`s with derive-based
serialization and no interop boundary at all: no C# mapping layer, no shim, no FFI tax. The price is
that it is **a new product with a new audience**, not a faster version of the .NET one:

| | Bet A: faster .NET engine | Bet B: complete Rust engine |
| :--- | :--- | :--- |
| Audience | existing .NET users | Rust users (and later bindings) |
| What carries over | all of it | formats, invariants, benchmark harness, design lessons |
| What has to be built | three subsystems (writer, row access, mutation log) | everything: storage, WAL, indexes, query layer, transactions, recovery, migrations, CLI, docs, tests |
| Time to something credible | weeks | 6-12 months for a v0.1 with an honest benchmark |
| Effect on 2.0 | ships as planned | the .NET release becomes "v2" and the Rust engine "v3 core", or two products |

The design work from this session transfers either way and is language-neutral: bucketized index
sidecars, row-span readers, append-only mutation logs, journal + crash recovery, the harness and its
stamp discipline. That is the asset; the implementation language is a choice about audience.

**Decision gate, not a decision now.** After 2.0 ships:

1. Is the goal *the same product, faster*? Then Bet A, on .NET, and Rust never enters the picture.
2. Is the goal *an engine in the Rust ecosystem* with XOD's model? Then Bet B, in its own repo, with a
   v0.1 scope of one format, single-node ACID, `serde` structs, and a benchmark against `sled`, `redb`
   and SQLite — because those are the incumbents a Rust user would compare it to, not SQLite on .NET.
3. Is it "both eventually"? Then the .NET engine is the product for now and the Rust engine is the core
   for later, and the 2.0 API should be the one worth reimplementing.

**What I would not do now:** start the Rust engine while the .NET release is unfinished and two other
sessions are competing for the same machine. The domain knowledge is captured; the release is not.
