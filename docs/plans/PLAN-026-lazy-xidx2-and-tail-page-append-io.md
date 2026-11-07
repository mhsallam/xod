# PLAN-026: Parallel/Lazy `XIDX2` Hydration, Tail-Append `.xpag` I/O & Coalesced Raw-Byte Splicing (`v1.2.3`)

- **Task ID**: `TASK-031`
- **Fixes Issue**: [`ISS-031`](../KNOWN-ISSUES.md)
- **Status**: Implemented (v1.2.3)

## Problem Statement
Profiling the remaining latency in `ColdOpenPlus1InsertMs` (`83 ms`) and `Update100Delete100Ms` (`89 ms`) in `XOD++ v1.2.2` identified three specific I/O and memory-copy overheads:
1. **Single-Threaded Upfront Deserialization of All `XIDX2` Property Trees on Cold Start (`~65 ms` of `83 ms`)**:
   When a `100,000`-row table with 3 indexed properties (`Id`, `Sku`, `CategoryId`) is opened on cold start, `IndexService.TryLoadFromDisk` sequentially deserializes all `200,200` keys and `300,000` item codes on a single thread before the first operation can proceed.
2. **Full Tail-Page Rewrite on Single-Row `Insert` + `Flush()` (`~18 ms` of `83 ms`)**:
   When inserting 1 row into an existing database whose tail `.xpag` file is `390 KB` (`176` rows, `full="false"`), `AddToPage` loads and parses the entire `390 KB` `.xpag` into an `XDocument`, adds 1 `<Row>`, and rewrites all `390 KB` via `.tmp` + `File.Move`.
3. **Repeated `512 KB` Array Reallocations on Multi-Row Deletes/Updates Within the Same Page (`~45 ms` of `89 ms`)**:
   In `Update100Delete100Ms`, deleting `100` consecutive rows from the same `.xpag` page calls `SpliceRowInRawPage` `100` times—allocating a new `512 KB` `byte[]` and copying `512 KB` via `Buffer.BlockCopy` `100` times (`50 MB` of redundant array allocations and copies for a single page).

---

## Design & Refactoring Steps

### 1. Parallel & Per-Property Framed `XIDX2` Deserialization
- Extend the `XIDX2` binary snapshot writer so each property block is prefixed with its byte length (`int blockByteLength`), enabling `TryLoadFromDisk` to slice the `byte[]` buffer into independent per-property segments in $O(1)$ (`< 1 ms`).
- Hydrate property index dictionaries in parallel across CPU cores (`Parallel.For` over the property blocks) using direct `ReadOnlySpan<byte>` string decoding instead of `BinaryReader.ReadString()`.

### 2. Incremental Tail-Append for Active `.xpag` Pages (`TryAppendRowToTailPage`)
- Add `TryAppendRowToTailPage` in `IOService`:
  - When appending new rows to an existing unencrypted XML `.xpag` page that is not yet in `cachedFiles`, instead of parsing the `390 KB` `.xpag` into an `XDocument`, append the new `<Row code="...">...</Row>` bytes directly before the closing `</Rows></TablePage>` trailer (`19` bytes from EOF)!
  - On `Flush()`, seek `-19` bytes from the end of the `.xpag` file on disk and write only the appended `<Row>` bytes + `</Rows></TablePage>` (`~2.2 KB` write instead of `390 KB` temp-file rewrite).

### 3. In-Place / Coalesced Raw-Byte Page Splicing (`cachedRawPageBytes`)
- Track the logical byte length (`cachedRawPageLengths`) separately from the underlying `byte[]` capacity in `IOService`!
- When `SpliceRowInRawPage` removes a row (`newRowElement == null`) or replaces a row with equal-or-smaller byte length, shift the tail bytes **in-place** within the existing `byte[]` buffer (`Buffer.BlockCopy` on the same array) and decrement `cachedRawPageLengths[normalizedName]`—**zero `512 KB` `new byte[]` allocation**!
- When replacing a row with a slightly larger byte length, grow the buffer with slack capacity (`+4096` bytes) so subsequent updates on the same page also execute in-place without reallocating.

---

## Target Benchmark Impact (`250 MB` / `100,000` Rows)
- **`ColdOpenPlus1InsertMs`**: `83 ms` $\rightarrow$ **`~15–25 ms`**.
- **`Update100Delete100Ms`**: `89 ms` $\rightarrow$ **`~25–40 ms`** (beating `LiteDB 5.0`'s `57 ms`).
