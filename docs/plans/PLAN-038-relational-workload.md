# PLAN-038: The relational workload — graph insert, load and cascade

- **Task ID**: `TASK-043`
- **Status**: Proposed (analysis measured, no code change yet)
- **Related**: `PLAN-036` proposal 3 (graph binding), `PLAN-035` (page size), `ISS-040`

## 1. Where the numbers stand (028cd2d)

| 10k customers (260k entities) | XOD XML | SQLite | LiteDB |
| :--- | ---: | ---: | ---: |
| Insert 10k graphs | 4,310 ms | 665 ms | 2,675 ms |
| Load 200 graphs | 153 ms | 12 ms | 192 ms |
| Delete 100 with cascade | 85 ms | 12 ms | 101 ms |

Same at 40k, roughly 3.5x the work. JSON and Binary are far worse on the read side (load 2.8 s at
10k) because they parse a page per row.

## 2. Where the time actually goes (probe, 2,000 customers / 10k orders / 40k lines)

Isolated program against the same models and options, XML:

| step | time |
| :--- | ---: |
| Insert 2,000 graphs (50k entities) | **876 ms** |
| 200 parent-only loads | 1 ms |
| 200 full graph loads | 30 ms |
| 200 child queries (orders only) | 5 ms |
| 200 child queries (orders + lines) | 16 ms |

Readings:

- **Insert is the dominant cost** and it is per-entity, not per-graph: ~17 us per entity at 10k scale.
  A graph of 26 entities costs ~440 us, almost all of it in the write path.
- **Load is not the parent**, and not the index walk: 200 parent reads take 1 ms. Full graph loads are
  150 us each, and the child queries alone are cheap (80 us for orders + lines). The remaining ~70 us
  per graph is hydration and per-row plumbing.
- The harness's 12x load gap vs SQLite at 10k is larger than this probe suggests at 2k, so part of it
  is page locality at scale, not per-row cost - worth confirming with an instrumented 10k run before
  optimising the read path.

## 3. Options, ranked by expected value

| # | Change | Target | Expected | Risk | Effort |
| :--- | :--- | :--- | ---: | :--- | :--- |
| 1 | **Pre-bound child relationship in `ParseChildren`**: resolve `[Children]`/`[ParentKey]` once per type, cache compiled getters/setters and the child PK reader; stop calling `propertyService.Properties(...)` per child and `GetPrimaryValues` twice per entity | graph insert | **~2x** (4.3 s -> ~2.2 s) | Low | Medium |
| 2 | **Group child inserts by type inside `InsertRange`**: today work interleaves Customer -> Order -> Line -> Order..., touching three page files per graph. Collecting children per type reduces page switching and flush churn | graph insert, cascade | 10-25% on top of #1 | Medium (ordering/identity) | Medium |
| 3 | **Delete batch per type with one table-file update**: `DeleteCustomersCascade` currently visits the `.xtab` per child to decrement the counter | cascade | ~1.5x (85 -> ~55 ms) | Low | Low |
| 4 | **Load: fetch children in one pass per parent page** (group ids by page, or a covering index read that returns codes then one bulk read) | graph load | 2-3x read path | Medium | High |
| 5 | Row-level access for JSON/Binary read path (same work as the withdrawn `ISS-039` proposal, now justified by relational loads only) | JSON/Binary load | 4-5x on those formats | High | High |

## 4. What I would do first

**Option 1 alone**, because it is the only change that moves the largest absolute number (graph insert)
with low risk, and because option 2 is easier once the bindings are pre-resolved.

Shape of the change:

1. Add a `ChildBinding` to `PropertyInfoItem`: parent-key getter, child-key setter, child primary-key
   reader, all compiled once at metadata time.
2. In `XmlEngine.Relations.ParseChildren`, resolve the binding once per property, before the child loop,
   and use it inside; remove the per-child `FirstOrDefault` on `propertyService.Properties` and the
   duplicate `GetPrimaryValues` call.
3. Keep the write order and the identity semantics identical; this is an allocation and reflection
   removal, not a reordering.

Measurement: interleaved A/B on `graphInsertMs` and `loadGraph200Ms` at 10k, three pairs, guardrails on
the flat suite. Success: `graphInsertMs` <= 2.5 s at 10k with tests 40/40; otherwise revert.

## 5. Not doing yet

- Reordering inserts (option 2) until option 1 is measured.
- Any change to the read path until an instrumented 10k run says whether the gap is per-row cost or
  page locality.
- JSON/Binary row spans: the largest win, but the highest risk, and it should follow a proven page-size
  or cache result.

---

## 6. Experiment log

### 2026-11-05: pre-bound child keys in `ParseChildren` (option 1) - **no win, reverted**

Resolved the `[Children]`/`[ParentKey]` mapping once per (parent type, child type, property) into a cached
`ChildKeyBinding[]`, so the child loop does no `propertyService.Properties(...)` LINQ lookups and no
`FirstOrDefault` per key; also computed `GetPrimaryValues` once per child instead of twice and reused it
for the reference lookup and the attributes. 40/40 tests green while live.

