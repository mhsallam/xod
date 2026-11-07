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
    }
}
