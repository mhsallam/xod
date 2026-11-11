# PLAN-046: The XOD file format (spec draft)

- **Task ID**: `TASK-057` (roadmap step 1, second of three specs - `PLAN-044`)
- **Status**: Spec accepted (2026-11-11). Sections 2-9 describe the files XOD 2.0 writes today, from
  the code and checked against real files; section 10 specifies the schema manifest from `PLAN-045` D1,
  with E1-E3 decided as recommended (section 13). Nothing is built.
- **Related**: [`PLAN-045`](PLAN-045-query-language-spec.md) (XOD SQL; D1 asks for the manifest),
  [`PLAN-044`](PLAN-044-roadmap-sql-analytics-rust-browser.md) (Rust core, browser),
  [`MIGRATION-GUIDE.md`](../MIGRATION-GUIDE.md) (the 2017 XOD 1.x files)

## 1. What this is

The on-disk format of an XOD database, exact enough for a second implementation (the CLI without a
model, a Rust core, the browser) to read the files XOD 2.0 writes, and to know which parts it may trust.

- **Scope:** files written by XOD 2.0.x and by the internal 1.2.x builds, which share this layout (the
  `tests/Xod.Tests/fixtures/v1.2.5-xml` fixture). The 2017 XOD 0.1/1.x files differ; see the migration
  guide.
- **Words:** *must* is a rule a reader or writer has to follow. *Writer* means the .NET library.
  *Today* means 2.0.1 as of 2026-11-11.
- **Where each statement comes from:** the code (`StorageSerializers`, `IOService.*`,
  `XmlEngine.Mutations`/`Relations`, `IndexService.*`, `TransactionService`, `FileCryptoHelper`,
  `CryptoHelper`, `ValueHelper`), checked against real files (section 12). Defects found on the way are
  listed in section 11 and filed as ISS-050 to ISS-055.

## 2. A database is a folder

| File | One per | What it is | Section |
| :--- | :--- | :--- | :--- |
| `<name>.xod` (`.xodj`, `.xodb`) | database | the database file: version and the format chosen at creation | 5 |
| `<Table>.xtab` | table | the table file: its pages in order (and, with D1, its schema) | 6 |
| `<code>.xpag` | page | rows of one table | 7 |
| `<Table>.xidx` | table | the index: a derived cache, may be missing or deleted at any time | 8 |
| `transaction.xwal` | commit in progress | the undo journal; present only during a commit or after a crash | 9 |
| `<file>.tmp` | write in progress | an atomic write cut short | 9 |
| `<name>.xod.lock` | open database | held open when `DatabaseOptions.LockDatabase` is on | 9 |

- **`<Table>`** is the class's `Type.FullName` (`MyApp.Shop.Customer.xtab`). Rows name the class by its
  short name (section 7).
- **`<code>`** is nine lowercase hex digits (`e2d552584`), from `ValueHelper.PickCode`.
- **The database file:** the path given to `XodContext` when it ends in `.xod`/`.xodj`/`.xodb`;
  otherwise the first `*.xod*` in the folder, or `data.xod` for a new database. The extension only picks
  the default encoding for new files; it doesn't describe the files.
- Readers must ignore files they don't know.

## 3. Layers

Every file except the journal, the lock and the binary index is a **document**: a tree of elements
(section 4), stored in one of three **encodings** (XML, JSON, Binary), optionally inside the **encryption
envelope** (section 3.2).

To read a file:

1. If it starts with `XODENC2\0`, open the envelope.
2. Detect the encoding from the first bytes:
   - `XODB` means Binary;
   - otherwise skip a UTF-8 BOM and whitespace; then `{` means JSON and anything else XML.
3. Decode the tree.

Detect the encoding per file, never from the database file's `Format` attribute or the extension.
Opening a database with a different `StorageFormat` option writes new and rewritten files in that
encoding and leaves the rest, so one folder can hold all three encodings.

### 3.1 The binary building blocks

These are used by the Binary encoding and the binary index:

