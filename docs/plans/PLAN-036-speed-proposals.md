# PLAN-036: Speed Proposals & Code Path Analysis (XOD 2.0 / XOD++)

- **Task ID**: `TASK-041`
- **Issue**: [`ISS-039`](../KNOWN-ISSUES.md) (Point lookups at 1 GB), plus write-path & relation bottlenecks
- **Status**: Proposed / Analysis Complete
- **Date**: 2026-11-04

> **Executive Summary for the Next Agent**:
> XOD wins bulk load (1,140 ms vs 1,763 ms), indexed queries (18.7 ms vs 37.7 ms), deep paging (0.18 ms vs 22.4 ms), disk footprint (254 MB vs 395 MB), and multi-reader throughput (187k vs 97k reads/s). It loses point lookups (89 ms vs 6.6 ms on XML; 1.25–2.4 s on JSON; 1.37–2.9 s on Binary), single mutations (46 ms vs 4.2 ms), graph inserts (3,995 ms vs 592 ms), and cascade deletes (73 ms vs 11.3 ms).
> **The cheapest and safest proposal to implement first is Proposal 1 (Direct-Byte Splice for Pure-Scalar Updates & Redundant `.xtab` Elimination in Cascade Delete)**: it requires zero format migration, touches no public APIs, eliminates repeated XML DOM tree serialization on single-row updates, and stops rewriting the parent `.xtab` file on every single deleted child row, cutting single update/delete latency by ~2–3× and cascade delete by ~3× immediately.

---

## 1. Where Is the Remaining Cost? Code Path Analysis

Every claim below cites exact files and lines in `src/Xod/`.

### 1.1 Point Lookups (`Find<T>(x => x.Id == id)`)
**Current Measured**: 1,000 lookups take 88.9 ms (XML), 1,249 ms (JSON), and 1,366 ms (Binary) at 250 MB. At 1 GB: 185 ms (XML), 2,175 ms (JSON), 2,947 ms (Binary) vs 9 ms (SQLite).

Tracing `db.Find<BenchRow>(x => x.Id == id)`:
1. **Expression Tree Allocation & Closure Inspection**:
   - `XodContext.cs:104` calls `engine.QueryExpression(query, include).FirstOrDefault()`.
   - `XmlEngine.Queries.cs:192` takes `EnterTableReadLock(type)`.
   - `XmlEngine.Queries.cs:196` calls `indexService.TryPlanExpression`:
     - `IndexService.QueryPlan.cs:18`: matches binary `Equal` node.
     - `IndexService.QueryPlan.cs:215`: calls `TryEvaluateConstant(right, out value)`.
     - `IndexService.QueryPlan.cs:281`: executes reflection `fi.GetValue(target)` on the compiler-generated closure field `id`.
2. **28-Character String Allocation per Integer PK Lookup**:
   - `IndexService.QueryPlan.cs:24` calls `NormalizeIndexValue(prop.PropertyType, constantValue)`.
   - `IndexService.Normalization.cs:72`: `SmallIntNormalizedCache` only caches values `0..2047`. For any ID $\ge 2048$, `NormalizeInt32` calls `FormatNormalizedInt64(iVal)` (lines 87–99), which executes `string.Create(28, ...)` and allocates a 28-character formatted string (`1000000000000050000.00000000`). In a 100k or 400k database, $>98\%$ of lookups miss the small cache and allocate this string.
   - `IndexService.QueryPlan.cs:26`: acquires `lock (syncLock)`.
   - `IndexService.QueryPlan.cs:29`: evaluates `tree.TryGetValue(normTarget, out var bucket)`. `propertyIndexes` stores `Dictionary<string, object>` where keys are 28-character strings, incurring string hashing and ordinal character comparisons.
   - `IndexService.QueryPlan.cs:32`: returns `candidateCodes = new[] { singleCode }`, allocating a new array per lookup.
3. **Format Divergence — The Whole-Page Parse Trap (`ISS-039`)**:
   - `XmlEngine.Queries.cs:240`: iterates `candidateCodes`.
   - `XmlEngine.Queries.cs:241`: calls `TryReadEntityByCode(code, meta, out fastObj)`.
   - `IOService.RawPages.cs:286`: `TryReadEntityFromRawPage` checks:
     ```csharp
     if (storageSerializer.Format != StorageFormat.Xml) return false;
     ```
     **For JSON and Binary, this returns `false` immediately.**
   - For JSON and Binary, execution falls back to `XmlEngine.Queries.cs:249`: `GetRawData(code)` $\rightarrow$ `ioService.ReadRowColdOrCached` (`IOService.RawPages.cs:273`):
     - `cachedFiles.TryGetValue(normalizedName)` checks the parsed-file cache.
     - If cold, `OpenFileOrCreate(normalizedName, typeof(TablePage), openOnly: true)` calls `Load(type, filePath)` (`IOService.Files.cs:341`).
     - **For JSON**: `JsonStorageSerializer.Deserialize` (`StorageSerializers.cs:117`) executes `JsonDocument.Parse(data)` and recursively instantiates hundreds of `XElement` DOM objects for all rows on the 512 KB page.
     - **For Binary**: `BinaryStorageSerializer.Deserialize` (`StorageSerializers.cs:234`) decompresses the entire 512 KB Brotli stream, then instantiates full `XElement` trees for the whole page.
     - `fullFile.GetRowByCode(rowCode)` extracts the single row.
     - `XmlEngine.Queries.cs:414`: `Read(type, rawRow)` reflects on properties to construct the POCO.
     - **Conclusion for JSON/Binary**: A point read forces a full 512 KB page decompress/parse and full-DOM recreation.
