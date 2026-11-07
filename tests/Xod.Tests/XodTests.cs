using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class ToDoItem
    {
        public int Id { get; set; }
        [Required]
        public string Title { get; set; }
        [Property(Position = ValuePosition.Attribute)]
        public bool Done { get; set; }
        [Markup]
        public string HtmlNotes { get; set; }
        public List<string> Tags { get; set; }
        public Dictionary<string, string> Metadata { get; set; }
    }

    public class UserAccount
    {
        public int Id { get; set; }

        [UniqueKey]
        [Required]
        public string Email { get; set; }

        [Crypto(CryptoMethod.SHA256)]
        public string PasswordHash { get; set; }

        [Crypto(CryptoMethod.AES256)]
        public string SecretApiKey { get; set; }

        [Property(Indexed = true)]
        public int Score { get; set; }
    }

    public class CompositeEnrollment
    {
        [PrimaryKey]
        public int StudentId { get; set; }

        [PrimaryKey]
        public int CourseId { get; set; }

        public string Grade { get; set; }
    }

    public class Author
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class BookAddress
    {
        // Complex type (no PrimaryKey / Id)
        public string WarehouseCity { get; set; }
        public string ShelfCode { get; set; }
    }

    public class Book
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public int AuthorId { get; set; }

        [ForeignKey(nameof(AuthorId))]
        public Author Author { get; set; }

        public BookAddress Location { get; set; }
    }

    public class Department
    {
        public int Id { get; set; }
        public string Name { get; set; }

        [Children]
        public List<Employee> Employees { get; set; }
    }

    public class Employee
    {
        public int Id { get; set; }
        public string FullName { get; set; }
        public int DepartmentId { get; set; }

        [ParentKey(nameof(DepartmentId))]
        public Department Department { get; set; }
    }

    public record ModernEventRecord
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateOnly EventDate { get; set; }
        public TimeOnly StartTime { get; set; }
    }

    public class XodTests : IDisposable
    {
        private readonly string tempRoot;

        public XodTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodPlusPlusTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        private string GetDbPath(string folderName) => Path.Combine(tempRoot, folderName, "data.xod");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }

        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        [InlineData(StorageFormat.Binary)]
        public void Crud_Operations_Work_Across_All_Storage_Engines(StorageFormat format)
        {
            string dbPath = GetDbPath($"crud_{format}");
            var options = new DatabaseOptions { StorageFormat = format };

            using (var db = new XodContext(dbPath, options: options))
            {
                db.Insert(new ToDoItem
                {
                    Title = "Build XOD++",
                    Done = false,
                    HtmlNotes = "<b>High priority</b> &amp; <script>alert(1)</script>",
                    Tags = new List<string> { "database", "dotnet", "xml" },
                    Metadata = new Dictionary<string, string>
                    {
                        ["owner"] = "mhsallam",
                        ["version"] = "2.0"
                    }
                });

                db.Insert(new ToDoItem
                {
                    Title = "Write unit tests",
                    Done = false,
                    Tags = new List<string> { "testing" }
                });

                Assert.Equal(2, db.Count<ToDoItem>());

                var first = db.Find<ToDoItem>(x => x.Id == 1);
                Assert.NotNull(first);
                Assert.Equal("Build XOD++", first.Title);
                Assert.False(first.Done);
                Assert.Equal("<b>High priority</b> &amp; <script>alert(1)</script>", first.HtmlNotes);
                Assert.Equal(new[] { "database", "dotnet", "xml" }, first.Tags);
                Assert.Equal("mhsallam", first.Metadata["owner"]);

                first.Done = true;
                first.Title = "Build XOD++ v2.0";
                Assert.True(db.Update(first));

                var updated = db.FirstMatch<ToDoItem>(x => x.Id == 1);
                Assert.True(updated.Done);
                Assert.Equal("Build XOD++ v2.0", updated.Title);

                var backward = db.SelectBackward<ToDoItem>().ToList();
                Assert.Equal(2, backward[0].Id);
                Assert.Equal(1, backward[1].Id);

                Assert.True(db.Delete(updated));
                Assert.Equal(1, db.Count<ToDoItem>());
            }
        }

        [Fact]
        public void UniqueKey_And_Composite_PrimaryKey_Are_Enforced()
        {
            string dbPath = GetDbPath("constraints");
            using var db = new XodContext(dbPath);

            db.Insert(new UserAccount
            {
                Email = "dev@example.com",
                PasswordHash = "my-secret",
                SecretApiKey = "not-a-real-key",
                Score = 90
            });

            // Duplicate [UniqueKey] should throw ReservedUniqueKeyException
            Assert.Throws<ReservedUniqueKeyException>(() =>
            {
                db.Insert(new UserAccount
                {
                    Email = "dev@example.com",
                    PasswordHash = "other",
                    Score = 50
                });
            });

            // Composite primary key test
            db.Insert(new CompositeEnrollment { StudentId = 10, CourseId = 101, Grade = "A" });
            db.Insert(new CompositeEnrollment { StudentId = 10, CourseId = 102, Grade = "B+" });

            // Duplicate composite key (10, 101) should throw ReservedPrimaryKeyException
            Assert.Throws<ReservedPrimaryKeyException>(() =>
            {
                db.Insert(new CompositeEnrollment { StudentId = 10, CourseId = 101, Grade = "C" });
            });
        }

        [Fact]
        public void Property_Level_Hashing_And_Reversible_AES256_Work()
        {
            string dbPath = GetDbPath("prop_crypto");
            using var db = new XodContext(dbPath);

            db.Insert(new UserAccount
            {
                Email = "alice@example.com",
                PasswordHash = "PlainPassword123",
                SecretApiKey = "super-sensitive-token-xyz",
                Score = 100
            });

            var user = db.Find<UserAccount>(u => u.Email == "alice@example.com");
            Assert.NotNull(user);
            // SHA-256 is one-way hashed (64 hex chars)
            Assert.NotEqual("PlainPassword123", user.PasswordHash);
            Assert.Equal(64, user.PasswordHash.Length);
            Assert.True(Xod.Helpers.CryptoHelper.VerifyHash("PlainPassword123", user.PasswordHash, CryptoMethod.SHA256));

            // AES-256-GCM is automatically decrypted on read!
            Assert.Equal("super-sensitive-token-xyz", user.SecretApiKey);

            // Updating an unrelated property must NOT re-hash PasswordHash (idempotency check)
            string originalHash = user.PasswordHash;
            user.Score = 250;
            Assert.True(db.Update(user));

            var reloaded = db.Find<UserAccount>(u => u.Id == user.Id);
            Assert.Equal(250, reloaded.Score);
            Assert.Equal(originalHash, reloaded.PasswordHash);
            Assert.True(Xod.Helpers.CryptoHelper.VerifyHash("PlainPassword123", reloaded.PasswordHash, CryptoMethod.SHA256));
            Assert.Equal("super-sensitive-token-xyz", reloaded.SecretApiKey);
        }

        [Fact]
        public void Indexed_Expression_Queries_Support_Exact_And_Range_Lookups()
        {
            string dbPath = GetDbPath("indexing");
            using var db = new XodContext(dbPath);

            for (int i = 1; i <= 20; i++)
            {
                db.Insert(new UserAccount
                {
                    Email = $"user{i}@example.com",
                    Score = i * 10
                });
            }

            // Expression tree query using [Property(Indexed = true)] range scan
            var highScorers = db.Query<UserAccount>(u => u.Score >= 150 && u.Score <= 180).ToList();
            Assert.Equal(4, highScorers.Count);
            Assert.Equal(new[] { 150, 160, 170, 180 }, highScorers.Select(u => u.Score).OrderBy(s => s).ToArray());

            // Exact lookup on [UniqueKey]
            var specific = db.Find<UserAccount>(u => u.Email == "user12@example.com");
            Assert.NotNull(specific);
            Assert.Equal(120, specific.Score);
        }

        [Fact]
        public void Relationships_ForeignKey_Complex_And_ParentChildren_Work()
        {
            string dbPath = GetDbPath("relationships");
            using var db = new XodContext(dbPath);

            var author = new Author { Name = "Mohammed Sallam" };
            db.Insert(author);

            var book = new Book
            {
                Title = "Little Guide for XOD Database Users",
                Author = author,
                Location = new BookAddress
                {
                    WarehouseCity = "Riyadh",
                    ShelfCode = "A-12"
                }
            };
            db.Insert(book);

            db.ClearCache();

            var loadedBook = db.Find<Book>(b => b.Id == 1, include: "*");
            Assert.NotNull(loadedBook);
            Assert.Equal(author.Id, loadedBook.AuthorId);
            Assert.NotNull(loadedBook.Author);
            Assert.Equal("Mohammed Sallam", loadedBook.Author.Name);
            Assert.NotNull(loadedBook.Location);
            Assert.Equal("Riyadh", loadedBook.Location.WarehouseCity);

            // 1-M Parent / Children relationship
            var dept = new Department
            {
                Name = "R&D",
                Employees = new List<Employee>
                {
                    new Employee { FullName = "Sara" },
                    new Employee { FullName = "Omar" }
                }
            };
            db.Insert(dept);

            db.ClearCache();

            var loadedDept = db.Find<Department>(d => d.Id == 1, include: "*");
            Assert.NotNull(loadedDept);
            Assert.Equal(2, loadedDept.Employees.Count);
            Assert.All(loadedDept.Employees, e => Assert.Equal(1, e.DepartmentId));

            // Cascade delete: deleting Department deletes its [Children] Employees
            Assert.True(db.Delete(loadedDept));
            Assert.Equal(0, db.Count<Employee>());
        }

        [Fact]
        public void Transactions_Commit_And_Rollback_Atomically()
        {
            string dbPath = GetDbPath("transactions");
            using var db = new XodContext(dbPath);

            db.Insert(new ToDoItem { Title = "Initial item" });
            Assert.Equal(1, db.Count<ToDoItem>());

            // Rollback transaction
            using (var tx = db.BeginTransaction())
            {
                db.Insert(new ToDoItem { Title = "Should be rolled back 1" });
                db.Insert(new ToDoItem { Title = "Should be rolled back 2" });
                Assert.Equal(3, db.Count<ToDoItem>());

                tx.Rollback();
            }

            Assert.Equal(1, db.Count<ToDoItem>());
            Assert.Equal("Initial item", db.First<ToDoItem>().Title);

            // Commit transaction
            using (var tx = db.BeginTransaction())
            {
                db.Insert(new ToDoItem { Title = "Committed item" });
                tx.Commit();
            }

            Assert.Equal(2, db.Count<ToDoItem>());
        }

        [Fact]
        public void Database_Encryption_Secure_ChangePassword_And_Loose_Work()
        {
            string dbPath = GetDbPath("security");
            using (var db = new XodContext(dbPath))
            {
                db.Insert(new ToDoItem { Title = "Confidential Roadmap" });
                db.Secure("CorrectHorseBatteryStaple");
            }

            // Opening without password should fail with SecurityException
            Assert.Throws<SecurityException>(() =>
            {
                using var unauthDb = new XodContext(dbPath);
            });

            // Opening with wrong password should fail with SecurityException
            Assert.Throws<SecurityException>(() =>
            {
                using var wrongPwdDb = new XodContext(dbPath, "WrongPassword");
            });

            // Opening with right password succeeds
            using (var secureDb = new XodContext(dbPath, "CorrectHorseBatteryStaple"))
            {
                var item = secureDb.First<ToDoItem>();
                Assert.Equal("Confidential Roadmap", item.Title);

                secureDb.ChangePassword("CorrectHorseBatteryStaple", "NewSecret2026!");
                secureDb.Loose("NewSecret2026!");
            }

            // Now unencrypted again
            using (var openDb = new XodContext(dbPath))
            {
                Assert.Equal("Confidential Roadmap", openDb.First<ToDoItem>().Title);
            }
        }

        [Fact]
        public async Task Migrator_And_Modern_Types_And_Async_Apis_Work()
        {
            string xmlDir = Path.Combine(tempRoot, "mig_xml");
            string jsonDir = Path.Combine(tempRoot, "mig_json");
            string binDir = Path.Combine(tempRoot, "mig_bin");

            using (var db = new XodContext(Path.Combine(xmlDir, "data.xod")))
            {
                await db.InsertAsync(new ModernEventRecord
                {
                    Name = "XOD++ Launch",
                    EventDate = new DateOnly(2026, 10, 26),
                    StartTime = new TimeOnly(23, 0, 0)
                });
            }

            // Convert XML -> JSON
            int jsonFiles = XodMigrator.ConvertDatabase(xmlDir, jsonDir, StorageFormat.Json);
            Assert.True(jsonFiles >= 3); // .xod, .xtab, .xpag

            using (var jsonDb = new XodContext(Path.Combine(jsonDir, "data.xod"), options: new DatabaseOptions { StorageFormat = StorageFormat.Json }))
            {
                var ev = await jsonDb.FindAsync<ModernEventRecord>(x => x.Id == 1);
                Assert.NotNull(ev);
                Assert.Equal("XOD++ Launch", ev.Name);
                Assert.Equal(new DateOnly(2026, 10, 26), ev.EventDate);
                Assert.Equal(new TimeOnly(23, 0, 0), ev.StartTime);
            }

            // Convert JSON -> Binary
            int binFiles = XodMigrator.ConvertDatabase(jsonDir, binDir, StorageFormat.Binary);
            Assert.True(binFiles >= 3);

            using (var binDb = new XodContext(Path.Combine(binDir, "data.xod"), options: new DatabaseOptions { StorageFormat = StorageFormat.Binary }))
            {
                var all = await binDb.SelectAsync<ModernEventRecord>();
                Assert.Single(all);
                Assert.Equal("XOD++ Launch", all[0].Name);
            }
        }

        [Fact]
        public void LazyLoad_Option_On_DatabaseOptions_Does_Not_Drop_Relationships_On_Insert()
        {
            string dbPath = GetDbPath("lazyload_decoupled");
            using var db = new XodContext(dbPath, options: new DatabaseOptions { LazyLoad = true });

            var dept = new Department
            {
                Name = "Platform Engineering",
                Employees = new List<Employee>
                {
                    new Employee { FullName = "Layla" },
                    new Employee { FullName = "Zaid" }
                }
            };

            db.Insert(dept);
            db.ClearCache();

            // Default read with LazyLoad = true does not hydrate Employees
            var shallowDept = db.Find<Department>(d => d.Id == 1);
            Assert.NotNull(shallowDept);
            Assert.Null(shallowDept.Employees);

            // Explicit include hydrates Employees that were persisted during Insert!
            var eagerDept = db.Find<Department>(d => d.Id == 1, include: nameof(Department.Employees));
            Assert.NotNull(eagerDept);
            Assert.NotNull(eagerDept.Employees);
            Assert.Equal(2, eagerDept.Employees.Count);
        }

        public class InventoryStock
        {
            public int Id { get; set; }

            [Required]
            public string Sku { get; set; }

            [Required]
            public int Quantity { get; set; }

            [Required]
            [Property(Position = ValuePosition.Attribute)]
            public bool IsDiscontinued { get; set; }
        }

        [Fact]
        public void Required_Attribute_Allows_Zero_And_False_And_Persists_Explicit_Scalars()
        {
            string dbPath = GetDbPath("required_scalars");
            using var db = new XodContext(dbPath);

            // Missing required string throws RequiredPropertyException
            Assert.Throws<RequiredPropertyException>(() =>
                db.Insert(new InventoryStock { Sku = "", Quantity = 5, IsDiscontinued = false }));

            // Zero and false on [Required] value types must succeed
            db.Insert(new InventoryStock { Sku = "SKU-ZERO", Quantity = 0, IsDiscontinued = false });
            db.ClearCache();

            var loaded = db.Find<InventoryStock>(x => x.Id == 1);
            Assert.NotNull(loaded);
            Assert.Equal("SKU-ZERO", loaded.Sku);
            Assert.Equal(0, loaded.Quantity);
            Assert.False(loaded.IsDiscontinued);

            // Verify raw XML page file explicitly persisted <Quantity>0</Quantity> and IsDiscontinued="False"
            string dir = Path.GetDirectoryName(dbPath);
            string pageXml = File.ReadAllText(Directory.GetFiles(dir, "*.xpag").Single());
            Assert.Contains("<Quantity>0</Quantity>", pageXml);
            Assert.Contains("IsDiscontinued=\"False\"", pageXml, StringComparison.OrdinalIgnoreCase);
        }

        // Plain POCOs with zero [Children], [ParentKey], or [ForeignKey] attributes
        public class Supplier
        {
            public int Id { get; set; }
            public string CompanyName { get; set; }
        }

        public class Category
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public List<ProductItem> Products { get; set; }
        }

        public class ProductItem
        {
            public int Id { get; set; }
            public string Title { get; set; }

            // Convention 1-M scalar FK only (no Category navigation property required!)
            public int CategoryId { get; set; }

            // Convention 1-Shared FK + navigation property (no [ForeignKey] attribute required!)
            public int SupplierId { get; set; }
            public Supplier Supplier { get; set; }
        }

        [Fact]
        public void Convention_Based_OneToMany_And_ForeignKey_Work_Without_Attributes()
        {
            string dbPath = GetDbPath("conventions");
            using var db = new XodContext(dbPath);

            var supplier = new Supplier { CompanyName = "Acme Hardware" };
            db.Insert(supplier);

            var category = new Category
            {
                Name = "Workstations",
                Products = new List<ProductItem>
                {
                    new ProductItem { Title = "DevBox Pro", SupplierId = supplier.Id },
                    new ProductItem { Title = "MiniServer X", SupplierId = supplier.Id }
                }
            };

            db.Insert(category);
            db.ClearCache();

            var loadedCategory = db.Find<Category>(c => c.Id == 1, include: "*");
            Assert.NotNull(loadedCategory);
            Assert.NotNull(loadedCategory.Products);
            Assert.Equal(2, loadedCategory.Products.Count);
            Assert.All(loadedCategory.Products, p =>
            {
                Assert.Equal(1, p.CategoryId);
                Assert.NotNull(p.Supplier);
                Assert.Equal("Acme Hardware", p.Supplier.CompanyName);
            });

            // Cascade delete on convention 1-M Children
            Assert.True(db.Delete(loadedCategory));
            Assert.Equal(0, db.Count<ProductItem>());
            // Shared Supplier must remain intact
            Assert.Equal(1, db.Count<Supplier>());
        }

        [Fact]
        public void Xtab_Metadata_Is_Not_Rewritten_On_Subsequent_Single_Row_Inserts()
        {
            string dbPath = GetDbPath("xtab_io_opt");
            using var db = new XodContext(dbPath);

            // First insert creates the .xtab and first .xpag
            db.Insert(new ToDoItem { Title = "First row" });

            string dir = Path.GetDirectoryName(dbPath);
            string xtabPath = Directory.GetFiles(dir, "*.xtab").Single();
            var sentinelTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(xtabPath, sentinelTime);

            // Subsequent single-row inserts append to the active .xpag without rewriting .xtab
            db.Insert(new ToDoItem { Title = "Second row" });
            db.Insert(new ToDoItem { Title = "Third row" });

            Assert.Equal(sentinelTime, File.GetLastWriteTimeUtc(xtabPath));
            Assert.Equal(3, db.Count<ToDoItem>());
        }

        [Fact]
        public async Task Typed_Insert_And_InsertOrUpdate_Return_Entity_With_Populated_Id()
        {
            string dbPath = GetDbPath("typed_insert");
            using var db = new XodContext(dbPath);

            ToDoItem created = db.Insert(new ToDoItem { Title = "Typed Return 1" });
            Assert.NotNull(created);
            Assert.Equal(1, created.Id);

            ToDoItem upserted = db.InsertOrUpdate(new ToDoItem { Id = 1, Title = "Typed Return 1 Updated", Done = true });
            Assert.NotNull(upserted);
            Assert.Equal(1, upserted.Id);
            Assert.Equal("Typed Return 1 Updated", db.Find<ToDoItem>(x => x.Id == 1).Title);

            ToDoItem asyncCreated = await db.InsertAsync(new ToDoItem { Title = "Typed Return 2" });
            Assert.NotNull(asyncCreated);
            Assert.Equal(2, asyncCreated.Id);

            // Untyped Insert(Type, object) still returns PrimaryKey dictionary for reflection callers
            object rawKeys = db.Insert(typeof(ToDoItem), new ToDoItem { Title = "Untyped Return 3" });
            var keyDict = Assert.IsType<Dictionary<string, object>>(rawKeys);
            Assert.Equal(3, keyDict["Id"]);
        }

        [Fact]
        public void Persistent_Xidx_Index_File_Is_Created_And_Used_On_Cold_Start()
        {
            string dbPath = GetDbPath("persistent_xidx");
            string dir = Path.GetDirectoryName(dbPath);

            using (var db = new XodContext(dbPath))
            {
                db.InsertRange(new[]
                {
                    new UserAccount { Email = "u1@xod.dev", Score = 100 },
                    new UserAccount { Email = "u2@xod.dev", Score = 250 },
                    new UserAccount { Email = "u3@xod.dev", Score = 400 }
                });
            }

            // Verify .xidx sidecar file was persisted on disk
            string xidxPath = Path.Combine(dir, $"{typeof(UserAccount).FullName}.xidx");
            Assert.True(File.Exists(xidxPath));
            string xidxXml = File.ReadAllText(xidxPath);
            Assert.Contains("<TableIndex", xidxXml);
            Assert.Contains("u2@xod.dev", xidxXml);

            // Cold start in a brand-new XodContext loads the .xidx file and resolves indexed queries
            using (var coldDb = new XodContext(dbPath))
            {
                var found = coldDb.Find<UserAccount>(u => u.Email == "u2@xod.dev");
                Assert.NotNull(found);
                Assert.Equal(250, found.Score);

                var highScorers = coldDb.Query<UserAccount>(u => u.Score >= 200).ToList();
                Assert.Equal(2, highScorers.Count);

                // Drop<UserAccount> removes .xidx along with .xtab and .xpag
                Assert.True(coldDb.Drop<UserAccount>());
                Assert.False(File.Exists(xidxPath));
            }
        }
    }
}
