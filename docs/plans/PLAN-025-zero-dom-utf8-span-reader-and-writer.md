# PLAN-025: Zero-DOM UTF-8 Span Reader & Streaming Page Writer (`v1.2.3`)

- **Task ID**: `TASK-030`
- **Fixes Issue**: [`ISS-030`](../KNOWN-ISSUES.md)
- **Status**: Implemented (v1.2.3)

## Problem Statement
In `XOD++ v1.2.2`, we eliminated full-page `XDocument.Load` calls on cold point lookups and indexed queries by caching raw `.xpag` `byte[]` buffers (`cachedRawPageBytes`) and slicing `<Row code="...">...</Row>` segments. However, two `System.Xml.Linq` (`XElement`) bottlenecks still remain on the hot path:
1. **Per-Row `XElement.Parse(string)` Allocation During Queries (`IndexedQuery20x500Ms`: `166 ms`, `PkLookup100Ms`: `18 ms`)**:
   For every matched row, `ReadRowColdOrCached` converts the `2.2 KB` UTF-8 `<Row>` byte slice into a UTF-16 `string`, calls `XElement.Parse(rowXml)` (instantiating an `XmlReader` and allocating `6–10` `XElement`/`XAttribute`/`XText` objects per row = `60,000+` DOM nodes per 10,000-row query), and then `XmlEngine.Read` iterates those `XElement`s to parse scalar values.
2. **Per-Row `XElement` Tree Construction & `XmlWriter` DOM Traversal During Bulk Inserts (`BulkInsert100kMs`: `2,483 ms`)**:
   Inserting `100,000` rows in `InsertRange` allocates `600,000+` `XElement` and `XAttribute` nodes across `424` `XDocument` instances, only to serialize them to UTF-8 bytes via `XmlWriter` when each `512 KB` page fills up.

---

## Design & Refactoring Steps

### 1. Pre-Encoded UTF-8 Tag Metadata on `PropertyInfoItem`
- Pre-compute `byte[] Utf8OpenTag` (`<PropName>`) and `byte[] Utf8CloseTag` (`</PropName>`) on each `PropertyInfoItem` during `PropertyService.BuildMetadata(Type)`.
- Store a fast lookup table in `TypeMetadata` mapping UTF-8 property tag names to `PropertyInfoItem`.

### 2. Zero-DOM UTF-8 Span Deserializer (`ReadFromUtf8Span`)
- Add `TryReadRowUtf8(Type type, TypeMetadata meta, string pageFileName, string rowCode, out object entity)` for standalone entities (`meta.IsStandalone && !meta.HasAttributeProperties && !meta.HasDefaultValueProperties`):
  - Locate `<Row code="{rowCode}">...</Row>` directly in `ReadOnlySpan<byte>` from `cachedRawPageBytes`.
  - Scan child `<PropName>Value</PropName>` tags directly in the UTF-8 byte slice using `ReadOnlySpan<byte>.IndexOf((byte)'<')` and `IndexOf((byte)'>')`.
  - Parse scalar primitives (`int`, `long`, `double`, `decimal`, `bool`, `Guid`) directly from `ReadOnlySpan<byte>` via `System.Buffers.Text.Utf8Parser` (zero string allocation for numbers/booleans/GUIDs!) and decode strings via `Encoding.UTF8.GetString(valueSpan)` (with XML entity unescaping only if `valueSpan.IndexOf((byte)'&') >= 0`).
  - Set properties on the target instance via the pre-compiled `PropertyInfoItem.Setter` delegate.

### 3. Direct UTF-8 Page Buffer Writer for Standalone Batch Inserts
- When `InsertRange` executes on a standalone entity (`meta.IsStandalone`) in unencrypted `StorageFormat.Xml` mode:
  - Format `<Row code="{itemCode}"><{TypeName}>...</{TypeName}></Row>` directly into a reusable `ArrayBufferWriter<byte>` (`64 KB`–`512 KB` buffer) using `Utf8Formatter` for numeric primitives and UTF-8 encoding with fast XML-character check for strings.
  - Assemble full `512 KB` `.xpag` byte buffers directly in memory and flush them to disk / `cachedRawPageBytes` without allocating `XDocument` or `XElement` nodes.

---

## Target Benchmark Impact (`250 MB` / `100,000` Rows)
- **`BulkInsert100kMs`**: `2,483 ms` $\rightarrow$ **`~1,200–1,450 ms`** (surpassing both `SQLite WAL` and `LiteDB 5.0`).
- **`IndexedQuery20x500Ms`**: `166 ms` $\rightarrow$ **`~45–65 ms`** (surpassing both `SQLite WAL` and `LiteDB 5.0`).
- **`PkLookup100Ms`**: `18 ms` $\rightarrow$ **`~5–8 ms`**.