4. **XML Cold vs Warm Point Reads**:
   - XML avoids DOM parsing via `TryReadEntityFromRawPage` (`IOService.RawPages.cs:277`), using `RawPageBuffer.TryFindRow` and `TryParseEntityFromUtf8Row` (lines 1136–1268) with `System.Buffers.Text.Utf8Parser`.
   - However, even for XML, each of the 1,000 lookups spends ~89 µs because:
     a) It executes the Expression inspection, field reflection, 28-char string allocation, and hash-lookup pipeline.
     b) It allocates `string.Concat` (`code.AsSpan(0, dot) + ".xpag"`) and `Substring` (`code.Substring(dot + 1)`) in `TryReadEntityByCode`.
     c) It takes `lock (syncLock)` in `IOService.RawPages.cs:292` to fetch the raw buffer.
     d) It parses the UTF-8 span and instantiates a new entity object every time. `ItemsCacheService` is bypassed for standalone entities (`XmlEngine.Queries.cs:432`), so repeated lookups for the same ID never hit an entity cache.

---

### 1.2 Single Mutations (`Update` and `Delete`)
**Current Measured**: 100 updates + 100 deletes take 46.2 ms (XML) vs 4.2 ms (SQLite).

1. **Single `Update` Path (`XmlEngine.Mutations.cs:353–451`)**:
   - `EnterTableWriteLock(type)` is acquired per update.
   - `EnsureIndexHydrated(type)` checks hydration under `hydrationLock`.
   - `TryLookupCodesByProperties` resolves the item code via index.
   - `ioService.TryGetRowFromRawPage(pageFileName, rowCode, out rawRow, ...)` parses the existing row into an `XElement` via `XElement.Parse(rowXml)` (`IOService.RawPages.cs:487`).
   - `Write(type, newItem, false, itemTrack, filter)` (`XmlEngine.Mutations.cs:412`) creates a brand-new `XElement xNewRow`.
   - `xOldRow.ElementEquals(xNewRow)` (line 433) executes deep XML tree comparison.
   - `indexService.RemoveRowElement(type, rawRow, itemCode)` (line 435) re-reads property strings from `rawRow`, parses them into boxed values, re-normalizes them, and removes them from the in-memory index tree.
   - `indexService.IndexItem(type, newItem, itemCode)` (line 436) extracts values from `newItem` and indexes them.
   - `newRowWrapper = new XElement("Row", ...)` is created.
   - `ioService.SpliceRowInRawPage` (`IOService.RawPages.cs:561`):
     - Line 568: executes `System.Text.Encoding.UTF8.GetBytes(newRowElement.ToString(SaveOptions.DisableFormatting))` — a full string serialization of the new element back into UTF-8.
     - Copies bytes into `rawPage.Bytes`.
     - In `Buffered` durability, marks page in `dirtyBufferedRawPages`.
   - **Cost summary**: 1 scalar property change (`Price += 10.0`) generates two `XElement` trees, parses one XML string, converts one XML tree to string, converts to UTF-8 bytes, and deep-compares XML trees.

2. **Single `Delete` Path (`XmlEngine.Mutations.cs:852–960`, `1111–1173`)**:
   - `EnterTableWriteLock(type)`.
   - `ioService.TryDeleteRow(pageFileName, rowCode, out deletedRow, out pageEmpty)` (`IOService.RawPages.cs:500`):
     - Calls `TryGetRowFromRawPage` (line 531), which calls `XElement.Parse(rowXml)` to return `deletedRow`.
     - Calls `SpliceRowInRawPage` with `newRowElement = null` (line 533): fills row bytes in `rawPage.Bytes` with whitespace (fast in-place tombstone).
   - `indexService.RemoveRowElement(type, rawRow, itemCode)` (`XmlEngine.Mutations.cs:1127`):
     - Because `rawRow` was parsed and passed, `RemoveRowElement` parses XML elements/attributes to find indexed property values, parses scalars, normalizes to 28-char strings, and removes from buckets. (If `rawRow` were `null`, it would take the `O(1)` reverse index `codeToIndexEntries` path!).
   - **The Hidden Disk Write**: Lines 1129–1162 in `DeleteByItemCode`:
     ```csharp
     XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
     ...
     XAttribute cntAttr = tablePage.Attribute("count");
     if (cntAttr != null && int.TryParse(cntAttr.Value, out int curCnt) && curCnt > 0)
     {
         cntAttr.Value = (curCnt - 1).ToString();
         ioService.Save(tableFile);
     }
     ```
     On **every single row delete**, the `.xtab` table file is marked dirty and saved!

---

### 1.3 Relational Writes (Graph Insert & Cascade Delete)
**Current Measured**:
- Graph insert 10k: 3,995 ms vs 592 ms (SQLite).
- Cascade delete 100: 73 ms vs 11.3 ms (SQLite).

