# PLAN-048: Fixes for the format defects (ISS-051 to ISS-057)

- **Task ID**: `TASK-058`
- **Status**: Fixed in the tree (2026-11-11), not released: no version change, nothing pushed. ISS-050
  and ISS-058 (property encryption) wait on the owner's choice (section 8).
- **Related**: [`PLAN-046`](PLAN-046-file-format-spec.md) (the format; sections 4.2, 6, 7.1, 7.5 and 8
  updated), [`PLAN-047`](PLAN-047-conformance-set.md) (the conformance set; markers removed, fresh-write
  check added), [`KNOWN-ISSUES.md`](../KNOWN-ISSUES.md)

## 1. What was wrong, and why

| Issue | Cause | The change makes it impossible by |
| :--- | :--- | :--- |
| ISS-053, ISS-054, ISS-056, ISS-057 | Index keys were made by pushing every number through `decimal` rounded to 8 places, and other types through their text, so a key was neither exact nor in C#'s order. The planner then dropped the conversions C# puts into a comparison (`(int)x.Tier == 2`) and compared keys of different types. | **Keys v2** (`IndexKeys`): one function makes every key, exact and in C#'s order for every ordered type. The planner (`TryResolveComparison`, one resolver for equality and ranges) moves a comparison onto the property's type only through conversions that change no value, and converts the constant back exactly or finds it has no equal. |
| ISS-051 | Two XML writers and several readers each had their own idea of which characters XML can carry: control characters were written as references the reader refused, CR was normalised to LF, a whitespace-only value was dropped. | **One policy** (`XmlText`): every XML read and write goes through it. Writers reference what XML can't carry raw; readers accept any reference and drop only whitespace beside elements. |
| ISS-052 | The next autonumber was the last row's value + 1, held only in memory. Deleting the highest row deleted the only record of it. | **A recorded mark**: a delete first raises `<Autonumber property last>` in the table document; a counter starts above both the mark and the highest row (from the index when there is one). The two delete paths now share one recorder (`RecordRowRemoved`). |
| ISS-061 (found while fixing ISS-051) | The document writer registered a new page in the table before building the row. When building threw (a refused value, a child that fails validation), the page stayed registered in memory and nothing saved the table, so on reopen the table file was missing and every later row of it was lost. | **The row first**: the writer chooses the page without changing the table (`ChooseAppendPage`), builds the row, and only then registers a new page (`AddPage`), as the fast row writer already did. |
| ISS-055 | The fast row writer had its own spellings (booleans, page header), and the table file carried per-page key ranges nothing read. | The fast writer writes what the document writer writes; the ranges are no longer written. `count` stays advisory, as `PLAN-046` section 6 says. |

## 2. Index keys v2 and the planner

- **Keys** (`src/Xod/Services/IndexKeys.cs`): integers, enums, `char` and `TimeSpan` as `value + 2^63`
  in 20 digits; `decimal` as sign, exponent and significant digits; `float`/`double` as order-preserving
  IEEE bits (NaN has no key, `-0.0` is `0.0`); `DateTime` by ticks with `Kind` ignored, as C# compares.
  `PLAN-046` section 8.3 has the exact forms.
- **Planner** (`IndexService.QueryPlan.cs`): ranges only for ordered kinds; a constant that can't be held
  exactly by the property's type matches nothing under `==` and leaves a range to the rows; NaN matches
  nothing; a user-defined operator or a conversion that would round is left to the rows.
- **Versioning**: the binary index's marker is now `-4` (`-5` since TASK-067, which adds a generation:
  PLAN-055) and the password form's root `TableIndex2`. The
  library loads only those; any older index is rebuilt from the pages. 2.0 reads `-4` as a negative
  property count and `TableIndex2` as not an index, and rebuilds too.
- **Uniqueness**: unique and primary-key checks use the same keys, so `0.000000001m` and `0.000000002m`
  are different keys and `0.0000000010m` is the same as `0.000000001m`.

## 3. Tests, each seen red first

