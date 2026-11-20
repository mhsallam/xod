# Migrating from XOD 1.x / Xod.Core to XOD++ (`xod2`)

XOD++ is designed as a **100% syntax-compatible drop-in upgrade** for applications written against XOD 1.x (`.NET Framework`) and `Xod.Core` (`.NET Core 1.0`).

---

## 1. Namespace & Context Compatibility

The root namespace remains `Xod` and the main entry point remains `XodContext`:

```csharp
using Xod;

// Classic XOD 1.x syntax works unchanged:
XodContext db = new XodContext(@"c:\xod\data.xod");
db.Insert(new ToDo() { Title = "Read a book..." });

var tdi = db.Find<ToDo>(s => s.Id == 1);      // Supported (and now B-Tree indexed!)
var tdi2 = db.FirstMatch<ToDo>(s => s.Id == 1); // Supported (from Xod.Core)

tdi.Done = true;
db.Update(tdi);
```

## 2. Attribute Compatibility

All classic XOD 1.x attributes work, with one exception, `[Crypto]` with no method (section 2.1):
- `[PrimaryKey]` (single and composite keys)
- `[UniqueKey]` (now fully enforced on `Insert` and `Update`)
- `[ForeignKey("AuthorId")]` and `[ForeignKey("AuthorId", "Id")]`
- `[Children]` and `[ParentKey("DepartmentId")]` *(plus in XOD++, these are now optional when using standard naming conventions!)*
- `[Property(Position = ValuePosition.Attribute, Cascade = CascadeOptions.Delete, AutoNumber = true, Indexed = true)]`. `IdentitySeed` and `IdentityIncrement` couldn't be set this way in 1.x or 2.0.x (they were `dynamic`, which C# refuses in an attribute, CS0655); the next release makes them settable, and refuses them on a `Guid` autonumber (see `RELEASE-NOTES-NEXT.md`)
- `[Crypto(CryptoMethod.MD5)]`, `[Crypto(CryptoMethod.SHA1)]`, plus new `SHA256`, `SHA512`, and reversible `AES256`
- `[Markup]`, `[Required]`, `[NotMapped]`, `[GenericTypeProperty("TypeName")]`

### 2.1 Encrypted databases and `[Crypto]` values from 1.x

- **A bare `[Crypto]` hashes with SHA256, not MD5.** In 1.x, `[Crypto]` with no method meant MD5. A 1.x model
  should name it, `[Crypto(CryptoMethod.MD5)]`, to go on hashing new values the way its stored ones were.
- **An encrypted 1.x database opens with its password** (`ISS-151`; tested against a database written by the 2017
  `xod.core` code). Files XOD++ writes are in its own format (AES-GCM); a database that was opened and written holds
  both until a password change (`ChangePassword`) writes every file again in the new format. 1.x hashed the password
  as ASCII (versions from September 2016) or in the system's code page (earlier ones): a password of ASCII
  characters opens either. A password with other characters, from the 2016-17 versions, opens in the form 1.x
  hashed: each such character as `?` (`"pässwort"` as `"p?sswort"`); change it afterwards with `ChangePassword`. One
  from the earlier versions doesn't open.
- **Stored 1.x digests are kept by an update of their row.** 1.x stored an MD5 or SHA1 digest as the decimal digits
  of each byte, run together; XOD++ stores them in hexadecimal. A read gives a 1.x digest back as stored, an update
  of the row keeps it, and `CryptoHelper.VerifyHash` and SQL's `VERIFY_HASH` verify a value against either form. A
  value set again is stored in the new form. `CryptoHelper.GetMD5HashData` and `GetSHA1HashData` return the new form,
  so code that compared their result with a stored 1.x digest should call `VerifyHash` instead. Its limits:
  - 1.x itself hashed a stored digest again at every update of its row, so a row a 1.x application updated without
    setting the value holds a digest of its digest, which neither 1.x nor XOD++ can verify;
  - a value with characters outside ASCII, hashed by a version before September 2016 (the system's code page),
    doesn't verify;
  - any other write hashes a digest again: an object read from a 1.x database and inserted elsewhere, a digest
    given to another row, a child without a primary key written again by its parent's update. Open the 1.x database
    with `DatabaseOptions.Legacy1xDigests = true` and a digest read from it stays a digest wherever it is written. A
    copy that makes a new string (serialized and back) is hashed still;
  - the other way round, a value a row stores in plaintext under `[Crypto]` (an attribute written by 2.0.0 or 2.0.1)
    is hashed by the next update of the row, unless it is digits that read as a 1.x digest, such as a 16-digit card
    number under MD5: that stays as stored, and with `Legacy1xDigests` on it is copied as stored too. Leave the
    option off for such a database (`ISS-171`).
- **Change the password after migrating** an encrypted 1.x database: its files are keyed by one unsalted SHA-256 of
  the password until `ChangePassword` writes them again in the new format.

## 3. Upgrading within 2.0.x

This guide is for 1.x and `Xod.Core` applications. An application already on 2.0.x upgrades by the checklist on the
site ([Upgrading](https://xod.sabaeon.com/docs/upgrading)) and the release notes of each version
([`RELEASE-NOTES-2.0.2.md`](RELEASE-NOTES-2.0.2.md), [`RELEASE-NOTES-2.0.3.md`](RELEASE-NOTES-2.0.3.md) and
[`RELEASE-NOTES-2.1.0.md`](RELEASE-NOTES-2.1.0.md), each from its section "Upgrading from"). One rule holds for every upgrade: don't open one database from two versions at once.
Close every context of the old version first: an older version takes none of the locks a newer one relies on.

## 4. New Capabilities Available When You Need Them

- **Strongly-typed `Insert<T>`**: `ToDo created = db.Insert(new ToDo { Title = "New" });` returns the `ToDo` instance with `Id` populated.
- **Pluggable Storage Formats**: Switch between `StorageFormat.Xml`, `StorageFormat.Json`, and `StorageFormat.Binary` via `DatabaseOptions` or convert existing `.xod` folders with `XodMigrator.ConvertDatabase(...)`.
- **ACID Transactions**: `using var tx = db.BeginTransaction(); ... tx.Commit();`
- **Async/Await APIs**: `await db.InsertAsync(...)`, `await db.FindAsync(...)`, `await db.QueryAsync(...)`
