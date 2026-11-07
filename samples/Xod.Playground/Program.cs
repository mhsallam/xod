using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xod;
using Xod.Helpers;

namespace Xod.Playground
{
    #region Sample Domain Models

    public class Author
    {
        public int Id { get; set; }

        [Required]
        public string Name { get; set; }

        [UniqueKey]
        public string Email { get; set; }

        [Crypto(CryptoMethod.SHA256)]
        public string PasswordHash { get; set; }
    }

    public class WarehouseLocation
    {
        // Complex type (no Id / PrimaryKey -> embedded lifecycle with parent)
        public string City { get; set; }
        public string Aisle { get; set; }
    }

    public class Book
    {
        public int Id { get; set; }

        [Required]
        public string Title { get; set; }

        [Property(Position = ValuePosition.Attribute)]
        public string Isbn { get; set; }

        [Property(Indexed = true)]
        public decimal Price { get; set; }

        [Required]
        public int StockCount { get; set; }

        public DateOnly PublishedDate { get; set; }

        // Convention-over-configuration ForeignKey (no [ForeignKey] attribute needed!)
        public int AuthorId { get; set; }
        public Author Author { get; set; }

        public WarehouseLocation Location { get; set; }

        public List<string> Tags { get; set; }

        public Dictionary<string, string> Specs { get; set; }
    }

    public class Project
    {
        public int Id { get; set; }
        public string Name { get; set; }

        // Convention-over-configuration 1-M Children (no [Children] attribute needed!)
        public List<TaskItem> Tasks { get; set; }
    }

    public class TaskItem
    {
        public int Id { get; set; }

        [Required]
        public string Title { get; set; }

        [Required]
        [Property(Position = ValuePosition.Attribute)]
        public bool Done { get; set; }

        [Required]
        [Property(Indexed = true)]
        public int Priority { get; set; }

        [Markup]
        public string Notes { get; set; }

        [Crypto(CryptoMethod.AES256)]
        public string SecretToken { get; set; }

        // Convention-over-configuration 1-M Parent link (no [ParentKey] attribute needed!)
        public int ProjectId { get; set; }
        public Project Project { get; set; }
    }

    #endregion

    public static class Program
    {
        private static readonly string PlaygroundRoot = Path.Combine(AppContext.BaseDirectory, "playground_data");
        private static StorageFormat currentFormat = StorageFormat.Xml;
        private static string currentPassword = null;

        private static string ActiveDbFolder => Path.Combine(PlaygroundRoot, "interactive_db");
        private static string ActiveDbFile => Path.Combine(ActiveDbFolder, "data.xod");

