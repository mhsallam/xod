// The conformance cases (PLAN-047). Each pairs a XOD SQL query with its meaning in C#. The C# runs
// against the oracle (the dataset in memory) to give the expected rows, and against XOD to check that
// the library returns them. Write the C# with SQL's semantics, not C#'s: NULL never equals anything,
// text compares ordinally, NULLs sort last ascending and first descending.
using System;
using System.Collections.Generic;
using System.Linq;
using Xod.Conformance.Model;
using Xod.Helpers;
using static Xod.Conformance.Sql;

namespace Xod.Conformance
{
    public static class Cases
    {
        public static IReadOnlyList<Case> All { get; } = Build();

        private static List<Case> Build()
        {
            var list = new List<Case>();
            void G(string group, params Case[] cases) => list.AddRange(cases.Select(c => c with { Group = group }));

            G("basic",
                new Case("basic-001", "SELECT Id, Name FROM Customer", "Id INTEGER, Name TEXT", true,
                    s => s.All<Customer>().Select(c => R(c.Id, c.Name))) { Note = "no ORDER BY: stored order (D5)" },
                new Case("basic-002", "SELECT Id, Sku FROM Product", "Id INTEGER, Sku TEXT", true,
                    s => s.All<Product>().Select(p => R(p.Id, p.Sku))) { Note = "stored order is insertion order, not Id order" },
                new Case("basic-003", "SELECT Name AS customer FROM Customer WHERE Id = 3", "customer TEXT", true,
                    s => s.Where<Customer>(c => c.Id == 3).Select(c => R(c.Name))),
                new Case("basic-004", "SELECT DISTINCT City FROM Customer", "City TEXT", false,
                    s => s.All<Customer>().Select(c => c.City).Distinct().Select(x => R(x))) { Note = "NULL is one distinct value" },
                new Case("basic-005", "SELECT DISTINCT Status FROM \"Order\" ORDER BY Status", "Status TEXT", true,
                    s => s.All<Order>().Select(o => o.Status).Distinct().OrderBy(x => x, Ordinal).Select(x => R(x))),
                new Case("basic-006", "SELECT COUNT(*) AS n FROM OrderLine", "n INTEGER", true,
                    s => new[] { R(s.All<OrderLine>().LongCount()) }),
                new Case("basic-007", "SELECT Id, Price * 2 AS twice FROM Product WHERE Id <= 3 ORDER BY Id", "Id INTEGER, twice DECIMAL", true,
                    s => s.Where<Product>(p => p.Id <= 3).OrderBy(p => p.Id).Select(p => R(p.Id, p.Price * 2))),
                new Case("basic-008", "SELECT Id, Quantity * UnitPrice AS amount FROM OrderLine WHERE OrderId = 1 ORDER BY Id", "Id INTEGER, amount DECIMAL", true,
                    s => s.Where<OrderLine>(l => l.OrderId == 1).OrderBy(l => l.Id).Select(l => R(l.Id, l.Quantity * l.UnitPrice))),
                new Case("basic-009", "SELECT 7 / 2 AS a, 7 / 2.0 AS b, 7 % 3 AS c FROM Customer WHERE Id = 1", "a INTEGER, b DECIMAL, c INTEGER", true,
                    s => s.Where<Customer>(c => c.Id == 1).Select(c => R(7L / 2, 7m / 2.0m, 7L % 3))) { Note = "integer division as C# (section 8); 2.0 is an exact literal" },
                new Case("basic-010", "SELECT Name || ' <' || Email || '>' AS contact FROM Customer WHERE Id = 2", "contact TEXT", true,
                    s => s.Where<Customer>(c => c.Id == 2).Select(c => R(c.Name + " <" + c.Email + ">")))
            );

            G("where",
                new Case("where-001", "SELECT Id FROM Customer WHERE Tier = 'Gold'", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.Tier == Tier.Gold).Select(c => R(c.Id))) { Note = "enum by name; Tier is indexed (ISS-056, fixed)" },
                new Case("where-002", "SELECT Id FROM Customer WHERE Rating > 3", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.Rating > 3).Select(c => R(c.Id))),
                new Case("where-003", "SELECT Id FROM Customer WHERE Rating <> 3", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.Rating != null && c.Rating != 3).Select(c => R(c.Id))) { Note = "NULL <> 3 is unknown, so NULL ratings are excluded" },
                new Case("where-004", "SELECT Id FROM Customer WHERE City = 'Riyadh'", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.City == "Riyadh").Select(c => R(c.Id))),
                new Case("where-005", "SELECT Id FROM Customer WHERE City = 'riyadh'", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.City == "riyadh").Select(c => R(c.Id))) { Note = "text comparison is case-sensitive" },
                new Case("where-006", "SELECT Id FROM Customer WHERE Name < 'L'", "Id INTEGER", false,
                    s => s.All<Customer>().Where(c => string.CompareOrdinal(c.Name, "L") < 0).Select(c => R(c.Id))) { Note = "ordinal order" },
                new Case("where-007", "SELECT Id FROM Customer WHERE CreditLimit BETWEEN 500 AND 1000.50", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.CreditLimit >= 500m && c.CreditLimit <= 1000.50m).Select(c => R(c.Id))),
                new Case("where-008", "SELECT Id FROM Customer WHERE Joined >= TIMESTAMP '2025-07-01 00:00:00'", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.Joined >= new DateTime(2025, 7, 1)).Select(c => R(c.Id))),
                new Case("where-009", "SELECT Id FROM Customer WHERE Active = TRUE", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.Active).Select(c => R(c.Id))) { Note = "Active is stored as an attribute" },
                new Case("where-010", "SELECT Id FROM Customer WHERE Id IN (1, 3, 5, 99)", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.Id == 1 || c.Id == 3 || c.Id == 5 || c.Id == 99).Select(c => R(c.Id))),
                new Case("where-011", "SELECT Id FROM Customer WHERE Id NOT IN (1, 2, 3) AND Tier = 'Basic'", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.Id != 1 && c.Id != 2 && c.Id != 3 && c.Tier == Tier.Basic).Select(c => R(c.Id))) { Note = "an indexed enum inside AND (ISS-056, fixed)" },
                new Case("where-012", "SELECT Id FROM \"Order\" WHERE Status IN ('open', 'paid')", "Id INTEGER", false,
                    s => s.Where<Order>(o => o.Status == "open" || o.Status == "paid").Select(o => R(o.Id))) { Note = "Status is indexed" },
                new Case("where-013", "SELECT Id FROM \"Order\" WHERE Placed < DATE '2026-04-01'", "Id INTEGER", false,
                    s => s.Where<Order>(o => o.Placed < new DateOnly(2026, 4, 1)).Select(o => R(o.Id))),
                new Case("where-014", "SELECT Id FROM \"Order\" WHERE Discount IS NULL", "Id INTEGER", false,
                    s => s.Where<Order>(o => o.Discount == null).Select(o => R(o.Id))),
                new Case("where-015", "SELECT Id FROM \"Order\" WHERE Discount IS NOT NULL AND Discount > 0.1", "Id INTEGER", false,
                    s => s.Where<Order>(o => o.Discount != null && o.Discount > 0.1m).Select(o => R(o.Id))),
                new Case("where-016", "SELECT Id FROM Product WHERE Category IS NULL", "Id INTEGER", false,
                    s => s.Where<Product>(p => p.Category == null).Select(p => R(p.Id))) { Note = "NULL in an indexed column" },
                new Case("where-017", "SELECT Id FROM Product WHERE Weight IS NULL", "Id INTEGER", false,
                    s => s.Where<Product>(p => p.Weight == null).Select(p => R(p.Id))),
                new Case("where-018", "SELECT Id FROM Product WHERE Discontinued = FALSE AND Price > 20", "Id INTEGER", false,
                    s => s.Where<Product>(p => !p.Discontinued && p.Price > 20m).Select(p => R(p.Id))),
                new Case("where-019", "SELECT Id FROM Product WHERE Category = 'kitchen'", "Id INTEGER", false,
                    s => s.Where<Product>(p => p.Category == "kitchen").Select(p => R(p.Id))) { Note = "indexed text" },
                new Case("where-020", "SELECT Id FROM Event WHERE Big > 9007199254740992", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Big > 9007199254740992L).Select(e => R(e.Id))) { Note = "an INTEGER above 2^53" },
                new Case("where-021", "SELECT Id FROM Event WHERE Huge = 18446744073709551615", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Huge == ulong.MaxValue).Select(e => R(e.Id))) { Note = "ulong above 2^63 is DECIMAL" },
                new Case("where-022", "SELECT Id FROM Event WHERE Ratio = 0.1", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Ratio == 0.1).Select(e => R(e.Id))) { Note = "a float32 0.1 widened to DOUBLE is not 0.1: no rows" },
                new Case("where-023", "SELECT Id FROM Event WHERE Score > 1e20", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Score > 1e20).Select(e => R(e.Id))),
                new Case("where-024", "SELECT Id FROM Event WHERE Code = 'C'", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Code == 'C').Select(e => R(e.Id))) { Note = "char is TEXT" },
                new Case("where-025", "SELECT Id FROM Event WHERE Perms = 'Read, Write'", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Perms == (Perm.Read | Perm.Write)).Select(e => R(e.Id))) { Note = "a [Flags] enum compares by its stored name" },
                new Case("where-026", "SELECT Id FROM Event WHERE Ref = UUID '8f14e45f-ceea-467f-a0e6-a3d1f2c4b005'", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Ref == Guid.Parse("8f14e45f-ceea-467f-a0e6-a3d1f2c4b005")).Select(e => R(e.Id))),
                new Case("where-027", "SELECT Id FROM Event WHERE StartsUtc < TIMESTAMP '2026-11-04 00:00:00'", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.StartsUtc < new DateTime(2026, 11, 4)).Select(e => R(e.Id))),
                new Case("where-028", "SELECT Id FROM Event WHERE At > TIMESTAMPTZ '2026-11-11 12:05:00+00:00'", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.At > new DateTimeOffset(2026, 11, 11, 12, 5, 0, TimeSpan.Zero)).Select(e => R(e.Id))) { Note = "compares instants across offsets" },
                new Case("where-029", "SELECT Id FROM Event WHERE Opens >= TIME '12:00'", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Opens >= new TimeOnly(12, 0)).Select(e => R(e.Id))),
                new Case("where-030", "SELECT Id FROM Event WHERE Length > INTERVAL '05:00:00'", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Length > TimeSpan.FromHours(5)).Select(e => R(e.Id))),
                new Case("where-031", "SELECT Id FROM Event WHERE MaybeFlag = TRUE", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.MaybeFlag == true).Select(e => R(e.Id))),
                new Case("where-032", "SELECT Id FROM Event WHERE Day BETWEEN DATE '2026-03-01' AND DATE '2026-06-30'", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Day >= new DateOnly(2026, 3, 1) && e.Day <= new DateOnly(2026, 6, 30)).Select(e => R(e.Id))),
                new Case("where-033", "SELECT Id FROM Event WHERE Fee = 0.000000001", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Fee == 0.000000001m).Select(e => R(e.Id))) { Note = "Fee is indexed; its keys are exact past 8 places (ISS-053, fixed)" },
                new Case("where-034", "SELECT Id FROM Event WHERE Seats < 0", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Seats < 0).Select(e => R(e.Id))),
                new Case("where-035", "SELECT Id FROM Event WHERE Priority >= 200", "Id INTEGER", false,
                    s => s.Where<Event>(e => e.Priority >= 200).Select(e => R(e.Id))),
                new Case("where-036", "SELECT Id FROM Customer WHERE Email = 'lina3@example.com'", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.Email == "lina3@example.com").Select(c => R(c.Id))) { Note = "a [UniqueKey]" }
            );

            G("null",
                new Case("null-001", "SELECT Id FROM Customer WHERE City = NULL", "Id INTEGER", false,
                    s => s.All<Customer>().Where(c => false).Select(c => R(c.Id))) { Note = "= NULL matches nothing" },
                new Case("null-002", "SELECT Id FROM Customer WHERE NOT (Rating = 3)", "Id INTEGER", false,
                    s => s.All<Customer>().Where(c => c.Rating != null && c.Rating != 3).Select(c => R(c.Id))),
                new Case("null-003", "SELECT Id, COALESCE(City, 'unknown') AS city FROM Customer ORDER BY Id", "Id INTEGER, city TEXT", true,
                    s => s.All<Customer>().OrderBy(c => c.Id).Select(c => R(c.Id, c.City ?? "unknown"))),
                new Case("null-004", "SELECT Id, NULLIF(Tier, 'Basic') AS tier FROM Customer ORDER BY Id", "Id INTEGER, tier TEXT", true,
                    s => s.All<Customer>().OrderBy(c => c.Id).Select(c => R(c.Id, c.Tier == Tier.Basic ? null : c.Tier.ToString()))),
                new Case("null-005", "SELECT COUNT(Rating) AS rated, COUNT(*) AS customers FROM Customer", "rated INTEGER, customers INTEGER", true,
                    s => { var all = s.All<Customer>().ToList(); return new[] { R(all.LongCount(c => c.Rating != null), all.LongCount()) }; }),
                new Case("null-006", "SELECT Id FROM Customer WHERE Rating IS NULL OR Rating < 2", "Id INTEGER", false,
                    s => s.Where<Customer>(c => c.Rating == null || c.Rating < 2).Select(c => R(c.Id))),
                new Case("null-007", "SELECT Id, Rating FROM Customer ORDER BY Rating, Id", "Id INTEGER, Rating INTEGER", true,
                    s => s.All<Customer>().AscNullsLast(c => c.Rating).ThenBy(c => c.Id).Select(c => R(c.Id, c.Rating))) { Note = "NULLS LAST ascending by default" },
                new Case("null-008", "SELECT Id, Rating FROM Customer ORDER BY Rating DESC, Id", "Id INTEGER, Rating INTEGER", true,
                    s => s.All<Customer>().DescNullsFirst(c => c.Rating).ThenBy(c => c.Id).Select(c => R(c.Id, c.Rating))) { Note = "NULLS FIRST descending by default" },
                new Case("null-009", "SELECT Id, Rating FROM Customer ORDER BY Rating ASC NULLS FIRST, Id", "Id INTEGER, Rating INTEGER", true,
                    s => s.All<Customer>().OrderBy(c => c.Rating != null).ThenBy(c => c.Rating).ThenBy(c => c.Id).Select(c => R(c.Id, c.Rating)))
            );

            G("order",
                new Case("order-001", "SELECT Id, Name FROM Product ORDER BY Name", "Id INTEGER, Name TEXT", true,
                    s => s.All<Product>().OrderBy(p => p.Name, Ordinal).Select(p => R(p.Id, p.Name))),
                new Case("order-002", "SELECT Id, Price FROM Product ORDER BY Price DESC, Id LIMIT 5", "Id INTEGER, Price DECIMAL", true,
                    s => s.All<Product>().OrderByDescending(p => p.Price).ThenBy(p => p.Id).Take(5).Select(p => R(p.Id, p.Price))),
                new Case("order-003", "SELECT Id FROM Product ORDER BY Id LIMIT 4 OFFSET 4", "Id INTEGER", true,
                    s => s.All<Product>().OrderBy(p => p.Id).Skip(4).Take(4).Select(p => R(p.Id))),
                new Case("order-004", "SELECT Id FROM Product LIMIT 3", "Id INTEGER", true,
                    s => s.All<Product>().Take(3).Select(p => R(p.Id))) { Note = "stored order (D5)" },
                new Case("order-005", "SELECT Id FROM Product LIMIT 3 OFFSET 13", "Id INTEGER", true,
                    s => s.All<Product>().Skip(13).Take(3).Select(p => R(p.Id))) { Note = "fewer rows than the limit remain" },
                new Case("order-006", "SELECT Id, Category FROM Product ORDER BY Category, Id", "Id INTEGER, Category TEXT", true,
                    s => s.All<Product>().AscNullsLast(p => p.Category, Ordinal).ThenBy(p => p.Id).Select(p => R(p.Id, p.Category))),
                new Case("order-007", "SELECT Name FROM Customer ORDER BY City DESC, Name", "Name TEXT", true,
                    s => s.All<Customer>().DescNullsFirst(c => c.City, Ordinal).ThenBy(c => c.Name, Ordinal).Select(c => R(c.Name))),
                new Case("order-008", "SELECT Id, Title FROM Event ORDER BY StartsUtc DESC LIMIT 3", "Id INTEGER, Title TEXT", true,
                    s => s.All<Event>().OrderByDescending(e => e.StartsUtc).Take(3).Select(e => R(e.Id, e.Title))),
                new Case("order-009", "SELECT Id, Tier FROM Customer ORDER BY Tier, Id", "Id INTEGER, Tier TEXT", true,
                    s => s.All<Customer>().OrderBy(c => c.Tier.ToString(), Ordinal).ThenBy(c => c.Id).Select(c => R(c.Id, c.Tier))) { Note = "enums order by name in v1" }
            );

            G("aggregate",
                new Case("aggregate-001", "SELECT COUNT(*) AS n FROM Customer WHERE Active = TRUE", "n INTEGER", true,
                    s => new[] { R(s.Where<Customer>(c => c.Active).LongCount()) }),
                new Case("aggregate-002", "SELECT Tier, COUNT(*) AS n FROM Customer GROUP BY Tier ORDER BY Tier", "Tier TEXT, n INTEGER", true,
                    s => s.All<Customer>().GroupBy(c => c.Tier.ToString()).OrderBy(g => g.Key, Ordinal).Select(g => R(g.Key, g.LongCount()))),
                new Case("aggregate-003", "SELECT Status, COUNT(*) AS n, SUM(Discount) AS discounts FROM \"Order\" GROUP BY Status ORDER BY Status", "Status TEXT, n INTEGER, discounts DECIMAL", true,
                    s => s.All<Order>().GroupBy(o => o.Status).OrderBy(g => g.Key, Ordinal).Select(g => R(g.Key, g.LongCount(), SumOrNull(g.Select(o => o.Discount))))) { Note = "SUM skips NULLs" },
                new Case("aggregate-004", "SELECT OrderId, SUM(Quantity) AS qty FROM OrderLine GROUP BY OrderId HAVING SUM(Quantity) > 4 ORDER BY OrderId", "OrderId INTEGER, qty INTEGER", true,
                    s => s.All<OrderLine>().GroupBy(l => l.OrderId).Where(g => g.Sum(l => (long)l.Quantity) > 4).OrderBy(g => g.Key).Select(g => R(g.Key, g.Sum(l => (long)l.Quantity)))),
                new Case("aggregate-005", "SELECT AVG(Quantity) AS avg_qty FROM OrderLine", "avg_qty DOUBLE", true,
                    s => new[] { R(s.All<OrderLine>().Average(l => (double)l.Quantity)) }) { Note = "AVG of INTEGER is DOUBLE" },
                new Case("aggregate-006", "SELECT AVG(Price) AS avg_price FROM Product", "avg_price DECIMAL", true,
                    s => new[] { R(s.All<Product>().Average(p => p.Price)) }) { Note = "AVG of DECIMAL stays DECIMAL" },
                new Case("aggregate-007", "SELECT MIN(Joined) AS earliest, MAX(Joined) AS latest FROM Customer", "earliest TIMESTAMP, latest TIMESTAMP", true,
                    s => { var all = s.All<Customer>().ToList(); return new[] { R(all.Min(c => c.Joined), all.Max(c => c.Joined)) }; }),
                new Case("aggregate-008", "SELECT COUNT(DISTINCT City) AS cities FROM Customer", "cities INTEGER", true,
                    s => new[] { R(s.All<Customer>().Where(c => c.City != null).Select(c => c.City).Distinct().LongCount()) }),
                new Case("aggregate-009", "SELECT Category, COUNT(*) AS n, MAX(Price) AS max_price FROM Product GROUP BY Category ORDER BY Category", "Category TEXT, n INTEGER, max_price DECIMAL", true,
                    s => s.All<Product>().GroupBy(p => p.Category).AscNullsLast(g => g.Key, Ordinal).Select(g => R(g.Key, g.LongCount(), g.Max(p => p.Price)))) { Note = "NULL is a group" },
                new Case("aggregate-010", "SELECT SUM(Big) AS total FROM Event", "total INTEGER", true,
                    s => new[] { R(s.All<Event>().Sum(e => e.Big)) }) { Note = "an INTEGER beyond 2^53 is a JSON string" },
                new Case("aggregate-011", "SELECT MAX(Huge) AS m FROM Event", "m DECIMAL", true,
                    s => new[] { R(s.All<Event>().Max(e => e.Huge)) }),
                new Case("aggregate-012", "SELECT SUM(Quantity * UnitPrice) AS revenue FROM OrderLine", "revenue DECIMAL", true,
                    s => new[] { R(s.All<OrderLine>().Sum(l => l.Quantity * l.UnitPrice)) }),
                new Case("aggregate-013", "SELECT AVG(Quantity) AS a, COUNT(*) AS n FROM OrderLine WHERE Quantity > 100", "a DOUBLE, n INTEGER", true,
                    s => { var none = s.Where<OrderLine>(l => l.Quantity > 100).ToList(); return new[] { R(none.Count == 0 ? null : (object)none.Average(l => (double)l.Quantity), none.LongCount()) }; }) { Note = "aggregates of no rows: AVG is NULL, COUNT is 0" }
            );

            G("join",
                new Case("join-001", "SELECT c.Name, o.Id FROM Customer c JOIN \"Order\" o ON o.CustomerId = c.Id ORDER BY o.Id", "Name TEXT, Id INTEGER", true,
                    s => from o in s.All<Order>() join c in s.All<Customer>() on o.CustomerId equals c.Id orderby o.Id select R(c.Name, o.Id)),
                new Case("join-002", "SELECT c.Id, o.Id AS order_id FROM Customer c LEFT JOIN \"Order\" o ON o.CustomerId = c.Id ORDER BY c.Id, order_id", "Id INTEGER, order_id INTEGER", true,
                    s =>
                    {
                        var orders = s.All<Order>().ToList();
                        return s.All<Customer>()
                            .SelectMany(c => orders.Where(o => o.CustomerId == c.Id).Select(o => (c.Id, Order: (int?)o.Id)).DefaultIfEmpty((c.Id, Order: null)))
                            .OrderBy(x => x.Item1).ThenBy(x => x.Order == null).ThenBy(x => x.Order)
                            .Select(x => R(x.Item1, x.Order));
                    }) { Note = "customers without orders keep a row with NULL" },
                new Case("join-003", "SELECT c.Name, COUNT(o.Id) AS orders FROM Customer c LEFT JOIN \"Order\" o ON o.CustomerId = c.Id GROUP BY c.Name ORDER BY c.Name", "Name TEXT, orders INTEGER", true,
                    s =>
                    {
                        var orders = s.All<Order>().ToList();
                        return s.All<Customer>().OrderBy(c => c.Name, Ordinal).Select(c => R(c.Name, orders.LongCount(o => o.CustomerId == c.Id)));
                    }),
                new Case("join-004", "SELECT o.Id, o.Customer.Name AS customer FROM \"Order\" o WHERE o.Status = 'paid' ORDER BY o.Id", "Id INTEGER, customer TEXT", true,
                    s =>
                    {
                        var customers = s.All<Customer>().ToDictionary(c => c.Id);
                        return s.Where<Order>(o => o.Status == "paid").OrderBy(o => o.Id).Select(o => R(o.Id, customers[o.CustomerId].Name));
                    }) { Note = "a to-one path (section 6)" },
                new Case("join-005", "SELECT l.Id, l.Order.Customer.Name AS customer, l.Product.Name AS product FROM OrderLine l WHERE l.Quantity = 4 ORDER BY l.Id", "Id INTEGER, customer TEXT, product TEXT", true,
                    s =>
                    {
                        var customers = s.All<Customer>().ToDictionary(c => c.Id);
                        var orders = s.All<Order>().ToDictionary(o => o.Id);
                        var products = s.All<Product>().ToDictionary(p => p.Id);
                        return s.Where<OrderLine>(l => l.Quantity == 4).OrderBy(l => l.Id)
                            .Select(l => R(l.Id, customers[orders[l.OrderId].CustomerId].Name, products[l.ProductId].Name));
                    }) { Note = "a two-step path and a reference path" },
                new Case("join-006", "SELECT c.Name, COUNT(o.Id) AS orders, SUM(l.Quantity) AS qty FROM Customer c LEFT JOIN c.Orders o LEFT JOIN o.Lines l GROUP BY c.Name ORDER BY c.Name", "Name TEXT, orders INTEGER, qty INTEGER", true,
                    s =>
                    {
                        var orders = s.All<Order>().ToList();
                        var lines = s.All<OrderLine>().ToList();
                        return s.All<Customer>().OrderBy(c => c.Name, Ordinal).Select(c =>
                        {
                            // One joined row per (order, line), a NULL line when an order has none, a NULL order when the customer has none.
                            var rows = orders.Where(o => o.CustomerId == c.Id)
                                .SelectMany(o => lines.Where(l => l.OrderId == o.Id).Select(l => ((int?)o.Id, (int?)l.Quantity)).DefaultIfEmpty(((int?)o.Id, null)))
                                .DefaultIfEmpty((null, null)).ToList();
                            return R(c.Name, rows.LongCount(r => r.Item1 != null), SumOrNull(rows.Select(r => r.Item2)));
                        });
                    }) { Note = "children joins; COUNT(o.Id) counts joined rows, as in SQL" },
                new Case("join-007", "SELECT o.Id, COUNT(*) AS lines FROM \"Order\" o JOIN o.Lines l GROUP BY o.Id ORDER BY o.Id", "Id INTEGER, lines INTEGER", true,
                    s =>
                    {
                        var lines = s.All<OrderLine>().ToList();
                        return s.All<Order>().Where(o => lines.Any(l => l.OrderId == o.Id)).OrderBy(o => o.Id).Select(o => R(o.Id, lines.LongCount(l => l.OrderId == o.Id)));
                    }) { Note = "an inner children join drops orders without lines" },
                new Case("join-008", "SELECT c.Name, (SELECT COUNT(*) FROM c.Orders) AS n FROM Customer c ORDER BY c.Id", "Name TEXT, n INTEGER", true,
                    s => s.All<Customer>().OrderBy(c => c.Id).Select(c => R(c.Name, (long)(c.Orders?.Count ?? 0)))) { Note = "a collection as a subquery source; uses the loaded children" },
                new Case("join-009", "SELECT Name FROM Customer c WHERE NOT EXISTS (SELECT 1 FROM \"Order\" o WHERE o.CustomerId = c.Id) ORDER BY Name", "Name TEXT", true,
                    s =>
                    {
                        var orders = s.All<Order>().ToList();
                        return s.All<Customer>().Where(c => !orders.Any(o => o.CustomerId == c.Id)).OrderBy(c => c.Name, Ordinal).Select(c => R(c.Name));
                    }),
                new Case("join-010", "SELECT Name FROM Product WHERE Id IN (SELECT ProductId FROM OrderLine WHERE Quantity >= 4) ORDER BY Name", "Name TEXT", true,
                    s =>
                    {
                        var ids = s.Where<OrderLine>(l => l.Quantity >= 4).Select(l => l.ProductId).ToHashSet();
                        return s.All<Product>().Where(p => ids.Contains(p.Id)).OrderBy(p => p.Name, Ordinal).Select(p => R(p.Name));
                    }),
                new Case("join-011", "SELECT Sku FROM Product WHERE Id NOT IN (SELECT ProductId FROM OrderLine) ORDER BY Sku", "Sku TEXT", true,
                    s =>
                    {
                        var ids = s.All<OrderLine>().Select(l => l.ProductId).ToHashSet();
                        return s.All<Product>().Where(p => !ids.Contains(p.Id)).OrderBy(p => p.Sku, Ordinal).Select(p => R(p.Sku));
                    }),
                new Case("join-012", "SELECT Id FROM Product WHERE Price > (SELECT AVG(Price) FROM Product) ORDER BY Id", "Id INTEGER", true,
                    s =>
                    {
                        var all = s.All<Product>().ToList();
                        decimal avg = all.Average(p => p.Price);
                        return all.Where(p => p.Price > avg).OrderBy(p => p.Id).Select(p => R(p.Id));
                    }),
                new Case("join-013", "SELECT t.Status, t.n FROM (SELECT Status, COUNT(*) AS n FROM \"Order\" GROUP BY Status) AS t WHERE t.n >= 3 ORDER BY t.Status", "Status TEXT, n INTEGER", true,
                    s => s.All<Order>().GroupBy(o => o.Status).Where(g => g.Count() >= 3).OrderBy(g => g.Key, Ordinal).Select(g => R(g.Key, g.LongCount()))),
                new Case("join-014", "SELECT COUNT(*) AS n FROM Customer CROSS JOIN \"Xod.Conformance.Model.Shop.Note\"", "n INTEGER", true,
                    s => new[] { R((long)s.All<Customer>().Count() * s.All<Model.Shop.Note>().Count()) }),
                new Case("join-015", "SELECT c.Id, c.Address.Country AS country FROM Customer c ORDER BY c.Id", "Id INTEGER, country TEXT", true,
                    s => s.All<Customer>().OrderBy(c => c.Id).Select(c => R(c.Id, c.Address?.Country))) { Note = "a path into an owned (complex) object; missing gives NULL" }
            );

            G("function",
                new Case("function-001", "SELECT Name, LOWER(Name) AS lo, UPPER(Name) AS up, LENGTH(Name) AS len FROM Customer WHERE Id <= 3 ORDER BY Id", "Name TEXT, lo TEXT, up TEXT, len INTEGER", true,
                    s => s.Where<Customer>(c => c.Id <= 3).OrderBy(c => c.Id).Select(c => R(c.Name, c.Name.ToLowerInvariant(), c.Name.ToUpperInvariant(), (long)c.Name.Length))),
                new Case("function-002", "SELECT Sku, SUBSTRING(Sku, 5, 3) AS num FROM Product ORDER BY Id LIMIT 3", "Sku TEXT, num TEXT", true,
                    s => s.All<Product>().OrderBy(p => p.Id).Take(3).Select(p => R(p.Sku, p.Sku.Substring(4, 3)))) { Note = "SUBSTRING counts from 1" },
                new Case("function-003", "SELECT REPLACE(Email, '@example.com', '') AS handle FROM Customer ORDER BY Id LIMIT 3", "handle TEXT", true,
                    s => s.All<Customer>().OrderBy(c => c.Id).Take(3).Select(c => R(c.Email.Replace("@example.com", "")))),
                new Case("function-004", "SELECT Id, POSITION('a' IN Name) AS p FROM Customer WHERE Id <= 4 ORDER BY Id", "Id INTEGER, p INTEGER", true,
                    s => s.Where<Customer>(c => c.Id <= 4).OrderBy(c => c.Id).Select(c => R(c.Id, (long)(c.Name.IndexOf('a', StringComparison.Ordinal) + 1)))) { Note = "1-based; 0 when absent" },
                new Case("function-005", "SELECT TRIM('  padded ') AS t FROM Customer WHERE Id = 1", "t TEXT", true,
                    s => s.Where<Customer>(c => c.Id == 1).Select(c => R("padded"))),
                new Case("function-006", "SELECT Id, ROUND(Price / 3, 2) AS third FROM Product WHERE Id <= 4 ORDER BY Id", "Id INTEGER, third DECIMAL", true,
                    s => s.Where<Product>(p => p.Id <= 4).OrderBy(p => p.Id).Select(p => R(p.Id, Round(p.Price / 3, 2)))),
                new Case("function-007", "SELECT Id, ABS(Seats) AS a, FLOOR(Score) AS f, CEIL(Score) AS c FROM Event WHERE Id <= 3 ORDER BY Id", "Id INTEGER, a INTEGER, f DOUBLE, c DOUBLE", true,
                    s => s.Where<Event>(e => e.Id <= 3).OrderBy(e => e.Id).Select(e => R(e.Id, (long)Math.Abs((int)e.Seats), Math.Floor(e.Score), Math.Ceiling(e.Score)))),
                new Case("function-008", "SELECT Id, EXTRACT(YEAR FROM Placed) AS y, EXTRACT(MONTH FROM Placed) AS m FROM \"Order\" WHERE Id <= 3 ORDER BY Id", "Id INTEGER, y INTEGER, m INTEGER", true,
                    s => s.Where<Order>(o => o.Id <= 3).OrderBy(o => o.Id).Select(o => R(o.Id, (long)o.Placed.Year, (long)o.Placed.Month))),
                new Case("function-009", "SELECT Id, DATE_TRUNC('month', Joined) AS month_start FROM Customer WHERE Id <= 3 ORDER BY Id", "Id INTEGER, month_start TIMESTAMP", true,
                    s => s.Where<Customer>(c => c.Id <= 3).OrderBy(c => c.Id).Select(c => R(c.Id, new DateTime(c.Joined.Year, c.Joined.Month, 1, 0, 0, 0, c.Joined.Kind)))),
                new Case("function-010", "SELECT Id, CASE WHEN Price >= 20 THEN 'high' WHEN Price >= 10 THEN 'mid' ELSE 'low' END AS band FROM Product ORDER BY Id", "Id INTEGER, band TEXT", true,
                    s => s.All<Product>().OrderBy(p => p.Id).Select(p => R(p.Id, p.Price >= 20m ? "high" : p.Price >= 10m ? "mid" : "low"))),
                new Case("function-011", "SELECT CAST(Id AS TEXT) || '-' || Sku AS label FROM Product WHERE Id <= 2 ORDER BY Id", "label TEXT", true,
                    s => s.Where<Product>(p => p.Id <= 2).OrderBy(p => p.Id).Select(p => R(p.Id + "-" + p.Sku))),
                new Case("function-012", "SELECT Name FROM Product WHERE Name LIKE 'T%' ORDER BY Name", "Name TEXT", true,
                    s => s.All<Product>().Where(p => Like(p.Name, "T%") == true).OrderBy(p => p.Name, Ordinal).Select(p => R(p.Name))),
                new Case("function-013", "SELECT Name FROM Product WHERE Name LIKE 't%' ORDER BY Name", "Name TEXT", true,
                    s => s.All<Product>().Where(p => Like(p.Name, "t%") == true).OrderBy(p => p.Name, Ordinal).Select(p => R(p.Name))) { Note = "LIKE is case-sensitive (D4)" },
                new Case("function-014", "SELECT Name FROM Product WHERE Name ILIKE 't%' ORDER BY Name", "Name TEXT", true,
                    s => s.All<Product>().Where(p => Like(p.Name, "t%", ignoreCase: true) == true).OrderBy(p => p.Name, Ordinal).Select(p => R(p.Name))),
                new Case("function-015", "SELECT Name FROM Product WHERE Name LIKE '_a%' ORDER BY Name", "Name TEXT", true,
                    s => s.All<Product>().Where(p => Like(p.Name, "_a%") == true).OrderBy(p => p.Name, Ordinal).Select(p => R(p.Name))),
                new Case("function-016", "SELECT 'a_b' LIKE 'a!_b' ESCAPE '!' AS m1, 'axb' LIKE 'a!_b' ESCAPE '!' AS m2 FROM Customer WHERE Id = 1", "m1 BOOLEAN, m2 BOOLEAN", true,
                    s => s.Where<Customer>(c => c.Id == 1).Select(c => R(Like("a_b", "a!_b", '!'), Like("axb", "a!_b", '!')))),
                new Case("function-017", "SELECT Id FROM Customer WHERE VERIFY_HASH(PasswordHash, 'pw-5')", "Id INTEGER", false,
                    s => s.All<Customer>().Where(c => CryptoHelper.VerifyHash("pw-5", c.PasswordHash)).Select(c => R(c.Id))),
                new Case("function-018", "SELECT PasswordHash FROM Customer WHERE Id = 1", "PasswordHash TEXT", true,
                    s => s.Where<Customer>(c => c.Id == 1).Select(c => R(c.PasswordHash))) { Note = "a hashed column reads as its hash" }
            );

            G("collection",
                new Case("collection-001", "SELECT Id, Tags['segment'] AS segment FROM Customer ORDER BY Id", "Id INTEGER, segment TEXT", true,
                    s => s.All<Customer>().OrderBy(c => c.Id).Select(c => R(c.Id, c.Tags != null && c.Tags.TryGetValue("segment", out var v) ? v : null))),
                new Case("collection-002", "SELECT Id FROM Customer WHERE Tags['source'] = 'web'", "Id INTEGER", false,
                    s => s.All<Customer>().Where(c => c.Tags != null && c.Tags.TryGetValue("source", out var v) && v == "web").Select(c => R(c.Id))),
                new Case("collection-003", "SELECT Id FROM Customer WHERE 'vip' = ANY(Labels)", "Id INTEGER", false,
                    s => s.All<Customer>().Where(c => c.Labels != null && c.Labels.Contains("vip")).Select(c => R(c.Id))),
                new Case("collection-004", "SELECT Id, CARDINALITY(Labels) AS n FROM Customer ORDER BY Id", "Id INTEGER, n INTEGER", true,
                    s => s.All<Customer>().OrderBy(c => c.Id).Select(c => R(c.Id, c.Labels == null ? null : (object)(long)c.Labels.Count))) { Note = "CARDINALITY(NULL) is NULL" },
                new Case("collection-005", "SELECT Id, Labels FROM Customer WHERE Id = 2", "Id INTEGER, Labels ARRAY", true,
                    s => s.Where<Customer>(c => c.Id == 2).Select(c => R(c.Id, c.Labels))),
                new Case("collection-006", "SELECT Id, Tags FROM Customer WHERE Id = 1", "Id INTEGER, Tags MAP", true,
                    s => s.Where<Customer>(c => c.Id == 1).Select(c => R(c.Id, c.Tags)))
            );

            G("type",
                new Case("type-001", "SELECT Id, Priority, Seats, Big, Huge FROM Event ORDER BY Id", "Id INTEGER, Priority INTEGER, Seats INTEGER, Big INTEGER, Huge DECIMAL", true,
                    s => s.All<Event>().OrderBy(e => e.Id).Select(e => R(e.Id, e.Priority, e.Seats, e.Big, e.Huge))),
                new Case("type-002", "SELECT Id, Ratio, Score, Price, Fee FROM Event ORDER BY Id", "Id INTEGER, Ratio DOUBLE, Score DOUBLE, Price DECIMAL, Fee DECIMAL", true,
                    s => s.All<Event>().OrderBy(e => e.Id).Select(e => R(e.Id, e.Ratio, e.Score, e.Price, e.Fee))) { Note = "a float32 column widens to DOUBLE: 0.1f is 0.10000000149011612" },
                new Case("type-003", "SELECT Id, Code, Perms, Ref, Html FROM Event ORDER BY Id", "Id INTEGER, Code TEXT, Perms TEXT, Ref UUID, Html TEXT", true,
                    s => s.All<Event>().OrderBy(e => e.Id).Select(e => R(e.Id, e.Code, e.Perms, e.Ref, e.Html))),
                new Case("type-004", "SELECT Id, StartsUtc, Floating, At FROM Event ORDER BY Id", "Id INTEGER, StartsUtc TIMESTAMP, Floating TIMESTAMP, At TIMESTAMPTZ", true,
                    s => s.All<Event>().OrderBy(e => e.Id).Select(e => R(e.Id, e.StartsUtc, e.Floating, e.At))) { Note = "a UTC timestamp keeps its Z; an unspecified one has no suffix" },
                new Case("type-005", "SELECT Id, Day, Opens, Length FROM Event ORDER BY Id", "Id INTEGER, Day DATE, Opens TIME, Length INTERVAL", true,
                    s => s.All<Event>().OrderBy(e => e.Id).Select(e => R(e.Id, e.Day, e.Opens, e.Length))),
                new Case("type-006", "SELECT Id, MaybeInt, MaybeWhen, MaybeFlag FROM Event ORDER BY Id", "Id INTEGER, MaybeInt INTEGER, MaybeWhen TIMESTAMP, MaybeFlag BOOLEAN", true,
                    s => s.All<Event>().OrderBy(e => e.Id).Select(e => R(e.Id, e.MaybeInt, e.MaybeWhen, e.MaybeFlag))),
                new Case("type-007", "SELECT Id, Joined, Active, CreditLimit, Rating FROM Customer ORDER BY Id", "Id INTEGER, Joined TIMESTAMP, Active BOOLEAN, CreditLimit DECIMAL, Rating INTEGER", true,
                    s => s.All<Customer>().OrderBy(c => c.Id).Select(c => R(c.Id, c.Joined, c.Active, c.CreditLimit, c.Rating)))
            );

            G("name",
                new Case("name-001", "SELECT Text FROM \"Xod.Conformance.Model.Shop.Note\" ORDER BY Id", "Text TEXT", true,
                    s => s.All<Model.Shop.Note>().OrderBy(n => n.Id).Select(n => R(n.Text))) { Note = "a full name, quoted" },
                new Case("name-002", "SELECT Text, Year FROM \"Xod.Conformance.Model.Archive.Note\" ORDER BY Id", "Text TEXT, Year INTEGER", true,
                    s => s.All<Model.Archive.Note>().OrderBy(n => n.Id).Select(n => R(n.Text, n.Year))),
                new Case("name-003", "select name from customer where id = 1", "Name TEXT", true,
                    s => s.Where<Customer>(c => c.Id == 1).Select(c => R(c.Name))) { Note = "names are case-insensitive (D2); the column keeps its own name" },
                new Case("name-004", "SELECT [Name] FROM [Customer] WHERE [Id] = 1", "Name TEXT", true,
                    s => s.Where<Customer>(c => c.Id == 1).Select(c => R(c.Name))) { Note = "[...] quoting (D6)" }
            );

            G("crypto",
                new Case("crypto-001", "SELECT Name, ApiKey FROM Integration ORDER BY Id", "Name TEXT, ApiKey TEXT", true,
                    s => s.All<Integration>().OrderBy(i => i.Id).Select(i => R(i.Name, i.ApiKey))) { Requires = "password", Note = "an encrypted column, opened with the password" },
                new Case("crypto-002", "SELECT Name, ApiKey FROM Integration ORDER BY Id", "Name TEXT, ApiKey TEXT", true,
                    s => s.All<Integration>().OrderBy(i => i.Id).Select(i => R(i.Name, (string)null)))
                {
                    Requires = "no-password", RunOnXod = false,
                    Note = "without the password an encrypted column reads as NULL with warning XQ008; the C# library instead decrypts with its built-in key (ISS-050)"
                }
            );

            G("error",
                Err("error-001", "SELEC Id FROM Customer", "XQ001"),
                Err("error-002", "SELECT Id FROM Customer WHERE Name = 'x' AND", "XQ001"),
                Err("error-003", "SELECT Id FROM Order", "XQ001", "Order is reserved; quote it"),
                Err("error-004", "SELECT Id FROM Nope", "XQ002"),
                Err("error-005", "SELECT Text FROM Note", "XQ003", "two tables are named Note"),
                Err("error-006", "SELECT Nope FROM Customer", "XQ004"),
                Err("error-007", "SELECT o.Buyer.Name FROM \"Order\" o", "XQ004"),
                Err("error-008", "SELECT Name + 1 AS x FROM Customer", "XQ005"),
                Err("error-009", "SELECT Tier, Name FROM Customer GROUP BY Tier", "XQ006"),
                Err("error-010", "SELECT Big - 1 AS b FROM Event WHERE Id = 4", "XQ007", "long.MinValue - 1")
            );

            var dupes = list.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0) throw new InvalidOperationException("duplicate case ids: " + string.Join(", ", dupes));
            return list;
        }

        private static Case Err(string id, string sql, string code, string note = null) =>
            new(id, sql, null, false, null) { Error = code, RunOnXod = false, Note = note };
    }
}
