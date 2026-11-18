# VERIFY-2026-11-18: the open issues, checked against the code

Every issue open on 2026-11-18 was checked against the code at `e978578`: a probe test written for each (a test that
asserts the behaviour the issue expects, so red means the defect is there), or, where a probe can't reach it, the code
read. Five reviewers worked apart, each on its own copy of the tree. The issues' own rows in
[`KNOWN-ISSUES.md`](KNOWN-ISSUES.md) carry what changed; this is the record of the check.

## Verdicts

| Issue | Verdict | What the probe showed, or why not |
| :--- | :--- | :--- |
| `ISS-130` | Reproduces | Delete by key leaves a cascade `[Property(Cascade = Delete)]` reference's row and fires no child trigger; by scan, both right. Two cascade walkers, with two trigger rules (and a third copy in an update's removal of dropped references) |
| `ISS-135` | Reproduces | A keyless child inserted on its own takes its parent's first entry: the parent listed `[B]` for `[A, B]` |
| `ISS-136` | Reproduces | 60 equal keyless rows over 5 pages: `Update` and `Delete` by example touched 12 and returned true. Same as `ISS-168`'s third part |
| `ISS-171` | Partly | A 1.x digest read and inserted elsewhere is hashed again; a 16-digit plaintext under MD5 is kept unhashed on update. The keyless child case is blocked by `ISS-179` and `ISS-180` |
| `ISS-140` | Reproduces | `' '`, U+00A0, `'\t'` read back `'\0'` (a `char?` null) in every format; an index rebuilt from rows keys 0 |
| `ISS-142` | Reproduces | `Int128`, `Half`, `BigInteger`, a custom struct read back as defaults; an `nint` made `Find` throw |
| `ISS-143` | Reproduces, wider | XML without a password: `Select(skip, take)` gave `[1, 77, 3]` for `[1, 2, 3, 4]`, `Last` a forged row. A property or class named `Row` truncates every row, and an update writes the loss back |
| `ISS-144` | Reproduces | A cut page: `Select()` throws, but `Find` returns null, and in XML `Select(skip, take)` and `Count` read part. Read path catches turn failures into absent rows |
| `ISS-134` | Partly | The full-page half is gone (batch pages are written in place or atomically). A new page is still created in place after the table file names it; a 0-byte page then breaks `Select` and `Insert`, and no recovery step handles it |
| `ISS-152` | Reproduces | 50 password inserts added 55 derived keys to the static cache, still there after dispose and a GC |
| `ISS-104` | Not testable here | Windows always takes the locks; by reading, the Unix in-process half holds |
| `ISS-105` | Reproduces, wider | A `Select` of the same table gives back the pooled buffer an index read is parsing; the next rent hands it out |
| `ISS-153` | **Does not reproduce** | Closed; a stray `other.xod.tmp` filed as `ISS-182` |
| `ISS-154` | **Does not reproduce** | Closed |
| `ISS-173` | Reproduces | The reader kept the old count and rows, by key too |
| `ISS-092` | Reproduces | The trigger and the caller's item see Id 0 where the row got 1; a design question (one object for the row and its triggers) |
| `ISS-094` | Reproduces | The documented limitation (an attribute can't say null) |
| `ISS-095` | Reproduces | A child deleted on its own stays listed; the convention key is used by one side only |
| `ISS-118` | Reproduces | `[DefaultValue]` is never read; XOD 1.x didn't read it either; PLAN-046 claims it does |
| `ISS-122` | Reproduces | `prop.GetCustomAttributes(true)` doesn't inherit for a property; `Attribute.GetCustomAttributes` does |
| `ISS-123` | Reproduces | A convention `Guid? Id` isn't an autonumber: two rows with a null key |
| `ISS-127` | Reproduces | A null list, map or reference reads back as the initializer's |
| `ISS-128` | Reproduces | An `IDictionary<K, V>` property is stored as a list and can't be read |
| `ISS-145` | Reproduces | An indexed `Find` returned row 21, the scan row 1 |
| `ISS-114` | Reproduces | An open rebuilds a correct index after an update that wrote only a page |
| `ISS-146` | Partly | The empty element is fixed; `<Flag>1</Flag>` reads `False` by key on a flat type |
| `ISS-156` | Partly | The lock message is fixed; five items remain |
| `ISS-161` | Reproduces | A `Guid.NewGuid()` initializer rewrites the manifest per process, and a row missing the value reads a different Guid each time |
| `ISS-162` | Reproduces | An enum stored as `gold` reads `Gold` through the class and `gold` through the manifest |
| `ISS-168` | Reproduces | A reference in an example is ignored; a list of references matches only empty lists |
| `ISS-111` | Reproduces | The reverse map isn't forgotten with its type; with a password, an index saved while marked undeleted is deleted by the next flush |
| `ISS-121` | Partly | Items 1, 6 and 8 changed or gone; the rest remain |
| `ISS-106` | Reproduces | About 0.7 of the cycles on busy loops; the proposed fix conflicts with Microsoft's documentation |
| `ISS-040`, `ISS-044`, `ISS-046`, `ISS-116` | Not re-measured | Performance and memory: their measurements need the stress harness on a quiet machine |
| `ISS-158` | Resolved in part | `TASK-086`, the same day |

## Found by the check, filed

- `ISS-179`: updating a parent by key throws when its list holds keyless children.
- `ISS-180`: updating a parent by example deletes its keyless children (data loss).
- `ISS-181`: an update can't point a `[ForeignKey]` reference at another row.
- `ISS-182`: a stray `<name>.xod.tmp` beside the database becomes a second database file.

## Severity changed

- `ISS-143` Medium to **High**: a property named `Row` loses data on update, with no `[Markup]` needed.
- `ISS-105` Low to **Medium**: a plain `Select` reaches it today.
