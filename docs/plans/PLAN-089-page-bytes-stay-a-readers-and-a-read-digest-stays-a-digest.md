# PLAN-089: a page's bytes stay a reader's; a 1.x digest read from a database said to hold them stays a digest

- **Task ID**: `TASK-105`
- **Status**: Done (2026-11-20)
- **Issues**: `ISS-105` (resolved), `ISS-171` (resolved, with named debt); `ISS-212` filed
- **Related**: `ISS-143` and `TASK-093` (where a dropped page stopped giving its array back), `ISS-129`, `ISS-151`,
  `ISS-064`, `ISS-007`, `ISS-191`

## 1. ISS-105: the cause

A reader takes a page from the cache under the cache's lock, lets the lock go, then finds and parses its row in the
page's bytes. Anything that gave that array back to the pool meanwhile let the next page loaded be read into it. The
reader then parsed another page's bytes, or none.

`TASK-093` (`ISS-143`, released in 2.1.0) already stopped a page that leaves the cache from giving its array back:

- an eviction;
- a document read of the page (the `Select` beside an index read that `VERIFY-2026-11-18` reproduced);
- a delete.

The record of `ISS-105` stayed open, and one give-back was left. A writer moving a cached page to a larger array gave
the old one back. That was safe only because no reader runs beside a write (the engine's write lock).

So: **whether a cached page's array could go back to the pool was decided at each site that let a page go, and every
site had to remember a reader might hold it. Now a page entering the cache stops being pooled, at the one place a page
can enter it, and no site can give its array back.**

## 2. ISS-105: the change

- **One way in** (`RawPageCache`). The cache is its own type. `Put` is the only way a page enters it: a load, the
  preload of a batch of codes, a new page. `Put` marks the buffer as not pooled. The GC takes the array once the last
  reader drops it.
- **No give-back on growth.** The two places a writer moves a page to a larger array (replacing a row, appending one)
  leave the old array to any reader holding it. This was hardening: the write lock already kept readers out.
- **Pooling stays where nobody else can hold the buffer**: a page read from disk for one caller and never cached
  (`ReadRawPageBufferFromDisk`, `RawPageBuffer.ReleasePooledBuffer`).
- **The test** (`XodPageBufferTests`) goes through the public calls, as the issue was verified. A reader is held at
  the step where it holds the row's bytes outside the lock (`IOService.RowBytesHeldStep`). Beside it:
  - a `Select` of the same table turns the page into a document;
  - a lookup by key in a table of the same shape loads its page into whatever array the pool hands out.

  The held reader must still return its own row.
- **The breaks:**

  | Break | Result |
  |---|---|
  | The document read gives the array back, and the cache leaves pages pooled (the state before `TASK-093`) | red, with `XodPageBufferLifetimeTests` |
  | The give-back put back alone | green: the cache alone holds |

  No test reaches the growth paths with a reader beside them, since the write lock keeps readers out.

## 3. ISS-171: the cause

A read gives a one-way `[Crypto]` value (MD5, SHA1, SHA256, SHA512) back as the text the row stores. A write hashes a
value unless it is a digest already. A 2.x digest is told by its hex form (`ISS-007`). A XOD 1.x digest is the decimal
digits of each hash byte, which a typed value can be too. `ISS-151` kept one only when the row an update replaces
stores the same text. Every write without that row hashed it again, and it no longer verified:

- a row read from a 1.x database and inserted into another;
- a digest read and given to another row;
- a keyless child, written again by each update of its parent (`ISS-191`).

So: **a read handed back a digest as a plain string, and each write guessed whether it was one.**

The stored text can't settle it. A value stored in plaintext under `[Crypto]` reads the same as a 1.x digest when it
is digits of that form, such as a 16-digit card number under MD5. Two cases store plaintext there:

- an attribute written by 2.0.1 or earlier (`ISS-064`);
- a property given `[Crypto]` later.

No database records which version wrote it. The first version of this change noted every value of a 1.x form a read
returned, and the review found that copied such a plaintext unhashed into another database, where 2.1.0 hashed it.

## 4. ISS-171: the change

- **The database's owner says** (`DatabaseOptions.Legacy1xDigests`, off by default). Only the owner knows a database
  came from XOD 1.x, so the option says so.
- **The note** (`Helpers/ReadDigests`, `XmlEngine.NoteReadDigest`). With the option on, a read notes each string it
  hands back for a one-way property that has a 1.x digest's form, with its method. The note goes in a
  `ConditionalWeakTable` keyed by the string object: an entry lives as long as its string. It is process-wide, so a
  digest read through one context and written through another is known. Two reads note:
  - `XmlEngine.ReadValue`, which every stored scalar and attribute of a class is read through;
  - SQL's columns read by a recorded schema.

  Only a value of a 1.x form is noted. That is the one thing a write keeps, so SHA256, SHA512 and 2.x digests are never
  noted.
- **The write** (`XmlEngine.StoredDigest`, the one place a one-way value is hashed). A value of a 1.x digest's form for
  the property's method is kept in two cases:
  - a read gave that very string back under the same method;
  - as before (`ISS-151`), the row an update replaces stores it.

  Anything else is hashed.
- **Told by identity, not by text.** A string the caller built with the same digits is another object, and is hashed.
- **The option is the source database's.** A value read from a database with it on is kept when written through any
  context, into any database: the mark is the string's, and the table is process-wide. With no context of the process
  having it on (the default), nothing is noted, and as in 2.1.0 only an update of a row keeps its 1.x digest.
- **The tests** (`XodLegacy1xDigestCopyTests`). With the option on:
  - the three writes above keep the digest, and it verifies;
  - so does a row read by SQL through its recorded schema;
  - a value typed with a digest's digits is hashed;
  - an SHA1 digest given to an MD5 property is hashed, though its digits fit MD5's form.

  Off: a plaintext of digits read and inserted elsewhere is hashed.
- **Red first:** the three writes.
- **The breaks, all caught:**

  | Break | Tests red |
  |---|---|
  | No note at `ReadValue` | 4 |
  | No note on the SQL path | 1 |
  | The method not compared | 1 |
  | The text compared rather than the object | 1 |
  | The option ignored | 1 |

## 5. ISS-171: the debt left

- **With the option on, a plaintext of digits is kept as a digest**, on any write of the string read. The option's
  doc says to leave it off for a database that may store `[Crypto]` values in plaintext.
- **A copy that makes a new string** (serialized and back, a mapper, `new string(...)`) is another object. It is known
  only by the row an update replaces; an insert of it elsewhere hashes it.
- **SQL over a table that records no schema** reads its columns as text, with no method, and notes nothing.
- **The mark lives in process-wide state, keyed by the string object**, not in a type. Two equal strings can be
  written differently, and nothing in the API shows it. Noting rests on the reads calling `XmlEngine.NoteReadDigest`
  (`ReadValue`, SQL's `FromManifest`): a read path added without it notes nothing, and no test would say so. A type
  saying "this is a digest already" would make that rule enforce itself, at the cost of the public API.
- **A model that moves a property from SHA1 to MD5** reads a stored SHA1 digest under MD5. If its digits fit MD5's
  form it is kept (by an update, and with the option on by any write), and verifies under neither.

## 6. ISS-212, filed

A page in the cache never gives its array back now, so the page pool refills only from buffers that never entered it.
Each load still allocates at least 528 KB, a floor that was for reuse. The fix (size a page's array to the page, and
drop or narrow the pool) is filed, not done here.

## 7. Verification

- The tests above, red first; the breaks listed.
- Two independent reviews. The first found:
  - the plaintext copied unhashed (now behind the option);
  - SQL's recorded schema not noting;
  - notes for values no write keeps;
  - the cache's way in kept by convention (now `RawPageCache`);
  - test claims that said more than the runs showed.

  The second found the result clean with named debt, and corrected the records:
  - the option belongs to the database read from, not the one written to;
  - a copy as new text is hashed;
  - the mark is kept by convention at the reads;
  - the test's red needs both defences gone.
- The public API snapshot updated for the new option.
- The gate (`node tools/gate.mjs`) green; the whole suite on Linux (the .NET 9 SDK container).
