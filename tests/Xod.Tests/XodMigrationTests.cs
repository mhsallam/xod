using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xod;
using Xunit;

// The fixture was written by XOD v1.2.5, which keys its files by Type.FullName. These types therefore
// live in the global namespace so the names match the stored ones exactly - that is the point of the
// test: a real 1.x database, opened by the current code.
// A table no fixture has, so the format test sees the page a context writes.
public class FormatProbe
{
    [PrimaryKey] public int Id { get; set; }
    public string Name { get; set; }
}

public class FixtureCustomer
{
    public int Id { get; set; }
    public string Name { get; set; }
    [Children] public List<FixtureOrder> Orders { get; set; }
}

public class FixtureOrder
{
    public int Id { get; set; }
    public int FixtureCustomerId { get; set; }
    public string Status { get; set; }
    [ParentKey(nameof(FixtureCustomerId))] public FixtureCustomer Customer { get; set; }
    [Children] public List<FixtureLine> Lines { get; set; }
}

public class FixtureLine
{
    public int Id { get; set; }
    public int FixtureOrderId { get; set; }
    public double Amount { get; set; }
    [ParentKey(nameof(FixtureOrderId))] public FixtureOrder Order { get; set; }
}

public class FixtureProduct
{
    [PrimaryKey] public int Id { get; set; }
    [UniqueKey] public string Sku { get; set; }
    public string Name { get; set; }
    public double Price { get; set; }
}

namespace Xod.Tests
{
    /// <summary>
    /// Gate layer 6 of TEST-PLAN-2.0: a database written by the released v1.2.5 code must open in 2.0,
    /// answer the same questions, and survive conversion to the other formats with its rows, its
    /// relationships and its unique key intact. The fixture is a committed 1.x artifact, not something
    /// the current code produced, so the test cannot pass by construction.
    /// </summary>
    public class XodMigrationTests : IDisposable
    {
        private readonly string tempRoot;