- **int32**: 4 bytes, little-endian, two's complement.
- **bool**: 1 byte, `0` or `1`.
- **string**: .NET `BinaryWriter.Write(string)`. The UTF-8 byte count comes first as an unsigned LEB128
  ("7-bit encoded"; at most 5 bytes), then the UTF-8 bytes.

### 3.2 The encryption envelope (database password)

With a password, every document file and the index are encrypted one by one. The journal isn't
encrypted, but it holds the files' encrypted bytes (section 9).

| Offset | Bytes | Content |
| :--- | :--- | :--- |
| 0 | 8 | `XODENC2\0` (ASCII, then a zero byte) |
| 8 | 16 | salt (random per write) |
| 24 | 12 | nonce (random per write) |
| 36 | 16 | GCM tag |
| 52 | rest | ciphertext |

- **Key:** `PBKDF2-HMAC-SHA256(P, salt, 100000 iterations, 32 bytes)`, where `P` is the UTF-8 bytes of
  the **lowercase hex SHA-256 of the UTF-8 password**. The library hashes the password first and uses
  the hex text as the passphrase everywhere.
- **Cipher:** AES-256-GCM, with the 8 magic bytes as associated data.
- A wrong password fails the tag check. The library reports `SecurityException`.
- **Legacy:** a file without the magic is tried as 1.x AES-CBC. The key is the passphrase's ASCII
  padded with `0` to 32 bytes; the IV is its first 16 bytes. Readers of 2.0 files may omit this.

## 4. Documents

### 4.1 The tree

An element has:
- a name;
- attributes (ordered, each a name and a text value);
- and **either** child elements, **or** a text value (optionally marked CDATA), **or** nothing.

There is no mixed content and there are no namespaces.

### 4.2 XML

- UTF-8 without a BOM. It starts with `<?xml version="1.0" encoding="utf-8"?>` and has no indentation.
- The five standard entities and numeric character references.
- Readers must refuse a DTD; the library prohibits them.
- **Whitespace between elements is padding** and must be ignored. Pages contain runs of spaces where rows
  were deleted or shrunk in place (section 7.3).
- **Two writers, two escapings.** The fast row writer (section 7.3) escapes only `&`, `<` and `>`. It
  writes every other character as UTF-8: a tab, a raw CR LF, even U+0001. The document writer
  (`XmlWriter`, with `CheckCharacters = false`) writes U+0001 as `&#x1;`, which XML 1.0 forbids.
  A reader of today's files must therefore:
  - accept raw control characters and references to them;
  - keep a raw CR in text, **without** XML's line-end normalisation. A conforming XML parser turns a
    raw CR LF into LF and changes the value; the library's fast reader keeps it.

  The library's own document reader fails on these files (ISS-051).

### 4.3 JSON

```json
{ "$engine": "XOD++ JSON Storage v2.0",
  "$root": { "$name": "Row", "$attrs": { "code": "3bab93e9b" }, "$children": [ ... ] } }
```

- An element is an object:
  - `$name`;
  - `$attrs`: an object of strings, present when there are attributes;
  - then `$children` (an array of elements), or `$value` (a string) with `"$cdata": true` for CDATA,
    or neither.
- The writer indents and escapes `+` as `+`.
- Readers must ignore `$engine`.

### 4.4 Binary

```
"XODB" 0x02 | Brotli( bool hasRoot | element )
element = string name | int32 attrCount | (string name, string value) * attrCount
        | int32 childCount | element * childCount            when childCount > 0
        | byte kind [string text]                             when childCount = 0
kind    = 0 empty, 1 text, 2 CDATA text
```

The fifth byte is the encoding's version; `2` is the only one. The whole payload after the five bytes
is one Brotli stream (quality "optimal"). A reader must check that the stream ends where the root
element ends.

## 5. The database file

```xml
<Database Version="2.0" Format="Xml" />
```

- `Version` is `2.0` for every 2.x release.
- `Format` is the encoding chosen when the database was created; `XodMigrator` rewrites it. It is
  advisory (section 3).
- The library creates the file when it is missing. Readers check only that it decodes.

## 6. The table file