Interleaved A/B on the relational 10k suite, four pairs, both harness builds prebuilt:

| graphInsertMs | runs | median |
| :--- | :--- | ---: |
| before | 3,985, 4,109, 4,173, 4,300 | **4,141 ms** |
| after | 4,054, 4,054, 4,123, 4,250 | **4,088 ms** |

**1.3%, inside the noise band** (the ranges overlap; two after-runs are slower than the fastest
before-run). `loadGraph200Ms` and `cascadeDelete100Ms` unchanged. Reverted.

What this rules out: metadata lookups and the duplicate primary-value dictionary are not where the graph
insert spends its ~17 us per entity. The cost is in the write itself - DOM creation per entity, the page
append, index maintenance and the save path - so the next attempt should instrument those phases on a
single run instead of guessing which one dominates.

---

## 7. Phase breakdown of the graph insert (2026-11-05)

Instrumented `AddToPage` with opt-in phase timers (`XOD_DIAG=1`) and ran the relational 10k harness once.
260,000 entities; the run took 4,069 ms of `graphInsertMs`. Cumulative phase times:

| phase | total | per entity | share of the insert |
| :--- | ---: | ---: | ---: |
| `Write` (DOM construction) | **8,939 ms** * | 15.6 us * | ~100% |
| page save (cache bookkeeping) | 56 ms | 0.2 us | 0.6% |
| table save (cache bookkeeping) | 64 ms | 0.2 us | 0.7% |
| index update | 161 ms | 0.6 us | 2% |
| row add to page document | 179 ms | 0.7 us | 2% |

* `Write` is recursive: a customer's write includes its orders' and lines' writes, so the cumulative
number is larger than the wall-clock insert. The comparison is what matters: every other phase together
is under 5% of it.

So meta-lookups, the table counter, index maintenance and page saving are all noise. **The insert is DOM
construction**, as the earlier probe implied and the reverted binding experiment demonstrated.

### What to change next

Make the scalar property writer cheaper. Today each property costs `prop.GetValue` (boxing), a runtime
`ValueHelper.IsScalarType` check, `ValueHelper.DefaultOf` for the omit test, `ValueHelper.FormatScalar`
(another type switch) and a fresh `XElement`. `PropertyInfoItem` already carries `ScalarKind` and typed
getters (`IntGetter`, `LongGetter`, ...) for the raw-write path, so the same information can skip the type
checks, the boxing and the double switch:

1. Switch on `prop.ScalarKind` in `WriteProperty`, format with `Utf8Formatter`/invariant `ToString` into
   the element value, and keep the existing path as the fallback for anything not covered.
2. Cache the "default value" omit decision on `PropertyInfoItem` instead of recomputing it per row.
3. Leave encryption, markup and attribute-position handling exactly as they are.

Expected: 20-40% on `graphInsertMs` (4.1 s -> 2.5-3.3 s), measured with the interleaved A/B, guardrails
on the flat suite. If it does not clear the noise band, revert and record - six hypotheses have already
died in this session and the register should show it.

### Second pass: the per-entity setup costs too (same run)

Extended the timers to the bookkeeping around the write (`OpenFileOrCreate`, `OpenPageOrCreate`,
`PickCode`) in case the DOM conclusion was hiding them. 260,000 entities:

| phase | total | per entity |
| :--- | ---: | ---: |
| page saves | 41 ms | 0.2 us |
| table saves | 66 ms | 0.3 us |
| index updates | 149 ms | 0.6 us |
| row adds to the page document | 179 ms | 0.7 us |
| opening the table document | 66 ms | 0.3 us |
| opening the page document | 194 ms | 0.7 us |
| generating the row code | 37 ms | 0.1 us |

Everything outside `Write` totals **~732 ms of the 4,069 ms wall clock (18%)**. The remaining ~12.8 us per
entity is inside `WriteProperty`: for each of roughly 6-8 scalar properties per entity it does a compiled
getter (boxing the value), a runtime `IsScalarType` check, a default-value comparison, a `FormatScalar`
switch that allocates a string, and then a `new XElement(name, text)` plus `xItem.Add(...)` — each of
which validates the XML name and allocates a node.

That is why the flat bulk path, which builds rows as UTF-8 bytes from precomputed tag fragments
(`prop.Utf8OpenTag`), runs at 11.8 us per 100k-row row, while the graph path costs ~15.6 us per entity
including all of the above.

### The next change, precisely

Extend the UTF-8 row writer from "pure scalar standalone" to **any entity whose write is scalar properties
plus reference/child elements**: emit the scalar part from `Utf8OpenTag`/`Utf8CloseTag` into a pooled byte
buffer (the existing `FormatStandaloneRowUtf8` already does exactly this for flat rows), and keep the DOM
path only for entities with filters, encryption, markup, dictionaries or complex collections. That removes
the per-property XElement entirely rather than shaving its cost.