1. **Graph Insert (`InsertGraphs`, `XmlEngine.Relations.cs:48–160`)**:
   - In 10k Customer graphs (1 Customer $\rightarrow$ 5 Orders $\rightarrow$ 4 OrderLines each), there are 260,000 total entities.
   - `InsertRange(customers)` (`XmlEngine.Mutations.cs:743`) calls `ioService.BeginBatch()`.
   - For each customer, `Create(Customer)` calls `AddToPage` $\rightarrow$ `Write` $\rightarrow$ `ParseChildren`.
   - **Bypass of Fast Raw Append**:
     - `XmlEngine.Mutations.cs:77`: `TryAppendRawRowToPage` requires `meta.IsPureScalarStandalone && !shallowWrite && writeTrack == null`.
     - For `Customer`, it has navigation collections, so it fails `IsPureScalarStandalone`.
     - For `Order` and `OrderLine`, `writeTrack` is non-null (`itemCreateTrack`), so they fail `writeTrack == null`.
     - **All 260,000 entities fall back to the slow DOM path**: `OpenFileOrCreate(tableFile)` $\rightarrow$ `OpenPageOrCreate` $\rightarrow$ `XElement` DOM creation $\rightarrow$ `pageFile.AddRow` $\rightarrow$ `ioService.Save(pageFile)`.
   - **Severe Redundant Reflection & LINQ in `ParseChildren`**:
     - `XmlEngine.Relations.cs:54–75`: For every child, it executes:
       `propertyService.Properties(childType.FullName).FirstOrDefault(...)`
       `propertyService.Properties(type.FullName).FirstOrDefault(...)`
     - Lines 86–94: Inside the child loop, for every parent key, it calls `propertyService.Properties(...)` again and runs reflection `parentKeyProp.Property.GetValue(item)` and `childKeyProp.Property.SetValue(child, parentKeyValue)`.
     - Line 109 & 113: Calls `GetPrimaryValues(childType, child)` **twice** per child entity. `GetPrimaryValues` (`XmlEngine.Validation.cs:199`) allocates a new `Dictionary<string, object>` and executes reflection `primaryProp.GetValue(item)` on every call.
     - For 240,000 child rows, this executes over 1.5 million reflection lookups and allocates over 480,000 redundant dictionaries.
   - **Interleaved Page Switching**:
     - Execution alternates: Customer 1 $\rightarrow$ Order 1 $\rightarrow$ Line 1..4 $\rightarrow$ Order 2 $\rightarrow$ Line 5..8...
     - Each switch touches a different `XFile` (`Customer.xtab`, `Order.xtab`, `OrderLine.xtab` and their `.xpag` files), taking locks and looking up dictionaries in `IOService`.

2. **Cascade Delete (`XmlEngine.Mutations.cs:1175–1238`)**:
   - `DeleteCustomersCascade` deletes 100 customers:
     - 100 Customers + 500 Orders + 2,000 OrderLines = 2,600 deletions.
   - Inside `Delete(type, item, isCascade: false)`:
     - `bool shouldBatch = !isCascade && !ioService.IsBatchOrBuffered;`
     - When called without an active batch/transaction, `shouldBatch` starts and ends a batch for **every customer**.
   - Inside `DeleteByItemCode`:
     - For each of the 2,600 deleted records, it loads the `.xtab` file, modifies `<Page count="...">`, and executes `ioService.Save(tableFile)`.
     - 2,600 `.xtab` file saves occur!
   - `indexService.RemoveRowElement` is called 2,600 times with parsed `XElement` DOMs, doing scalar string parsing instead of using `codeToIndexEntries`.

---

## 2. Ranked Proposals

| Rank | Proposal | Target Metrics | Expected Gain | Risk | Effort |
| :---: | :--- | :--- | :---: | :---: | :---: |
| **1** | **Direct-Byte Splice for Pure-Scalar Updates & Batch `.xtab` Saves in Delete** | `updateDeleteMs`, `cascadeDeleteMs` | **2.5×–3× faster** updates/deletes; **2×–3× faster** cascade delete | Very Low | Low |
| **2** | **Zero-DOM UTF-8 Span Scanner & Row Index for JSON Pages (`ISS-039` Part 1)** | `pkLookup1000Ms` (JSON), `updateDeleteMs` (JSON) | **3×–5× faster** JSON point reads (1,249 ms $\rightarrow$ ~300 ms) | Low | Medium |
| **3** | **Compiled Pre-Resolved Graph Property Binding in `ParseChildren`** | `graphInsertMs` | **2× faster** graph inserts (3,995 ms $\rightarrow$ ~2,000 ms) | Low | Medium |
| **4** | **Integer PK Fast Path & Direct POCO Point-Lookup Cache** | `pkLookup1000Ms` (XML/JSON/Binary) | **3×–4× faster** XML point reads (89 ms $\rightarrow$ ~25 ms); repeat hits sub-5 µs | Low | Medium |
| **5** | **Block-Indexed Row Pages for Binary Storage (`ISS-039` Part 2)** | `pkLookup1000Ms` (Binary) | **5×–8× faster** Binary point reads (1,366 ms $\rightarrow$ ~250 ms) | Medium | High |
| **6** | **Batch Table Row-Count Adjustments & Defer `.xtab` File Saves to Batch End** | `bulkInsertMs`, `graphInsertMs`, `cascadeDeleteMs` | **15–25% faster** writes across all mutations | Very Low | Low |

