![XOD](https://raw.githubusercontent.com/mhsallam/xod/main/docs/screens/logo.svg)

# XOD

An embedded database for .NET. Your objects go in, files you can read come out.

[![NuGet version](https://img.shields.io/nuget/v/Xod?label=nuget)](https://www.nuget.org/packages/Xod)
[![NuGet downloads](https://img.shields.io/nuget/dt/Xod?label=downloads)](https://www.nuget.org/packages/Xod)
[![Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue)](https://github.com/mhsallam/xod/blob/main/LICENSE.md)
![.NET 8 and 9](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-512BD4)
![60 tests passing](https://img.shields.io/badge/tests-60%20passing-brightgreen)

XOD 2.0 starts here. The 2017 library (0.1.x) is on the [`legacy`](https://github.com/mhsallam/xod/tree/legacy) branch.

---

```csharp
using Xod;

using var db = new XodContext("app.xod");

db.Insert(new Customer { Name = "Nadia", Orders = { new Order { Status = "open" } } });

var open = db.Query<Order>(o => o.Status == "open").ToList();
```

There is no server to install, no port to open and no migration between versions of your model. You
point XOD at a folder, and your classes are the schema.

## The files are the point

Open an XML database in an editor and you find rows, not a binary blob:

```xml
<Row code="9b2e14c1"><Customer>
  <Id>1</Id>
  <Name>Nadia</Name>
  <Orders refType="children">
    <Order refType="children"><Id>1</Id><Status>open</Status></Order>
  </Orders>
</Customer></Row>
```

That means `git diff` shows what changed, a backup is a folder copy, and a support ticket can include
the data. JSON and Brotli-compressed binary formats are available for the same database, and
`XodMigrator` switches between them without your model assembly.

## Getting started

```bash
dotnet add package Xod
```

```csharp
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; }

    [Children]
    public List<Order> Orders { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string Status { get; set; }

    [ParentKey(nameof(CustomerId))]
    public Customer Customer { get; set; }
}
```

Relationships are ordinary collections. Primary keys, unique keys and secondary indexes are attributes
on the properties you already have. Writes are transactional and journal to `.xwal`, so a process killed
mid-write comes back whole.

## Numbers

One Windows machine, 100,000 rows, same schema and same operations for each engine. Full detail, raw
result files and the losing metrics are on the benchmark page in the `xod-site` repository.

| 250 MB | XOD (XML) | SQLite | LiteDB |
| :--- | ---: | ---: | ---: |
| Bulk load | **1,183 ms** | 2,037 ms | 2,487 ms |
| 1,000 lookups by key | **4.5 ms** | 7.1 ms | 41.6 ms |
| 20 indexed queries | **20 ms** | 43 ms | 119 ms |
| Page to row 90,000 | **0.2 ms** | 20 ms | 154 ms |
| 100 updates + 100 deletes | 51 ms | **4.9 ms** | 18 ms |
| Size on disk | **254 MB** | 395 MB | 293 MB |
| Peak memory | **410 MB** | 455 MB | 692 MB |

Fastest in each row is in bold.

Bulk loads and reads are the strong side. Single-row updates and deletes are not: SQLite does them about
ten times faster, and at 1 GB the gap widens. If your workload is many small writes in a tight loop,
SQLite is the better tool.

## Where it fits

XOD is for applications that own their data: a desktop app, a service with its own folder, a device
with no room for a server. Bulk loads, lookups, indexed queries and paging are where it is fastest, and
the whole database is a directory you can copy or commit.

It is not the right tool for every job:

- **Several processes writing to one database.** One process owns the folder at a time. Inside that
  process XOD is concurrent - many readers and a queued writer, the case the 140,000 reads/second
  figure measures - but it is a library, not a shared server. If several services must write the same
  data, use a server database.
- **A tight loop of single-row writes.** SQLite is faster at that operation - about ten times on the
  250 MB set, and more at 1 GB. XOD spends its effort on bulk load, reads and paging instead.
- **Replication, clustering or network access.** XOD is a library, not a service.
- **Bulk relational graphs.** Inserting and loading customer/order/line graphs is the benchmark where
  XOD trails SQLite today. If that is your dominant workload, measure both before choosing.

The full numbers, including the cases above, are on the benchmark page.

## What is in the box

Concurrency is per table: many readers and a queued writer inside the process that owns the folder.
Transactions with a write-ahead journal; primary, unique and secondary indexes; one-to-many,
many-to-many and self joins; cascade delete; `[Required]` validation; AES-256-GCM property encryption;
`Select<T>(skip, take)` streaming; `record`, `DateOnly`, `TimeOnly` and `Dictionary<K,V>`; and a
command-line tool for stats, conversion and encryption.

The engine targets .NET 8 and .NET 9. The test suite covers the API, stress, referential integrity,
crash safety, migration from a real 1.x database, format equivalence, a million rows, a differential
check against SQLite and the public API surface: 54 tests, no skips, green on Windows and Linux. The
release gate is in [docs/TEST-PLAN-2.0.md](https://github.com/mhsallam/xod/blob/main/docs/TEST-PLAN-2.0.md).

## Licence

Apache-2.0. Versions published before 2.0.0 (the 0.1.x line and the 1.0.0 betas) were MIT and stay under those terms. See [NOTICE](https://github.com/mhsallam/xod/blob/main/NOTICE).

If XOD saves you time, sponsoring the project or a paid support arrangement keeps it maintained. Issues
and pull requests are welcome; for anything large, please open an issue first so we can agree on the
shape.
