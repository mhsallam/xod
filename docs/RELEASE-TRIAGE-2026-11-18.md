# Release triage, 2026-11-18

What blocks the next stable release, and what waits. The test: can an ordinary application lose or corrupt data,
or get a wrong answer with no error? Those block. Rare edge cases, performance, crash windows already mitigated and
small API defects wait; they stay open in [KNOWN-ISSUES](KNOWN-ISSUES.md).

## Blockers, in order

| # | Issue | What a user meets |
| :--- | :--- | :--- |
| 1 | `ISS-143`, `ISS-144` | A text value holding `</Row>`, or a property or class named `Row`, makes reads drop rows or forge one; a row that fails to read reads as absent |
| 2 | `ISS-188` | A key with a part at its default names different rows with and without indexing, and isn't kept unique |
| 3 | `ISS-189` | An edit to a keyed child in its parent's list, saved by the parent's update, is silently not written |
| 4 | `ISS-181` | An update that points a reference at another row throws |
| 5 | `ISS-135` | A keyless child inserted on its own takes another child's place in its parent's list |
| 6 | `ISS-184` | A collection class that isn't generic itself (`class PointList : List<Point>`) loses its items |
| 7 | `ISS-152` | With a password, memory grows at every write and is never freed |
| 8 | `ISS-134` | A crash between a page's two write steps leaves a 0-byte page that makes the database unreadable |

## Decisions

- **`ISS-188`**: a key part at its default is no value. A lookup, update or delete by a key with a part at its
  default is refused (`MissingPrimaryKeyValueException`), the same with and without indexing; this matches the
  autonumber convention, where an `Id` of `0` means "not set yet".
- **`ISS-191`**: an update keeps writing every keyless child again, and the docs say so: a child without a key has
  no identity to match. The refusal of a child's `Insert` by a trigger and a keyless child with a `[UniqueKey]` wait.

## Waits

- Performance: `ISS-040`, `ISS-044`, `ISS-046`, `ISS-116`.
- Very large databases only: `ISS-105`.
- Crash and lock edge cases, mitigated or rare: `ISS-043`, `ISS-052`, `ISS-062`, `ISS-091`, `ISS-098`, `ISS-102`,
  `ISS-104`, `ISS-115`, `ISS-173`, `ISS-182`.
- XOD 1.x digests: `ISS-171`.
- Small API defects and limitations: `ISS-092`, `ISS-094`, `ISS-095`, `ISS-106`, `ISS-111`, `ISS-114`, `ISS-118`,
  `ISS-121`, `ISS-122`, `ISS-123`, `ISS-127`, `ISS-128`, `ISS-145`, `ISS-146`, `ISS-156`, `ISS-161`, `ISS-162`,
  `ISS-168`, `ISS-183`, `ISS-190`, `ISS-193`, `ISS-194`, and `ISS-191` beyond its documentation.
- Public text (`ISS-157`): checked when the release is prepared.