---

### Proposal 1: Direct-Byte Splice for Pure-Scalar Updates & Eliminate Per-Row `.xtab` Writes
*(Recommended to run first: cheap, safe, immediately measurable)*

#### Mechanism
1. In `XmlEngine.Mutations.cs:399` (`UpdateItem`):
   When `meta.IsPureScalarStandalone` is true and no navigation properties or complex filters are involved:
   - Do NOT parse `rawRow` into `XElement` via `TryGetRowFromRawPage`.
   - Obtain the old row's offset and length from `RawPageBuffer.TryFindRow`.
   - Format `newItem` directly to UTF-8 bytes using the existing `FormatStandaloneRowUtf8(newItem, rowCode, meta, exceptionService, out byte[] newBytes)`.
   - Compare `newBytes` with `rawPage.Span.Slice(startOffset, oldLength)`. If identical, skip.
   - If changed, update the in-memory index via `meta.IndexedProperties` getters directly (no `XElement` parsing).
   - Call `ioService.SpliceRowInRawPageBytes(pageFileName, startOffset, oldLength, newBytes, rowCode, out _)`.
2. In `XmlEngine.Mutations.cs:1153–1162` (`DeleteByItemCode`):
   - When updating `<Page count="...">` on `.xtab`, check `ioService.IsBatchOrBuffered`.
   - If in batch/buffered mode, update the XML attribute in memory, but DO NOT call `ioService.Save(tableFile)` per deleted row. Instead, mark `tableFile` dirty once in `dirtyBatchFiles`/`dirtyBufferedFiles`.

#### Code to Change
- `src/Xod/Engines/Xml/XmlEngine.Mutations.cs` (`UpdateItem`, `DeleteByItemCode`).
- `src/Xod/Services/IOService.RawPages.cs` (add overload `SpliceRowInRawPage(..., ReadOnlySpan<byte> replacementBytes, ...)` so `FormatStandaloneRowUtf8` can pass scratch bytes without converting through `XElement.ToString()`).

#### Metric Moved
- `updateDeleteMs` (100 updates + 100 deletes): 46.2 ms $\rightarrow$ **~16–20 ms**.
- `cascadeDeleteMs` (100 cascades): 73 ms $\rightarrow$ **~25–30 ms**.

#### How It Could Regress
- If `newBytes` is longer than `oldLength`, `SpliceRowInRawPage` must handle byte shifting without corrupting `rawPage.RowIndex`. The existing `SpliceRowInRawPage` (lines 614–650) already handles expansion; we must ensure `RowIndex` is either updated or invalidated identically.
- Verification: run `XodIntegrityTests` and `XodStressTests`.

---

### Proposal 2: Zero-DOM UTF-8 Span Scanner & Row Index for JSON Pages (`ISS-039` Part 1)

#### Mechanism
Currently, `RawPageBuffer` is XML-only. JSON pages are loaded into `XFile` with full `JsonDocument` DOM trees.
1. Extend `RawPageBuffer` to support JSON storage format:
   - In JSON, each row is formatted as:
     `{"$name":"Row","$attrs":{"code":"<rowCode>"},"$children":[...]}`
   - Implement `EnsureJsonRowIndex()` using `System.Text.Json.Utf8JsonReader` in a single forward pass over `rawPage.Bytes`. It records `RowIndex[rowCode] = (Offset, Length)`.
2. Implement `TryParseEntityFromJsonRow(ReadOnlySpan<byte> rowSpan, TypeMetadata meta, out object entity)`:
   - Reads the `$children` array of properties using `Utf8JsonReader`.
   - Maps property names directly to `meta.ReadPropertyMap`.
   - Parses values using `reader.GetInt32()`, `reader.GetString()`, `reader.GetDecimal()`, etc., assigning via compiled setter delegates (`prop.IntSetter`, `prop.StringSetter`).
   - Zero allocations of `XDocument`, `XElement`, or `JsonDocument`.
3. In `IOService.RawPages.cs`:
   - Allow `TryReadEntityFromRawPage` and `ReadRowColdOrCached` to serve JSON pages via `RawPageBuffer` when `storageSerializer.Format == StorageFormat.Json`.

#### Code to Change
- `src/Xod/Services/RawPageBuffer.cs`: add JSON row delimiter scanning.
- `src/Xod/Services/IOService.RawPages.cs`: enable JSON in `ReadRawPageBufferFromDisk` and add `TryParseEntityFromJsonRow`.
- `src/Xod/Engines/Xml/XmlEngine.Queries.cs`: enable `useFastReader` for JSON format in `QueryExpression`.

#### Metric Moved
- `pkLookup1000Ms` on JSON at 250 MB: 1,249 ms $\rightarrow$ **~250–350 ms** (~4× faster).
- `pkLookup1000Ms` on JSON at 1 GB: 2,175 ms $\rightarrow$ **~500–600 ms**.
- `updateDeleteMs` on JSON: 188 ms $\rightarrow$ **~70 ms**.

