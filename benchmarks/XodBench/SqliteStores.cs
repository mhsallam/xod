using Microsoft.Data.Sqlite;

namespace XodBench;

internal static class Sql
{
    // synchronous=OFF: no fsync on commit, matching XOD (which never fsyncs). WAL lets readers run beside a writer.
    // 256 MB page cache + mmap: a normal "tuned" embedded config; memory use is reported as peak working set.
    public const string Pragmas = @"
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = OFF;
        PRAGMA cache_size = -262144;
        PRAGMA mmap_size = 268435456;
        PRAGMA temp_store = MEMORY;
        PRAGMA busy_timeout = 10000;
        PRAGMA foreign_keys = ON;";

    public static SqliteConnection Open(string file)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            Cache = SqliteCacheMode.Private
        }.ToString());
        conn.Open();
        Exec(conn, Pragmas);
        return conn;
    }

    public static void Exec(SqliteConnection conn, string sql, SqliteTransaction tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static SqliteCommand Prepare(SqliteConnection conn, string sql, params string[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
        {
            var prm = cmd.CreateParameter();
            prm.ParameterName = p;
            cmd.Parameters.Add(prm);
        }
        cmd.Prepare();
        return cmd;
    }
}

public sealed class SqliteFlatStore : IFlatStore
{
    private const string Cols = "Id, Sku, CategoryId, Price, Payload";
    private string file;
    private SqliteConnection conn;
    private SqliteCommand getCmd, catCmd, pageCmd, lastCmd, countCmd;

    public void Open(string dir, bool create, bool concurrentMode)
    {
        file = Path.Combine(dir, "bench.sqlite");
        conn = Sql.Open(file);
        if (create)
        {
            Sql.Exec(conn, @"
                CREATE TABLE IF NOT EXISTS BenchRow (
                    Id INTEGER PRIMARY KEY,
                    Sku TEXT NOT NULL UNIQUE,
                    CategoryId INTEGER NOT NULL,
                    Price REAL NOT NULL,
                    Payload TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_BenchRow_CategoryId ON BenchRow(CategoryId);");
        }
        getCmd = Sql.Prepare(conn, $"SELECT {Cols} FROM BenchRow WHERE Id = $id;", "$id");
        catCmd = Sql.Prepare(conn, $"SELECT {Cols} FROM BenchRow WHERE CategoryId = $c;", "$c");
        pageCmd = Sql.Prepare(conn, $"SELECT {Cols} FROM BenchRow ORDER BY Id LIMIT $t OFFSET $s;", "$t", "$s");
        lastCmd = Sql.Prepare(conn, $"SELECT {Cols} FROM BenchRow ORDER BY Id DESC LIMIT 1;");
        countCmd = Sql.Prepare(conn, "SELECT COUNT(*) FROM BenchRow;");
    }

    private static BenchRow Map(SqliteDataReader r) => new BenchRow
    {
        Id = r.GetInt32(0),
        Sku = r.GetString(1),
        CategoryId = r.GetInt32(2),
        Price = r.GetDouble(3),
        Payload = r.GetString(4)
    };

    private static List<BenchRow> ReadAll(SqliteCommand cmd)
    {
        var list = new List<BenchRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private static BenchRow ReadOne(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public void BulkInsert(IReadOnlyList<BenchRow> rows)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = Sql.Prepare(conn, "INSERT INTO BenchRow (Sku, CategoryId, Price, Payload) VALUES ($s, $c, $p, $d);", "$s", "$c", "$p", "$d");
        cmd.Transaction = tx;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            cmd.Parameters[0].Value = row.Sku;
            cmd.Parameters[1].Value = row.CategoryId;
            cmd.Parameters[2].Value = row.Price;
            cmd.Parameters[3].Value = row.Payload;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InsertOneAndCommit(BenchRow row)
    {
        using var cmd = Sql.Prepare(conn, "INSERT INTO BenchRow (Sku, CategoryId, Price, Payload) VALUES ($s, $c, $p, $d);", "$s", "$c", "$p", "$d");
        cmd.Parameters[0].Value = row.Sku;
        cmd.Parameters[1].Value = row.CategoryId;
        cmd.Parameters[2].Value = row.Price;
        cmd.Parameters[3].Value = row.Payload;
        cmd.ExecuteNonQuery();
    }

    public BenchRow GetById(int id)
    {
        getCmd.Parameters[0].Value = id;
        return ReadOne(getCmd);
    }

    public int Count() => Convert.ToInt32(countCmd.ExecuteScalar());

    public BenchRow Last() => ReadOne(lastCmd);

    public List<BenchRow> QueryByCategory(int categoryId)
    {
        catCmd.Parameters[0].Value = categoryId;
        return ReadAll(catCmd);
    }

    public List<BenchRow> Page(int skip, int take)
    {
        pageCmd.Parameters[0].Value = take;
        pageCmd.Parameters[1].Value = skip;
        return ReadAll(pageCmd);
    }

    public void UpdateAndDelete(int[] updateIds, int[] deleteIds)
    {
        using var tx = conn.BeginTransaction();
        getCmd.Transaction = tx;
        using var up = Sql.Prepare(conn, "UPDATE BenchRow SET Sku = $s, CategoryId = $c, Price = $p, Payload = $d WHERE Id = $id;", "$s", "$c", "$p", "$d", "$id");
        using var del = Sql.Prepare(conn, "DELETE FROM BenchRow WHERE Id = $id;", "$id");
        up.Transaction = tx;
        del.Transaction = tx;
        foreach (int id in updateIds)
        {
            var row = GetById(id);
            row.Price += 10.0;
            up.Parameters[0].Value = row.Sku;
            up.Parameters[1].Value = row.CategoryId;
            up.Parameters[2].Value = row.Price;
            up.Parameters[3].Value = row.Payload;
            up.Parameters[4].Value = row.Id;
            up.ExecuteNonQuery();
        }
        foreach (int id in deleteIds)
        {
            del.Parameters[0].Value = id;
            del.ExecuteNonQuery();
        }
        tx.Commit();
        getCmd.Transaction = null;
    }

    public IFlatWorker CreateWorker() => new Worker(file);

    public void Dispose()
    {
        getCmd?.Dispose(); catCmd?.Dispose(); pageCmd?.Dispose(); lastCmd?.Dispose(); countCmd?.Dispose();
        conn?.Dispose();
    }

    /// <summary>One connection per thread: the standard way to use SQLite concurrently.</summary>
    private sealed class Worker : IFlatWorker
    {
        private readonly SqliteConnection conn;
        private readonly SqliteCommand get, up;

        public Worker(string file)
        {
            conn = Sql.Open(file);
            get = Sql.Prepare(conn, $"SELECT {Cols} FROM BenchRow WHERE Id = $id;", "$id");
            up = Sql.Prepare(conn, "UPDATE BenchRow SET Sku = $s, CategoryId = $c, Price = $p, Payload = $d WHERE Id = $id;", "$s", "$c", "$p", "$d", "$id");
        }

        public BenchRow Get(int id)
        {
            get.Parameters[0].Value = id;
            return ReadOne(get);
        }

        public void Update(int id)
        {
            var row = Get(id);
            row.Price += 1.0;
            up.Parameters[0].Value = row.Sku;
            up.Parameters[1].Value = row.CategoryId;
            up.Parameters[2].Value = row.Price;
            up.Parameters[3].Value = row.Payload;
            up.Parameters[4].Value = row.Id;
            up.ExecuteNonQuery();
        }

        public void Dispose()
        {
            get.Dispose(); up.Dispose(); conn.Dispose();
        }
    }
}

public sealed class SqliteRelStore : IRelStore
{
    private SqliteConnection conn;
    private SqliteCommand custCmd, ordersCmd, linesCmd, statusCmd;

    public void Open(string dir, bool create)
    {
        conn = Sql.Open(Path.Combine(dir, "rel.sqlite"));
        if (create)
        {
            Sql.Exec(conn, @"
                CREATE TABLE IF NOT EXISTS Product (Id INTEGER PRIMARY KEY, Sku TEXT NOT NULL UNIQUE, Name TEXT NOT NULL, Price REAL NOT NULL);
                CREATE TABLE IF NOT EXISTS Customer (Id INTEGER PRIMARY KEY, Email TEXT NOT NULL UNIQUE, Name TEXT NOT NULL, Country TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Orders (Id INTEGER PRIMARY KEY, CustomerId INTEGER NOT NULL REFERENCES Customer(Id) ON DELETE CASCADE,
                                                   OrderDate INTEGER NOT NULL, Status TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS OrderLine (Id INTEGER PRIMARY KEY, OrderId INTEGER NOT NULL REFERENCES Orders(Id) ON DELETE CASCADE,
                                                      ProductId INTEGER NOT NULL REFERENCES Product(Id), Quantity INTEGER NOT NULL, UnitPrice REAL NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_Customer_Country ON Customer(Country);
                CREATE INDEX IF NOT EXISTS IX_Orders_CustomerId ON Orders(CustomerId);
                CREATE INDEX IF NOT EXISTS IX_Orders_Status ON Orders(Status);
                CREATE INDEX IF NOT EXISTS IX_OrderLine_OrderId ON OrderLine(OrderId);");
        }
        custCmd = Sql.Prepare(conn, "SELECT Id, Email, Name, Country FROM Customer WHERE Id = $id;", "$id");
        ordersCmd = Sql.Prepare(conn, "SELECT Id, CustomerId, OrderDate, Status FROM Orders WHERE CustomerId = $id ORDER BY Id;", "$id");
        linesCmd = Sql.Prepare(conn, @"
            SELECT l.Id, l.OrderId, l.ProductId, l.Quantity, l.UnitPrice, p.Sku, p.Name, p.Price
            FROM Orders o JOIN OrderLine l ON l.OrderId = o.Id JOIN Product p ON p.Id = l.ProductId
            WHERE o.CustomerId = $id ORDER BY l.Id;", "$id");
        statusCmd = Sql.Prepare(conn, "SELECT Id, CustomerId, OrderDate, Status FROM Orders WHERE Status = $s;", "$s");
    }

    public void InsertProducts(IReadOnlyList<Product> products)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = Sql.Prepare(conn, "INSERT INTO Product (Id, Sku, Name, Price) VALUES ($i, $s, $n, $p);", "$i", "$s", "$n", "$p");
        cmd.Transaction = tx;
        foreach (var p in products)
        {
            cmd.Parameters[0].Value = p.Id; cmd.Parameters[1].Value = p.Sku; cmd.Parameters[2].Value = p.Name; cmd.Parameters[3].Value = p.Price;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InsertGraphs(IReadOnlyList<Customer> customers)
    {
        using var tx = conn.BeginTransaction();
        using var c = Sql.Prepare(conn, "INSERT INTO Customer (Id, Email, Name, Country) VALUES ($i, $e, $n, $c);", "$i", "$e", "$n", "$c");
        using var o = Sql.Prepare(conn, "INSERT INTO Orders (Id, CustomerId, OrderDate, Status) VALUES ($i, $c, $d, $s);", "$i", "$c", "$d", "$s");
        using var l = Sql.Prepare(conn, "INSERT INTO OrderLine (Id, OrderId, ProductId, Quantity, UnitPrice) VALUES ($i, $o, $p, $q, $u);", "$i", "$o", "$p", "$q", "$u");
        c.Transaction = tx; o.Transaction = tx; l.Transaction = tx;
        foreach (var cu in customers)
        {
            c.Parameters[0].Value = cu.Id; c.Parameters[1].Value = cu.Email; c.Parameters[2].Value = cu.Name; c.Parameters[3].Value = cu.Country;
            c.ExecuteNonQuery();
            foreach (var od in cu.Orders)
            {
                o.Parameters[0].Value = od.Id; o.Parameters[1].Value = cu.Id; o.Parameters[2].Value = od.OrderDate.Ticks; o.Parameters[3].Value = od.Status;
                o.ExecuteNonQuery();
                foreach (var ln in od.Lines)
                {
                    l.Parameters[0].Value = ln.Id; l.Parameters[1].Value = od.Id; l.Parameters[2].Value = ln.ProductId;
                    l.Parameters[3].Value = ln.Quantity; l.Parameters[4].Value = ln.UnitPrice;
                    l.ExecuteNonQuery();
                }
            }
        }
        tx.Commit();
    }

    private static Order MapOrder(SqliteDataReader r) => new Order
    {
        Id = r.GetInt32(0),
        CustomerId = r.GetInt32(1),
        OrderDate = new DateTime(r.GetInt64(2), DateTimeKind.Utc),
        Status = r.GetString(3)
    };

    public Customer LoadGraph(int customerId)
    {
        Customer customer;
        custCmd.Parameters[0].Value = customerId;
        using (var r = custCmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            customer = new Customer { Id = r.GetInt32(0), Email = r.GetString(1), Name = r.GetString(2), Country = r.GetString(3), Orders = new List<Order>() };
        }

        var byId = new Dictionary<int, Order>();
        ordersCmd.Parameters[0].Value = customerId;
        using (var r = ordersCmd.ExecuteReader())
        {
            while (r.Read())
            {
                var o = MapOrder(r);
                o.Lines = new List<OrderLine>();
                customer.Orders.Add(o);
                byId[o.Id] = o;
            }
        }

        linesCmd.Parameters[0].Value = customerId;
        using (var r = linesCmd.ExecuteReader())
        {
            while (r.Read())
            {
                var line = new OrderLine
                {
                    Id = r.GetInt32(0),
                    OrderId = r.GetInt32(1),
                    ProductId = r.GetInt32(2),
                    Quantity = r.GetInt32(3),
                    UnitPrice = r.GetDouble(4),
                    Product = new Product { Id = r.GetInt32(2), Sku = r.GetString(5), Name = r.GetString(6), Price = r.GetDouble(7) }
                };
                byId[line.OrderId].Lines.Add(line);
            }
        }
        return customer;
    }

    public List<Order> OrdersByStatus(string status)
    {
        statusCmd.Parameters[0].Value = status;
        var list = new List<Order>();
        using var r = statusCmd.ExecuteReader();
        while (r.Read()) list.Add(MapOrder(r));
        return list;
    }

    public void UpdateOrderStatuses(int[] orderIds, string newStatus)
    {
        using var tx = conn.BeginTransaction();
        using var get = Sql.Prepare(conn, "SELECT Id, CustomerId, OrderDate, Status FROM Orders WHERE Id = $id;", "$id");
        using var up = Sql.Prepare(conn, "UPDATE Orders SET CustomerId = $c, OrderDate = $d, Status = $s WHERE Id = $id;", "$c", "$d", "$s", "$id");
        get.Transaction = tx; up.Transaction = tx;
        foreach (int id in orderIds)
        {
            Order o;
            get.Parameters[0].Value = id;
            using (var r = get.ExecuteReader()) { r.Read(); o = MapOrder(r); }
            o.Status = newStatus;
            up.Parameters[0].Value = o.CustomerId; up.Parameters[1].Value = o.OrderDate.Ticks; up.Parameters[2].Value = o.Status; up.Parameters[3].Value = o.Id;
            up.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeleteCustomersCascade(int[] customerIds)
    {
        using var tx = conn.BeginTransaction();
        using var del = Sql.Prepare(conn, "DELETE FROM Customer WHERE Id = $id;", "$id");
        del.Transaction = tx;
        foreach (int id in customerIds)
        {
            del.Parameters[0].Value = id;
            del.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public (int customers, int orders, int lines) Counts()
    {
        int Scalar(string sql) { using var cmd = conn.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt32(cmd.ExecuteScalar()); }
        return (Scalar("SELECT COUNT(*) FROM Customer"), Scalar("SELECT COUNT(*) FROM Orders"), Scalar("SELECT COUNT(*) FROM OrderLine"));
    }

    public void Dispose()
    {
        custCmd?.Dispose(); ordersCmd?.Dispose(); linesCmd?.Dispose(); statusCmd?.Dispose();
        conn?.Dispose();
    }
}
