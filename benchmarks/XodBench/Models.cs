using LiteDB;
using Xod;

namespace XodBench;

// One POCO set shared by all engines. Xod attributes are ignored by LiteDB/SQLite and
// [BsonIgnore] is ignored by XOD, so every engine stores exactly the same fields.

public class BenchRow
{
    [Xod.PrimaryKey]
    [Property(AutoNumber = true)]
    public int Id { get; set; }

    [UniqueKey]
    public string Sku { get; set; }

    [Property(Indexed = true)]
    public int CategoryId { get; set; }

    public double Price { get; set; }

    public string Payload { get; set; }
}

public class Product
{
    [Xod.PrimaryKey]
    public int Id { get; set; }

    [UniqueKey]
    public string Sku { get; set; }

    public string Name { get; set; }

    public double Price { get; set; }
}

public class Customer
{
    [Xod.PrimaryKey]
    public int Id { get; set; }

    [UniqueKey]
    public string Email { get; set; }

    public string Name { get; set; }

    [Property(Indexed = true)]
    public string Country { get; set; }

    [Children]
    [BsonIgnore]
    public List<Order> Orders { get; set; }
}

public class Order
{
    [Xod.PrimaryKey]
    public int Id { get; set; }

    public int CustomerId { get; set; }

    [ParentKey(nameof(CustomerId))]
    [BsonIgnore]
    public Customer Customer { get; set; }

    public DateTime OrderDate { get; set; }

    [Property(Indexed = true)]
    public string Status { get; set; }

    [Children]
    [BsonIgnore]
    public List<OrderLine> Lines { get; set; }
}

public class OrderLine
{
    [Xod.PrimaryKey]
    public int Id { get; set; }

    public int OrderId { get; set; }

    [ParentKey(nameof(OrderId))]
    [BsonIgnore]
    public Order Order { get; set; }

    public int ProductId { get; set; }

    [ForeignKey(nameof(ProductId))]
    [BsonIgnore]
    public Product Product { get; set; }

    public int Quantity { get; set; }

    public double UnitPrice { get; set; }
}