```xml
<Table>
  <Pages dataType="Xod.TablePage" collType="generic" refType="complex">
    <Page file="e2d552584.xpag" full="false" count="1">
      <Index property="Id" begins="1000000000000000001.00000000" ends="1000000000000000001.00000000" />
    </Page>
  </Pages>
  <Indexes dataType="Xod.PageIndex" collType="generic" refType="complex" />
</Table>
```

- **`<Page>` order is the table's order.** Rows are read page by page in this order, then row by row
  inside each page. This is the "stored order" that `PLAN-045` D5 guarantees.
- `file` (required): the page file's name.
- `full`: `true` once the page reached the page size (section 7.4). A full page gets no new rows.
- `count` (advisory): the library uses it only to skip whole **full** pages when paging. It is exact on
  disk only as of the table file's last write; appends to a page that isn't full don't rewrite the table
  file. Readers must count rows themselves.
- `<Index>` (advisory, unused): `begins` is the first normalised key written to the page and `ends` the
  **last** (not the largest). Nothing in the library reads them. Readers must ignore them (ISS-055).
- `<Indexes>` is always empty.
- **Unknown children** (the `<Schema>` of section 10) must be ignored. 2.0 keeps them when it rewrites the
  file (section 12).

## 7. Pages and rows

### 7.1 The page

```xml
<TablePage Full="False">
  <Rows dataType="Xod.PageRow" collType="generic" refType="complex">
    <Row code="ec4cb8f22"><Order><Id>1</Id><CustomerId>1</CustomerId><Status>open</Status><Total>30</Total></Order></Row>
    ...
  </Rows>
</TablePage>
```

- A page created by the fast writer starts as `<TablePage full="false"><Rows>` instead: lower-case,
  without the `Rows` attributes (ISS-055). Nothing reads the page's own `Full`/`full`.
- Each `<Row>` has a `code` (nine lowercase hex digits, unique in its page) and exactly one child: the
  **entity element**, named by the class's short name (`Type.Name`).
- A row's **item code** is `<page code>.<row code>` (`30ee1031c.ec4cb8f22`). References and the index use
  it.

### 7.2 The entity element

- **Attributes:** scalar properties marked `[Property(Position = ValuePosition.Attribute)]`, in the
  scalar text form (section 7.5).
- **Children:** one per stored property, named by the property. The library writes scalars first, but
  readers must not depend on the order.
- `[NotMapped]` and read-only properties are absent.
- **An absent property** means null for a nullable type and the type's default otherwise. The writer
  omits:
  - null;
  - an empty string (it reads back as null);
  - `Guid.Empty`;
  - the default `DateTime`, `DateOnly`, `TimeOnly` and `TimeSpan`;
  - a key or relationship-key property that is 0.

  It writes `0`, `false` and the default `DateTimeOffset` (`0001-01-01T00:00:00.0000000+00:00`). So
  `TimeSpan? x = TimeSpan.Zero` reads back as null. Without a model a reader can't tell "absent" from
  "null", which the manifest fixes (section 10).

Property element shapes:

| Shape | Marked by | Content |
| :--- | :--- | :--- |
| scalar | no `collType`/`refType` | text; CDATA when `[Markup]` |
| map | `collType="dictionary"` | `<Entry key="k">value</Entry>`, value in scalar form, empty text for null |
| scalar list | `dataType="System.String" collType="generic"` (or `"array"`) | one child per non-null item, named by the item type's short name (`String`, `Int32`), text value |
| entity list | `dataType`, `collType`, `refType` | one child per item, named by the item's short name: `<Order Id="1">30ee1031c.ec4cb8f22</Order>` |
| entity | `refType` | the item code as text, key attributes |

`refType`:
- **`reference`**: a row of another table that has a primary key. Its key values are attributes
  (`<Order refType="reference" Id="1">30ee1031c.ec4cb8f22</Order>`).
- **`complex`**: a row that belongs to this one. Its class has no primary key, and it is stored in that
  class's own table (`<Address refType="complex">d87d1f9df.4cbfb76f5</Address>`, a row of
  `Address.xtab`).
- **`children`**: on a list; the items are rows of the child table, deleted with the parent.
- **`parent`**: on a child, back to its parent:
  `<Order refType="parent" hostProp="Lines" Id="1">a79f11f2a.956eace4c</Order>`, where `hostProp` names
  the parent's children property.