#### How It Could Regress
- JSON escaping: JSON string values with quotes or backslashes must be unescaped properly by `Utf8JsonReader.GetString()`.
- Format compliance: Indented vs non-indented JSON. `Utf8JsonReader` tokenizes whitespace-insensitively, so it handles both automatically.
- Verification: `XodIntegrityTests` (all 3 formats) and `XodCacheTests`.

---

### Proposal 3: Compiled Pre-Resolved Graph Property Binding in `ParseChildren`

#### Mechanism
Currently, `ParseChildren` (`XmlEngine.Relations.cs:48–125`) executes dynamic LINQ queries and property metadata lookups for every single child element in a loop:
```csharp
PropertyInfoItem childParentPropItem = ... propertyService.Properties(childType.FullName)...;
parentKeyProp = propertyService.Properties(type.FullName)...;
```
For 240,000 children in `InsertGraphs`, this is called 240,000 times.

We introduce a cached `ChildRelationshipBinding` on `PropertyInfoItem`:
1. On type initialization in `PropertyService`, pre-resolve:
   - `ParentKeyProperty`: compiled getter delegate `Func<object, object>` or `IntGetter`.
   - `ChildForeignKeyProperty`: compiled setter delegate `Action<object, object>` or `IntSetter`.
   - `ChildPrimaryKeyGetter`: compiled delegate returning the primary key tuple or dictionary without allocations.
2. In `ParseChildren`:
   - Fetch the pre-computed `ChildRelationshipBinding` once per collection, outside the child loop.
   - Inside `foreach (var child in children)`:
     - Read parent value via compiled getter (0 reflection).
     - Assign child foreign key via compiled setter (0 reflection).
     - Retrieve child PK via pre-allocated value reader (0 dictionary allocations).
3. If `childType` is pure-scalar/standalone (or leaf), stream it into the table page buffer without full DOM instantiation.

#### Code to Change
- `src/Xod/Metadata/PropertyInfoItem.cs`: add `ChildBinding` struct.
- `src/Xod/Services/PropertyService.cs`: pre-compute relationship binding during `GetMetadata`.
- `src/Xod/Engines/Xml/XmlEngine.Relations.cs`: rewrite `ParseChildren` to use the pre-computed binding.

#### Metric Moved
- `graphInsertMs` (10k customer graphs): 3,995 ms $\rightarrow$ **~1,800–2,200 ms** (nearly 2× faster).
- `200 graph loads`: 143 ms $\rightarrow$ **~80–90 ms**.

#### How It Could Regress
- Convention-based key naming (`type.Name + parentPkProp.PropertyName`) must match existing behavior exactly. Pre-compute must replicate `ConventionChildKey` logic.
- Verification: `Relationships_ForeignKey_Complex_And_ParentChildren_Work` and `XodStressTests`.

---

### Proposal 4: Integer Primary Key Fast Path & Standalone Point-Lookup Cache

#### Mechanism
1. **Enlarge Small Int Cache**:
   - `IndexService.Normalization.cs`: increase `SmallIntNormalizedCache` from `2048` to `131,072` (or use a direct `NormalizeInt32` with a `stackalloc char[28]` or pre-hashed key).
   - 131,072 pre-allocated strings take ~4 MB of RAM and eliminate 100% of the 28-character string allocations during 100k benchmarks.
2. **Specialized `FindById<T>` / Expression Fast-Path**:
   - In `IndexService.QueryPlan.cs:TryPlanExpression`:
     - If the expression is `x => x.Id == constant` on an `Int32` primary key:
     - Directly fetch the `string itemCode` without allocating `candidateCodes = new[] { singleCode }`.
3. **Bounded Standalone Entity LRU Cache**:
   - Add a lightweight `EntityCache<Type, string, object>` (clamped to 2,048 entries, LRU or clock) in `XmlEngine`.
   - When a standalone entity is hydrated by PK lookup, put it in the cache.
   - On `UpdateItem` or `DeleteByItemCode`, evict the `itemCode`.
   - Subsequent `Find(x => x.Id == id)` calls hit the cache in ~0.5 µs.

#### Code to Change
- `src/Xod/Services/IndexService.Normalization.cs` (`SmallIntNormalizedCache`).
- `src/Xod/Engines/Xml/XmlEngine.Queries.cs` (`QueryExpression`).
- `src/Xod/Engines/Xml/XmlEngine.Mutations.cs` (invalidation on update/delete).

#### Metric Moved
- `pkLookup1000Ms` on XML (cold/mixed): 88.9 ms $\rightarrow$ **~25–35 ms**.
- Repeat lookups: **sub-5 µs** per read.

#### How It Could Regress
- Cache stale read: if an entity is updated via `Update` or deleted via `Delete`, the entity cache MUST be invalidated. Invalidation must be placed directly in `DeleteByItemCode` and `UpdateItem`.
- Verification: `Scale_Rapid_Update_Delete_And_Upsert_Via_PrimaryKey_Index` and concurrency stress tests.

---

### Proposal 5: Block-Indexed Row Pages for Binary Storage (`ISS-039` Part 2)

