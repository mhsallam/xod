using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class CtNote { public int Id { get; set; } public string Text { get; set; } }
    public class CtNoteList : List<CtNote> { }
    public class CtNumberList : List<int> { }

    public class CtWithNoteList { public int Id { get; set; } public string Name { get; set; } public CtNoteList Notes { get; set; } }
    public class CtWithNumberList { public int Id { get; set; } public CtNumberList Numbers { get; set; } }
    public class CtWithSet { public int Id { get; set; } public HashSet<string> Tags { get; set; } }
    public class CtWithArrayList { public int Id { get; set; } public ArrayList Items { get; set; } }
    public class CtWithListOfLists { public int Id { get; set; } public List<CtNumberList> Rows { get; set; } }

    public class CtWithSupported
    {
        public int Id { get; set; }
        public List<CtNote> List { get; set; }
        public IList<string> IList { get; set; }
        public ICollection<int> ICollection { get; set; }
        public IEnumerable<string> IEnumerable { get; set; }
        public IReadOnlyList<int> IReadOnlyList { get; set; }
        public IReadOnlyCollection<string> IReadOnlyCollection { get; set; }
        public int[] Array { get; set; }
        public Dictionary<string, int> Map { get; set; }
    }

    /// <summary>
    /// ISS-184: a collection the reader can't give back is refused when its class is first used, naming the property,
    /// as a value XOD can't store is (PLAN-073). A read makes a <c>List&lt;T&gt;</c> (or an array), so a collection
    /// property is an array, a map, or a type a <c>List&lt;T&gt;</c> can be assigned to. A collection class that isn't
    /// generic itself (<c>class NoteList : List&lt;Note&gt;</c>) was stored as a class and read back empty; a
    /// <c>HashSet&lt;T&gt;</c> was stored, and its read threw.
    /// </summary>
    public class XodCollectionTypeTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-colltype-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private XodContext Open(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return new XodContext(Path.Combine(dir, "ct.xod"));
        }

        public static IEnumerable<object[]> Unreadable() => new[]
        {
            new object[] { new CtWithNoteList { Name = "n", Notes = new CtNoteList { new CtNote { Text = "a" } } }, "Notes" },
            new object[] { new CtWithNumberList { Numbers = new CtNumberList { 1, 2 } }, "Numbers" },
            new object[] { new CtWithSet { Tags = new HashSet<string> { "x" } }, "Tags" },
            new object[] { new CtWithArrayList { Items = new ArrayList { 1, "two" } }, "Items" },
            new object[] { new CtWithListOfLists { Rows = new List<CtNumberList> { new CtNumberList { 1 } } }, "Rows" },
        };

        [Theory]
        [MemberData(nameof(Unreadable))]
        public void A_collection_the_reader_cant_give_back_is_refused(object item, string property)
        {
            using var db = Open("refused-" + property);
            var e = Assert.Throws<XodException>(() => db.Insert(item.GetType(), item));
            Assert.Contains("'" + property + "'", e.Message);
        }

        // A guard: the collections a read gives back are stored as before.
        [Fact]
        public void The_collections_a_read_gives_back_round_trip()
        {
            using (var db = Open("supported"))
            {
                db.Insert(new CtWithSupported
                {
                    List = new() { new CtNote { Text = "a" } },
                    IList = new List<string> { "b" },
                    ICollection = new List<int> { 3 },
                    IEnumerable = new List<string> { "c" },
                    IReadOnlyList = new List<int> { 4 },
                    IReadOnlyCollection = new List<string> { "d" },
                    Array = new[] { 5 },
                    Map = new() { ["e"] = 6 },
                });
            }
            using (var db = Open("supported"))
            {
                var s = db.Select<CtWithSupported>(include: "*").Single();
                Assert.Equal("a", s.List.Single().Text);
                Assert.Equal("b", s.IList.Single());
                Assert.Equal(3, s.ICollection.Single());
                Assert.Equal("c", s.IEnumerable.Single());
                Assert.Equal(4, s.IReadOnlyList.Single());
                Assert.Equal("d", s.IReadOnlyCollection.Single());
                Assert.Equal(5, s.Array.Single());
                Assert.Equal(6, s.Map["e"]);
            }
        }
    }
}