Key attributes on reference elements are written with `XmlConvert` (`true`, not `True`). The item code
is the link; the key attributes duplicate the target's key. A reader resolves the code and must treat a
code with no row as a dangling reference.

### 7.3 Changes in place (XML)

In XML, the fast writer edits page bytes instead of rewriting the page:
- **Append:** a new row goes in before `</Rows></TablePage>`.
- **Delete:** the row's bytes are overwritten with spaces.
- **Update:** a row that fits in its old length is overwritten and padded with spaces; a longer one
  shifts the rest of the page.

A row keeps its position through updates. JSON and Binary pages are always rewritten whole.

The fast writer handles classes with only scalar properties ("pure scalar"); everything else goes through
the document writer. The two differ in boolean case (`true` against `True`) and in escaping
(section 4.2).

### 7.4 Page size

`DatabaseOptions.PageSize` (default 524,288 bytes) is compared with the page's **stored** size: after
Brotli for Binary, and after encryption. A Binary page therefore holds several times the rows of an XML
page of the same setting.

### 7.5 Scalar text forms

Every scalar is invariant-culture text (`ValueHelper.FormatScalar`; the fast writer uses
`Utf8Formatter`, which gives the same text except for booleans).

| .NET type | Written | Readers accept |
| :--- | :--- | :--- |
| `bool` | `True`/`False` (document writer); `true`/`false` (fast writer, key attributes) | either case; `1`/`0` |
| integers | `-42` | the same |
| `float`, `double` | shortest round-trip: `0.1`, `1E+30`, `NaN`, `Infinity`, `-Infinity` | the same |
| `decimal` | keeps its scale, never an exponent: `12.50` | the same |
| `string`, `char` | as is (escaping in section 4.2) | |
| enum | the member name (`Gold`); `[Flags]` as `A, B`; an undefined value as its number | name (any case) or number |
| `Guid` | `8f14e45f-ceea-467f-a0e6-a3d1f2c4b001` | the same |
| `DateTime` | `O`: `2026-11-11T09:30:00.0000000Z` for UTC, `+03:00` for local, no suffix for unspecified | round-trip parse |
| `DateTimeOffset` | `O`: `2026-11-11T12:00:00.0000000+03:00` | the same |
| `DateOnly` | `1990-05-17` | |
| `TimeOnly` | `06:45:00.0000000` | |
| `TimeSpan` | `c`: `[-][d.]hh:mm:ss[.fffffff]` (`08:30:00`) | |

A local `DateTime` stores the offset of the machine that wrote it. The text alone doesn't say whether
`"10"` is a number or a code; that is D1's problem (section 10).

### 7.6 Property crypto

- **Hashed** (`[Crypto]`, default SHA-256; also SHA-512, SHA-1, MD5): the lowercase hex digest of the
  UTF-8 value. A value that already is lowercase hex of the digest's length is stored unchanged, so
  updates don't hash twice. A plain value that happens to look like a digest is therefore stored as is.
- **Encrypted** (`AES256`): `enc:aes256:` + base64(nonce 12 | tag 16 | ciphertext), AES-256-GCM, no
  associated data.
  - Key: `PBKDF2-HMAC-SHA256(passphrase, "XOD++::PropertyEncryptionSalt::v2", 100000, 32)`.
  - The passphrase is the database's hashed password (section 3.2) or, **without a database password,
    the built-in constant `XOD++DefaultPropertySecretKey`**. Anyone with XOD can decrypt such values
    (ISS-050).

## 8. The index

The pages are the truth. The `.xidx` maps each indexed property's **normalised key** to item codes. The
library rebuilds it from the pages when it is missing or looks stale, and deletes every index during
crash recovery. Readers may ignore it. A reader that uses it must accept the staleness limits in ISS-043.

### 8.1 Binary form (no database password)

