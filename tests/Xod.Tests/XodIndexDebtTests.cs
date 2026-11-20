using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xod;
using Xod.Services;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-111: the reverse map (which row holds which keys) holds each of a row's keys once. Forgetting a type's index
    /// forgets its entries there too, so a later build doesn't add them again beside the stale ones; and a rebuild from
    /// the pages registered each row before the map was built, which then added it again.
    /// </summary>
    public class XodIndexDebtTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-idxdebt-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class IdItem
        {
            public int Id { get; set; }
            [Property(Indexed = true)] public string Tag { get; set; }
        }

        // At the owner: the reverse map is private state with no answer of its own to check, so it is read directly.
        // IdItem indexes two properties (its key, Id, and Tag): a row has two entries.
        [Fact]
        public void Forgetting_a_type_forgets_its_reverse_entries()
        {
            var properties = new PropertyService();
            properties.LoadType(typeof(IdItem));
            var index = new IndexService(properties);

            XElement Row(string tag) => new XElement("Row", new XAttribute("code", "r1"),
                new XElement(nameof(IdItem), new XElement("Id", "1"), new XElement("Tag", tag)));

            index.IndexRowElement(typeof(IdItem), Row("old"), "p1");
            index.MarkTypeHydrated(typeof(IdItem));
            index.RemoveItemCode(typeof(IdItem), "p1.r9"); // builds the reverse map
            Assert.Equal(2, ReverseEntries(index, "p1.r1"));

            index.Remove(typeof(IdItem));
            index.IndexRowElement(typeof(IdItem), Row("new"), "p1");
            index.MarkTypeHydrated(typeof(IdItem));
            index.RemoveItemCode(typeof(IdItem), "p1.r9");
            Assert.Equal(2, ReverseEntries(index, "p1.r1"));
        }

        // A row indexed once the reverse map is built is in it once.
        [Fact]
        public void A_row_indexed_after_the_map_is_built_is_in_it_once()
        {
            var properties = new PropertyService();
            properties.LoadType(typeof(IdItem));
            var index = new IndexService(properties);
            index.MarkTypeHydrated(typeof(IdItem));
            index.RemoveItemCode(typeof(IdItem), "p1.r9"); // builds the (empty) reverse map

            index.IndexRowElement(typeof(IdItem), new XElement("Row", new XAttribute("code", "r1"),
                new XElement(nameof(IdItem), new XElement("Id", "1"), new XElement("Tag", "t"))), "p1");
            Assert.Equal(2, ReverseEntries(index, "p1.r1"));
        }

        private static int ReverseEntries(IndexService index, string code)
        {
            var map = (IDictionary)typeof(IndexService).GetField("codeToIndexEntries", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(index);
            return map.Contains(code) ? ((ICollection)map[code]).Count : 0;
        }
    }
}
