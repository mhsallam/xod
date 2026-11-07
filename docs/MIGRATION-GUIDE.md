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

All classic XOD 1.x attributes work identically:
- `[PrimaryKey]` (single and composite keys)
- `[UniqueKey]` (now fully enforced on `Insert` and `Update`)
- `[ForeignKey("AuthorId")]` and `[ForeignKey("AuthorId", "Id")]`
- `[Children]` and `[ParentKey("DepartmentId")]` *(plus in XOD++, these are now optional when using standard naming conventions!)*
- `[Property(Position = ValuePosition.Attribute, Cascade = CascadeOptions.Delete, AutoNumber = true, Indexed = true, IdentitySeed = 1, IdentityIncrement = 1)]`
- `[Crypto(CryptoMethod.MD5)]`, `[Crypto(CryptoMethod.SHA1)]`, plus new `SHA256`, `SHA512`, and reversible `AES256`
- `[Markup]`, `[Required]`, `[NotMapped]`, `[GenericTypeProperty("TypeName")]`

## 3. New Capabilities Available When You Need Them

- **Strongly-typed `Insert<T>`**: `ToDo created = db.Insert(new ToDo { Title = "New" });` returns the `ToDo` instance with `Id` populated.
- **Pluggable Storage Formats**: Switch between `StorageFormat.Xml`, `StorageFormat.Json`, and `StorageFormat.Binary` via `DatabaseOptions` or convert existing `.xod` folders with `XodMigrator.ConvertDatabase(...)`.
- **ACID Transactions**: `using var tx = db.BeginTransaction(); ... tx.Commit();`
- **Async/Await APIs**: `await db.InsertAsync(...)`, `await db.FindAsync(...)`, `await db.QueryAsync(...)`
