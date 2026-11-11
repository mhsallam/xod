# PLAN-049: Property encryption with a key of the database's own (ISS-050, ISS-058, ISS-063, ISS-064)

- **Task ID**: `TASK-059`
- **Status**: Fixed in the tree (2026-11-11), not released: no version change, nothing pushed. The owner
  chose the design on 2026-11-11 ([`PLAN-048`](PLAN-048-format-defect-fixes.md) section 8: (a) with (b),
  and refusing indexes on `[Crypto]` properties).
- **Related**: [`PLAN-046`](PLAN-046-file-format-spec.md) sections 5 and 7.6 (the format, updated),
  [`PLAN-047`](PLAN-047-conformance-set.md) (the independent reader now decrypts both forms),
  [`KNOWN-ISSUES.md`](../KNOWN-ISSUES.md)

## 1. What was wrong, and why

| Issue | Cause | The change makes it impossible by |
| :--- | :--- | :--- |
| ISS-050 | An `AES256` value's key was derived from the database password, or from a constant built into the library when there was none. | Values are encrypted with a random **data key** of the database's own, stored protected by the application's key (`DatabaseOptions.PropertyEncryptionKey`) or the password. With neither, the write is refused: there is no constant to fall back on. |
| ISS-058 | The key was the current password's: changing the password changed the key, and nothing re-encrypted. | The data key doesn't change; a password change protects it again. For values 2.0 wrote, the outgoing password's key is kept, protected by the data key. |
| ISS-063 | The index keyed a `[Crypto]` property's plaintext; a rebuilt index keyed the stored form. | Such a property can't be indexed, unique or a key (`IndexDataTypeException` when the type is loaded). |
| ISS-064 | The writer applied `[Crypto]` only on the element path; an attribute, a list or a dictionary was stored as plaintext. | One place applies `[Crypto]` to a string before it is placed, element or attribute; on any other type `[Crypto]` is refused (`CryptoDataTypeException`). |

## 2. The design

- **One owner**, `PropertyKeyService`: makes, protects, opens and keeps the keys; encrypts and decrypts
  values. The writer and reader call it; `CryptoHelper.EncryptProperty`/`DecryptProperty` (2.0's public
  helpers) are no longer used by the library and are marked obsolete.
- **The record is the database file** (`PropertyKey`, `LegacyPropertyKey`; `PLAN-046` sections 5 and 7.6).
  The opened data key is cached; anything that decides from the file reads it afresh
  (`IOService.Reopen`), since another context may have made or changed the key.
- **A new key is written at once** (`IOService.SaveNow`), outside any batch or transaction: values whose
  key never reached the file could never be read, while a stored key no value uses costs nothing.
- **Password changes** (`Secure`, `ChangePassword`, `Loose`): the security service's checks run first
  (`CheckCurrentPassword`, `CheckCanSecure`: one copy of the rules). Then the key gets the protection it
  needs afterwards, **keeping the current ones** (`PasswordChanging`); then the files change password;
  then the protections by a password that is no longer the database's are removed (`PasswordChanged`).
  A change that fails before any file switches leaves the key openable with the password that still
  opens the files. (One that fails after some files switched leaves files that neither password opens:
  `ISS-065`, as in 2.0, not fixed here.)
  Refusals change nothing.
  - Whether values exist (2.0's or new) is read from the pages only when it decides something: before
    keeping a 2.0 key for a database that has no data key, and before removing the password from a
    database whose key the password protects. Every page this context sees counts, written or not
    (`IOService.PageFileNames`). A password change rewrites every file anyway.
- **Values 2.0 wrote** stay readable: the current password's 2.0 key, each kept `LegacyPropertyKey`, then
  the constant's. They are rewritten in the new form when their row is.

## 3. Tests, each seen red first

| Test | Red before | Proves |
| :--- | :--- | :--- |
| `XodPropertyCryptoTests` (XML, JSON, Binary): no key refused; an application key protects element and attribute values and nothing on disk holds the plaintext; values survive `ChangePassword`, `Secure` and `Loose`; removing the password needs another key, drops a key no value uses, and leaves no protection by the old password in the now-plain file; a wrong password changes nothing; a key made in a rolled-back transaction isn't used afterwards; `[Crypto]` can't be indexed | 16 of the first 18 on the code before (the wrong-password and rollback tests guard the new code); the 19th, added later, proven by breaking the code | the four issues, in every encoding |
| The review's findings: `Id` by convention can't be `[Crypto]`; `[Crypto]` on a list refused; a password change that fails before the files switch (the first file it rewrites held open) keeps the values readable and can be made again; removing the password sees values not yet on disk (buffered); a context opened earlier uses the key another made | all five red on the code the review read | the review's findings 1-5 |
| conformance `KnownDefectTests`: ISS-050; 2.0's values in the committed password fixture survive two password changes and removing it; in the no-password fixture, adding one; removing a password 2.0's values need is refused without an application key; a change that would lose them is refused | the ISS-050 and two ISS-058 tests failed on the code before | values 2.0 wrote stay readable |
| `read.mjs --check` and `--check-fresh` decrypt every AES256 value, 2.0's and new, from the spec | red with a wrong application key or a wrong expected value | the key format, read by an independent reader |

Each guard was broken on purpose and the tests went red: no key falling back to the constant; the key saved
in the batch with no reset on rollback; the 2.0 key not kept; `Loose` not refused (with and without a data
key); the scan finding no 2.0 values; a change without the application key not refused; `Loose` always
refused; `[Crypto]` indexes allowed; attributes not encrypted; the database file read from the cache;
pages not on disk unseen by the scan; the old protection replaced before the files change; stale
protections never removed.

Changed tests: two older `XodTests` wrote an `AES256` value with no key; they now pass an application key.
The conformance dataset writes with one when there is no password (`Dataset.PropertyKey`); the committed
fixtures are unchanged (2.0's values, under 2.0's keys).

## 4. The gate

Run 2026-11-11 on the tree after the review's fixes, in a clean output folder:

| Check | Result |
| :--- | :--- |
| `Xod.Tests` (net8, net9) | 130/130 each |
| `Xod.Conformance` (net8, net9) | 14/14 each |
| `read.mjs --check` (committed fixtures) | ok: xml, json, binary, password, v1.2.5 |
| `read.mjs --check-fresh` (the dataset as the tree writes it, `AES256` values decrypted by the reader) | ok: xml, json, binary, password |
| `XodBench.Tests` | 26/26 |

Every new test was red on the code before its fix, and each guard was proven by breaking the code it
guards (removing the check, or the kept protection) and watching the test fail. The test of a password
change that fails is deterministic: it holds open the first file the change rewrites, so the failure always
lands after the key's new protection is written and before any file switches. (An earlier version held a
page whose name is random; when it sorted after the database file the change failed after files had
switched, which is `ISS-065`.)

