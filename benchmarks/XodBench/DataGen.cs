using System.Text;

namespace XodBench;

/// <summary>Deterministic data shared by every engine (same seed, same values).</summary>
public static class DataGen
{
    private static readonly string[] Words =
    {
        "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do",
        "eiusmod", "tempor", "incididunt", "ut", "labore", "et", "dolore", "magna", "aliqua", "enim",
        "minim", "veniam", "quis", "nostrud", "exercitation", "ullamco", "laboris", "nisi", "aliquip", "ex",
        "ea", "commodo", "consequat", "duis", "aute", "irure", "reprehenderit", "voluptate", "velit", "esse",
        "cillum", "fugiat", "nulla", "pariatur", "excepteur", "sint", "occaecat", "cupidatat", "non", "proident",
        "warehouse", "invoice", "shipment", "riyadh", "jeddah", "dammam", "order", "customer", "supplier", "ledger"
    };

    public static readonly string[] Countries =
    {
        "SA", "AE", "EG", "JO", "KW", "QA", "BH", "OM", "MA", "TN",
        "US", "GB", "DE", "FR", "IN", "PK", "TR", "MY", "ID", "NG"
    };

    public const int PayloadPoolSize = 4096;
    public const int Categories = 200;
    public const int Statuses = 50;

    private static string[] payloadPool;

    /// <summary>
    /// 4,096 distinct, varied ~2.4 KB payloads. Rows reference the pool so 400k rows do not need
    /// ~2 GB of managed strings; no engine compresses, so on-disk bytes are identical either way.
    /// </summary>
    public static string[] PayloadPool()
    {
        if (payloadPool != null)
            return payloadPool;

        var rng = new Random(1234);
        var pool = new string[PayloadPoolSize];
        var sb = new StringBuilder(2600);
        for (int i = 0; i < pool.Length; i++)
        {
            sb.Clear();
            int target = 2300 + rng.Next(200);
            while (sb.Length < target)
            {
                sb.Append(Words[rng.Next(Words.Length)]);
                sb.Append(rng.Next(12) == 0 ? ". " : " ");
            }
            pool[i] = sb.ToString(0, target);
        }
        payloadPool = pool;
        return pool;
    }

    public static List<BenchRow> FlatRows(int count)
    {
        string[] pool = PayloadPool();
        var rows = new List<BenchRow>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new BenchRow
            {
                Sku = $"SKU-{i:D7}",
                CategoryId = (i % Categories) + 1,
                Price = (i % 500) * 1.25,
                Payload = pool[i % PayloadPoolSize]
            });
        }
        return rows;
    }

    public static BenchRow ExtraRow(int index) => new BenchRow
    {
        Sku = $"SKU-{index:D7}",
        CategoryId = 1,
        Price = 99.99,
        Payload = PayloadPool()[index % PayloadPoolSize]
    };

    public static List<Product> Products(int count)
    {
        var rng = new Random(77);
        var list = new List<Product>(count);
        for (int i = 1; i <= count; i++)
        {
            list.Add(new Product
            {
                Id = i,
                Sku = $"P-{i:D6}",
                Name = $"Product {Words[i % Words.Length]} {i}",
                Price = Math.Round(1 + rng.NextDouble() * 500, 2)
            });
        }
        return list;
    }

    /// <summary>Customer -> Orders -> Lines graph with explicit, engine-independent ids.</summary>
    public static List<Customer> CustomerGraphs(int customers, int ordersPerCustomer, int linesPerOrder, List<Product> products)
    {
        var rng = new Random(99);
        var baseDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<Customer>(customers);
        int orderId = 0, lineId = 0;
        for (int c = 1; c <= customers; c++)
        {
            var customer = new Customer
            {
                Id = c,
                Email = $"customer{c}@example.com",
                Name = $"Customer {c}",
                Country = Countries[c % Countries.Length],
                Orders = new List<Order>(ordersPerCustomer)
            };
            for (int o = 0; o < ordersPerCustomer; o++)
            {
                orderId++;
                var order = new Order
                {
                    Id = orderId,
                    CustomerId = c,
                    OrderDate = baseDate.AddMinutes(orderId),
                    Status = $"S{orderId % Statuses:D2}",
                    Lines = new List<OrderLine>(linesPerOrder)
                };
                for (int l = 0; l < linesPerOrder; l++)
                {
                    lineId++;
                    var p = products[rng.Next(products.Count)];
                    order.Lines.Add(new OrderLine
                    {
                        Id = lineId,
                        OrderId = orderId,
                        ProductId = p.Id,
                        Quantity = 1 + rng.Next(10),
                        UnitPrice = p.Price
                    });
                }
                customer.Orders.Add(order);
            }
            list.Add(customer);
        }
        return list;
    }
}
