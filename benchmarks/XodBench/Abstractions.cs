using Xod;

namespace XodBench;

/// <summary>Flat single-table operations. Every method must leave its work committed unless noted.</summary>
public interface IFlatStore : IDisposable
{
    void Open(string dir, bool create, bool concurrentMode);
    /// <summary>Insert all rows and commit (one batch / transaction).</summary>
    void BulkInsert(IReadOnlyList<BenchRow> rows);
    void InsertOneAndCommit(BenchRow row);
    BenchRow GetById(int id);
    int Count();
    BenchRow Last();
    List<BenchRow> QueryByCategory(int categoryId);
    /// <summary>Rows in primary-key order.</summary>
    List<BenchRow> Page(int skip, int take);
    /// <summary>Read-modify-write updates (Price += 10) and deletes, committed once.</summary>
    void UpdateAndDelete(int[] updateIds, int[] deleteIds);
    /// <summary>Thread-safe worker for the concurrency suite.</summary>
    IFlatWorker CreateWorker();
}

public interface IFlatWorker : IDisposable
{
    BenchRow Get(int id);
    /// <summary>Read-modify-write a single row and commit it.</summary>
    void Update(int id);
}

/// <summary>Customer -> Order -> OrderLine -> Product operations.</summary>
public interface IRelStore : IDisposable
{
    void Open(string dir, bool create);
    void InsertProducts(IReadOnlyList<Product> products);
    /// <summary>Insert customers with nested orders and lines, committed once.</summary>
    void InsertGraphs(IReadOnlyList<Customer> customers);
    /// <summary>Customer with Orders, each Order with Lines, each Line with Product.</summary>
    Customer LoadGraph(int customerId);
    /// <summary>Shallow orders (no lines) with the given status.</summary>
    List<Order> OrdersByStatus(string status);
    void UpdateOrderStatuses(int[] orderIds, string newStatus);
    /// <summary>Delete customers and all their orders and lines, committed once.</summary>
    void DeleteCustomersCascade(int[] customerIds);
    (int customers, int orders, int lines) Counts();
}

public static class Engines
{
    public static readonly string[] All = { "xod-xml", "xod-json", "xod-bin", "sqlite", "litedb" };

    public static string Label(string key) => key switch
    {
        "xod" or "xod-xml" => "XOD++ (XML)",
        "xod-json" => "XOD++ (JSON)",
        "xod-bin" => "XOD++ (Binary)",
        "sqlite" => "SQLite 3 (WAL)",
        "litedb" => "LiteDB 5.0",
        _ => key
    };

    public static IFlatStore Flat(string key) => key switch
    {
        "xod" or "xod-xml" => new XodFlatStore(StorageFormat.Xml),
        "xod-json" => new XodFlatStore(StorageFormat.Json),
        "xod-bin" => new XodFlatStore(StorageFormat.Binary),
        "sqlite" => new SqliteFlatStore(),
        "litedb" => new LiteFlatStore(),
        _ => throw new ArgumentException(key)
    };

    public static IRelStore Rel(string key) => key switch
    {
        "xod" or "xod-xml" => new XodRelStore(StorageFormat.Xml),
        "xod-json" => new XodRelStore(StorageFormat.Json),
        "xod-bin" => new XodRelStore(StorageFormat.Binary),
        "sqlite" => new SqliteRelStore(),
        "litedb" => new LiteRelStore(),
        _ => throw new ArgumentException(key)
    };
}
