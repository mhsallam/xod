using System;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-138: a referenced object read from the disk, never from what an earlier read left. The objects a read
    /// builds for references are shared within that read (one instance per row it references), and with nothing
    /// after it: another read, a write in between (this context's or another's), or a caller that changed what it
    /// was handed.
    /// </summary>
    public class XodItemCacheTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-itemcache-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class CacheCustomer
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class CacheOrder
        {
            public int Id { get; set; }
            public string Note { get; set; }
            public CacheCustomer Customer { get; set; }
        }

        private string NewDb(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "cache.xod");
        }

        private static CacheCustomer Seed(XodContext db, int orders = 1)
        {
            var c = db.Insert(new CacheCustomer { Name = "Old" });
            for (int i = 0; i < orders; i++)
                db.Insert(new CacheOrder { Note = "o" + (i + 1), Customer = c });
            return c;
        }

        // The review's probe S4: the order's customer stayed "Old" after the customer was updated.
        [Fact]
        public void A_referenced_object_updated_in_this_context_is_read_as_updated()
        {
            using var db = new XodContext(NewDb("update"));
            var c = Seed(db);
            Assert.Equal("Old", db.Find<CacheOrder>(o => o.Id == 1).Customer.Name);

            Assert.True(db.Update(new CacheCustomer { Id = c.Id, Name = "New" }));

            Assert.Equal("New", db.Find<CacheOrder>(o => o.Id == 1).Customer.Name);
        }

        // The review's probe S4b: the order still returned the customer after it was deleted.
        [Fact]
        public void A_referenced_object_deleted_in_this_context_is_gone_from_the_reference()
        {
            using var db = new XodContext(NewDb("delete"));
            var c = Seed(db);
            Assert.NotNull(db.Find<CacheOrder>(o => o.Id == 1).Customer);

            db.Delete(new CacheCustomer { Id = c.Id });

            Assert.Null(db.Find<CacheCustomer>(x => x.Id == c.Id));
            Assert.Null(db.Find<CacheOrder>(o => o.Id == 1).Customer);
        }

        // The review's probe S4, across contexts: another context's update stayed unseen. Not the item cache's doing
        // since ISS-138 (it keeps nothing past a read): the context's page cache never looks at the disk again, so
        // the row itself reads as it was (ISS-167).
        [Fact(Skip = "ISS-167: a context's page cache doesn't see another context's writes")]
        public void A_referenced_object_another_context_updated_is_read_as_updated()
        {
            string path = NewDb("across");
            using var a = new XodContext(path);
            var c = Seed(a);
            Assert.Equal("Old", a.Find<CacheOrder>(o => o.Id == 1).Customer.Name);

            using (var b = new XodContext(path))
                Assert.True(b.Update(new CacheCustomer { Id = c.Id, Name = "FromB" }));

            Assert.Equal("FromB", a.Find<CacheCustomer>(x => x.Id == c.Id).Name);
            Assert.Equal("FromB", a.Find<CacheOrder>(o => o.Id == 1).Customer.Name);
        }

        // One instance was handed to every reader: what one caller changed, without saving, the next read returned.
        [Fact]
        public void Each_read_hands_out_its_own_referenced_objects()
        {
            using var db = new XodContext(NewDb("instances"));
            Seed(db);
            var first = db.Find<CacheOrder>(o => o.Id == 1);
            first.Customer.Name = "changed, not saved";

            var second = db.Find<CacheOrder>(o => o.Id == 1);

            Assert.NotSame(first.Customer, second.Customer);
            Assert.Equal("Old", second.Customer.Name);
        }

        // Within one read, the rows that reference one row share its object, as before.
        [Fact]
        public void The_rows_of_one_read_share_the_object_they_reference()
        {
            using var db = new XodContext(NewDb("shared"));
            Seed(db, orders: 3);

            var orders = db.Select<CacheOrder>().ToList();

            Assert.Equal(3, orders.Count);
            Assert.All(orders, o => Assert.Same(orders[0].Customer, o.Customer));
        }

        // A SQL query is a read like the others: what it shares ends with it, so the reads after it on the thread
        // start afresh.
        [Fact]
        public void A_sql_query_shares_nothing_with_the_reads_after_it()
        {
            using var db = new XodContext(NewDb("sql"));
            var c = Seed(db);
            Assert.Equal(1, db.Sql("SELECT COUNT(*) FROM CacheOrder").Rows.Count);
            var first = db.Find<CacheOrder>(o => o.Id == 1);
            Assert.Equal("Old", first.Customer.Name);
            Assert.NotSame(first.Customer, db.Find<CacheOrder>(o => o.Id == 1).Customer);

            Assert.True(db.Update(new CacheCustomer { Id = c.Id, Name = "New" }));

            Assert.Equal("New", db.Find<CacheOrder>(o => o.Id == 1).Customer.Name);
        }

        // A write made inside a read (a standalone table's write, from a query's predicate) ends what the read
        // shares: the rows it reads after the write reference the row as written.
        [Fact]
        public void A_write_inside_a_read_is_seen_by_the_rest_of_that_read()
        {
            using var db = new XodContext(NewDb("inside"));
            var c = Seed(db, orders: 2);

            var seen = db.Query<CacheOrder>(o => RenameOnFirst(db, c.Id, o)).ToList();

            Assert.Equal(2, seen.Count);
            Assert.Equal("New", db.Find<CacheCustomer>(x => x.Id == c.Id).Name);
            Assert.Equal("New", seen.Single(o => o.Id == 2).Customer.Name);
        }

        // A row's code is unique only within its page: codes are picked from a counter that starts anywhere in each
        // process, so pages written by different runs can share one. Two rows referenced in one read by the same code
        // on different pages are two objects (the review's suspicion, unfiled: the cache was keyed by the row's code).
        [Fact]
        public void Rows_of_two_pages_that_share_a_row_code_are_two_objects()
        {
            string path = NewDb("same-code");
            string dir = Path.GetDirectoryName(path);
            using (var db = new XodContext(path))
            {
                var a = db.Insert(new CacheCustomer { Name = "A" });
                var b = db.Insert(new CacheCustomer { Name = "B" });
                db.Insert(new CacheOrder { Note = "1", Customer = a });
                db.Insert(new CacheOrder { Note = "2", Customer = b });
            }

            // B moves to a page of its own, under A's row code, and order 2 references it there.
            string tablePath = Directory.GetFiles(dir, "*CacheCustomer.xtab").Single();
            var table = System.Xml.Linq.XDocument.Load(tablePath);
            var pageEntry = table.Descendants("Page").Single();
            string pagePath = Path.Combine(dir, pageEntry.Attribute("file").Value);
            var page = System.Xml.Linq.XDocument.Load(pagePath);
            var rows = page.Descendants("Row").ToList();
            string codeA = rows[0].Attribute("code").Value;
            var rowB = rows[1];
            rowB.Remove();
            pageEntry.SetAttributeValue("full", "false");
            pageEntry.SetAttributeValue("count", "1");
            Save(page, pagePath);

            const string secondPage = "c0de5a11e";
            rowB.SetAttributeValue("code", codeA);
            var rowsElement = page.Root.Element("Rows");
            var second = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement(page.Root.Name, page.Root.Attributes(),
                new System.Xml.Linq.XElement(rowsElement.Name, rowsElement.Attributes(), rowB)));
            Save(second, Path.Combine(dir, secondPage + ".xpag"));
            pageEntry.AddAfterSelf(new System.Xml.Linq.XElement("Page", new System.Xml.Linq.XAttribute("file", secondPage + ".xpag"),
                new System.Xml.Linq.XAttribute("full", "false"), new System.Xml.Linq.XAttribute("count", "1")));
            Save(table, tablePath);

            string ordersPath = Path.Combine(dir, System.Xml.Linq.XDocument.Load(Directory.GetFiles(dir, "*CacheOrder.xtab").Single())
                .Descendants("Page").Single().Attribute("file").Value);
            var orders = System.Xml.Linq.XDocument.Load(ordersPath);
            orders.Descendants("Customer").Single(e => (string)e.Attribute("Id") == "2").Value = secondPage + "." + codeA;
            Save(orders, ordersPath);
            foreach (string index in Directory.GetFiles(dir, "*.xidx"))
                File.Delete(index);

            using (var db = new XodContext(path))
            {
                Assert.Equal("B", db.Find<CacheCustomer>(c => c.Id == 2).Name);
                var read = db.Select<CacheOrder>().OrderBy(o => o.Id).ToList();
                Assert.Equal(new[] { "A", "B" }, read.Select(o => o.Customer.Name).ToArray());
            }
        }

        private static void Save(System.Xml.Linq.XDocument doc, string path)
        {
            var settings = new System.Xml.XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false) };
            using var writer = System.Xml.XmlWriter.Create(path, settings);
            doc.Save(writer);
        }

        private static bool RenameOnFirst(XodContext db, int customerId, CacheOrder order)
        {
            if (order.Id == 1)
                db.Update(new CacheCustomer { Id = customerId, Name = "New" });
            return true;
        }
    }
}
