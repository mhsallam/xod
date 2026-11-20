using System;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-215: a reference by a foreign key to a type that names this one back (<c>SelfForeign</c>: the row's own type,
    /// or two types that reference each other) follows <c>Cascade = Delete</c>, as any other reference does. The rows a
    /// row owns (OwnedRows) left it out whatever it declared, so deleting the row kept the row it referenced.
    /// </summary>
    public class XodSelfCascadeDeleteTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-selfdel-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class SdPerson
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int ManagerId { get; set; }
            [ForeignKey(nameof(ManagerId)), Property(Cascade = CascadeOptions.Delete)] public SdPerson Manager { get; set; }
        }

        public class SdPlainPerson
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int ManagerId { get; set; }
            [ForeignKey(nameof(ManagerId))] public SdPlainPerson Manager { get; set; }
        }

        private XodContext Open(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return new XodContext(Path.Combine(dir, "sd.xod"), null, new DatabaseOptions { EnableIndexing = indexing });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Deleting_a_row_deletes_the_chain_its_references_cascade_to(bool indexing)
        {
            using var db = Open("chain", indexing);
            db.Insert(new SdPerson { Name = "top" });
            db.Insert(new SdPerson { Name = "middle", ManagerId = 1 });
            db.Insert(new SdPerson { Name = "bottom", ManagerId = 2 });
            db.Insert(new SdPerson { Name = "other" });

            Assert.True(db.Delete(new SdPerson { Id = 3 }));
            Assert.Equal(new[] { "other" }, db.Select<SdPerson>().Select(p => p.Name).ToArray());
        }

        // Two rows that reference each other with the cascade: the delete takes both and ends.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_cycle_of_cascading_deletes_ends(bool indexing)
        {
            using var db = Open("cycle", indexing);
            db.Insert(new SdPerson { Name = "a" });
            db.Insert(new SdPerson { Name = "b", ManagerId = 1 });
            var a = db.Find<SdPerson>(p => p.Name == "a");
            a.ManagerId = 2;
            Assert.True(db.Update(a));
            db.Insert(new SdPerson { Name = "other" });

            Assert.True(db.Delete(new SdPerson { Id = 1 }));
            Assert.Equal(new[] { "other" }, db.Select<SdPerson>().Select(p => p.Name).ToArray());
        }

        // An update that sets the reference to nothing deletes the row it named, as for any reference that declares
        // Cascade = Delete (OwnedRows, the rule the delete and the update take, ISS-187).
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_update_that_drops_a_cascading_self_reference_deletes_its_row(bool indexing)
        {
            using var db = Open("drop", indexing);
            db.Insert(new SdPerson { Name = "mid" });
            db.Insert(new SdPerson { Name = "emp", ManagerId = 1 });
            var emp = db.Find<SdPerson>(p => p.Name == "emp");
            emp.Manager = null;
            emp.ManagerId = 0;
            Assert.True(db.Update(emp));

            Assert.Equal(new[] { "emp" }, db.Select<SdPerson>().Select(p => p.Name).ToArray());
        }

        // A reference repointed by its key while it holds the object read: the key and the element name one row, as for
        // any reference by a foreign key (the object's keys are written to the key), so the delete's cascade takes the
        // row the stored key names. The element kept the old row while the key named the new one.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_self_reference_repointed_by_its_key_stores_one_row(bool indexing)
        {
            using var db = Open("repoint", indexing);
            db.Insert(new SdPerson { Name = "mid" });
            db.Insert(new SdPerson { Name = "other" });
            db.Insert(new SdPerson { Name = "emp", ManagerId = 1 });
            var emp = db.Find<SdPerson>(p => p.Name == "emp");
            Assert.Equal("mid", emp.Manager?.Name);
            emp.ManagerId = 2;
            Assert.True(db.Update(emp));

            var stored = db.Find<SdPerson>(p => p.Name == "emp");
            Assert.Equal(stored.ManagerId, stored.Manager?.Id);
            int named = stored.ManagerId;
            Assert.True(db.Delete(new SdPerson { Id = stored.Id }));
            var left = db.Select<SdPerson>().Select(p => p.Id).ToArray();
            Assert.DoesNotContain(named, left);
            Assert.Single(left);
        }

        // Without the cascade a reference only links: the row it names stays.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_plain_self_reference_keeps_its_row(bool indexing)
        {
            using var db = Open("plain", indexing);
            db.Insert(new SdPlainPerson { Name = "boss" });
            db.Insert(new SdPlainPerson { Name = "emp", ManagerId = 1 });

            Assert.True(db.Delete(new SdPlainPerson { Id = 2 }));
            Assert.Equal(new[] { "boss" }, db.Select<SdPlainPerson>().Select(p => p.Name).ToArray());
        }
    }
}
