using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-201: an <c>object</c> property holding a collection is refused when it is written. No collection reads back
    /// from an <c>object</c> property (a read takes a collection's item type from the declared type, which names none),
    /// yet one was written, and read back null: a list, an array, a map, and a <c>HashSet&lt;T&gt;</c> or a subclass of a
    /// list, which a declared property can't be either (ISS-184).
    /// </summary>
    public class XodObjectCollectionTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-objcoll-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class OcRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public object Payload { get; set; }
        }

        public class OcWords : List<string> { }

        private XodContext Open(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return new XodContext(Path.Combine(dir, "oc.xod"));
        }

        [Fact]
        public void A_set_held_by_an_object_property_is_refused_and_nothing_is_written()
        {
            using var db = Open("set");
            var e = Assert.ThrowsAny<XodException>(() => db.Insert(new OcRow { Name = "a", Payload = new HashSet<int> { 1, 2 } }));
            Assert.Contains("Payload", e.Message);
            Assert.Contains("can't read a collection back from an object property", e.Message);
            Assert.Empty(db.Select<OcRow>());
        }

        [Fact]
        public void A_subclass_of_a_list_held_by_an_object_property_is_refused()
        {
            using var db = Open("subclass");
            Assert.ThrowsAny<XodException>(() => db.Insert(new OcRow { Name = "a", Payload = new OcWords { "x" } }));
            Assert.Empty(db.Select<OcRow>());
        }

        [Fact]
        public void An_update_to_such_a_value_is_refused_and_the_row_kept()
        {
            using var db = Open("update");
            db.Insert(new OcRow { Name = "a" });
            var row = db.Find<OcRow>(r => r.Id == 1);
            row.Name = "b";
            row.Payload = new HashSet<string> { "x" };
            Assert.ThrowsAny<XodException>(() => db.Update(row));
            Assert.Equal("a", db.Find<OcRow>(r => r.Id == 1)?.Name);
        }

        // A list, an array and a map, which a declared property can be, read back null from an object property too.
        [Fact]
        public void A_list_an_array_or_a_map_held_by_an_object_property_is_refused()
        {
            using var db = Open("list");
            Assert.ThrowsAny<XodException>(() => db.Insert(new OcRow { Name = "a", Payload = new List<string> { "x", "y" } }));
            Assert.ThrowsAny<XodException>(() => db.Insert(new OcRow { Name = "b", Payload = new[] { 1, 2 } }));
            Assert.ThrowsAny<XodException>(() => db.Insert(new OcRow { Name = "c", Payload = new Dictionary<string, int> { ["k"] = 1 } }));
            Assert.Empty(db.Select<OcRow>());
        }

        // A value that is no collection is still written: a string, a number.
        [Fact]
        public void A_value_that_is_no_collection_is_written()
        {
            using var db = Open("scalar");
            db.Insert(new OcRow { Name = "a", Payload = "text" });
            db.Insert(new OcRow { Name = "b", Payload = 5 });
            Assert.Equal(2, db.Count<OcRow>());
        }
    }
}
