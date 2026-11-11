// The conformance dataset (PLAN-047): fixed values, no clocks, no random numbers, so the same objects
// come out on every machine and runtime. Build() returns fresh objects each call: the library may change
// objects it inserts, and the oracle must see the data as written here.
using System;
using System.Collections.Generic;
using System.Linq;
using Xod.Conformance.Model;
using Xod.Helpers;

namespace Xod.Conformance
{
    public sealed class Dataset
    {
        // Lists are in insertion order, which is the stored order (PLAN-046 section 6).
        public List<Product> Products { get; } = new();
        public List<Customer> Customers { get; } = new();
        public List<Event> Events { get; } = new();
        public List<Integration> Integrations { get; } = new();
        public List<Model.Shop.Note> ShopNotes { get; } = new();
        public List<Model.Archive.Note> ArchiveNotes { get; } = new();

        public IEnumerable<Order> Orders => Customers.SelectMany(c => c.Orders ?? new List<Order>());
        public IEnumerable<OrderLine> Lines => Orders.SelectMany(o => o.Lines ?? new List<OrderLine>());

        public const string Password = "conformance-password";

        public IEnumerable<T> All<T>() => typeof(T) switch
        {
            var t when t == typeof(Product) => Products.Cast<T>(),
            var t when t == typeof(Customer) => Customers.Cast<T>(),
            var t when t == typeof(Order) => Orders.Cast<T>(),
            var t when t == typeof(OrderLine) => Lines.Cast<T>(),
            var t when t == typeof(Event) => Events.Cast<T>(),
            var t when t == typeof(Integration) => Integrations.Cast<T>(),
            var t when t == typeof(Model.Shop.Note) => ShopNotes.Cast<T>(),
            var t when t == typeof(Model.Archive.Note) => ArchiveNotes.Cast<T>(),
            _ => throw new ArgumentException("not in the dataset: " + typeof(T).FullName)
        };

