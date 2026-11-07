using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    // A three-level graph: Customer -> Orders -> OrderLines, the shape the benchmark's relational
    // suite uses. Named separately from the other fixtures so the intent is visible in test output.
    public class InvCustomer
    {
        public int Id { get; set; }
        public string Name { get; set; }

        [Children]
        public List<InvOrder> Orders { get; set; }
    }

    public class InvOrder
    {
        public int Id { get; set; }
        public int InvCustomerId { get; set; }
        public string Status { get; set; }

        [ParentKey(nameof(InvCustomerId))]
        public InvCustomer Customer { get; set; }

        [Children]
        public List<InvLine> Lines { get; set; }
    }

    public class InvLine
    {
        public int Id { get; set; }
        public int InvOrderId { get; set; }
        public double Amount { get; set; }

        [ParentKey(nameof(InvOrderId))]
        public InvOrder Order { get; set; }
    }

    /// <summary>
    /// Referential integrity under cascade delete. These assert exact row counts after the graph is
    /// deleted and the database is reopened cold, because "Delete returned true" is not the same as
    /// "the rows are gone".
    ///
    /// ISS-037: on JSON and Binary storage, deleting a shell entity — the case the engine documents
    /// ("Ensure full entity graph is loaded for cascade delete if caller passed a shell object") —
    /// deletes only the parent row. The child rows are left behind and Delete still returns true.
    /// Deleting an entity hydrated with include: "*" works. XML is unaffected either way.
    /// </summary>
    public class XodIntegrityTests : IDisposable
    {
        private const int OrdersPerCustomer = 5;
        private const int LinesPerOrder = 4;

        private readonly string tempRoot;

        public XodIntegrityTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodIntegrity_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        private string GetDbPath(string name) => Path.Combine(tempRoot, name, "data.xod");

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

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

        private static (int customers, int orders, int lines) CountRows(XodContext db) =>
            (db.Count<InvCustomer>(), db.Count<InvOrder>(), db.Count<InvLine>());

        /// <summary>
        /// Deleting a parent removes its orders and their lines, whichever way the caller obtained the
        /// entity. This is the shape that produced the orphan rows in the 40k benchmark run.
        /// </summary>
        [Theory]
        [InlineData(StorageFormat.Xml, true, 200)]
        [InlineData(StorageFormat.Xml, false, 200)]
        [InlineData(StorageFormat.Json, true, 200)]
        [InlineData(StorageFormat.Json, false, 200)]
        [InlineData(StorageFormat.Binary, true, 200)]
        [InlineData(StorageFormat.Binary, false, 200)]
        public void Cascade_Delete_Removes_Every_Child_Row_And_Survives_Reopen(StorageFormat format, bool loadedInFull, int customers)
        {
            string dbPath = GetDbPath($"cascade_{format}_{loadedInFull}");

            using (var db = new XodContext(dbPath, options: new DatabaseOptions { StorageFormat = format, PageSize = 8192 }))
            {
                db.InsertRange(BuildGraphs(customers));
                Assert.Equal((customers, customers * OrdersPerCustomer, customers * OrdersPerCustomer * LinesPerOrder), CountRows(db));
            }

            var doomed = Enumerable.Range(1, customers).Where(id => id % 3 == 0).ToList();
            using (var db = new XodContext(dbPath, options: new DatabaseOptions { StorageFormat = format, PageSize = 8192 }))
            {
                foreach (int id in doomed)
                {
                    var customer = loadedInFull
                        ? db.Find<InvCustomer>(c => c.Id == id, include: "*")
                        : db.Find<InvCustomer>(c => c.Id == id, include: "");
                    Assert.NotNull(customer);
                    Assert.True(db.Delete(customer));
                }
            }

            int expectedCustomers = customers - doomed.Count;
            int expectedOrders = expectedCustomers * OrdersPerCustomer;

            using (var db = new XodContext(dbPath, options: new DatabaseOptions { StorageFormat = format, PageSize = 8192 }))
            {
                Assert.Equal((expectedCustomers, expectedOrders, expectedOrders * LinesPerOrder), CountRows(db));

                int survivorId = Enumerable.Range(1, customers).First(id => id % 3 != 0);
                var survivor = db.Find<InvCustomer>(c => c.Id == survivorId, include: "*");
                Assert.NotNull(survivor);
                Assert.Equal(OrdersPerCustomer, survivor.Orders.Count);
                Assert.All(survivor.Orders, o => Assert.Equal(LinesPerOrder, o.Lines.Count));
            }
        }

        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        [InlineData(StorageFormat.Binary)]
        public void Repeated_Delete_Of_Same_Graph_Is_Idempotent_And_Leaves_No_Rows(StorageFormat format)
        {
            string dbPath = GetDbPath($"idempotent_{format}");
            const int customers = 60;

            using (var db = new XodContext(dbPath, options: new DatabaseOptions { StorageFormat = format, PageSize = 8192 }))
                db.InsertRange(BuildGraphs(customers));

            using (var db = new XodContext(dbPath, options: new DatabaseOptions { StorageFormat = format, PageSize = 8192 }))
            {
                var target = db.Find<InvCustomer>(c => c.Id == 7, include: "");
                Assert.True(db.Delete(target));
                Assert.False(db.Delete(target));   // second delete is a no-op, not a cascade twice

                Assert.Equal(customers - 1, db.Count<InvCustomer>());
                Assert.Equal((customers - 1) * OrdersPerCustomer, db.Count<InvOrder>());
                Assert.Equal((customers - 1) * OrdersPerCustomer * LinesPerOrder, db.Count<InvLine>());
            }
        }
    }
}
