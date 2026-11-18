using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-179: an update of a parent by key writes its list again, and then descended into every child of the list with
    /// an update by key. A child without a key can't be updated by key, so the update threw
    /// <c>MissingPrimaryKeyValueException</c> and its statement was undone, for a plain list and a <c>[Children]</c> list.
    /// A child without a key is written again by the parent's write; only a child the write linked to its stored row
    /// (one with a key) is updated after it.
    /// </summary>
    public class XodUpdateChildrenTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-updchildren-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class UcKid
        {
            public string Tag { get; set; }
            public string Pad { get; set; }
        }

        public class UcBox
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public List<UcKid> Kids { get; set; }
        }

        public class UcNote
        {
            public int UcHostId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(UcHostId))] public UcHost Host { get; set; }
        }

        public class UcHost
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Children] public List<UcNote> Notes { get; set; }
        }

        public class UcPart
        {
            public int Id { get; set; }
            public string Label { get; set; }
        }

        public class UcRig
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Property(Cascade = CascadeOptions.Update)] public List<UcPart> Parts { get; set; }
        }

        private string NewPath(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "uc.xod");
        }

        private static XodContext Open(string path, bool indexing) =>
            new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing });

        private static string[] Sorted(IEnumerable<string> values) => values.OrderBy(v => v, StringComparer.Ordinal).ToArray();

        private static void AssertBoxes(XodContext db)
        {
            Assert.Equal(7, db.Count<UcKid>());
            var boxes = db.Select<UcBox>(include: "*").OrderBy(b => b.Id).ToList();
            Assert.Equal(new[] { 1, 2, 3 }, boxes.Select(b => b.Id));
            Assert.Equal("B1 renamed", boxes[0].Name);
            Assert.Equal(new[] { "changed1", "new1", "same" }, Sorted(boxes[0].Kids.Select(k => k.Tag)));
            foreach (var box in boxes.Skip(1))
                Assert.Equal(new[] { "own" + box.Id, "same" }, Sorted(box.Kids.Select(k => k.Tag)));
            Assert.Equal(new[] { "changed1", "new1", "own2", "own3", "same", "same", "same" }, Sorted(db.Select<UcKid>().Select(k => k.Tag)));
        }

        // A parent read with its list, a kid changed and one added, updated by its key: the parent's list is the new
        // one, its dropped kid is gone, and the other parents' equal kids stay.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Updating_a_parent_by_key_writes_its_keyless_list_again(bool indexing)
        {
            string path = NewPath("plain", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 3; i++)
                    db.Insert(new UcBox { Id = i, Name = "B" + i, Kids = new List<UcKid> { new UcKid { Tag = "same", Pad = "k" }, new UcKid { Tag = "own" + i, Pad = "k" } } });

                var box = db.Find<UcBox>(b => b.Id == 1, include: "*");
                box.Name = "B1 renamed";
                box.Kids.Single(k => k.Tag == "own1").Tag = "changed1";
                box.Kids.Add(new UcKid { Tag = "new1", Pad = "k" });
                Assert.True(db.Update(box));

                AssertBoxes(db);
            }
            using (var db = Open(path, indexing))
                AssertBoxes(db);
        }

        private static void AssertHosts(XodContext db)
        {
            Assert.Equal(4, db.Count<UcNote>());
            var hosts = db.Select<UcHost>(include: "*").OrderBy(h => h.Id).ToList();
            Assert.Equal("H1 renamed", hosts[0].Name);
            Assert.Equal(new[] { "changed1", "new1" }, Sorted(hosts[0].Notes.Select(n => n.Text)));
            Assert.Equal(new[] { "own2", "same" }, Sorted(hosts[1].Notes.Select(n => n.Text)));
            Assert.All(hosts.SelectMany(h => h.Notes.Select(n => (h.Id, n.UcHostId))), p => Assert.Equal(p.Item1, p.Item2));
            Assert.Equal(new[] { 1, 1, 2, 2 }, db.Select<UcNote>().Select(n => n.UcHostId).OrderBy(i => i));
        }

        // The same for a [Children] list: each note keeps its parent key, and the host's dropped note is gone.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Updating_a_parent_by_key_writes_its_keyless_children_again(bool indexing)
        {
            string path = NewPath("children", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 2; i++)
                    db.Insert(new UcHost { Id = i, Name = "H" + i, Notes = new List<UcNote> { new UcNote { Text = "same" }, new UcNote { Text = "own" + i } } });

                var host = db.Find<UcHost>(h => h.Id == 1, include: "*");
                host.Name = "H1 renamed";
                host.Notes.RemoveAll(n => n.Text == "same");
                host.Notes.Single(n => n.Text == "own1").Text = "changed1";
                host.Notes.Add(new UcNote { Text = "new1" });
                Assert.True(db.Update(host));

                AssertHosts(db);
            }
            using (var db = Open(path, indexing))
                AssertHosts(db);
        }

        private static void AssertRigs(XodContext db)
        {
            Assert.Equal(new[] { "a1 relabelled", "a2", "b1", "c1" }, Sorted(db.Select<UcPart>().Select(p => p.Label)));
            var rig = db.Find<UcRig>(r => r.Id == 1, include: "*");
            Assert.Equal(new[] { "a1 relabelled", "a2", "c1" }, Sorted(rig.Parts.Select(p => p.Label)));
            Assert.Equal(new[] { "b1" }, db.Find<UcRig>(r => r.Id == 2, include: "*").Parts.Select(p => p.Label));
        }

        // A keyed item of a list that cascades updates keeps its row: the update links the stored row and writes the
        // item's new values into it, and a new item gets a row of its own. A plain list only links (ISS-189).
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Updating_a_parent_by_key_updates_its_keyed_children_in_their_rows(bool indexing)
        {
            string path = NewPath("keyed", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new UcRig { Id = 1, Name = "R1", Parts = new List<UcPart> { new UcPart { Label = "a1" }, new UcPart { Label = "a2" } } });
                db.Insert(new UcRig { Id = 2, Name = "R2", Parts = new List<UcPart> { new UcPart { Label = "b1" } } });

                var rig = db.Find<UcRig>(r => r.Id == 1, include: "*");
                rig.Parts.Single(p => p.Label == "a1").Label = "a1 relabelled";
                rig.Parts.Add(new UcPart { Label = "c1" });
                Assert.True(db.Update(rig));

                AssertRigs(db);
            }
            using (var db = Open(path, indexing))
                AssertRigs(db);
        }

        public class UcLot
        {
            public int Id { get; set; }
            [Property(Cascade = CascadeOptions.Delete)] public List<object> Items { get; set; }
        }

        private static XodContext OpenBuffered(string path, bool indexing) =>
            new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing, Durability = DurabilityMode.Buffered });

        // In buffered mode, before anything is flushed: the rows the update drops are deleted, each with its trigger.
        // A row's type was found from the table files on disk, which a table not flushed yet isn't, so the update
        // kept every row it dropped.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Before_a_flush_an_update_deletes_the_kids_it_drops(bool indexing)
        {
            string path = NewPath("buffered-update", indexing);
            using (var db = OpenBuffered(path, indexing))
            {
                db.Insert(new UcBox { Id = 1, Name = "B1", Kids = new List<UcKid> { new UcKid { Tag = "x" }, new UcKid { Tag = "y" } } });
                int deleted = 0;
                db.AfterAction += (_, e) => { if (e.Action == DatabaseActions.Delete && e.Item is UcKid) deleted++; };

                var box = db.Find<UcBox>(b => b.Id == 1, include: "*");
                box.Kids.RemoveAt(0);
                Assert.True(db.Update(box));

                Assert.Equal(2, deleted);
                Assert.Equal(new[] { "y" }, db.Select<UcKid>().Select(k => k.Tag));
                db.Flush();
            }
            using (var db = OpenBuffered(path, indexing))
            {
                Assert.Equal(new[] { "y" }, db.Select<UcKid>().Select(k => k.Tag));
                Assert.Equal(new[] { "y" }, db.Find<UcBox>(b => b.Id == 1, include: "*").Kids.Select(k => k.Tag));
            }
        }

        // The same lookup finds the type of a row a cascade through an object list deletes.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Before_a_flush_a_cascade_through_an_object_list_deletes_its_rows(bool indexing)
        {
            string path = NewPath("buffered-cascade", indexing);
            using (var db = OpenBuffered(path, indexing))
            {
                db.Insert(new UcLot { Id = 1, Items = new List<object> { new UcKid { Tag = "x" }, new UcPart { Label = "p" } } });
                Assert.True(db.Delete(new UcLot { Id = 1 }));

                Assert.Empty(db.Select<UcKid>());
                Assert.Empty(db.Select<UcPart>());
                db.Flush();
            }
            using (var db = OpenBuffered(path, indexing))
            {
                Assert.Empty(db.Select<UcKid>());
                Assert.Empty(db.Select<UcPart>());
            }
        }

        // In a transaction, the tables it made are written only at its commit: the same lookup found no type for their
        // rows there too.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void In_a_transaction_an_update_and_a_cascade_delete_the_rows_of_tables_it_made(bool indexing)
        {
            string path = NewPath("tx-new-tables", indexing);
            using (var db = Open(path, indexing))
            {
                using (var tx = db.BeginTransaction())
                {
                    db.Insert(new UcBox { Id = 1, Name = "B1", Kids = new List<UcKid> { new UcKid { Tag = "x" }, new UcKid { Tag = "y" } } });
                    var box = db.Find<UcBox>(b => b.Id == 1, include: "*");
                    box.Kids.RemoveAt(0);
                    Assert.True(db.Update(box));

                    db.Insert(new UcLot { Id = 1, Items = new List<object> { new UcPart { Label = "p" } } });
                    Assert.True(db.Delete(new UcLot { Id = 1 }));

                    Assert.Equal(new[] { "y" }, db.Select<UcKid>().Select(k => k.Tag));
                    Assert.Empty(db.Select<UcPart>());
                    tx.Commit();
                }
                Assert.Equal(new[] { "y" }, db.Select<UcKid>().Select(k => k.Tag));
                Assert.Empty(db.Select<UcPart>());
            }
            using (var db = Open(path, indexing))
            {
                Assert.Equal(new[] { "y" }, db.Select<UcKid>().Select(k => k.Tag));
                Assert.Empty(db.Select<UcPart>());
            }
        }

        // DropAll drops every table this context sees, one not written yet included: before a flush, and in a
        // transaction that made it. It listed the table files on disk, and kept the others.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DropAll_drops_a_table_not_written_yet(bool transaction)
        {
            string path = NewPath("dropall", transaction);
            using (var db = transaction ? Open(path, true) : OpenBuffered(path, true))
            {
                db.Count<UcBox>();
                if (transaction)
                {
                    using var tx = db.BeginTransaction();
                    db.Insert(new UcPart { Label = "p" });
                    Assert.True(db.DropAll());
                    Assert.Empty(db.Select<UcPart>());
                    tx.Commit();
                }
                else
                {
                    db.Insert(new UcPart { Label = "p" });
                    Assert.True(db.DropAll());
                    Assert.Empty(db.Select<UcPart>());
                    db.Flush();
                }
                Assert.Empty(db.Select<UcPart>());
            }
            using (var db = Open(path, true))
                Assert.Empty(db.Select<UcPart>());
        }
    }
}