#### Mechanism
Currently, `BinaryStorageSerializer` wraps the entire page in a single `BrotliStream`.
To achieve $O(1)$ point reads in Binary format without decompressing the entire 512 KB:
1. Introduce Binary Storage Format version 3 (`XODB\x03`):
   - **Header**: Magic `XODB\x03`, Page Attribute flags, `RowCount` (int32).
   - **Row Offset Table**: Array of `(int32 RowCodeHash, int32 ByteOffset, int32 ByteLength)` located at the page start or tail.
   - **Row Data**: Each row is individually or chunk-compressed (e.g., 8 KB blocks, or raw binary element encoding without page-level Brotli, since 512 KB uncompressed binary is already small).
2. Point read:
   - Read row offset table in $O(1)$.
   - Seek directly to `ByteOffset` and read `ByteLength` bytes.
   - Decode only that row's binary fields directly into the entity.
3. Backward Compatibility:
   - `StorageSerializerFactory.DetectFormat` detects `XODB\x02` vs `XODB\x03`.
   - Existing `XODB\x02` pages are deserialized using the existing whole-page Brotli reader.
   - New or rewritten pages use `XODB\x03`.

#### Code to Change
- `src/Xod/Engines/Serializers/StorageSerializers.cs` (`BinaryStorageSerializer`).
- `src/Xod/Services/IOService.RawPages.cs` (binary row slicing path).

#### Metric Moved
- `pkLookup1000Ms` on Binary: 1,366 ms $\rightarrow$ **~180–250 ms** (~6× faster).
- `pkLookup1000Ms` on Binary at 1 GB: 2,947 ms $\rightarrow$ **~400 ms**.

#### How It Could Regress
- Requires format versioning. Old databases must load seamlessly without migration commands.
- File size trade-off: per-row or chunked compression may yield slightly larger files than a single optimal Brotli stream (~75 MB vs 70 MB at 250 MB).

---

### Proposal 6: Batch Table Row-Count Adjustments & Defer `.xtab` Saves to Batch End

#### Mechanism
Currently, both `AddToPage` (`XmlEngine.Mutations.cs:126`) and `DeleteByItemCode` (`XmlEngine.Mutations.cs:1159`) increment/decrement `tablePageElement.Attribute("count")` and call `ioService.Save(tableFile)`.
In `BeginBatch()` or `DurabilityMode.Buffered`:
- Track table page row counts in an in-memory dictionary `Dictionary<string, int> pendingPageCountDeltas`.
- Apply deltas to `<Page count="...">` in the XML DOM and save `tableFile` only when the batch completes (`EndBatch()`) or when `Flush()` is called.

#### Code to Change
- `src/Xod/Engines/Xml/XmlEngine.Mutations.cs` (`AddToPage`, `DeleteByItemCode`).
- `src/Xod/Services/IOService.Files.cs`.

#### Metric Moved
- `bulkInsertMs`: 1,140 ms $\rightarrow$ **~1,050 ms**.
- `graphInsertMs`: cuts redundant `.xtab` saves across 260k entities.
- `cascadeDeleteMs`: cuts 2,600 `.xtab` saves.

#### How It Could Regress
- Crash recovery: if a crash occurs mid-batch, `.xtab` row counts could be slightly out of sync with actual page counts. However, `XodContext` already has page self-healing and recovers page row counts on startup.
- Verification: `Crash_Recovery_Orphan_Tmp_And_Stale_Index_Self_Heal_On_Startup`.

---

## 3. Recommended Implementation Experiments

### Experiment 1: The Single-Mutation Fast Path (Testing Proposal 1)
- **Hypothesis**: Replacing `XElement` DOM creation and string conversion with `FormatStandaloneRowUtf8` in `UpdateItem`, and eliminating per-row `.xtab` saves in `DeleteByItemCode`, will drop `updateDeleteMs` on XML from 46 ms to $<20$ ms on the same machine.
- **Test Command**:
  ```powershell
  dotnet test -c Release --filter "Scale_Rapid_Update_Delete_And_Upsert_Via_PrimaryKey_Index"
  dotnet run -c Release --project benchmarks/XodBench -- --engines xod-xml --scales 250mb --iterations 3
  ```
- **Success Criteria**:
  - `updateDeleteMs` $\le 22$ ms.
  - 40/40 tests pass.
  - Verification check `store.GetById(updateIds[0]).Price == before + 10.0` passes.

### Experiment 2: JSON Zero-DOM UTF-8 Span Scanner (Testing Proposal 2)
- **Hypothesis**: Reading JSON rows directly via `Utf8JsonReader` inside `RawPageBuffer` without instantiating `JsonDocument` or `XDocument` will drop 1,000 JSON PK lookups from 1,249 ms to $<350$ ms.
- **Test Command**:
  ```powershell
  dotnet test -c Release --filter "XodIntegrityTests"
  dotnet run -c Release --project benchmarks/XodBench -- --engines xod-json --scales 250mb --iterations 3
  ```
- **Success Criteria**:
  - `pkLookup1000Ms` (JSON) $\le 400$ ms.
  - `XodIntegrityTests` 9/9 pass.

