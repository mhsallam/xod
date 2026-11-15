using System;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-139: a stored row matches an example when each property the example sets is equal to the row's, as C# says
    /// equal and as the index keys say (IndexKeys: text exact, 12.5m equal to 12.50m), whether or not the property is
    /// indexed and whether indexing is on: the query by example, Update and Delete by an item, and the primary-key and
    /// unique checks made without an index. Matching compared the stored text case-insensitively in the current
    /// culture, so the same call gave other rows with indexing than without.
    /// </summary>
    public class XodExampleMatchTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-example-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class ExPerson
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Property(Indexed = true)] public string City { get; set; }
            public decimal Amount { get; set; }
        }

        public class ExCoded
        {
            [PrimaryKey] public string Code { get; set; }
            public string Name { get; set; }
        }

        public class ExPriced
        {
            [PrimaryKey] public decimal Price { get; set; }
            public string Name { get; set; }
        }

        public class ExTagged
        {
            public int Id { get; set; }
            [UniqueKey] public string Tag { get; set; }
        }

        private XodContext Open(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return new XodContext(Path.Combine(dir, "ex.xod"), null, new DatabaseOptions { EnableIndexing = indexing });
        }

        private static void SeedPeople(XodContext db)
        {
            db.Insert(new ExPerson { Name = "Alice", City = "Paris", Amount = 12.50m });
            db.Insert(new ExPerson { Name = "bob", City = "paris", Amount = 3m });
            db.Insert(new ExPerson { Name = "Ca­rl", City = "Rome", Amount = 1m });
            db.Insert(new ExPerson { Name = "", City = "Oslo", Amount = 2m });
        }

        // The review's probe S2.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_query_by_example_compares_text_exactly(bool indexing)
        {
            using var db = Open("text", indexing);
            SeedPeople(db);

            Assert.Empty(db.Query(new ExPerson { Name = "alice" }));
            Assert.Empty(db.Query(new ExPerson { City = "PARIS" }));
            Assert.Empty(db.Query(new ExPerson { Name = "Carl" }));
            Assert.Equal("bob", Assert.Single(db.Query(new ExPerson { City = "paris" })).Name);
            // An indexed property and one that isn't: the index finds the candidates, the match decides.
            Assert.Empty(db.Query(new ExPerson { City = "Paris", Name = "alice" }));
            Assert.Single(db.Query(new ExPerson { City = "Paris", Name = "Alice" }));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_query_by_example_compares_numbers_by_value(bool indexing)
        {
            using var db = Open("numbers", indexing);
            SeedPeople(db);

            Assert.Equal("Alice", Assert.Single(db.Query(new ExPerson { Amount = 12.5m })).Name);
            Assert.Equal("Alice", Assert.Single(db.Query(new[] { new ExPerson { Amount = 12.5m }, new ExPerson { Name = "nobody" } })).Name);
        }

        // An example's property takes part unless it is null or its type's default, which for text is the empty
        // string (ValueHelper.DefaultOf), as before. An example that sets nothing matches nothing, as before: a
        // Delete by it takes no row.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_example_that_sets_nothing_matches_nothing(bool indexing)
        {
            using var db = Open("empty", indexing);
            SeedPeople(db);
            db.Insert(new ExNote { Text = "kept", Weight = 1m });

            Assert.Empty(db.Query(new ExPerson { Name = "" }));
            Assert.Equal("Oslo", Assert.Single(db.Query(new ExPerson { Name = "", City = "Oslo" })).City);
            Assert.False(db.Delete(new ExNote()));
            Assert.False(db.Delete(new ExPerson()));
            Assert.Single(db.Select<ExNote>());
            Assert.Equal(4, db.Select<ExPerson>().Count());
        }

        // The review's probe S2b: without indexing, "abc" was refused beside "ABC", Update(Code = "abc") rewrote the
        // "ABC" row, and a decimal key 12.5 was accepted beside 12.50; with indexing, each went the other way.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Primary_keys_compare_as_the_index_compares_them(bool indexing)
        {
            using var db = Open("keys", indexing);
            db.Insert(new ExCoded { Code = "ABC", Name = "upper" });
            db.Insert(new ExCoded { Code = "abc", Name = "lower" });

            Assert.True(db.Update(new ExCoded { Code = "abc", Name = "changed" }));
            Assert.Equal(new[] { "ABC=upper", "abc=changed" },
                db.Select<ExCoded>().Select(c => c.Code + "=" + c.Name).OrderBy(s => s, StringComparer.Ordinal).ToArray());

            Assert.True(db.Delete(new ExCoded { Code = "abc" }));
            Assert.Equal("ABC", Assert.Single(db.Select<ExCoded>()).Code);
            Assert.False(db.Delete(new ExCoded { Code = "aBc" }));

            db.Insert(new ExPriced { Price = 12.50m, Name = "p" });
            Assert.ThrowsAny<Exception>(() => db.Insert(new ExPriced { Price = 12.5m, Name = "dup" }));
            Assert.Single(db.Select<ExPriced>());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Unique_keys_compare_as_the_index_compares_them(bool indexing)
        {
            using var db = Open("unique", indexing);
            db.Insert(new ExTagged { Tag = "Red" });
            db.Insert(new ExTagged { Tag = "red" });
            Assert.ThrowsAny<Exception>(() => db.Insert(new ExTagged { Tag = "red" }));
            Assert.Equal(2, db.Select<ExTagged>().Count());
        }

        public class ExCustomer
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class ExOrder
        {
            public int Id { get; set; }
            public string Note { get; set; }
            public ExCustomer Customer { get; set; }
        }

        // A reference in an example has nothing to match by (an example is written without its references) and
        // doesn't take part, as before: an example that sets only a reference matches nothing (the review of the
        // first version: it matched every row), and beside a value it is left out (ISS-168).
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_reference_in_an_example_does_not_take_part(bool indexing)
        {
            using var db = Open("reference", indexing);
            var c1 = db.Insert(new ExCustomer { Name = "one" });
            var c2 = db.Insert(new ExCustomer { Name = "two" });
            db.Insert(new ExOrder { Note = "a", Customer = c1 });
            db.Insert(new ExOrder { Note = "b", Customer = c2 });
            db.Insert(new ExOrder { Note = "c" });

            Assert.Empty(db.Query(new ExOrder { Customer = c1 }));
            Assert.Empty(db.Query(new[] { new ExOrder { Customer = c1 } }));
            Assert.Equal("b", Assert.Single(db.Query(new ExOrder { Note = "b", Customer = c1 })).Note);
        }

        public class ExSecret
        {
            public int Id { get; set; }
            [Crypto(CryptoMethod.AES256)] public string Body { get; set; }
        }

        // An encrypted value is matched by what it reads back as: its stored form changes with every write.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_encrypted_property_matches_by_its_value(bool indexing)
        {
            string dir = Path.Combine(tempRoot, "secret" + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            using var db = new XodContext(Path.Combine(dir, "ex.xod"), "pw", new DatabaseOptions { EnableIndexing = indexing });
            db.Insert(new ExSecret { Body = "Hello" });
            db.Insert(new ExSecret { Body = "other" });

            Assert.Equal("Hello", Assert.Single(db.Query(new ExSecret { Body = "Hello" })).Body);
            Assert.Empty(db.Query(new ExSecret { Body = "hello" }));
        }

        // A type without a primary key is updated and deleted by its example's values (with a key, by the key).
        public class ExNote
        {
            public string Text { get; set; }
            public decimal Weight { get; set; }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Update_and_delete_by_example_take_the_rows_that_match_exactly(bool indexing)
        {
            using var db = Open("bulk", indexing);
            db.Insert(new ExNote { Text = "Alice", Weight = 12.50m });
            db.Insert(new ExNote { Text = "bob", Weight = 3m });

            Assert.True(db.Update(new ExNote { Text = "bob" }, new ExNote { Text = "Bob", Weight = 3m }));
            Assert.False(db.Update(new ExNote { Text = "alice" }, new ExNote { Text = "Alicia", Weight = 1m }));
            Assert.Equal(new[] { "Alice", "Bob" },
                db.Select<ExNote>().Select(p => p.Text).OrderBy(s => s, StringComparer.Ordinal).ToArray());

            Assert.False(db.Delete(new ExNote { Text = "ALICE" }));
            Assert.True(db.Delete(new ExNote { Weight = 12.5m }));
            Assert.Equal("Bob", Assert.Single(db.Select<ExNote>()).Text);
        }
    }
}
