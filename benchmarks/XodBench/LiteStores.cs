using LiteDB;

namespace XodBench;

internal static class Lite
{
    public static LiteDatabase Open(string file) =>
        new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Direct });
}

public sealed class LiteFlatStore : IFlatStore
{
    private LiteDatabase db;
    private ILiteCollection<BenchRow> col;

    public void Open(string dir, bool create, bool concurrentMode)
    {
        db = Lite.Open(Path.Combine(dir, "bench.litedb"));
        col = db.GetCollection<BenchRow>("BenchRow");
        if (create)
        {
            col.EnsureIndex(x => x.Sku, true);
            col.EnsureIndex(x => x.CategoryId);
        }
    }

    public void BulkInsert(IReadOnlyList<BenchRow> rows)
    {
        db.BeginTrans();
        col.InsertBulk(rows, 5000);
        db.Commit();
    }

    public void InsertOneAndCommit(BenchRow row) => col.Insert(row);

    public BenchRow GetById(int id) => col.FindById(id);

    public int Count() => col.Count();

    public BenchRow Last() => col.Query().OrderByDescending(x => x.Id).FirstOrDefault();

    public List<BenchRow> QueryByCategory(int categoryId) => col.Find(x => x.CategoryId == categoryId).ToList();

    public List<BenchRow> Page(int skip, int take) => col.Query().OrderBy(x => x.Id).Skip(skip).Limit(take).ToList();

    public void UpdateAndDelete(int[] updateIds, int[] deleteIds)
    {
        db.BeginTrans();
        foreach (int id in updateIds)
        {
            var row = col.FindById(id);
            row.Price += 10.0;
            col.Update(row);
        }
        foreach (int id in deleteIds)
        {
            col.Delete(id);
        }
        db.Commit();
    }

    /// <summary>LiteDatabase is thread-safe; one shared instance is the documented pattern.</summary>
    public IFlatWorker CreateWorker() => new Worker(col);

    public void Dispose() => db?.Dispose();

    private sealed class Worker : IFlatWorker
    {
        private readonly ILiteCollection<BenchRow> col;
        public Worker(ILiteCollection<BenchRow> col) => this.col = col;
        public BenchRow Get(int id) => col.FindById(id);
        public void Update(int id)
        {
            var row = col.FindById(id);
            row.Price += 1.0;
            col.Update(row);
        }
        public void Dispose() { }
    }
}

/// <summary>
/// Normalized collections with indexed foreign-key fields, assembled manually
/// (this is what LiteDB DbRef/Include does internally).
/// </summary>
public sealed class LiteRelStore : IRelStore
{
    private LiteDatabase db;
    private ILiteCollection<Product> products;
    private ILiteCollection<Customer> customers;
    private ILiteCollection<Order> orders;
    private ILiteCollection<OrderLine> lines;

    public void Open(string dir, bool create)
    {
        db = Lite.Open(Path.Combine(dir, "rel.litedb"));
        products = db.GetCollection<Product>("Product");
        customers = db.GetCollection<Customer>("Customer");
        orders = db.GetCollection<Order>("Orders");
        lines = db.GetCollection<OrderLine>("OrderLine");
        if (create)
        {
            products.EnsureIndex(x => x.Sku, true);
            customers.EnsureIndex(x => x.Email, true);
            customers.EnsureIndex(x => x.Country);
            orders.EnsureIndex(x => x.CustomerId);
            orders.EnsureIndex(x => x.Status);
            lines.EnsureIndex(x => x.OrderId);
        }
    }

    public void InsertProducts(IReadOnlyList<Product> items)
    {
        db.BeginTrans();
        products.InsertBulk(items, 5000);
        db.Commit();
    }

    public void InsertGraphs(IReadOnlyList<Customer> items)
    {
        db.BeginTrans();
        customers.InsertBulk(items, 5000);
        orders.InsertBulk(items.SelectMany(c => c.Orders), 5000);
        lines.InsertBulk(items.SelectMany(c => c.Orders).SelectMany(o => o.Lines), 5000);
        db.Commit();
    }

    public Customer LoadGraph(int customerId)
    {
        var customer = customers.FindById(customerId);
        if (customer == null) return null;
        customer.Orders = orders.Find(o => o.CustomerId == customerId).OrderBy(o => o.Id).ToList();
        foreach (var order in customer.Orders)
        {
            int oid = order.Id;
            order.Lines = lines.Find(l => l.OrderId == oid).OrderBy(l => l.Id).ToList();
            foreach (var line in order.Lines)
            {
                line.Product = products.FindById(line.ProductId);
            }
        }
        return customer;
    }

    public List<Order> OrdersByStatus(string status) => orders.Find(o => o.Status == status).ToList();

    public void UpdateOrderStatuses(int[] orderIds, string newStatus)
    {
        db.BeginTrans();
        foreach (int id in orderIds)
        {
            var o = orders.FindById(id);
            o.Status = newStatus;
            orders.Update(o);
        }
        db.Commit();
    }

    public void DeleteCustomersCascade(int[] customerIds)
    {
        db.BeginTrans();
        foreach (int cid in customerIds)
        {
            int id = cid;
            foreach (var o in orders.Find(x => x.CustomerId == id).ToList())
            {
                int oid = o.Id;
                lines.DeleteMany(l => l.OrderId == oid);
                orders.Delete(oid);
            }
            customers.Delete(id);
        }
        db.Commit();
    }

    public (int customers, int orders, int lines) Counts() => (customers.Count(), orders.Count(), lines.Count());

    public void Dispose() => db?.Dispose();
}