### Experiment 3: Pre-Bound Relational Graph Insert (Testing Proposal 3)
- **Hypothesis**: Eliminating repeated LINQ lookups and duplicate `GetPrimaryValues` dictionaries in `ParseChildren` will cut 10k relational graph insert from ~3,995 ms to $<2,200$ ms.
- **Test Command**:
  ```powershell
  dotnet run -c Release --project benchmarks/XodBench -- --engines xod-xml --scales 250mb --relational --iterations 3
  ```
- **Success Criteria**:
  - `graphInsertMs` $\le 2,200$ ms.
  - Counts match `(c, c * ordersPer, c * ordersPer * linesPer)`.

---

## 4. What We Would NOT Do (And Why)

1. **Do NOT lower the default page size below 512 KB.**
   - *Evidence*: `PLAN-035` swept 32 KB–512 KB across all 3 formats. While 64–128 KB pages reduced JSON/Binary lookups by ~1.6–1.9×, they made indexed queries **25×–100× slower** (9 ms $\rightarrow$ 229 ms) and bulk load 2×–4× slower. 512 KB wins XML across every metric. The defect is whole-page parsing, not page size. Fixing whole-page parsing solves the lookup cost without sacrificing indexed queries.
2. **Do NOT weaken durability or drop fsync/Flush to match SQLite.**
   - The fair benchmark harness (`PLAN-028`) was built specifically because previous benchmarks were unfair. XOD and SQLite run with equivalent group-commit durability in `DurabilityMode.Buffered`. Never remove integrity guarantees to chase a number.
3. **Do NOT break format parity or make XML/JSON/Binary diverge in feature capabilities.**
   - Rule 3 from `HANDOFF-2026-11-04.md`: "Fix the cause, not the symptom... do not special-case the format." Any capability added (like row-level access or fast tombstones) must have a defined, robust implementation for all three formats.
4. **Do NOT introduce a background server process, daemon, or memory-mapped file lock manager.**
   - XOD's core identity is a clean, dependency-free, cross-platform embedded database. In-process locking with `ReaderWriterLockSlim` and cross-process file locks (`DatabaseOptions.LockDatabase`) are sufficient.
5. **Do NOT add non-deterministic caching without strict invalidation ownership.**
   - Caching an entity without hooking every mutation path (`Update`, `Delete`, `CascadeDelete`, `Truncate`, `DropType`) creates phantom reads. If an entity cache is added (Proposal 4), invalidation must occur at the mutation owner (`UpdateItem`, `DeleteByItemCode`).

---

## 5. Review by the Claude-side agent (2026-11-04)

Read against the code at `446ea28`. The analysis is sound and the file citations are accurate. Four
corrections and one change of order before implementation.

### What checks out

- `SmallIntNormalizedCache` is 2048 entries (`IndexService.Normalization.cs:8-16`); every integer
  above 2047 goes through `FormatNormalizedInt64`.
- Every single delete opens the `.xtab` and saves it to decrement `<Page count>` — confirmed at
  `XmlEngine.Mutations.cs:1138-1160`; the cascade path reaches it once per child.
- The XML update path parses the old row into `XElement` (`Mutations.cs:399`), compares two DOM trees
  (`:433`) and serializes the new one back (`:438` → `SpliceRowInRawPage`).
- Raw-page fast paths are gated to XML at `IOService.RawPages.cs:285, 396, 470, 690, 1289, 1342, 1396`.
  JSON and Binary therefore pay a whole-page parse per row — `ISS-039` as filed.

### Corrections

1. **"2,600 `.xtab` saves" is API churn, not I/O.** Under `DurabilityMode.Buffered` and inside a batch,
   `IOService.Save` does no disk write: it takes `syncLock`, drops the cached raw page for that file,
   recomputes cache cost and adds the file to a dirty set (`IOService.Files.cs:223-259`). Deferring the
   count adjustment is still right — the lock and the cache eviction per row are real — but expect the
   win to come from removing that churn, not from removing 2,600 writes. Measure before claiming 3×.
2. **Quote the committed set, not a side run.** The numbers in §1.1 do not match `benchmarks/results/`:
   at 250 MB JSON is 1,111 ms and Binary 1,253 ms (not 1,249/1,366); at 1 GB JSON is 1,938 ms and
   Binary 2,416 ms (not 2,175/2,947). Attach the result file name to each figure, or the provenance rule
   in `results/README.md` is broken by the document that argues for it.
3. **Experiment 3's command is invalid.** The harness has no `--relational` flag (`XodBench/Program.cs`:
   `--engine`, `--suite`, `--scale`, `--dir`, `--iteration`, and the parent `--engines/--scales/--iterations`).
   Use `--suite relational --scale 10k`.
4. **Proposal 1 and Proposal 6 overlap.** Both defer `.xtab` count saves; they should land as one change
   with one measurement, not two proposals chasing the same files.

### Order I would actually run

1. **Deferred `.xtab` count saves** (proposals 1b + 6): smallest, safest, measurable on `updateDeleteMs`,
   `cascadeDeleteMs` and `graphInsertMs` at once. Run `XodIntegrityTests` plus the crash-recovery test.
2. **XML update byte path** (1a): format the new row with `FormatStandaloneRowUtf8` and splice bytes,
   skipping the two DOM trees and the deep compare. Needs the `SpliceRowInRawPage(ReadOnlySpan<byte>)`
   overload and care with the row index when the row grows.
