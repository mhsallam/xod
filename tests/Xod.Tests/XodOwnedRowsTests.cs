using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-187: the rows a row owns were chosen by two rules. The delete's cascade followed each property's
    /// <c>Cascade</c> flag, and ran only for a type flagged as having one; the update deleted the dropped entries of a
    /// list by what the row stored (<c>complex</c>, <c>children</c>, a cascading <c>reference</c>), and never a single
    /// entry. So a single keyless object got a new row at every update and kept the old one, a cascading reference set
    /// to null kept its row, a plain keyless list outlived its parent's delete, a keyless child's own keyless list was
    /// left at every update, and a <c>[Children]</c> list whose <c>Cascade</c> named <c>Update</c> alone outlived its
    /// parent. One rule now decides, and the update and the delete both take it: a row owns the rows of a type without a
    /// key it names, its children, and a keyed row its property declares <c>Cascade = Delete</c> for.
    /// </summary>
    public class XodOwnedRowsTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-owned-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class OrVenue
        {
            public string Name { get; set; }
        }

        public class OrEvent
        {
            public int Id { get; set; }
            public string Title { get; set; }
            public OrVenue Venue { get; set; }
        }

        public class OrAddr
        {
            public int Id { get; set; }
            public string Line { get; set; }
        }

        public class OrDoc
        {
            public int Id { get; set; }
            public string Title { get; set; }
            [Property(Cascade = CascadeOptions.Delete)] public OrAddr Addr { get; set; }
        }

        public class OrLetter
        {
            public int Id { get; set; }
            public string Title { get; set; }
            public OrAddr Addr { get; set; }
        }

        public class OrKid
        {
            public string Tag { get; set; }
        }

        public class OrBox
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public List<OrKid> Kids { get; set; }
        }

        public class OrGrain
        {
            public string G { get; set; }
        }

        public class OrSeed
        {
            public string S { get; set; }
            public List<OrGrain> Grains { get; set; }
        }

        public class OrSack
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public List<OrSeed> Seeds { get; set; }
        }

        public class OrNote
        {
            public int Id { get; set; }
            public int OrHostId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(OrHostId))] public OrHost Host { get; set; }
        }

        public class OrHost
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Children, Property(Cascade = CascadeOptions.Update)] public List<OrNote> Notes { get; set; }
        }

        public class OrPart
        {
            public int Id { get; set; }
            public string Label { get; set; }
        }

        public class OrRig
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public List<OrPart> Parts { get; set; }
        }

        public class OrLine
        {
            public int Id { get; set; }
            public int OrOrderId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(OrOrderId))] public OrOrder Order { get; set; }
        }

        public class OrOrder
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Children] public List<OrLine> Lines { get; set; }
        }

        public class OrHolder
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public object Thing { get; set; }
        }

        public class OrBag
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public List<object> Items { get; set; }
        }

        private string NewPath(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "or.xod");
        }

        private static XodContext Open(string path, bool indexing) =>
            new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing });

        private static string[] Sorted(IEnumerable<string> values) => values.OrderBy(v => v, StringComparer.Ordinal).ToArray();

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_update_deletes_the_row_of_the_keyless_object_it_replaces(bool indexing)
        {
            string path = NewPath("single", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new OrEvent { Title = "a", Venue = new OrVenue { Name = "X" } });
                db.Insert(new OrEvent { Title = "b", Venue = new OrVenue { Name = "X" } });
                db.Insert(new OrEvent { Title = "c", Venue = new OrVenue { Name = "Z" } });

                var a = db.Find<OrEvent>(e => e.Title == "a");
                a.Venue.Name = "Y";
                Assert.True(db.Update(a));
                Assert.Equal(new[] { "X", "Y", "Z" }, Sorted(db.Select<OrVenue>().Select(v => v.Name)));

                // Nothing changed but the title: the object is written again, and still one row per event.
                a = db.Find<OrEvent>(e => e.Title == "a");
                a.Title = "a2";
                Assert.True(db.Update(a));
                Assert.Equal(new[] { "X", "Y", "Z" }, Sorted(db.Select<OrVenue>().Select(v => v.Name)));

                // By example, a new object: the other event's equal one stays as it was.
                a = db.Find<OrEvent>(e => e.Title == "a2");
                Assert.True(db.Update(a, new OrEvent { Id = a.Id, Title = "a2", Venue = new OrVenue { Name = "X" } }));
                Assert.Equal(new[] { "X", "X", "Z" }, Sorted(db.Select<OrVenue>().Select(v => v.Name)));
                Assert.Equal("X", db.Find<OrEvent>(e => e.Title == "b").Venue.Name);
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(new[] { "X", "X", "Z" }, Sorted(db.Select<OrVenue>().Select(v => v.Name)));
                Assert.Equal("X", db.Find<OrEvent>(e => e.Title == "a2").Venue.Name);
                Assert.Equal("X", db.Find<OrEvent>(e => e.Title == "b").Venue.Name);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_update_that_clears_an_owned_object_deletes_its_row(bool indexing)
        {
            string path = NewPath("cleared", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new OrEvent { Title = "a", Venue = new OrVenue { Name = "X" } });
                db.Insert(new OrEvent { Title = "b", Venue = new OrVenue { Name = "X" } });
                db.Insert(new OrDoc { Title = "d", Addr = new OrAddr { Line = "L" } });

                var a = db.Find<OrEvent>(e => e.Title == "a");
                a.Venue = null;
                Assert.True(db.Update(a));

                var d = db.Find<OrDoc>(x => x.Title == "d");
                d.Addr = null;
                Assert.True(db.Update(d));

                Assert.Equal(1, db.Count<OrVenue>());
                Assert.Equal(0, db.Count<OrAddr>());
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(1, db.Count<OrVenue>());
                Assert.Equal("X", db.Find<OrEvent>(e => e.Title == "b").Venue.Name);
                Assert.Null(db.Find<OrEvent>(e => e.Title == "a").Venue);
                Assert.Equal(0, db.Count<OrAddr>());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Deleting_a_parent_deletes_its_keyless_list(bool indexing)
        {
            string path = NewPath("list", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new OrBox { Name = "a", Kids = new() { new OrKid { Tag = "1" }, new OrKid { Tag = "2" } } });
                db.Insert(new OrBox { Name = "b", Kids = new() { new OrKid { Tag = "1" } } });

                Assert.True(db.Delete(db.Find<OrBox>(x => x.Name == "a")));
                Assert.Equal(new[] { "1" }, Sorted(db.Select<OrKid>().Select(k => k.Tag)));
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(1, db.Count<OrKid>());
                Assert.Equal(new[] { "1" }, db.Find<OrBox>(x => x.Name == "b").Kids.Select(k => k.Tag).ToArray());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_keyless_child_takes_its_own_keyless_list_with_it(bool indexing)
        {
            string path = NewPath("nested", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new OrSack
                {
                    Name = "s",
                    Seeds = new() { new OrSeed { S = "1", Grains = new() { new OrGrain { G = "g1" }, new OrGrain { G = "g2" } } } }
                });
                db.Insert(new OrSack { Name = "t", Seeds = new() { new OrSeed { S = "1", Grains = new() { new OrGrain { G = "g1" } } } } });

                for (int i = 0; i < 3; i++)
                {
                    var s = db.Find<OrSack>(x => x.Name == "s");
                    s.Seeds.Add(new OrSeed { S = "n" + i });
                    Assert.True(db.Update(s));
                    Assert.Equal(3, db.Count<OrGrain>());
                    Assert.Equal(i + 3, db.Count<OrSeed>());
                }

                Assert.True(db.Delete(db.Find<OrSack>(x => x.Name == "s")));
                Assert.Equal(1, db.Count<OrSeed>());
                Assert.Equal(new[] { "g1" }, db.Select<OrGrain>().Select(g => g.G).ToArray());
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(1, db.Count<OrSeed>());
                Assert.Equal(1, db.Count<OrGrain>());
                Assert.Equal("g1", db.Find<OrSack>(x => x.Name == "t").Seeds.Single().Grains.Single().G);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Children_whose_cascade_names_update_alone_are_deleted_with_their_parent(bool indexing)
        {
            string path = NewPath("children", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new OrHost { Name = "h", Notes = new() { new OrNote { Text = "n1" }, new OrNote { Text = "n2" } } });
                db.Insert(new OrHost { Name = "k", Notes = new() { new OrNote { Text = "n1" } } });

                Assert.True(db.Delete(db.Find<OrHost>(x => x.Name == "h")));
                Assert.Equal(1, db.Count<OrNote>());
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(1, db.Count<OrNote>());
                Assert.Equal("n1", db.Find<OrHost>(x => x.Name == "k").Notes.Single().Text);
            }
        }

        // A guard: a row a parent links by key isn't the parent's; dropping it or deleting the parent keeps it.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Rows_linked_by_key_without_a_cascade_outlive_their_parent(bool indexing)
        {
            string path = NewPath("linked", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new OrRig { Name = "r", Parts = new() { new OrPart { Label = "p1" }, new OrPart { Label = "p2" } } });
                db.Insert(new OrLetter { Title = "l", Addr = new OrAddr { Line = "L" } });

                var r = db.Find<OrRig>(x => x.Name == "r");
                r.Parts.RemoveAt(0);
                Assert.True(db.Update(r));
                Assert.Equal(2, db.Count<OrPart>());

                Assert.True(db.Delete(db.Find<OrRig>(x => x.Name == "r")));
                Assert.Equal(2, db.Count<OrPart>());

                var l = db.Find<OrLetter>(x => x.Title == "l");
                l.Addr = null;
                Assert.True(db.Update(l));
                Assert.Equal(1, db.Count<OrAddr>());

                db.Insert(new OrLetter { Title = "m", Addr = new OrAddr { Line = "M" } });
                Assert.True(db.Delete(db.Find<OrLetter>(x => x.Title == "m")));
                Assert.Equal(2, db.Count<OrAddr>());
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(2, db.Count<OrPart>());
                Assert.Equal(2, db.Count<OrAddr>());
            }
        }

        // A guard: an owned row the new row still names is kept by the update.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_update_keeps_the_owned_rows_its_new_row_still_names(bool indexing)
        {
            string path = NewPath("kept", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new OrOrder { Name = "o", Lines = new() { new OrLine { Text = "l1" } } });
                db.Insert(new OrDoc { Title = "d", Addr = new OrAddr { Line = "L" } });

                var o = db.Find<OrOrder>(x => x.Name == "o");
                o.Name = "o2";
                o.Lines.Add(new OrLine { Text = "l2" });
                Assert.True(db.Update(o));
                Assert.Equal(new[] { "l1", "l2" }, Sorted(db.Select<OrLine>().Select(l => l.Text)));

                var d = db.Find<OrDoc>(x => x.Title == "d");
                d.Title = "d2";
                Assert.True(db.Update(d));
                Assert.Equal(1, db.Count<OrAddr>());
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(new[] { "l1", "l2" }, Sorted(db.Find<OrOrder>(x => x.Name == "o2").Lines.Select(l => l.Text)));
                Assert.Equal("L", db.Find<OrDoc>(x => x.Title == "d2").Addr.Line);
            }
        }

        // A row owns a keyed row only through a child list or a cascade its property declares (review of TASK-092): an
        // object property took an implicit cascade, and a list of object stores each item as written for it alone,
        // a keyed one too, so an update or a delete took a row another row still named.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_object_property_keeps_the_keyed_row_it_links_and_owns_a_keyless_one(bool indexing)
        {
            string path = NewPath("object", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new OrAddr { Line = "c" });
                var c = db.Find<OrAddr>(x => x.Line == "c");
                db.Insert(new OrHolder { Name = "h1", Thing = c });
                db.Insert(new OrHolder { Name = "h2", Thing = c });
                db.Insert(new OrLetter { Title = "l", Addr = c });
                db.Insert(new OrHolder { Name = "h3", Thing = new OrVenue { Name = "v" } });

                var h1 = db.Find<OrHolder>(x => x.Name == "h1");
                h1.Thing = null;
                Assert.True(db.Update(h1));
                Assert.Equal(1, db.Count<OrAddr>());

                Assert.True(db.Delete(db.Find<OrHolder>(x => x.Name == "h2")));
                Assert.Equal(1, db.Count<OrAddr>());

                var h3 = db.Find<OrHolder>(x => x.Name == "h3");
                h3.Thing = null;
                Assert.True(db.Update(h3));
                Assert.Equal(0, db.Count<OrVenue>());
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal("c", db.Find<OrLetter>(x => x.Title == "l").Addr.Line);
                Assert.Equal(0, db.Count<OrVenue>());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Deleting_a_list_of_object_keeps_a_keyed_item_and_deletes_a_keyless_one(bool indexing)
        {
            string path = NewPath("objects", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new OrBag { Name = "b", Items = new List<object> { new OrAddr { Line = "x" }, new OrVenue { Name = "v" } } });
                db.Insert(new OrLetter { Title = "l", Addr = db.Find<OrAddr>(x => x.Line == "x") });

                Assert.True(db.Delete(db.Find<OrBag>(x => x.Name == "b")));
                Assert.Equal(1, db.Count<OrAddr>());
                Assert.Equal(0, db.Count<OrVenue>());
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal("x", db.Find<OrLetter>(x => x.Title == "l").Addr.Line);
            }
        }

        // XOD 1.x stored a single object whose key was unset when it was written as one written for its row alone
        // (refType="complex"), whatever its type; another row may link it by key since.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_keyed_row_stored_by_1x_as_its_row_s_alone_is_kept_while_another_row_links_it(bool indexing)
        {
            string path = NewPath("v1", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new OrLetter { Title = "l1", Addr = new OrAddr { Line = "y" } });
                db.Insert(new OrLetter { Title = "l2", Addr = db.Find<OrAddr>(x => x.Line == "y") });
                db.Insert(new OrLetter { Title = "l3", Addr = db.Find<OrAddr>(x => x.Line == "y") });
            }

            int edited = 0;
            var entry = new System.Text.RegularExpressions.Regex("<Addr refType=\"reference\"[^>]*>");
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag"))
            {
                string text = File.ReadAllText(file);
                if (!text.Contains("<OrLetter>"))
                    continue;
                text = entry.Replace(text, "<Addr refType=\"complex\">", 2);
                edited++;
                File.WriteAllText(file, text);
            }
            Assert.Equal(1, edited);

            using (var db = Open(path, indexing))
            {
                Assert.Equal("y", db.Find<OrLetter>(x => x.Title == "l1").Addr.Line);
                Assert.True(db.Delete(db.Find<OrLetter>(x => x.Title == "l1")));
                var l2 = db.Find<OrLetter>(x => x.Title == "l2");
                l2.Addr = null;
                Assert.True(db.Update(l2));
                Assert.Equal(1, db.Count<OrAddr>());
                Assert.Equal("y", db.Find<OrLetter>(x => x.Title == "l3").Addr.Line);
            }
        }
    }
}
