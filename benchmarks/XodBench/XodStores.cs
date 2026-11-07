using Xod;

namespace XodBench;

internal static class BenchEnv
{
    // 512 KB is the historical default; XOD_PAGE_SIZE lets a page-size sweep override it.
    public static int PageSize { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("XOD_PAGE_SIZE"), out var kb) && kb > 0
            ? kb * 1024
            : 524288;
}

public sealed class XodFlatStore : IFlatStore
{
    private readonly StorageFormat format;
    private XodContext db;

    public XodFlatStore(StorageFormat format = StorageFormat.Xml)
    {
        this.format = format;
    }

    public void Open(string dir, bool create, bool concurrentMode)
    {
        db = new XodContext(Path.Combine(dir, "bench.xod"), options: new DatabaseOptions
        {
            InitialCreate = true,
            EnableIndexing = true,
            StorageFormat = format,
            // Page size is a workload parameter, not a fixed law: the sweep sets XOD_PAGE_SIZE.
            PageSize = BenchEnv.PageSize,
            // Bulk/mutation phases group-commit with Flush(); the concurrency suite commits every op.
            Durability = concurrentMode ? DurabilityMode.Immediate : DurabilityMode.Buffered
        });
    }

    public void BulkInsert(IReadOnlyList<BenchRow> rows)
    {
        db.InsertRange(rows);
        db.Flush();
    }

    public void InsertOneAndCommit(BenchRow row)
    {
        db.Insert(row);
        db.Flush();
    }

    public BenchRow GetById(int id) => db.Find<BenchRow>(x => x.Id == id);

    public int Count() => db.Count<BenchRow>();

    public BenchRow Last() => db.Last<BenchRow>();

    public List<BenchRow> QueryByCategory(int categoryId) => db.Query<BenchRow>(x => x.CategoryId == categoryId).ToList();

    public List<BenchRow> Page(int skip, int take) => db.Select<BenchRow>(skip, take).ToList();

    public void UpdateAndDelete(int[] updateIds, int[] deleteIds)
    {
        foreach (int id in updateIds)
        {
            var row = db.Find<BenchRow>(x => x.Id == id);
            row.Price += 10.0;
            db.Update(row);
        }
        foreach (int id in deleteIds)
        {
            db.Delete(new BenchRow { Id = id });
        }
        db.Flush();
    }

    public IFlatWorker CreateWorker() => new Worker(db);

    public void Dispose() => db?.Dispose();

    private sealed class Worker : IFlatWorker
    {
        private readonly XodContext db;
        public Worker(XodContext db) => this.db = db;
        public BenchRow Get(int id) => db.Find<BenchRow>(x => x.Id == id);
        public void Update(int id)
        {
            var row = db.Find<BenchRow>(x => x.Id == id);
            row.Price += 1.0;
            db.Update(row);
        }
        public void Dispose() { }
    }
}

public sealed class XodRelStore : IRelStore
{
    private readonly StorageFormat format;
    private XodContext db;

    public XodRelStore(StorageFormat format = StorageFormat.Xml)
    {
        this.format = format;
    }

    public void Open(string dir, bool create)
    {
        db = new XodContext(Path.Combine(dir, "rel.xod"), options: new DatabaseOptions
        {
            InitialCreate = true,
            EnableIndexing = true,
            StorageFormat = format,
            // Page size is a workload parameter, not a fixed law: the sweep sets XOD_PAGE_SIZE.
            PageSize = BenchEnv.PageSize,
            Durability = DurabilityMode.Buffered
        });
    }

    public void InsertProducts(IReadOnlyList<Product> products)
    {
        db.InsertRange(products);
        db.Flush();
    }

    public void InsertGraphs(IReadOnlyList<Customer> customers)
    {
        db.InsertRange(customers);
        db.Flush();
    }

    public Customer LoadGraph(int customerId) => db.Find<Customer>(c => c.Id == customerId, include: "*");

    public List<Order> OrdersByStatus(string status) => db.Query<Order>(o => o.Status == status, include: "").ToList();

    public void UpdateOrderStatuses(int[] orderIds, string newStatus)
    {
        var filter = new UpdateFilter { Properties = new[] { nameof(Order.Status) }, Behavior = UpdateFilterBehavior.Target };
        foreach (int id in orderIds)
        {
            var order = db.Find<Order>(o => o.Id == id, include: "");
            order.Status = newStatus;
            db.Update(order, filter);
        }
        db.Flush();
    }

    public void DeleteCustomersCascade(int[] customerIds)
    {
        foreach (int id in customerIds)
        {
            var customer = db.Find<Customer>(c => c.Id == id, include: "");
            db.Delete(customer);
        }
        db.Flush();
    }

    public (int customers, int orders, int lines) Counts() =>
        (db.Count<Customer>(), db.Count<Order>(), db.Count<OrderLine>());

    public void Dispose() => db?.Dispose();
}