| Test | Red before | Proves |
| :--- | :--- | :--- |
| `XodIndexSemanticsTests` (36 predicates over edge values, XML/JSON/Binary/password, before and after reopening) | all four runs threw `OverflowException`; with the overflowing values replaced, wrong answers for enum, char, decimal, float, long, TimeSpan and DateTime predicates | an index never changes an answer |
| `Unique_decimals_that_differ_past_the_eighth_place_are_different_keys` | `ReservedUniqueKeyException` | exact unique keys |
| `An_index_file_with_v1_keys_is_rebuilt_from_the_pages` (-2, -3) and `An_encrypted_index_document_with_v1_keys_is_rebuilt_from_the_pages` | red when the loader was made to accept the old marker or root | old indexes are never trusted |
| `XodTextRoundTripTests` (13 strings, both writers, element and attribute, all encodings, insert and update) | XML threw `XmlException` on read; then CR, whitespace-only values lost; the in-place rewrite threw | text round-trips exactly |
| `XodAutonumberTests` (17 cases: highest deleted, table emptied, explicit Ids out of order, a delete in a later context, an autonumber with no index, both delete paths, all encodings) | 12 failed with the mark switched off | a deleted value isn't handed out again |
| `A_string_with_a_lone_surrogate_is_refused_not_changed` (insert and update, both writers, all encodings) | stored U+FFFD in place of the surrogate; once refused, the JSON, Binary and password runs lost the table on reopen (ISS-061) | a value that can't be stored unchanged is refused |
| `XodRefusedRowTests` (a parent whose child fails validation, the table's first row, all encodings) | 4/4 with the page registered before the row | a row that fails to build leaves its table as it was |
| `Values_the_writers_leave_out_are_keyed_as_they_read_back` (Xml, Json) | the index kept rows under the value written (`""`, `Guid.Empty`, a default date) while the rebuilt index keyed what reads back | the index agrees with the rows, however it was built |
| `An_index_built_in_another_zone_is_rebuilt_from_the_pages`, and the encrypted form | 2/2 with the zone check off | an index of local times is never used in another zone |
| `XodWriterAgreementTests` | red with either the old boolean or the old page header | the writers agree; no page key ranges |
| conformance `where-001`, `where-011`, `where-033`; `KnownDefectTests` for ISS-051, 052, 054 | known markers that reported "behaves correctly now" | |
| `read.mjs --check-fresh` | red with a wrong key rule in the reader | what the tree writes, an independent reader reads |

## 4. The gate, 2026-11-11 (after the review's findings were fixed)

- `Xod.Tests`: 107/107 on .NET 8 and on .NET 9, once the API snapshot took the new public
  `InvalidTextException` (an addition, regenerated with `XOD_UPDATE_API=1`; nothing removed).
- Conformance: 10/10 on each framework.
- `read.mjs --check`: the five committed fixtures, unchanged. `--check-fresh`: four fresh databases, 141
  keys, all v2, all equal to their pages.
- `XodBench.Tests`: 26/26.
- Not measured: insert and query throughput. The insert path's key for an `int` is the same work as
  before; reading an XML document now makes one extra pass to drop padding; a write checks each string
  for surrogates (one vectorised scan).

## 5. Independent review

A fresh agent reviewed the change with the problem statements, not the design, and answered the four
questions. Each finding was checked before acting on it:

1. **The index keyed values the writers leave out as written, not as they read back.** An empty string,
   `Guid.Empty`, a default date or a zero with a `DefaultValue` isn't stored, so a read gives the
   property's value without a row; a rebuilt index keyed that, the insert path keyed the written value,
   and the two disagreed. Confirmed. Fixed: one owner, `StoredValue`, says what is left out and what reads
   back, and both index paths key the read-back value.
2. **A `[Markup]` value went into CDATA, which can't carry a reference**, so a control character in markup
   still broke the page. Confirmed. Fixed: `XmlText.TextNode` falls back from CDATA when a value needs a
   reference.
3. **A lone surrogate was stored as U+FFFD.** Confirmed. Fixed: `StoredValue.Text` refuses it with
   `InvalidTextException`, on every write path. The test for it found ISS-061.
4. **A local `DateTime`'s key depends on the machine's zone**, and a local time in a daylight-saving gap
   moves when read back. Confirmed by reading; not reproduced, since this machine's zone can't be changed
   in a test. Fixed: the key is made from the value as it reads back, and the index records its zone and
   is rebuilt in another.
5. **Nits:** an unused `IndexKeys.Version`; `Encode` converting values whose key is their text; the fast
   reader's `Unescape` throwing on a reference to a surrogate (the fast reader caught it and fell back to
   the document reader, so answers didn't change); two comments claiming more than the code. All fixed.
## 6. Verdicts

| Issue | Verdict |
| :--- | :--- |
| ISS-053, ISS-054, ISS-056 | **Clean.** The cause (lossy, misordered keys and a planner that compared keys of different types) is gone. |
| ISS-057 | **Clean, with named debt**: the zone is checked when an index loads, not when the process's zone changes while it runs; the daylight-saving case has no test (it needs a zone with a gap). |
| ISS-051 | **Clean.** One character policy; lone surrogates refused. |
| ISS-052 | **Clean for deletes, with named debt**: ISS-059 (an update that changes an autonumber), concurrent contexts without `LockDatabase`, and a crash between a delete's page write and its mark write. |
| ISS-055 | **Clean.** |
| ISS-061 | **Clean for the table; named debt**: rows a failed row's children already inserted stay (ISS-062). |
| ISS-050, ISS-058, ISS-063 | Fixed separately, in [`PLAN-049`](PLAN-049-property-encryption.md). |

## 7. Remaining debt

- **An update that changes an autonumber value** (`Update(item, newItem)` can, even for a key) goes past
  the counter: lowering the highest value loses its record, and a value raised above a counter this
  context already started isn't seen by it, so a later insert may be handed the same value (for a key
  the insert then fails its uniqueness check; for a non-key autonumber it is a silent duplicate).
  Filed as ISS-059.