```
"XIDX2:<TableIndex>\n"                 19 bytes, UTF-8
int32 marker                           -3: snapshot + closed log (written today)
                                       -2: snapshot + log (older); >= 0: unframed snapshot (older, see below)
int32 propertyCount
per property:
  string name | int32 entryCount | string minKey | string maxKey | int32 blockBytes
  block (blockBytes):  per entry: string key | int32 codeCount | string itemCode * codeCount
log, to end of file, per record:
  byte op | string property | string key | string itemCode
  op 1 add, 2 remove (property "" and key "": remove the code everywhere), 3 end of flush ("" "" "")
```

- `minKey`/`maxKey` are the ordinal minimum and maximum of the block's keys. The library uses them to
  load a block only when a lookup falls inside it.
- Entries in a block are unordered.
- With marker `-3`, the log must end exactly after an op-3 record. If it doesn't, a flush was cut short
  and the index isn't trusted.
- **Unframed (marker >= 0):** the marker is the property count. Each property is then `string name |
  int32 entryCount | entries`, with no keys or length in the header.

### 8.2 Document form (database password)

With a password the index is a document (sections 3-4) in the database's encoding, encrypted like the
rest:

```xml
<TableIndex type="MyApp.Customer">
  <Property name="Email"><Entry key="nadia@example.com" codes="e2d552584.bb84ea1aa" /></Property>
</TableIndex>
```

`codes` is a comma-separated list. It is rewritten whole on every save.

### 8.3 Normalised keys

The key depends on the property's **type**, which is another reason a model-less reader needs the
manifest:

| Type | Key |
| :--- | :--- |
| `string` | the value |
| `int`, `long` (within ±9×10^17) | `10^18 + value`, 19 digits, then `.00000000`: `1` is `1000000000000000001.00000000` |
| other numbers | the same shift through `decimal`, rounded to 8 places (ISS-053; above about ±7.9×10^28 it throws, ISS-054) |
| `DateTime` | UTC ticks, 20 digits |
| `DateTimeOffset` | UTC ticks, 20 digits |
| `DateOnly` | day number, 10 digits |
| `TimeOnly` | ticks, 20 digits |
| anything else (enum, `bool`, `Guid`, `TimeSpan`) | the scalar text (section 7.5) |

Keys compare ordinally; for numbers that matches numeric order. An entry holds one or more item codes;
the file doesn't say whether the property is unique (the manifest does).

## 9. Durability files

- **Atomic writes.** A file is written to `<file>.tmp`, then renamed over the original. When the library
  opens a database, a leftover `.tmp` is deleted if its target exists or it is empty, and renamed into
  place otherwise.
- **The journal is an undo journal**, despite the `.xwal` name. Before a commit writes any file, the
  library writes `transaction.xwal` (through `.tmp`). It records the bytes of every file the transaction
  changes, as they were before, and the files it created:

  ```xml
  <TransactionJournal id="<guid, 32 hex>" timestampUtc="2026-11-11T09:30:00.0000000Z">
    <Snapshot file="e2d552584.xpag">base64 of the file before the transaction</Snapshot>
    <Created file="7d2fe6d94.xpag" />
  </TransactionJournal>
  ```

  The journal is always XML and never encrypted. Snapshots hold the files' stored bytes, so encrypted
  files stay encrypted inside it. It is deleted after the last file is written.
- **Recovery** (at open, after the `.tmp` step), when a journal exists:
  1. delete each `Created` file;
  2. write each `Snapshot` back (through `.tmp`);
  3. delete **every** `.xidx`;
  4. delete the journal.

  A journal that doesn't parse is deleted. Because it is written whole before any data file, an
  unparseable journal means the commit hadn't started.
- **A reader that finds a journal** must either recover (if it may write) or read the snapshot bytes in
  place of the files they name, and treat `Created` files as absent. Otherwise it would read a commit
  that never finished.
- **The lock** (`LockDatabase`): `<database file>.lock` is held open with no sharing. A reader that
  can't open it must treat the database as in use. Without the option there is no lock and nothing
  coordinates two processes (ISS-042).

## 10. The schema manifest (D1; E1-E3 decided)

`PLAN-045` D1 chose a manifest written by the .NET library. Everything in sections 7.2, 7.5 and 8.3 that
"needs the type" reads it from here.

### 10.1 Where: inside the table file (E1)