        public XodMigrationTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodMigration_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private static string FixtureDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "tests", "Xod.Tests", "fixtures", "v1.2.5-xml");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("fixture v1.2.5-xml not found above " + AppContext.BaseDirectory);
        }

        private string CopyFixture(string name)
        {
            string target = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(FixtureDirectory()))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            return Path.Combine(target, "fixture.xod");
        }

        private static string Canonical(XodContext db)
        {
            var customers = db.Select<FixtureCustomer>(0, 200).OrderBy(c => c.Id)
                .Select(c => new { c.Id, c.Name }).ToList();
            var orders = db.Select<FixtureOrder>(0, 200).OrderBy(o => o.Id)
                .Select(o => new { o.Id, o.FixtureCustomerId, o.Status }).ToList();
            var lines = db.Select<FixtureLine>(0, 500).OrderBy(l => l.Id)
                .Select(l => new { l.Id, l.FixtureOrderId, Amount = Math.Round(l.Amount, 6) }).ToList();
            var products = db.Select<FixtureProduct>(0, 200).OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.Sku, p.Name, Price = Math.Round(p.Price, 6) }).ToList();

            var graph = db.Find<FixtureCustomer>(c => c.Id == 4, include: "*");
            object graphShape = graph == null ? null : new
            {
                graph.Id,
                graph.Name,
                Orders = graph.Orders?.OrderBy(o => o.Id).Select(o => new
                {
                    o.Id,
                    o.Status,
                    Lines = o.Lines?.OrderBy(l => l.Id).Select(l => new { l.Id, Amount = Math.Round(l.Amount, 6) }).ToList(),
                }).ToList(),
            };

            var byStatus = db.Query<FixtureOrder>(o => o.Status == "S03").OrderBy(o => o.Id)
                .Select(o => o.Id).ToList();

            return JsonSerializer.Serialize(new
            {
                Counts = new
                {
                    Customers = db.Count<FixtureCustomer>(),
                    Orders = db.Count<FixtureOrder>(),
                    Lines = db.Count<FixtureLine>(),
                    Products = db.Count<FixtureProduct>(),
                },
                Customers = customers,
                Orders = orders,
                Lines = lines,
                Products = products,
                Graph = graphShape,
                ByStatus = byStatus,
            });
        }

        [Fact]
        public void A_v1_2_5_database_opens_and_answers_the_same_questions()
        {
            string path = CopyFixture("open");

            using var db = new XodContext(path);
            Assert.Equal(10, db.Count<FixtureCustomer>());
            Assert.Equal(50, db.Count<FixtureOrder>());
            Assert.Equal(150, db.Count<FixtureLine>());
            Assert.Equal(50, db.Count<FixtureProduct>());

            var graph = db.Find<FixtureCustomer>(c => c.Id == 4, include: "*");
            Assert.NotNull(graph);
            Assert.Equal(5, graph.Orders?.Count ?? 0);
            Assert.All(graph.Orders, o => Assert.Equal(3, o.Lines?.Count ?? 0));

            var product = db.Find<FixtureProduct>(p => p.Sku == "SKU-007");
            Assert.NotNull(product);
            Assert.Equal("Product 7", product.Name);
        }

        [Fact]
        public void Migration_to_every_format_preserves_the_database()
        {
            string xmlPath = CopyFixture("source");
            string baseline;
            using (var db = new XodContext(xmlPath))
                baseline = Canonical(db);

            foreach (var (format, folder) in new[] { (StorageFormat.Json, "json"), (StorageFormat.Binary, "binary") })
            {
                string target = Path.Combine(tempRoot, folder);
                int converted = XodMigrator.ConvertDatabase(Path.GetDirectoryName(xmlPath)!, target, format);
                Assert.True(converted > 0, $"nothing converted for {format}");

                string targetFile = Directory.GetFiles(target)
                    .First(f => f.EndsWith(".xod", StringComparison.OrdinalIgnoreCase));

                using var migrated = new XodContext(targetFile);
                Assert.Equal(baseline, Canonical(migrated));

                // the unique key has to survive the conversion, not just the rows
                Assert.ThrowsAny<Exception>(() => migrated.Insert(new FixtureProduct
                {
                    Id = 999,
                    Sku = "SKU-007",
                    Name = "duplicate",
                    Price = 1,
                }));
            }
        }

        // ISS-093: XodMigrator records the format it converts to in the database file, but the file keeps its name (a
        // .xod converted to Binary), and nothing read the record: a later context wrote new files in the encoding the
        // extension picks. Unless a format is given, the database's own is written now.
        [Fact]
        public void A_converted_database_is_written_in_the_format_it_was_converted_to()
        {
            string xmlPath = CopyFixture("convert-format");
            string target = Path.Combine(tempRoot, "convert-format-binary");
            XodMigrator.ConvertDatabase(Path.GetDirectoryName(xmlPath)!, target, StorageFormat.Binary);
            string converted = Directory.GetFiles(target).Single(f => f.EndsWith(".xod", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(StorageFormat.Binary, NewPageFormat(converted, null, out StorageFormat seen));
            Assert.Equal(StorageFormat.Binary, seen);
            // Options that give no format leave it to the database too.
            Assert.Equal(StorageFormat.Binary, NewPageFormat(converted, new DatabaseOptions { LockDatabase = false }, out _));
            // A format given is the one written (PLAN-046 section 3: a folder can hold all three).
            Assert.Equal(StorageFormat.Json, NewPageFormat(converted, new DatabaseOptions { StorageFormat = StorageFormat.Json }, out seen));
            Assert.Equal(StorageFormat.Json, seen);
            // ... and leaves the database's record as it was.
            Assert.Equal(StorageFormat.Binary, NewPageFormat(converted, null, out _));
        }

        [Fact]
        public void A_database_converted_in_place_stays_in_its_new_format()
        {
            string xmlPath = CopyFixture("in-place-format");
            string dir = Path.GetDirectoryName(xmlPath)!;
            XodMigrator.ConvertDatabase(dir, dir, StorageFormat.Binary);
            Assert.Equal(StorageFormat.Binary, NewPageFormat(xmlPath, null, out _));
        }

        // A new database whose options give no format takes the one its extension picks (PLAN-046 section 2); it took
        // XML whatever the extension.
        [Fact]
        public void A_new_database_takes_its_extension_s_format_when_none_is_given()
        {
            string path = Path.Combine(tempRoot, "new-binary", "data.xodb");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Assert.Equal(StorageFormat.Binary, NewPageFormat(path, new DatabaseOptions { InitialCreate = true }, out StorageFormat seen));
            Assert.Equal(StorageFormat.Binary, seen);
            // Its database file records the format it was created in, which the next context takes.
            Assert.Equal(StorageFormat.Binary, NewPageFormat(path, new DatabaseOptions { InitialCreate = true }, out seen));
            Assert.Equal(StorageFormat.Binary, seen);
        }

        // The review of TASK-071: with no format given and no record, the format came from the path given, not from
        // the database file it names: a folder named "shop.bin" got a Binary data.xod, and a database opened through
        // its folder was written in XML whatever its file's extension.
        [Fact]
        public void The_database_file_s_extension_picks_the_format_not_the_path_s()
        {
            string folder = Path.Combine(tempRoot, "shop.bin");
            Directory.CreateDirectory(folder);
            Assert.Equal(StorageFormat.Xml, NewPageFormat(folder, null, out StorageFormat seen));
            Assert.Equal(StorageFormat.Xml, seen);

            string binary = Path.Combine(tempRoot, "unrecorded", "data.xodb");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            Assert.Equal(StorageFormat.Xml, NewPageFormat(binary, new DatabaseOptions { StorageFormat = StorageFormat.Xml }, out _));
            SetRecordedFormat(binary, null);
            Assert.Equal(StorageFormat.Binary, NewPageFormat(Path.GetDirectoryName(binary)!, null, out seen));
            Assert.Equal(StorageFormat.Binary, seen);
        }

        // A record is a format's name; a number or a list of names, which Enum.TryParse takes, is none, and the
        // extension decides (review of TASK-071: both were read as Json).
        [Theory]
        [InlineData("1")]
        [InlineData("Xml, Json")]
        public void A_record_that_names_no_format_leaves_it_to_the_extension(string record)
        {
            string path = Path.Combine(tempRoot, "odd-record-" + Guid.NewGuid().ToString("N")[..6], "data.xodb");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Assert.Equal(StorageFormat.Xml, NewPageFormat(path, new DatabaseOptions { StorageFormat = StorageFormat.Xml }, out _));
            SetRecordedFormat(path, record);
            Assert.Equal(StorageFormat.Binary, NewPageFormat(path, null, out _));
        }

        // A .xodb database that records Xml (created in that format) is written in XML without options, and its rows
        // read back through XML's own paths (review of TASK-071: untested).
        [Fact]
        public void A_database_is_written_as_its_record_says_whatever_its_extension()
        {
            string path = Path.Combine(tempRoot, "xml-in-xodb", "data.xodb");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Assert.Equal(StorageFormat.Xml, NewPageFormat(path, new DatabaseOptions { StorageFormat = StorageFormat.Xml }, out _));
            Assert.Equal(StorageFormat.Xml, NewPageFormat(path, null, out StorageFormat seen));
            Assert.Equal(StorageFormat.Xml, seen);
            using var db = new XodContext(path);
            Assert.Equal(2, db.Count<FormatProbe>());
            Assert.Equal("probe", db.Find<FormatProbe>(p => p.Id == 2)?.Name);
        }

        // Sets (or, with null, takes out) the Format a database file records; the file must be in XML.
        private static void SetRecordedFormat(string databaseFile, string record)
        {
            string text = File.ReadAllText(databaseFile);
            Assert.Contains("Format=\"", text);
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+Format=\"[^\"]*\"", record == null ? "" : " Format=\"" + record + "\"");
            File.WriteAllText(databaseFile, text);
        }

        // The format of the page file a context writes for a new row of FormatProbe (a new page, or the one it
        // rewrites), read from the file's own first bytes; seen: the format the context reports. The path names a
        // database file or its folder.
        private static StorageFormat NewPageFormat(string path, DatabaseOptions options, out StorageFormat seen)
        {
            string dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
            var before = Directory.GetFiles(dir, "*.xpag").ToDictionary(f => f, f => Convert.ToBase64String(File.ReadAllBytes(f)));
            using (var db = new XodContext(path, options: options))
            {
                seen = db.StorageFormat;
                db.Insert(new FormatProbe { Id = db.Count<FormatProbe>() + 1, Name = "probe" });
            }
            string page = Directory.GetFiles(dir, "*.xpag")
                .Single(f => !before.TryGetValue(f, out string bytes) || bytes != Convert.ToBase64String(File.ReadAllBytes(f)));
            byte[] head = File.ReadAllBytes(page).Take(8).ToArray();
            if (head.Length >= 4 && head[0] == (byte)'X' && head[1] == (byte)'O' && head[2] == (byte)'D' && head[3] == (byte)'B')
                return StorageFormat.Binary;
            string text = System.Text.Encoding.UTF8.GetString(head).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            return text.StartsWith("{", StringComparison.Ordinal) ? StorageFormat.Json : StorageFormat.Xml;
        }
    }
}