API additions (`api-surface.txt`): `DatabaseOptions.PropertyEncryptionKey`,
`IndexDataTypeException(string)`, `CryptoDataTypeException`.

## 5. Independent review

A fresh agent reviewed the change against the problem statements, read-only, and reproduced each of its
serious findings with a probe. Each was then shown again by a test red on the reviewed code:

1. **A password change that failed part-way lost every value**: the key was protected for the new
   password, and written, before the files changed. Fixed: the old protection stays until the files have
   changed (section 2).
2. **The scan for values read only the pages on disk**, so `Loose` could drop a key that buffered or
   uncommitted rows used. Fixed: the scan reads every page the context sees.
3. **A context opened earlier used its cached database file** and made a second key over another
   context's. Fixed: the file is read afresh whenever the key isn't held yet. Two contexts making the
   first key at the same moment remain (debt).
4. **A property named `Id` became the key by convention after the `[Crypto]` check**, so its plaintext
   went into the index. Fixed: the checks run once every key is known.
5. **`[Crypto]` on a list or dictionary was ignored**, as on an attribute. Fixed by refusing it there;
   encrypting collection items would mean changing both collection readers.
6. **No associated data**: a ciphertext can be moved between rows, and a value can be planted in 2.0's form
   under the public constant, or as bare text. Confidentiality holds; integrity against someone who can
   write the files doesn't. Named debt.
7. **2.0's values written without a password** stay readable by anyone until rewritten. Named debt.
8. **Comments that claimed more than the code**: `SaveNow` doesn't flush to the device; a rollback can't
   remove a key. Corrected.
9. **Hygiene**: the protection key is derived without the shared cache (the application key no longer sits
   in it); the reader states the GCM tag length. PBKDF2 stays at 100,000 iterations, as the file envelope
   uses (debt).

The reviewer also suggested indexing a hashed property's digest instead of refusing the index. The owner
chose refusing; the alternative is noted for a later release.

## 6. Verdicts

| Issue | Verdict |
| :--- | :--- |
| ISS-050 | **Clean for new values, with named debt**: 2.0's values written without a password stay under the public constant until rewritten. |
| ISS-058 | **Clean, with named debt**: the key survives every password change, including one that fails before the files switch; a crash between the files' switch and the removal of the old protection leaves that protection until the next change. A change that fails after some files switched locks the whole database (`ISS-065`, open, pre-existing): no key can help there. |
| ISS-063 | **Clean by prohibition** (the owner's choice): no index on a `[Crypto]` property. |
| ISS-064 | **Clean**: applied on attributes; refused on non-string types. |

## 7. Remaining debt

- **Password changes aren't atomic** (as in 2.0; `ISS-065`, open): a failure or crash part-way leaves files
  under two passwords, and the database then opens with neither. The fix is a rewrite into new files switched
  in one step, or a journal; it touches the file envelope, not the property key, so it is not part of this
  task. The key is ready for it: protected by both passwords until `PasswordChanged`.
- **Two contexts making the first key at the same moment** without `DatabaseOptions.LockDatabase`: the
  last write of the database file wins and the other's values can't be read. (A context opened earlier
  now reads the key another made.)
- **PBKDF2 at 100,000 iterations**, as the file envelope; current guidance asks for more. Raising it
  needs an iteration count in the format.
- **Values 2.0 wrote without a password** stay readable by anyone with XOD until their rows are rewritten;
  no tool re-encrypts them in bulk.
- **No associated data binds a value to its row or property**: someone who can write the files can move a
  ciphertext between them. (They can also write any value at all; the encryption is for confidentiality.)
- **The scan for values** counts any string starting `enc:aes256`, `[Crypto]` or not, so it errs towards
  keeping a key or refusing to remove a password.
