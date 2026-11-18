using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-130: a row is deleted by its item code, and so is every row its cascade deletes, with one cascade for every
    /// path and one trigger rule: each row the cascade deletes fires its own BeforeAction and AfterAction, and a
    /// BeforeAction that refuses keeps that row. A delete by key cascaded one way (no single reference, no child
    /// trigger) and a delete by a scan another (each child deleted by example, so an equal child of another parent went
    /// too: ISS-185); an update deleted the entries it dropped by example (ISS-180), and DeleteWhere each match by
    /// example (ISS-186).
    /// </summary>
    public class XodCascadeDeleteTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-cascade-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class CdAddress
        {
            public int Id { get; set; }
            public string Street { get; set; }
        }

        public class CdLine
        {
            public string Tag { get; set; }
            public string Pad { get; set; }
        }

        public class CdOrder
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Property(Cascade = CascadeOptions.Delete)] public CdAddress Address { get; set; }
            [Property(Cascade = CascadeOptions.Delete)] public List<CdLine> Lines { get; set; }
        }

        public class CdKid
        {
            public string Tag { get; set; }
            public string Pad { get; set; }
        }

        public class CdParent
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public List<CdKid> Kids { get; set; }
        }

        public class CdLoose
        {
            public string Group { get; set; }
        }

        private string NewPath(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "cd.xod");
        }

        private static XodContext Open(string path, bool indexing) =>
            new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing });

        private static void SeedOrders(XodContext db)
        {
            for (int i = 1; i <= 3; i++)
            {
                db.Insert(new CdOrder
                {
                    Id = i,
                    Name = "O" + i,
                    Address = new CdAddress { Id = i, Street = "S" + i },
                    Lines = new List<CdLine> { new CdLine { Tag = "same", Pad = "p" }, new CdLine { Tag = "own" + i, Pad = "p" } },
                });
            }
        }

        private static void AssertOrderOneGone(XodContext db)
        {
            Assert.Equal(new[] { 2, 3 }, db.Select<CdOrder>().Select(o => o.Id).OrderBy(i => i));
            Assert.Equal(new[] { 2, 3 }, db.Select<CdAddress>().Select(a => a.Id).OrderBy(i => i));
            Assert.Equal(new[] { "own2", "own3", "same", "same" }, db.Select<CdLine>().Select(l => l.Tag).OrderBy(t => t, StringComparer.Ordinal));
            foreach (var order in db.Select<CdOrder>(include: "*"))
            {
                Assert.Equal("S" + order.Id, order.Address?.Street);
                Assert.Equal(new[] { "own" + order.Id, "same" }, order.Lines.Select(l => l.Tag).OrderBy(t => t, StringComparer.Ordinal));
            }
        }

        // A delete by key (with indexing: the row found by its key; without: by a scan) deletes the order's cascade
        // reference and its own lines, no other order's, and fires the triggers of each row it deletes.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_cascade_deletes_its_own_rows_by_code_and_fires_each_trigger(bool indexing)
        {
            string path = NewPath("cascade", indexing);
            using (var db = Open(path, indexing))
            {
                SeedOrders(db);
                var before = new List<string>();
                var after = new List<string>();
                db.BeforeAction += (_, e) => { if (e.Action == DatabaseActions.Delete) before.Add(e.Type.Name + ":" + Describe(e.Item)); };
                db.AfterAction += (_, e) => { if (e.Action == DatabaseActions.Delete) after.Add(e.Type.Name + ":" + Describe(e.Item)); };

                Assert.True(db.Delete(new CdOrder { Id = 1 }));

                var expected = new[] { "CdAddress:S1", "CdLine:own1", "CdLine:same", "CdOrder:1" };
                Assert.Equal(expected, before.OrderBy(s => s, StringComparer.Ordinal));
                Assert.Equal(expected, after.OrderBy(s => s, StringComparer.Ordinal));
                AssertOrderOneGone(db);
            }
            using (var db = Open(path, indexing))
                AssertOrderOneGone(db);
        }

        private static string Describe(object item) => item switch
        {
            CdOrder o => o.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CdAddress a => a.Street,
            CdLine l => l.Tag,
            CdLoose g => g.Group,
            _ => item?.GetType().Name,
        };

        // A cascaded row whose BeforeAction refuses stays; the rest of the delete goes on.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_cascaded_row_whose_trigger_refuses_is_kept(bool indexing)
        {
            string path = NewPath("refuse", indexing);
            using var db = Open(path, indexing);
            SeedOrders(db);
            db.BeforeAction += (_, e) => { if (e.Action == DatabaseActions.Delete && e.Item is CdLine { Tag: "own1" }) e.Cancel = true; };

            Assert.True(db.Delete(new CdOrder { Id = 1 }));

            Assert.Equal(new[] { 2, 3 }, db.Select<CdOrder>().Select(o => o.Id).OrderBy(i => i));
            Assert.Equal(new[] { "own1", "own2", "own3", "same", "same" }, db.Select<CdLine>().Select(l => l.Tag).OrderBy(t => t, StringComparer.Ordinal));
        }

        private static void SeedParents(XodContext db)
        {
            for (int i = 1; i <= 3; i++)
                db.Insert(new CdParent { Id = i, Name = "P" + i, Kids = new List<CdKid> { new CdKid { Tag = "same", Pad = "k" }, new CdKid { Tag = "own" + i, Pad = "k" } } });
        }

        private static void AssertParentsKeepTheirKids(XodContext db, int expectedKids)
        {
            Assert.Equal(expectedKids, db.Count<CdKid>());
            var parents = db.Select<CdParent>(include: "*").ToList();
            Assert.Equal(expectedKids, parents.Sum(p => p.Kids?.Count ?? 0));
            foreach (var parent in parents.Where(p => p.Id != 1))
                Assert.Equal(new[] { "own" + parent.Id, "same" }, parent.Kids.Select(k => k.Tag).OrderBy(t => t, StringComparer.Ordinal));
        }

        // ISS-180: an update of a parent by example writes its keyless kids again and deletes the entries it dropped,
        // by their codes: the copies it just wrote, and other parents' equal kids, stay.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_update_of_a_parent_by_example_keeps_its_keyless_kids(bool indexing)
        {
            string path = NewPath("update-same", indexing);
            using (var db = Open(path, indexing))
            {
                SeedParents(db);
                var p1 = db.Find<CdParent>(p => p.Id == 1, include: "*");
                Assert.True(db.Update(p1, p1));

                AssertParentsKeepTheirKids(db, 6);
                Assert.Equal(new[] { "own1", "same" }, db.Find<CdParent>(p => p.Id == 1, include: "*").Kids.Select(k => k.Tag).OrderBy(t => t, StringComparer.Ordinal));
            }
            using (var db = Open(path, indexing))
                AssertParentsKeepTheirKids(db, 6);
        }

        // ISS-180: the entry an update drops is deleted, and only that row: another parent's equal kid stays.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_update_that_drops_a_kid_deletes_only_that_row(bool indexing)
        {
            string path = NewPath("update-drop", indexing);
            using (var db = Open(path, indexing))
            {
                SeedParents(db);
                var p1 = db.Find<CdParent>(p => p.Id == 1, include: "*");
                Assert.True(db.Update(p1, new CdParent { Id = 1, Name = "P1", Kids = new List<CdKid> { new CdKid { Tag = "own1", Pad = "k" } } }));

                AssertParentsKeepTheirKids(db, 5);
                Assert.Equal(new[] { "own1" }, db.Find<CdParent>(p => p.Id == 1, include: "*").Kids.Select(k => k.Tag));
            }
            using (var db = Open(path, indexing))
                AssertParentsKeepTheirKids(db, 5);
        }

        public class CdNode
        {
            public int Id { get; set; }
            public string Tag { get; set; }
            [Property(Cascade = CascadeOptions.Delete)] public List<CdNode> Subs { get; set; }
        }

        // A row an earlier cascade of the same statement took is not deleted again, and fires no second trigger: the
        // root's cascade deletes the sub, then the sub's own match finds nothing.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_row_a_cascade_took_is_not_deleted_or_triggered_again(bool indexing)
        {
            string path = NewPath("taken", indexing);
            using var db = Open(path, indexing);
            var root = db.Insert(new CdNode { Tag = "x" });
            var sub = db.Insert(new CdNode { Tag = "x" });
            var stored = db.Find<CdNode>(n => n.Id == root.Id, include: "*");
            Assert.True(db.Update(stored, new CdNode { Id = root.Id, Tag = "x", Subs = new List<CdNode> { db.Find<CdNode>(n => n.Id == sub.Id) } }));
            Assert.Equal(new[] { sub.Id }, db.Find<CdNode>(n => n.Id == root.Id, include: "*").Subs.Select(n => n.Id));

            var before = new List<int>();
            var after = new List<int>();
            db.BeforeAction += (_, e) => { if (e.Action == DatabaseActions.Delete) before.Add(((CdNode)e.Item).Id); };
            db.AfterAction += (_, e) => { if (e.Action == DatabaseActions.Delete) after.Add(((CdNode)e.Item).Id); };

            Assert.Equal(1, db.DeleteWhere<CdNode>(n => n.Tag == "x"));

            Assert.Equal(new[] { root.Id, sub.Id }, before.OrderBy(i => i));
            Assert.Equal(new[] { root.Id, sub.Id }, after.OrderBy(i => i));
            Assert.Equal(0, db.Count<CdNode>());
        }

        // Two rows whose cascades name each other are both deleted, each once: a row is removed before its cascade, and
        // a row already gone is not deleted again (the cascade used to recurse until the stack overflowed).
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_cascade_cycle_deletes_each_row_once(bool indexing)
        {
            string path = NewPath("cycle", indexing);
            using var db = Open(path, indexing);
            var a = db.Insert(new CdNode { Tag = "a" });
            var b = db.Insert(new CdNode { Tag = "b" });
            Assert.True(db.Update(db.Find<CdNode>(n => n.Id == a.Id, include: "*"), new CdNode { Id = a.Id, Tag = "a", Subs = new List<CdNode> { db.Find<CdNode>(n => n.Id == b.Id) } }));
            Assert.True(db.Update(db.Find<CdNode>(n => n.Id == b.Id, include: "*"), new CdNode { Id = b.Id, Tag = "b", Subs = new List<CdNode> { db.Find<CdNode>(n => n.Id == a.Id) } }));
            int before = 0;
            db.BeforeAction += (_, e) => { if (e.Action == DatabaseActions.Delete) before++; };

            Assert.True(db.Delete(new CdNode { Id = a.Id }));

            Assert.Equal(2, before);
            Assert.Equal(0, db.Count<CdNode>());
        }

        public class CdThing
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class CdBag
        {
            public int Id { get; set; }
            [Property(Cascade = CascadeOptions.Delete)] public List<object> Items { get; set; }
            [Property(Cascade = CascadeOptions.Delete)] public object One { get; set; }
        }

        // A cascade through an object-typed list or property deletes each row as the type its page holds: its index
        // keys go with it (its key can be inserted again), its table forgets an emptied page, and its trigger names its
        // type. A list of object deleted each as System.Object, which has no table.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_cascade_through_object_properties_deletes_each_row_as_its_type(bool indexing)
        {
            string path = NewPath("object", indexing);
            using (var db = Open(path, indexing))
            {
                db.Insert(new CdBag { Id = 1, Items = new List<object> { new CdThing { Id = 10, Name = "a" }, new CdThing { Id = 11, Name = "b" } }, One = new CdThing { Id = 12, Name = "c" } });
                Assert.Equal(3, db.Count<CdThing>());
                var types = new List<string>();
                db.BeforeAction += (_, e) => { if (e.Action == DatabaseActions.Delete) types.Add(e.Type.Name); };

                Assert.True(db.Delete(new CdBag { Id = 1 }));

                Assert.Equal(new[] { "CdBag", "CdThing", "CdThing", "CdThing" }, types.OrderBy(t => t, StringComparer.Ordinal));
                Assert.Equal(0, db.Count<CdThing>());
                db.Insert(new CdThing { Id = 10, Name = "again" });
                Assert.Equal(new[] { "again" }, db.Select<CdThing>().Select(t => t.Name));
            }
            using (var db = Open(path, indexing))
            {
                Assert.Equal(new[] { "again" }, db.Select<CdThing>().Select(t => t.Name));
                db.Insert(new CdThing { Id = 11, Name = "again too" });
                Assert.Equal(2, db.Count<CdThing>());
            }
        }

        // With every page walked (ISS-136), an update or a cascade delete of one parent touches its own keyless children
        // only, though 30 parents' equal children are spread over many pages: the first-page stop was what bounded the
        // deletes by example these used to make (ISS-180, ISS-185).
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Over_many_pages_one_parent_s_update_or_delete_touches_only_its_rows(bool indexing)
        {
            string path = NewPath("pages", indexing);
            var options = new DatabaseOptions { EnableIndexing = indexing, PageSize = 2048 };
            string pad = new string('k', 60);
            using (var db = new XodContext(path, null, options))
            {
                for (int i = 1; i <= 30; i++)
                {
                    db.Insert(new CdParent { Id = i, Name = "P" + i, Kids = new List<CdKid> { new CdKid { Tag = "same", Pad = pad }, new CdKid { Tag = "own" + i, Pad = pad } } });
                    db.Insert(new CdOrder { Id = i, Name = "O" + i, Lines = new List<CdLine> { new CdLine { Tag = "same", Pad = pad }, new CdLine { Tag = "own" + i, Pad = pad } } });
                }
                Assert.True(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag").Length > 6);

                var p1 = db.Find<CdParent>(p => p.Id == 1, include: "*");
                Assert.True(db.Update(p1, p1));
                Assert.True(db.Delete(db.Find<CdOrder>(o => o.Id == 1, include: "*")));

                Assert.Equal(60, db.Count<CdKid>());
                Assert.Equal(60, db.Select<CdParent>(include: "*").Sum(p => p.Kids.Count));
                Assert.Equal(58, db.Count<CdLine>());
                Assert.Equal(29, db.Select<CdLine>().Count(l => l.Tag == "same"));
            }
            using (var db = new XodContext(path, null, options))
            {
                Assert.Equal(60, db.Count<CdKid>());
                Assert.Equal(58, db.Count<CdLine>());
                Assert.All(db.Select<CdOrder>(include: "*"), o => Assert.Equal(new[] { "own" + o.Id, "same" }, o.Lines.Select(l => l.Tag).OrderBy(t => t, StringComparer.Ordinal)));
            }
        }

        // ISS-186: DeleteWhere deletes each match by its code: equal rows without a key are each counted, and each
        // fires its triggers.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DeleteWhere_counts_and_triggers_each_equal_row(bool indexing)
        {
            string path = NewPath("where", indexing);
            using var db = Open(path, indexing);
            for (int i = 0; i < 30; i++)
                db.Insert(new CdLoose { Group = i < 20 ? "g" : "other" });
            int before = 0, after = 0;
            db.BeforeAction += (_, e) => { if (e.Action == DatabaseActions.Delete) before++; };
            db.AfterAction += (_, e) => { if (e.Action == DatabaseActions.Delete) after++; };

            Assert.Equal(20, db.DeleteWhere<CdLoose>(r => r.Group == "g"));

            Assert.Equal(20, before);
            Assert.Equal(20, after);
            Assert.Equal(10, db.Count<CdLoose>());
            Assert.All(db.Select<CdLoose>(), r => Assert.Equal("other", r.Group));
        }
    }
}