- **Values the writers leave out** don't read back as written (ISS-060); the index now agrees with the
  rows, not with what was written.
- **A row that fails part-way** keeps the children it already inserted (ISS-062): no rollback outside a
  transaction.
- **Local `DateTime` (ISS-057):** the zone is checked when an index loads, not when a running process's
  zone changes; the daylight-saving gap case has no test.
- **Delete write order (ISS-052):** a batch flushes its files in no fixed order, so a crash between a
  delete's page write and its mark write can lose the mark.
- **Two contexts on one database** without `DatabaseOptions.LockDatabase` each hold their own counter.

## 8. Open: property encryption (ISS-050, ISS-058, ISS-063)

ISS-050 and ISS-058 have one cause: an `AES256` property's key is derived from the database password,
or from a constant when there is none. With no password anyone can decrypt (ISS-050); when a password is
added or changed, nothing re-encrypts, so every value becomes unreadable (ISS-058). ISS-063 is next to
them: an encrypted or hashed property that is indexed is keyed by its plaintext, which lands in the
unencrypted index when there is no password. The choice is the owner's:

- **(a) Require a key.** An `AES256` write needs a database password or a key the application supplies;
  existing values stay readable. Closes ISS-050; ISS-058 needs (c) as well.
- **(b) A data key per database**, random, stored wrapped by the password (or the application's key).
  Changing the password rewraps one key and re-encrypts nothing, which closes ISS-058; with no password
  there is still nothing to wrap it with, so it goes with (a).
- **(c) Re-encrypt on `Secure` and `ChangePassword`**: every `AES256` value rewritten under the new key.
  Closes ISS-058 without (b), at the cost of rewriting every row that holds one.
- **For ISS-063**, either refuse `Indexed`/`UniqueKey` on a `[Crypto]` property, or key the stored form
  (equality only, and only while the key doesn't change).

Recommended: (a) with (b), and refusing an index on a `[Crypto]` property. **Decided 2026-11-11: the
owner accepted the recommendation; built in [`PLAN-049`](PLAN-049-property-encryption.md).** The site's security page also
needs correcting (it says AES256 values are encrypted with no caveat); deploying that needs the owner's
go.
