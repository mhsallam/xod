# PLAN-083: a context owns its file keys

- **Task ID**: `TASK-098`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-152`
- **Related**: `ISS-205`, `ISS-206` (filed)

## 1. The cause

With a password, every file write drew a random salt and derived its key from the password (100,000 rounds of
PBKDF2), then kept the key in a static cache keyed by the salt. A new salt each time missed the cache every time:
an insert took about 20 ms against under 1 ms without a password, and the cache grew by one key per write. It was never
emptied, so the keys stayed in the process after the database was closed.

So: **a key was derived per write, and cached by no one who could let it go. The change makes the context the one
owner of its file keys: one salt and key for its writes, one key per salt it reads, collected with the context.**

## 2. The change

- `Helpers/FileKeys`, one per context (`IOService`), built from its password and rebuilt when the password changes:
  - `ForWrite`: the salt and key of a write. Derived at the context's first write, then used with a new nonce per
    write. A new salt and key are drawn every 2^30 writes: AES-GCM with random 96-bit nonces allows at most 2^32
    encryptions under one key (NIST SP 800-38D, section 8.3).
  - `ForSalt`: the key of a file's salt, derived once per salt. The cache holds a `Lazy<byte[]>` per salt, so reads
    racing on a new salt derive it once (`ConcurrentDictionary.GetOrAdd` may run its factory on each racing thread;
    the `Lazy` it keeps runs once).
- `CryptoHelper.DeriveKey` no longer caches.
- Every encrypt and decrypt goes through a `FileKeys`:
  - the page and table writes and reads;
  - the index load;
  - a password change, which builds one for the old password and one for the new;
  - the migrator, with one per conversion.
- The file format is unchanged: each file still carries its salt, and files written by earlier versions read as
  before.

The obsolete `CryptoHelper.EncryptProperty` / `DecryptProperty` with a passphrase of the caller's now derive their key
at each call. The built-in key is derived once.

## 3. Tests

`XodFileKeyTests` (a collection run alone, since the count of derivations is process-wide):

- 40 inserts in one context derive between 1 and 4 keys: **red before** (40);
- a new context derives the key of a file it reads: a static cache would hand it over;
- a write key is replaced after its bound, and every file written under each key still reads;
- the migrator derives one write key per conversion.

Breaks, each caught: the write key derived at every write; a key cache shared by every context; a write key never
replaced; a conversion keyed per file. The racing derivations are left to `Lazy`'s documented default
(`ExecutionAndPublication`: the factory runs once); the review's probe saw 8 threads derive 8 keys without it.

An independent review with probes: memory after 10 contexts × 100 inserts went from +1014 cached keys to none kept; a
password change leaves no file under the old password; its findings (the GCM bound, the racing derivations, the
migrator) fixed here, the rest filed.

## 4. Left open

- `ISS-205`: `IOService` keeps the password string beside its `FileKeys`; some decisions read one, some the other.
- `ISS-206`: a database written by 2.0.x has a salt per file, and each new context derives the key of every salt it
  reads (about 13 ms per file) until the file is written again.