        public static Dataset Build()
        {
            var d = new Dataset();

            // Products: inserted in this order, so the stored order is not Id order.
            string[] categories = { "kitchen", "garden", "office", "toys", null };
            int[] insertOrder = { 7, 3, 12, 1, 15, 9, 4, 11, 2, 14, 6, 10, 5, 13, 8 };
            foreach (int id in insertOrder)
            {
                d.Products.Add(new Product
                {
                    Id = id,
                    Sku = $"SKU-{id:D3}",
                    Name = ProductNames[id - 1],
                    Category = id == 13 ? null : categories[id % 4],
                    Price = 2.5m * id + (id % 3 == 0 ? 0.99m : 0m),
                    Weight = id % 5 == 0 ? null : 0.25 * id,
                    Discontinued = id % 6 == 0,
                });
            }

            int orderId = 0, lineId = 0;
            for (int i = 1; i <= 12; i++)
            {
                var c = new Customer
                {
                    Id = i,
                    Name = CustomerNames[i - 1],
                    Email = $"{CustomerNames[i - 1].Split(' ')[0].ToLowerInvariant()}{i}@example.com",
                    Tier = (Tier)(i % 3),
                    City = i % 5 == 0 ? null : Cities[i % Cities.Length],
                    Rating = i % 4 == 0 ? null : (i * 7) % 5 + 1,
                    CreditLimit = 500m * (i % 4) + 0.50m * (i % 2),
                    Joined = new DateTime(2025, 1 + i % 12, 1 + (i * 3) % 28, 8 + i % 9, (i * 11) % 60, 0, DateTimeKind.Utc),
                    Active = i % 7 != 0,
                    PasswordHash = CryptoHelper.GetSHA256HashData("pw-" + i),
                    Tags = i % 3 == 0 ? null : new Dictionary<string, string> { ["segment"] = i % 2 == 0 ? "retail" : "trade", ["source"] = i < 6 ? "web" : "store" },
                    Labels = i % 4 == 1 ? null : Enumerable.Range(0, 1 + i % 3).Select(k => LabelWords[(i + k) % LabelWords.Length]).ToList(),
                    Address = i % 3 == 2 ? null : new Address { City = Cities[(i + 1) % Cities.Length], Country = i % 2 == 0 ? "SA" : "AE" },
                    Orders = new List<Order>(),
                };

                int orders = i % 4; // customers 4, 8 and 12 have none
                for (int k = 0; k < orders; k++)
                {
                    orderId++;
                    var o = new Order
                    {
                        Id = orderId,
                        CustomerId = i,
                        Status = Statuses[(i + k) % Statuses.Length],
                        Placed = new DateOnly(2026, 1 + (orderId % 9), 1 + (orderId * 5) % 27),
                        Discount = orderId % 3 == 0 ? null : 0.05m * (orderId % 4),
                        Lines = new List<OrderLine>(),
                    };
                    int lines = orderId % 5 == 0 ? 0 : 1 + orderId % 3; // every fifth order has no lines
                    for (int l = 0; l < lines; l++)
                    {
                        lineId++;
                        int productId = 1 + (orderId * 3 + l * 5) % 15;
                        o.Lines.Add(new OrderLine
                        {
                            Id = lineId,
                            OrderId = orderId,
                            ProductId = productId,
                            Quantity = 1 + (lineId * 7) % 4,
                            UnitPrice = 2.5m * productId + (productId % 3 == 0 ? 0.99m : 0m),
                        });
                    }
                    c.Orders.Add(o);
                }
                d.Customers.Add(c);
            }

            for (int i = 1; i <= 10; i++)
            {
                d.Events.Add(new Event
                {
                    Id = i,
                    Title = EventTitles[i - 1],
                    Priority = (byte)(i * 25),
                    Seats = (short)(i % 2 == 0 ? -i * 100 : i * 1000),
                    Big = i == 3 ? 9007199254740993L : i == 4 ? long.MinValue : i * 1_000_000_007L,
                    Huge = i == 5 ? ulong.MaxValue : (ulong)i * 10_000_000_000UL,
                    Ratio = i == 2 ? 0.1f : i * 0.25f,
                    Score = i == 6 ? 1e30 : i == 7 ? -0.5 : i / 8.0,
                    Price = i == 8 ? 12.50m : i * 3.75m,
                    Code = (char)('A' + i - 1),
                    Perms = (Perm)(i % 8),
                    Ref = Guid.Parse($"8f14e45f-ceea-467f-a0e6-a3d1f2c4b0{i:D2}"),
                    StartsUtc = new DateTime(2026, 11, i, 9, 30, 15, DateTimeKind.Utc).AddTicks(i * 1234567),
                    Floating = new DateTime(2026, 12, i, 18, 0, 0, DateTimeKind.Unspecified),
                    At = new DateTimeOffset(2026, 11, 11, 12, i, 0, TimeSpan.FromHours(new[] { 0, 3, -5, 5.5 }[i % 4])),
                    Day = new DateOnly(2026, 1 + i, 2 * i),
                    Opens = new TimeOnly(6 + i, i * 5),
                    Length = i == 9 ? new TimeSpan(1, 2, 3, 4) : TimeSpan.FromMinutes(i * 45),
                    MaybeInt = i % 3 == 0 ? null : i * i,
                    MaybeWhen = i % 2 == 0 ? null : new DateTime(2027, i, 1, 0, 0, 0, DateTimeKind.Utc),
                    MaybeFlag = i % 3 == 0 ? null : i % 2 == 0,
                    Html = i % 4 == 0 ? null : $"<p>Event <b>{i}</b> &amp; more</p>",
                    Fee = i == 1 ? 0.000000001m : i == 2 ? 0.000000002m : i * 1.5m,
                });
            }

            d.Integrations.Add(new Integration { Id = 1, Name = "billing", ApiKey = "sk-billing-0001" });
            d.Integrations.Add(new Integration { Id = 2, Name = "mail", ApiKey = "sk-mail-0002" });
            d.Integrations.Add(new Integration { Id = 3, Name = "maps", ApiKey = null });

            d.ShopNotes.Add(new Model.Shop.Note { Id = 1, Text = "restock kettles" });
            d.ShopNotes.Add(new Model.Shop.Note { Id = 2, Text = "window display" });
            d.ShopNotes.Add(new Model.Shop.Note { Id = 3, Text = "staff rota" });
            d.ArchiveNotes.Add(new Model.Archive.Note { Id = 1, Text = "2019 audit", Year = 2019 });
            d.ArchiveNotes.Add(new Model.Archive.Note { Id = 2, Text = "2021 move", Year = 2021 });

            return d;
        }

        /// <summary>Writes the dataset into an empty folder with the 2.0 library.</summary>
        public static void Write(string folder, StorageFormat format, string password)
        {
            var d = Build();
            var options = new DatabaseOptions { StorageFormat = format };
            using var db = new XodContext(System.IO.Path.Combine(folder, "app.xod"), password, options);
            foreach (var p in d.Products) db.Insert(p);
            foreach (var c in d.Customers) db.Insert(c);
            foreach (var e in d.Events) db.Insert(e);
            foreach (var x in d.Integrations) db.Insert(x);
            foreach (var n in d.ShopNotes) db.Insert(n);
            foreach (var n in d.ArchiveNotes) db.Insert(n);
        }

        private static readonly string[] ProductNames =
        {
            "Kettle", "Toaster", "Trowel", "Stapler", "Teapot", "Rake", "Notebook", "Kite",
            "Blender", "Hose", "Lamp", "Puzzle", "Mug", "Shears", "Desk",
        };
        private static readonly string[] CustomerNames =
        {
            "Nadia Karim", "Omar Saleh", "Lina Haddad", "Yusuf Amin", "Sara Nasser", "Adam Fares",
            "Huda Ali", "Karim Zaid", "Mona Rahman", "Tariq Aziz", "Rania Odeh", "Zain Musa",
        };
        private static readonly string[] Cities = { "Riyadh", "Jeddah", "Dubai", "Amman", "Cairo", "Doha" };
        private static readonly string[] Statuses = { "open", "paid", "shipped", "cancelled" };
        private static readonly string[] LabelWords = { "vip", "new", "wholesale", "late-payer", "newsletter" };
        private static readonly string[] EventTitles =
        {
            "Launch", "Workshop", "Webinar", "Meetup", "Hackathon", "Gala", "Fair", "Expo", "Summit", "Retreat",
        };
    }
}