        public static void Main(string[] args)
        {
            Directory.CreateDirectory(PlaygroundRoot);

            if (args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
            {
                RunGuidedTour();
                return;
            }

            if (args.Contains("--benchmark", StringComparer.OrdinalIgnoreCase))
            {
                RunBenchmark();
                return;
            }

            RunInteractiveLoop();
        }

        private static void RunInteractiveLoop()
        {
            EnsureInteractiveSeedData();

            while (true)
            {
                Console.WriteLine();
                WriteHeader($"XOD++ Interactive Test Console  [Engine: {currentFormat} | Encrypted: {(currentPassword != null ? "YES" : "NO")}]");
                Console.WriteLine($"  Database Path: {ActiveDbFile}");
                Console.WriteLine();
                Console.WriteLine("  --- Guided Demos & Benchmarks ---");
                Console.WriteLine("  [1] Run Full Automated Feature Tour (CRUD, Relations, Indexes, Crypto, Transactions, Migration)");
                Console.WriteLine("  [2] Run Storage Engine Benchmark (XML vs JSON vs Binary speed & size)");
                Console.WriteLine();
                Console.WriteLine("  --- Interactive Database Playground ---");
                Console.WriteLine("  [3] List all Projects & Tasks");
                Console.WriteLine("  [4] Add a new Task");
                Console.WriteLine("  [5] Toggle Task Done / Update Task");
                Console.WriteLine("  [6] Query Tasks by Priority Range (Indexed B-Tree Query)");
                Console.WriteLine("  [7] Delete a Task");
                Console.WriteLine("  [8] Test ACID Transaction Rollback (Insert & Rollback)");
                Console.WriteLine("  [9] Inspect Raw Database Files on Disk (.xod / .xtab / .xpag / .xidx)");
                Console.WriteLine("  [10] Convert Active Database Format (XML <-> JSON <-> Binary)");
                Console.WriteLine("  [11] Toggle AES-256-GCM Database Password Encryption");
                Console.WriteLine("  [0] Exit");
                Console.Write("\nSelect an option: ");

                string input = Console.ReadLine()?.Trim();
                if (input == null || input == "0" || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    break;

                Console.WriteLine();
                try
                {
                    switch (input)
                    {
                        case "1":
                            RunGuidedTour();
                            break;
                        case "2":
                            RunBenchmark();
                            break;
                        case "3":
                            ListInteractiveTasks();
                            break;
                        case "4":
                            AddInteractiveTask();
                            break;
                        case "5":
                            ToggleInteractiveTask();
                            break;
                        case "6":
                            QueryInteractiveTasks();
                            break;
                        case "7":
                            DeleteInteractiveTask();
                            break;
                        case "8":
                            DemoInteractiveTransactionRollback();
                            break;
                        case "9":
                            InspectRawDatabaseFiles();
                            break;
                        case "10":
                            ConvertInteractiveDatabaseFormat();
                            break;
                        case "11":
                            ToggleInteractiveEncryption();
                            break;
                        default:
                            Console.WriteLine("Invalid option. Please choose 0-11.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    WriteColor($"[Error] {ex.GetType().Name}: {ex.Message}", ConsoleColor.Red);
                }
            }
        }

        private static XodContext OpenActiveDb()
        {
            return new XodContext(ActiveDbFile, currentPassword, new DatabaseOptions
            {
                StorageFormat = currentFormat,
                InitialCreate = true
            });
        }

        private static void EnsureInteractiveSeedData()
        {
            if (Directory.Exists(ActiveDbFolder))
            {
                Directory.Delete(ActiveDbFolder, recursive: true);
            }

            using var db = OpenActiveDb();
            var proj = new Project
            {
                Name = "XOD++ Core Engine",
                Tasks = new List<TaskItem>
                {
                    new TaskItem
                    {
                        Title = "Implement B-Tree Range Index",
                        Done = true,
                        Priority = 10,
                        Notes = "<b>Completed</b> sorted key normalization",
                        SecretToken = "token-alpha-999"
                    },
                    new TaskItem
                    {
                        Title = "Add JSON & Binary Storage Serializers",
                        Done = true,
                        Priority = 8,
                        Notes = "Supports lossless XodMigrator conversion",
                        SecretToken = "token-beta-777"
                    },
                    new TaskItem
                    {
                        Title = "Build Interactive Console App",
                        Done = false,
                        Priority = 5,
                        Notes = "Let user test CRUD & inspect files",
                        SecretToken = "token-gamma-555"
                    }
                }
            };
            db.Insert(proj);
        }

        private static void ListInteractiveTasks()
        {
            using var db = OpenActiveDb();
            var stats = db.GetStatistics();
            WriteColor($"Database Stats: {stats.TableCount} tables, {stats.TotalPageCount} pages, {stats.TotalRowCount} total rows, {stats.TotalSizeBytes:N0} bytes on disk", ConsoleColor.Cyan);

            var tasks = db.Select<TaskItem>().ToList();
            if (tasks.Count == 0)
            {
                Console.WriteLine("No tasks found.");
                return;
            }

            Console.WriteLine("\n  ID | Status | Priority | Title                                    | Decrypted AES-256 Secret");
            Console.WriteLine("  ---+--------+----------+------------------------------------------+-------------------------");
            foreach (var t in tasks)
            {
                string status = t.Done ? "[DONE]" : "[TODO]";
                Console.WriteLine($"  {t.Id,2} | {status} | {t.Priority,8} | {t.Title,-40} | {t.SecretToken}");
            }
        }

        private static void AddInteractiveTask()
        {
            Console.Write("Task Title: ");
            string title = Console.ReadLine()?.Trim();
            Console.Write("Priority (1-10, default 5): ");
            if (!int.TryParse(Console.ReadLine(), out int priority))
                priority = 5;
            Console.Write("Secret Token (will be encrypted with AES-256-GCM on disk): ");
            string secret = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(secret))
                secret = "secret-" + Guid.NewGuid().ToString("N")[..6];

            using var db = OpenActiveDb();
            var proj = db.First<Project>();
            var newTask = new TaskItem
            {
                Title = title,
                Priority = priority,
                Done = false,
                SecretToken = secret,
                Notes = "<p>Created via interactive CLI</p>",
                ProjectId = proj?.Id ?? 1
            };

            db.Insert(newTask);
            WriteColor($"Inserted Task #{newTask.Id} ('{newTask.Title}')!", ConsoleColor.Green);
        }

        private static void ToggleInteractiveTask()
        {
            ListInteractiveTasks();
            Console.Write("\nEnter Task ID to toggle Done status: ");
            if (!int.TryParse(Console.ReadLine(), out int id))
                return;

            using var db = OpenActiveDb();
            var task = db.Find<TaskItem>(t => t.Id == id);
            if (task == null)
            {
                WriteColor($"Task #{id} not found.", ConsoleColor.Yellow);
                return;
            }

            task.Done = !task.Done;
            db.Update(task);
            WriteColor($"Updated Task #{task.Id}: Done = {task.Done}", ConsoleColor.Green);
        }

        private static void QueryInteractiveTasks()
        {
            Console.Write("Minimum Priority (e.g. 5): ");
            if (!int.TryParse(Console.ReadLine(), out int minP)) minP = 1;
            Console.Write("Maximum Priority (e.g. 10): ");
            if (!int.TryParse(Console.ReadLine(), out int maxP)) maxP = 10;

            using var db = OpenActiveDb();
            var sw = Stopwatch.StartNew();
            var matches = db.Query<TaskItem>(t => t.Priority >= minP && t.Priority <= maxP).ToList();
            sw.Stop();

            WriteColor($"Found {matches.Count} task(s) with Priority between {minP} and {maxP} in {sw.Elapsed.TotalMilliseconds:F2} ms (using B-Tree Index):", ConsoleColor.Cyan);
            foreach (var t in matches)
            {
                Console.WriteLine($"  * #{t.Id} [Priority {t.Priority}] {t.Title} (Done: {t.Done})");
            }
        }

        private static void DeleteInteractiveTask()
        {
            ListInteractiveTasks();
            Console.Write("\nEnter Task ID to delete: ");
            if (!int.TryParse(Console.ReadLine(), out int id))
                return;

            using var db = OpenActiveDb();
            var task = db.Find<TaskItem>(t => t.Id == id);
            if (task == null)
            {
                WriteColor($"Task #{id} not found.", ConsoleColor.Yellow);
                return;
            }

            if (db.Delete(task))
                WriteColor($"Deleted Task #{id}.", ConsoleColor.Green);
        }

        private static void DemoInteractiveTransactionRollback()
        {
            using var db = OpenActiveDb();
            int beforeCount = db.Count<TaskItem>();
            Console.WriteLine($"Row count before transaction: {beforeCount}");

            using (var tx = db.BeginTransaction())
            {
                Console.WriteLine("  -> Beginning transaction...");
                db.Insert(new TaskItem { Title = "Temporary Ghost Task 1", Priority = 99 });
                db.Insert(new TaskItem { Title = "Temporary Ghost Task 2", Priority = 99 });
                Console.WriteLine($"  -> Row count inside uncommitted transaction: {db.Count<TaskItem>()}");
                Console.WriteLine("  -> Calling tx.Rollback()...");
                tx.Rollback();
            }

            int afterCount = db.Count<TaskItem>();
            WriteColor($"Row count after Rollback(): {afterCount} (all uncommitted changes cleanly reverted!)", ConsoleColor.Green);
        }

        private static void InspectRawDatabaseFiles()
        {
            if (!Directory.Exists(ActiveDbFolder))
            {
                Console.WriteLine("Database directory does not exist yet.");
                return;
            }

            var files = Directory.GetFiles(ActiveDbFolder);
            WriteColor($"Files in {ActiveDbFolder} ({files.Length} files):", ConsoleColor.Cyan);
            for (int i = 0; i < files.Length; i++)
            {
                var fi = new FileInfo(files[i]);
                Console.WriteLine($"  [{i + 1}] {fi.Name} ({fi.Length:N0} bytes)");
            }

            Console.Write("\nEnter file number to view raw contents on disk (or Enter to skip): ");
            if (int.TryParse(Console.ReadLine(), out int idx) && idx >= 1 && idx <= files.Length)
            {
                string selectedFile = files[idx - 1];
                byte[] bytes = File.ReadAllBytes(selectedFile);
                Console.WriteLine($"\n--- Raw Disk Contents of {Path.GetFileName(selectedFile)} ---");
                if (currentFormat == StorageFormat.Binary || currentPassword != null)
                {
                    Console.WriteLine($"[Binary / Encrypted Payload - {bytes.Length} bytes]");
                    Console.WriteLine(Convert.ToHexString(bytes.Take(Math.Min(128, bytes.Length)).ToArray()));
                }
                else
                {
                    Console.WriteLine(File.ReadAllText(selectedFile));
                }
                Console.WriteLine("---------------------------------------------------");
            }
        }

        private static void ConvertInteractiveDatabaseFormat()
        {
            Console.Write("Select target format (xml / json / binary): ");
            string fmt = Console.ReadLine()?.Trim();
            if (!Enum.TryParse<StorageFormat>(fmt, ignoreCase: true, out var targetFormat))
            {
                WriteColor("Invalid format. Choose xml, json, or binary.", ConsoleColor.Yellow);
                return;
            }

            int converted = XodMigrator.ConvertDatabase(ActiveDbFolder, ActiveDbFolder, targetFormat, currentPassword);
            currentFormat = targetFormat;
            WriteColor($"Successfully migrated {converted} files in-place to {currentFormat} format!", ConsoleColor.Green);
        }

        private static void ToggleInteractiveEncryption()
        {
            using var db = OpenActiveDb();
            if (currentPassword == null)
            {
                Console.Write("Enter new password to encrypt the database: ");
                string pwd = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(pwd)) return;

                db.Secure(pwd);
                currentPassword = pwd;
                WriteColor("Database encrypted with AES-256-GCM + PBKDF2-SHA256!", ConsoleColor.Green);
            }
            else
            {
                db.Loose(currentPassword);
                currentPassword = null;
                WriteColor("Database decrypted back to plain storage format!", ConsoleColor.Green);
            }
        }

        private static void RunGuidedTour()
        {
            string tourDir = Path.Combine(PlaygroundRoot, "guided_tour");
            if (Directory.Exists(tourDir))
                Directory.Delete(tourDir, recursive: true);

            WriteHeader("XOD++ Automated End-to-End Feature Tour");

            string xmlDbPath = Path.Combine(tourDir, "xml_db", "bookstore.xod");
            using (var db = new XodContext(xmlDbPath))
            {
                // 1. Triggers
                db.BeforeAction += (_, e) => Console.WriteLine($"  [Trigger:Before] {e.Action} on {e.Type.Name}");

                // 2. Strongly-typed Insert<T>, Convention Relationships, Idempotent SHA256 Hashing & [Required] Zero/False
                Console.WriteLine("\n1. Inserting Author & Books (Convention ForeignKey, Strongly-Typed Insert<T>, Idempotent SHA256)...");
                Author author = db.Insert(new Author
                {
                    Name = "Mohammed Sallam",
                    Email = "mhsallam@example.com",
                    PasswordHash = "MySuperSecretPass123!"
                });

                var loadedAuthor = db.Find<Author>(a => a.Id == author.Id);
                string initialHash = loadedAuthor.PasswordHash;
                loadedAuthor.Name = "Mohammed H. Sallam";
                db.Update(loadedAuthor); // Updating Author must NOT double-hash PasswordHash!
                var reloadedAuthor = db.Find<Author>(a => a.Id == author.Id);
                WriteColor($"   Author SHA256 Hash Idempotent Across Update: {initialHash == reloadedAuthor.PasswordHash} (VerifyHash: {CryptoHelper.VerifyHash("MySuperSecretPass123!", reloadedAuthor.PasswordHash, CryptoMethod.SHA256)})", ConsoleColor.Green);

                Book book1 = db.Insert(new Book
                {
                    Title = "XOD Database Guide",
                    Isbn = "978-1-00000-001",
                    Price = 29.99m,
                    StockCount = 0, // [Required] int = 0 is valid and explicitly persisted
                    PublishedDate = new DateOnly(2016, 5, 1),
                    Author = reloadedAuthor,
                    Location = new WarehouseLocation { City = "Riyadh", Aisle = "B4" },
                    Tags = new List<string> { "xml", "database", "orm" },
                    Specs = new Dictionary<string, string> { ["Edition"] = "1st", ["Format"] = "PDF" }
                });

                Book book2 = db.Insert(new Book
                {
                    Title = "XOD++ Next-Gen Architecture",
                    Isbn = "978-1-00000-002",
                    Price = 49.99m,
                    StockCount = 25,
                    PublishedDate = new DateOnly(2026, 10, 27),
                    Author = reloadedAuthor,
                    Location = new WarehouseLocation { City = "Riyadh", Aisle = "A1" },
                    Tags = new List<string> { "dotnet9", "btree", "aes-gcm" },
                    Specs = new Dictionary<string, string> { ["Edition"] = "2nd", ["Engine"] = "Multi-Format" }
                });

                // 3. Query with B-Tree Index & verify .xidx sidecar file
                Console.WriteLine("\n2. Querying Books with Price >= 40 using Expression Tree Planner & Persistent .xidx Index...");
                var premiumBooks = db.Query<Book>(b => b.Price >= 40m).ToList();
                foreach (var b in premiumBooks)
                {
                    Console.WriteLine($"   Found Book #{b.Id}: '{b.Title}' (${b.Price}, Stock: {b.StockCount}) by {b.Author?.Name} @ {b.Location?.City}/{b.Location?.Aisle}");
                }
                int xidxCount = Directory.GetFiles(Path.GetDirectoryName(xmlDbPath), "*.xidx").Length;
                WriteColor($"   Persisted .xidx Sidecar B-Tree Index Files on Disk: {xidxCount}", ConsoleColor.Green);

                // 4. Test [UniqueKey] validation
                Console.WriteLine("\n3. Verifying [UniqueKey] enforcement on Author.Email...");
                try
                {
                    db.Insert(new Author { Name = "Duplicate User", Email = "mhsallam@example.com" });
                }
                catch (ReservedUniqueKeyException ex)
                {
                    WriteColor($"   Correctly caught ReservedUniqueKeyException: {ex.Message}", ConsoleColor.Green);
                }

                // 5. Test ACID Transaction Rollback
                Console.WriteLine("\n4. Testing ACID Transaction Rollback...");
                using (var tx = db.BeginTransaction())
                {
                    db.Insert(new Book { Title = "Uncommitted Draft Book", Price = 999m, Author = author });
                    Console.WriteLine($"   Book count inside transaction: {db.Count<Book>()}");
                    tx.Rollback();
                }
                WriteColor($"   Book count after tx.Rollback(): {db.Count<Book>()}", ConsoleColor.Green);
            }

            // 6. Cross-Engine Migration (XML -> JSON -> Binary)
            Console.WriteLine("\n5. Migrating XML database -> JSON database -> Binary database...");
            string xmlDir = Path.Combine(tourDir, "xml_db");
            string jsonDir = Path.Combine(tourDir, "json_db");
            string binDir = Path.Combine(tourDir, "bin_db");

            XodMigrator.ConvertDatabase(xmlDir, jsonDir, StorageFormat.Json);
            XodMigrator.ConvertDatabase(jsonDir, binDir, StorageFormat.Binary);

            using (var jsonDb = new XodContext(Path.Combine(jsonDir, "bookstore.xod"), options: new DatabaseOptions { StorageFormat = StorageFormat.Json }))
            using (var binDb = new XodContext(Path.Combine(binDir, "bookstore.xod"), options: new DatabaseOptions { StorageFormat = StorageFormat.Binary }))
            {
                var fromJson = jsonDb.Find<Book>(b => b.Id == 2);
                var fromBin = binDb.Find<Book>(b => b.Id == 2);

                WriteColor($"   JSON Engine Read  : {fromJson.Title} (Edition: {fromJson.Specs["Edition"]}, Date: {fromJson.PublishedDate})", ConsoleColor.Green);
                WriteColor($"   Binary Engine Read: {fromBin.Title} (Edition: {fromBin.Specs["Edition"]}, Date: {fromBin.PublishedDate})", ConsoleColor.Green);

                Console.WriteLine($"\n   Storage Footprint Comparison:");
                Console.WriteLine($"     * XML Engine Total Size   : { new XodContext(xmlDbPath).GetStatistics().TotalSizeBytes,8:N0} bytes");
                Console.WriteLine($"     * JSON Engine Total Size  : { jsonDb.GetStatistics().TotalSizeBytes,8:N0} bytes");
                Console.WriteLine($"     * Binary Engine Total Size: { binDb.GetStatistics().TotalSizeBytes,8:N0} bytes (Brotli compressed)");
            }
        }

        private static void RunBenchmark()
        {
            const int recordCount = 250;
            WriteHeader($"XOD++ Storage Engine Benchmark ({recordCount} entities per engine)");

            string benchRoot = Path.Combine(PlaygroundRoot, "benchmark");
            if (Directory.Exists(benchRoot))
                Directory.Delete(benchRoot, recursive: true);

            // Warm up JIT once so the first engine (Xml) isn't penalized by one-time JIT compilation
            using (var warmupDb = new XodContext(Path.Combine(benchRoot, "warmup", "w.xod")))
            {
                warmupDb.Insert(new TaskItem { Title = "Warmup", Priority = 1, Done = false, SecretToken = "w" });
                _ = warmupDb.Find<TaskItem>(t => t.Id == 1);
            }

            Console.WriteLine("  Engine  | Single Immediate (250) | Buffered + Flush (250) | Batch InsertRange (250) | Point Lookup | Range Query | Disk Size");
            Console.WriteLine("  --------+------------------------+------------------------+-------------------------+--------------+-------------+----------");

            foreach (var format in new[] { StorageFormat.Xml, StorageFormat.Json, StorageFormat.Binary })
            {
                string dbPath = Path.Combine(benchRoot, format.ToString(), "bench.xod");
                using var db = new XodContext(dbPath, options: new DatabaseOptions { StorageFormat = format });

                var swInsert = Stopwatch.StartNew();
                for (int i = 1; i <= recordCount; i++)
                {
                    db.Insert(new TaskItem
                    {
                        Title = $"Benchmark Task Item #{i}",
                        Priority = i % 100,
                        Done = (i % 2 == 0),
                        Notes = "Benchmark payload text for XOD++ storage comparison",
                        SecretToken = $"secret-{i}"
                    });
                }
                swInsert.Stop();

                string bufferedDbPath = Path.Combine(benchRoot, format + "_buffered", "bench.xod");
                using var bufferedDb = new XodContext(bufferedDbPath, options: new DatabaseOptions
                {
                    StorageFormat = format,
                    Durability = DurabilityMode.Buffered
                });
                var swBuffered = Stopwatch.StartNew();
                for (int i = 1; i <= recordCount; i++)
                {
                    bufferedDb.Insert(new TaskItem
                    {
                        Title = $"Benchmark Task Item #{i}",
                        Priority = i % 100,
                        Done = (i % 2 == 0),
                        Notes = "Benchmark payload text for XOD++ storage comparison",
                        SecretToken = $"secret-{i}"
                    });
                }
                bufferedDb.Flush();
                swBuffered.Stop();

                string batchDbPath = Path.Combine(benchRoot, format + "_batch", "bench.xod");
                using var batchDb = new XodContext(batchDbPath, options: new DatabaseOptions { StorageFormat = format });
                var batchItems = Enumerable.Range(1, recordCount).Select(i => new TaskItem
                {
                    Title = $"Benchmark Task Item #{i}",
                    Priority = i % 100,
                    Done = (i % 2 == 0),
                    Notes = "Benchmark payload text for XOD++ storage comparison",
                    SecretToken = $"secret-{i}"
                }).ToList();

                var swBatch = Stopwatch.StartNew();
                batchDb.InsertRange(batchItems);
                swBatch.Stop();

                db.ClearCache();

                var swPoint = Stopwatch.StartNew();
                var item = db.Find<TaskItem>(t => t.Id == recordCount / 2);
                swPoint.Stop();

                var swRange = Stopwatch.StartNew();
                var range = db.Query<TaskItem>(t => t.Priority >= 40 && t.Priority <= 60).ToList();
                swRange.Stop();

                var stats = db.GetStatistics();
                Console.WriteLine($"  {format,-7} | {swInsert.ElapsedMilliseconds,19} ms | {swBuffered.ElapsedMilliseconds,19} ms | {swBatch.ElapsedMilliseconds,20} ms | {swPoint.Elapsed.TotalMilliseconds,9:F2} ms | {swRange.Elapsed.TotalMilliseconds,8:F2} ms | {stats.TotalSizeBytes,8:N0} B");
            }
        }

        private static void WriteHeader(string title)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("====================================================================================");
            Console.WriteLine($"  {title}");
            Console.WriteLine("====================================================================================");
            Console.ResetColor();
        }

        private static void WriteColor(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }
}