```xml
<Table>
  <Pages .../>
  <Indexes .../>
  <Schema version="1" type="MyApp.Customer" element="Customer" writer="<library version>">
    <Column name="Id" type="int32" key="1" autonumber="true" />
    <Column name="Name" type="text" required="true" />
    <Column name="Email" type="text" unique="true" />
    <Column name="Tier" type="enum" indexed="true">
      <Member name="Basic" value="0" /><Member name="Gold" value="1" />
    </Column>
    <Column name="Active" type="bool" at="attribute" />
    <Column name="Rating" type="int32" nullable="true" />
    <Column name="Password" type="text" crypto="sha256" />
    <Column name="Bio" type="text" markup="true" />
    <Column name="Tags" type="map" keyType="text" valueType="text" />
    <Column name="Phones" type="list" itemType="text" />
    <Relation name="Address" kind="complex" table="MyApp.Address" />
    <Relation name="Orders" kind="children" table="MyApp.Order" localKey="Id" remoteKey="CustomerId" list="generic" />
  </Schema>
</Table>
```

**Why the table file, not a `.xsch` beside it:**
- It gets the table file's atomic write, journal and encryption.
- It is converted by `XodMigrator`.
- **2.0 keeps it.** The already-published 2.0.0 and 2.0.1 rewrite the table file in place without
  dropping an element they don't know. This was checked in all three encodings and with a password,
  across 12 table-file rewrites (section 12). A separate file would also survive, but would need its
  own write, journal and encryption paths: a second writer for the same table.

### 10.2 Contents

- **On `<Schema>`:**
  - `version`: of the manifest format;
  - `type`: `Type.FullName`, the file name;
  - `element`: the short name the rows use;
  - `writer`: the library version that wrote it, for diagnosis only.
- **`<Column>`, one per stored scalar, map or list property:**
  - `name`.
  - `type`: one of `bool`, `int8`, `uint8`, `int16`, `uint16`, `int32`, `uint32`, `int64`, `uint64`,
    `float32`, `float64`, `decimal`, `char`, `text`, `uuid`, `timestamp`, `timestamptz`, `date`, `time`,
    `interval`, `enum`, `map`, `list`. Finer than the SQL types in `PLAN-045` section 4, because writes
    must respect the C# range. The SQL type follows from it by that table.
  - `nullable`.
  - `at="attribute"` when stored as an attribute.
  - `key`: position in the primary key, from 1.
  - `autonumber`, `unique`, `indexed`, `required`.
  - `crypto`: `md5`, `sha1`, `sha256`, `sha512` or `aes256`.
  - `markup`.
  - `default`: a `[DefaultValue]`, in scalar text form.
  - `enum` columns list every member (name and numeric value), and `flags="true"` for `[Flags]`.
- **`<Relation>`, one per reference, children, complex or parent property:**
  - `kind`;
  - the target `table`;
  - the key mapping (`localKey`/`remoteKey`; several pairs separated by commas);
  - `list` (`generic`/`array`) for collections;
  - `cascade`.

### 10.3 When it is written (E2)

- The library builds the manifest from the model when it loads a type, compares it with the stored one,
  and writes it **with the first write of that type in the context** if it differs or is missing. The
  table file is being written then anyway, so a read never writes. Read-only folders keep working.
- A database written by 2.0.0/2.0.1 gets its manifest at the next write by a release that has it.
- A model-less reader on a database **without** a manifest reads every column as text and says so, as
  `PLAN-045` section 3 requires. Inferring types is rejected (D1(b)).

### 10.4 When the model changes (E3)

- The manifest describes **the model of the last writer**. Rows written under an older model may hold
  properties it doesn't list. A model-less reader exposes those as text, with a warning, and never
  fails on them.
- A value that doesn't parse as its manifest type (a column whose C# type changed) reads as null, with a
  warning, in SQL. The C# library keeps today's behaviour.
- A release older than the manifest keeps the element but doesn't update it. A database written by 2.0.x
  after a newer release may have rows its manifest doesn't describe. `writer` shows when that happened.

## 11. Defects found writing this spec

