using System;
using System.Collections.Generic;
using System.IO;
using Xod;

// Model names are global so the parent test process, which declares the same types, maps the files
// the dying child wrote.
public class CrashCustomer
{
    public int Id { get; set; }
    public string Name { get; set; }
    [Children] public List<CrashOrder> Orders { get; set; }
}

public class CrashOrder
{
    public int Id { get; set; }
    public int CrashCustomerId { get; set; }
    public string Status { get; set; }
    [ParentKey(nameof(CrashCustomerId))] public CrashCustomer Customer { get; set; }
    public double Total { get; set; }
}

/// <summary>
/// Child process for the crash-safety test (gate layer 4). It writes a fixed-size graph in one
/// transaction, flushes, prints BATCH and repeats until the parent kills it. Every batch is 25
/// customers with 3 orders each, so an all-or-nothing database can only contain a multiple of 25
/// customers and 75 orders - a partial batch is a durability failure.
/// </summary>
public static class Program
{
    private const int BatchCustomers = 25;
    private const int OrdersPerCustomer = 3;

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: Xod.CrashWriter <setup|burst> <directory>");
            return 2;
        }

        string mode = args[0];
        string dir = args[1];
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "crash.xod");

        var options = new DatabaseOptions
        {
            InitialCreate = true,
            EnableIndexing = true,
            StorageFormat = StorageFormat.Xml,
        };

        if (mode == "setup")
        {
            using var seed = new XodContext(path, options: options);
            seed.InsertRange(BuildBatch(0));
            seed.Flush();
            Console.WriteLine("SETUP");
            return 0;
        }

        if (mode == "onebatch")
        {
            // one transaction, committed once; XOD_CRASH_AFTER_ROWS makes the process die part-way
            using var db = new XodContext(path, options: options);
            int batch = (int)(db.Count<CrashCustomer>() / BatchCustomers);
            db.InsertRange(BuildBatch(batch));
            db.Flush();
            Console.WriteLine("COMMITTED");
            return 0;
        }

        if (mode == "count")
        {
            using var check = new XodContext(path);
            int customers = check.Count<CrashCustomer>();
            int orders = check.Count<CrashOrder>();
            int orphans = 0;
            for (int id = 1; id <= orders; id++)
            {
                var order = check.Find<CrashOrder>(o => o.Id == id);
                if (order == null) { orphans++; continue; }
                if (order.CrashCustomerId > customers) orphans++;
            }
            Console.WriteLine($"COUNT customers={customers} orders={orders} orphans={orphans}");
            return 0;
        }

        if (mode == "burst")
        {
            using var db = new XodContext(path, options: options);
            // continue after whatever setup (or a previous burst) already committed
            int batch = (int)(db.Count<CrashCustomer>() / BatchCustomers);
            while (true)
            {
                db.InsertRange(BuildBatch(batch));
                db.Flush();
                batch++;
                Console.WriteLine($"BATCH {batch}");
                Console.Out.Flush();
            }
        }

        Console.Error.WriteLine($"unknown mode {mode}");
        return 2;
    }

    private static List<CrashCustomer> BuildBatch(int batch)
    {
        var list = new List<CrashCustomer>(BatchCustomers);
        for (int i = 0; i < BatchCustomers; i++)
        {
            int customerId = batch * BatchCustomers + i + 1;
            var orders = new List<CrashOrder>(OrdersPerCustomer);
            for (int o = 0; o < OrdersPerCustomer; o++)
            {
                orders.Add(new CrashOrder
                {
                    Id = (customerId - 1) * OrdersPerCustomer + o + 1,
                    Status = $"S{o:D2}",
                    Total = (customerId + o) * 1.5,
                });
            }

            list.Add(new CrashCustomer { Id = customerId, Name = $"Customer {customerId:D6}", Orders = orders });
        }

        return list;
    }
}