3. **JSON row spans** (proposal 2): the big one for `ISS-039`; the `Utf8JsonReader`-per-row design is
   right. Keep the `RawPageBuffer` contract, add JSON to the gate only when the parse path is proven.
4. **Graph property binding** (proposal 3): mechanical, low risk, ~2× on `graphInsertMs`.
5. **Entity cache** (proposal 4b) **after** the read path is fast. It hides invalidation bugs behind a
   faster number, and the stress tests do not yet cover `Truncate`/`DropType` invalidation.
6. **Binary format v3** (proposal 5) **last, and only after** 2.0 gate layers 6 and 7 (migration from a
   real 1.x database, format equality) are green. A new on-disk version is a release decision, not a
   benchmark improvement.

Proposal 4a (enlarging the integer cache) is worth ten minutes on its own: 131,072 pre-normalised
strings is ~4 MB and removes the 28-character allocation from every integer key lookup.

*Reviewed 2026-11-04 after reading the plan against `src/Xod` at `446ea28`.*

---

## 6. Experiment log

### 2026-11-04 - Deferred `.xtab` counter saves (proposals 1b + 6): **no win, reverted**

Added `IOService.MarkDirty(XFile)`: inside a batch or `Buffered` durability it adds the file to the
existing dirty set without taking the cache path (`Save` re-registers the file, drops its cached raw
page and recomputes its cost per row). Applied it to the table-counter update on the delete path and
to both `Save(tableFile)` calls on the insert path. 40/40 tests green throughout.

| Metric (XML, 250 MB unless noted) | Before | After | Verdict |
| :--- | ---: | ---: | :--- |
| `updateDeleteMs` (median of 5) | 49.0 ms | 52.8 ms | inside noise (42-67 ms range both sides) |
| `bulkInsertMs` (median of 5) | 1,254 ms | 1,261 ms | no change |
| `cascadeDelete100Ms` (relational 10k, median of 3) | 83.7 ms | 79.9 ms | inside noise (77-100 vs 76-84) |

**What this rules out:** the per-row table-file save is not where mutation time goes. `Save` under
`Buffered` does no disk I/O, and its cache bookkeeping turns out to be cheap next to the row work.
The cost is in the row itself: `XElement` parse on read, the second DOM tree on write, the deep
`ElementEquals`, the serialise-back, and the page splice. That is proposal 1a, not 1b.

**Also learned:** `updateDeleteMs` mixes 100 updates with 100 deletes and both sides vary by +-25%
between runs on this machine, which is too coarse to prove a 10-20% change. Before the next attempt,
split the metric into `update100Ms` and `delete100Ms` (additive change to the harness, keep the
existing metric) so each half can be judged on its own.

### 2026-11-04 - Byte-level replace for pure-scalar updates (proposal 1a): **slower, reverted**

Split `SpliceRowInRawPage` into an `XElement` overload and a byte-level `(byte[], bool isDelete)` overload;
added `IOService.TryReplaceStandaloneRow` (find span -> `FormatStandaloneRowUtf8` -> byte compare ->
splice) and wired it into the primary-key branch of `UpdateItem`, with `RemoveRowElement(type, null, code)`
for the index delta. 40/40 tests green while live.

Interleaved A/B against a baseline harness build, 4 pairs each: before median **47.9 ms**, after
median **63.6 ms** for `updateDeleteMs` - **33% worse**. Reverted.

Hypotheses for the loss (test before retrying): the fast path may fall back after already paying the
validation and a raw-page load; `RemoveItemCode` may cost more than parsing the row; `FormatStandaloneRowUtf8`
writes the same bytes as the DOM serialiser, so only parse and compare were saved; or the cost sits in the
lock, hydration, splice copy or flush path rather than in the DOM work.

Full numbers and the measurement protocol: `docs/REPORT-2026-11-04-speed-experiments.md`.

---

## 7. Withdrawn after the warm-up fix (2026-11-04, end of day)

The recorded lookups that motivated this plan were measured with a harness that warmed only 100 ids.
`Runner` now probes the whole key range before timing, and the picture inverts:

| 1,000 lookups | 250 MB | 1 GB |
| :--- | ---: | ---: |
| XOD++ (XML) | 4.5 ms | 4.8 ms |
| XOD++ (JSON) | 2.4 ms | 3.2 ms |
| XOD++ (Binary) | 2.4 ms | 2.9 ms |
| SQLite 3 (WAL) | 7.1 ms | 9.3 ms |
| LiteDB 5.0 | 41.6 ms | 54.9 ms |

Consequences:

- **`ISS-039` is closed as a measurement defect.** There is no row-access gap to close on this metric.
- **Proposal 2 (JSON row spans, part 1) loses its justification.** Do not build it for point lookups.
- **Proposal 3 (graph binding) and the relational load path still stand:** `loadGraph200Ms` at 10k is
  153 ms (XML) and ~2,810 ms (JSON/Binary) against 12 ms for SQLite. That is a real gap and it is where
  a row-access change would now have to prove itself.
- Proposals 4 and 5 (integer cache, Binary format v3) are not justified by the corrected data either.

The current numbers come from `benchmarks/results/bench-v1.2.7-a2c31e5-20261104_225445.json` (75 runs,
clean stamp).