Each was reproduced on 2026-11-11 (section 12) and filed in `KNOWN-ISSUES.md`.

| Issue | Severity | What happens |
| :--- | :--- | :--- |
| ISS-051 | High | In XML, a string containing a control character (U+0001-U+001F except tab, LF, CR) saved by the document writer makes its whole page unreadable. `Find` returns null; `Count` and `Query` throw `DatabaseFileException`. The writer allows the character; the reader (`XDocument.Load` with default settings) refuses it. `XmlStorageSerializer.SharedReaderSettings` is defined but unused. |
| ISS-050 | High | `[Crypto(AES256)]` without a database password is encrypted with a constant built into the library. A value was decrypted with nothing but constants from the code. The site's security page says the value "is encrypted with AES-256-GCM" with no caveat. |
| ISS-052 | High | Auto-numbering isn't stored: the next number is the last row's value + 1. Delete the row with the highest Id, reopen, and the next insert reuses that Id. |
| ISS-053 | High | An indexed `decimal` (or `double`) key is rounded to 8 places, and lookups trust the key: `Amount == 0.000000001m` returned that row and the `0.000000002` row. This breaks `PLAN-045` principle 3: answers must not depend on an index. |
| ISS-054 | Medium | An indexed `double`/`float` beyond about ±7.9×10^28 can't be inserted (`OverflowException` in key normalisation). |
| ISS-055 | Low | Page metadata in the table file is unreliable or unused (`count` stale for open pages, `<Index begins/ends>` never read and `ends` is the last value, not the largest). The fast and document writers disagree on booleans, escaping and the new-page header. |

## 12. How this was checked

All of these were run on 2026-11-11 against `main` (`7cbf017`, library 2.0.1).

- **An independent reader.** A sample program wrote one database in each encoding plus a
  password-protected one, covering every scalar type, nulls and defaults, an attribute property, a hash,
  CDATA, a map, a list, a complex object, children, a reference, an update and a delete. A separate
  reader (Node, about 250 lines, `node:crypto` and `node:zlib` only) was written from sections 3-8, not
  by calling the library. It:
  - read all four databases to the same content (identical after masking the one generated Guid);
  - resolved every reference;
  - rebuilt every index from the pages and found it equal to the `.xidx`: seven indexes, both index
    forms;
  - failed authentication on a wrong password.
- **The manifest's home.** `<Schema>` was added to a table file in XML, JSON, Binary and XML with a
  password. Then 2.0.1 inserted 300 rows across up to 51 pages, updated and deleted, and the database
  was reopened. The element was intact and the data correct in all four. `XodMigrator` copies the whole
  document, so it keeps it too.
- **Mixed encodings.** An XML database reopened with `StorageFormat.Json` and written to held XML and JSON
  files side by side, and read back correctly.
- **The defects in section 11:** each reproduced by a small program.

Not checked: `[Inherited]` and `[GenericTypeProperty]` (not described here yet), arrays of entities,
self-references, and files written by 2.0 on Linux (the code has no platform branch).

## 13. Decisions for the owner

Decided 2026-11-11: the owner accepted all three recommendations.

| | Decision | Decided |
| :--- | :--- | :--- |
| **E1** | Where the manifest lives | inside the table file, as `<Schema>` (10.1) |
| **E2** | When it is written | with the first write of a type in a context; reads never write (10.3) |
| **E3** | Model changes | the manifest describes the last writer's model; unknown properties read as text (10.4) |

## 14. Next

1. ~~The owner's decisions on E1-E3~~ - all three recommendations accepted, 2026-11-11.
2. ~~The conformance set~~ - built in [`PLAN-047`](PLAN-047-conformance-set.md) (2026-11-11): the sample
   generator and the independent reader are in `conformance/` and the CI gate, with the cases from
   section 11.
3. Fixing ISS-050 to ISS-055 is separate work, in the order the owner picks. ISS-051 and ISS-050 come
   first: one loses a page of data on read, the other is a wrong public claim.

## Verdict

Spec accepted. Sections 2-9 describe today's files and were checked against them. Section 10 is
decided (E1-E3) and not built. Six defects were found and are filed, none fixed.