Expected: 2-3x on `graphInsertMs` (4.1 s -> 1.4-2.0 s) and a smaller gain on flat bulk. Risk is medium:
the byte output must be identical to the DOM's for every scalar kind, which the existing value-validation
checks and `XodIntegrityTests` cover; measure with the interleaved A/B, revert if it does not clear noise.

### 2026-11-05: UTF-8 row writer for scalar-only entities - **the 42% was a failed run, not a speed-up**

Enabled `TryAppendRawRowToPage` for any entity whose non-scalar properties are null, opt-in through
`XOD_UTF8_WRITE=1`. The first measurement showed `graphInsertMs` 4,428 -> 2,573 ms with `loadGraph200Ms`
and `cascadeDelete100Ms` at **0**, and I reported that as "42% faster and wrong".

**Correction: the harness caught it and I misread the capture.** Re-running one worker with the flag on:

```
Error: System.InvalidOperationException: line 1: product  vs 893
   at XodBench.Runner.ValidateGraph ...
```

`ValidateGraph` checks that every order line carries its product reference, and it threw. The worker
failed with a non-zero exit code. My A/B loop captured any `RESULT_JSON` line, deserialised it, and
recorded its default-zero metrics without checking `Error` or the exit code - so a **failed run was
counted as a data point**. The 42% figure is withdrawn; there was never a valid timing.

The lesson is mine, not the harness's: a result line is not a result. Check `Error` and the exit code
before it enters a comparison.

### What the failure actually says about the writer

`CanFormatRowUtf8` bailed only when a non-scalar property had a **non-null value**. `OrderLine.Product`
is null in the benchmark data but has `ProductId` set, and the DOM path emits a `<Product refType=
"reference">` element from the foreign key anyway. So the byte writer silently dropped the product link.

To use it for entities, the formatter must emit the same reference elements the DOM path does from
foreign-key values (and skip children/parents exactly as that path does), or bail on any property that
participates in a relationship regardless of whether its navigation is populated. The second is the safe
first step - it costs the OrderLine case (which is the one worth having), so the real work is emitting
the FK reference element.

### Next, in order

1. Emit foreign-key reference elements in the byte formatter, with a fixture test that compares the
   bytes against `Write(...).ToString()` for a populated and an unpopulated navigation.
2. Re-measure `graphInsertMs` with a capture that rejects any run whose `Error` is set or whose exit code
   is non-zero.
3. Only then claim a number.

### 2026-11-05: reference resolution measured at zero - hypothesis rejected

Before building the cache I instrumented `MapToReference` to prove the 160,000-lookup claim. One run of the
relational suite with `XOD_DIAG=1`:

```
refLookupMs=0 refLookups=0
```

**The graph insert never calls the reference path.** `meta.WriteProperties` does not include the foreign
navigation, so nothing looks a product up while writing an order line; the product is hydrated from
`ProductId` on read. The "160k lookups" paragraph above is withdrawn - it was inferred from the byte
writer's failure rather than measured, which is the second time in this document that inference was
mistaken for evidence.

So the earlier phase data stands unchanged: **the insert cost is inside `WriteProperty` itself** - a
boxed getter, a type check, a string allocation and an `XElement` per property - and the writer is still
the right lever. What is not yet explained is why the byte-written row lost the product reference when the
DOM path emits no reference element either; that needs a row-level comparison (dump both rows for the same
OrderLine and diff them) before the writer is attempted again.

### Next, in order

1. Dump the DOM-written row and the byte-written row for one `OrderLine` and diff them. The cause of the
   failure is in that diff, not in a theory.
2. Fix the writer for that difference, add it as a fixture test, and only then re-run the A/B with the
   failed-run guard.

### 2026-11-05: the row dump ends the writer line of work

Inserted the same graph through the public API and dumped one stored row:

```xml
<Row code="95537e3f8"><OrderLine><Id>15286</Id><OrderId>3822</OrderId><ProductId>766</ProductId>
<Quantity>2</Quantity><UnitPrice>2.5</UnitPrice>
<Order refType="parent" hostProp="Lines" Id="3822">bbef36058.bc1c92c2e</Order></OrderLine></Row>
```

The row carries a **parent reference element** with the parent's row code, written during the child's
create because the parent is already known through the write track. That explains both observations: no
product lookup happens (the product is hydrated from `ProductId`), and the byte formatter produced a row
without the parent element, which is why the graph load could not rebuild the relationship.

A byte writer for relational entities would therefore have to receive the write track, know the parent's
code, emit the parent element with its attributes, and stay byte-identical to the DOM. That is a
serializer project, not an optimisation.

**Conclusion: stop here.** The DOM cost is real and well measured, but removing it for relational entities
means re-implementing the relationship bookkeeping in the writer - a multi-session change with format risk.
The engine's write path stays as it is for 2.x, and the work moves to the 2.0 release gate, which is the
thing actually blocking the product.

If this is picked up again, start from the row shape above and write the fixture test first: the DOM row
for one OrderLine is the specification the byte writer must reproduce exactly.
