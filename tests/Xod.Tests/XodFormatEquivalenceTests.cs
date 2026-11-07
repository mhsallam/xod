using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// Gate layer 7 of TEST-PLAN-2.0: XML, JSON and Binary must be the same database wearing different
    /// files. The same script - insert a graph, read it back by id, by index, by page and with include,
    /// update one row, cascade-delete one graph, close and reopen - is run against each format and the
    /// results are serialised to one canonical string. The three strings have to be identical, cold and
    /// warm, which is a stronger statement than "each format round-trips its own data".
    /// </summary>
    public class XodFormatEquivalenceTests : IDisposable
    {
        private const int Customers = 40;
        private const int OrdersPerCustomer = 5;
        private const int LinesPerOrder = 3;

        private readonly string tempRoot;

        public XodFormatEquivalenceTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodFormatEq_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string GetDbPath(string name) => Path.Combine(tempRoot, name, "data.xod");

        private static List<InvCustomer> BuildGraphs(int customers)
        {
            var list = new List<InvCustomer>(customers);
            for (int c = 1; c <= customers; c++)
            {
                var orders = new List<InvOrder>(OrdersPerCustomer);
                for (int o = 1; o <= OrdersPerCustomer; o++)
                {
                    var lines = new List<InvLine>(LinesPerOrder);
                    for (int l = 1; l <= LinesPerOrder; l++)
                    {
                        lines.Add(new InvLine
                        {
                            Id = (c - 1) * OrdersPerCustomer * LinesPerOrder + (o - 1) * LinesPerOrder + l,
                            Amount = (c + o + l) * 1.5
                        });
                    }

                    orders.Add(new InvOrder
                    {
                        Id = (c - 1) * OrdersPerCustomer + o,
                        Status = $"S{o:D2}",
                        Lines = lines
                    });
                }

                list.Add(new InvCustomer { Id = c, Name = $"Customer {c:D5}", Orders = orders });
            }

            return list;
        }

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        private static string Canonical(XodContext db)
        {
            var customers = db.Select<InvCustomer>(0, Customers + 10).OrderBy(c => c.Id)
                .Select(c => new { c.Id, c.Name }).ToList();

            var orders = db.Select<InvOrder>(0, Customers * OrdersPerCustomer + 10).OrderBy(o => o.Id)
                .Select(o => new { o.Id, o.InvCustomerId, o.Status }).ToList();

            var lines = db.Select<InvLine>(0, Customers * OrdersPerCustomer * LinesPerOrder + 10).OrderBy(l => l.Id)
                .Select(l => new { l.Id, l.InvOrderId, Amount = Math.Round(l.Amount, 6) }).ToList();

            var byStatus = db.Query<InvOrder>(o => o.Status == "S02").OrderBy(o => o.Id)
                .Select(o => new { o.Id, o.InvCustomerId, o.Status }).ToList();

            var graph = db.Find<InvCustomer>(c => c.Id == 7, include: "*");
            object graphShape = graph == null
                ? null
                : new
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

            var page = db.Select<InvOrder>(3, 5).OrderBy(o => o.Id).Select(o => o.Id).ToList();

            return JsonSerializer.Serialize(new
            {
                Counts = new { Customers = db.Count<InvCustomer>(), Orders = db.Count<InvOrder>(), Lines = db.Count<InvLine>() },
                LastCustomer = db.Last<InvCustomer>()?.Id,
                Customers = customers,
                Orders = orders,
                Lines = lines,
                ByStatus = byStatus,
                Graph = graphShape,
                Page = page,
            }, Json);
        }

        private string RunScript(StorageFormat format, string name)
        {
            string path = GetDbPath(name);
            var options = new DatabaseOptions
            {
                StorageFormat = format,
                InitialCreate = true,
                EnableIndexing = true,
                Durability = DurabilityMode.Buffered,
            };

            string afterInsert;
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(BuildGraphs(Customers));
                db.Flush();
                afterInsert = Canonical(db);

                // one update and one cascade delete: the two operations whose results have diverged
                // between formats before (ISS-037), so they belong in the equivalence fingerprint
                var order = db.Find<InvOrder>(o => o.Id == 3);
                order.Status = "X01";
                db.Update(order);

                var doomed = db.Find<InvCustomer>(c => c.Id == 2, include: "*");
                db.Delete(doomed);
                db.Flush();
            }

            // cold reopen: nothing above may have been satisfied only by the in-memory state
            using (var reopened = new XodContext(path, options: options))
            {
                return afterInsert + "\n---\n" + Canonical(reopened);
            }
        }

        [Fact]
        public void Xml_json_and_binary_return_identical_results_for_the_same_script()
        {
            string xml = RunScript(StorageFormat.Xml, "xml");
            string json = RunScript(StorageFormat.Json, "json");
            string binary = RunScript(StorageFormat.Binary, "binary");

            Assert.Equal(xml, json);
            Assert.Equal(xml, binary);
        }

        [Fact]
        public void Every_format_reports_the_same_counts_after_cascade_delete()
        {
            foreach (StorageFormat format in new[] { StorageFormat.Xml, StorageFormat.Json, StorageFormat.Binary })
            {
                string path = GetDbPath($"counts-{format}");
                var options = new DatabaseOptions
                {
                    StorageFormat = format,
                    InitialCreate = true,
                    EnableIndexing = true,
                    Durability = DurabilityMode.Buffered,
                };

                using var db = new XodContext(path, options: options);
                db.InsertRange(BuildGraphs(10));
                db.Flush();

                var doomed = db.Find<InvCustomer>(c => c.Id == 3, include: "*");
                Assert.True(db.Delete(doomed));
                db.Flush();

                Assert.Equal(9, db.Count<InvCustomer>());
                Assert.Equal(9 * OrdersPerCustomer, db.Count<InvOrder>());
                Assert.Equal(9 * OrdersPerCustomer * LinesPerOrder, db.Count<InvLine>());
                Assert.Null(db.Find<InvCustomer>(c => c.Id == 3));
            }
        }
    }
}
