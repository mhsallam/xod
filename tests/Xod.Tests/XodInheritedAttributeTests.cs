using System;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-122: XOD's attributes on a base class's <c>virtual</c> property apply to an <c>override</c>. They were read
    /// with <c>PropertyInfo.GetCustomAttributes(true)</c>, which ignores <c>inherit</c> for a property, so a key, an
    /// autonumber, a unique key or <c>[NotMapped]</c> declared on the base property did nothing on a derived type.
    /// </summary>
    public class XodInheritedAttributeTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-inhatt-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string NewPath(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ia.xod");
        }

        // A value type XOD can't store.
        public struct IaPoint { public int X; public int Y; }

        public abstract class IaBase
        {
            [PrimaryKey, Property(AutoNumber = true)] public virtual int Number { get; set; }
            [UniqueKey] public virtual string Code { get; set; }
            public string Name { get; set; }
        }

        public class IaItem : IaBase
        {
            public override int Number { get; set; }
            public override string Code { get; set; }
        }

        public abstract class IaHiddenBase
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [NotMapped] public virtual IaPoint Where { get; set; }
        }

        public class IaHidden : IaHiddenBase
        {
            public override IaPoint Where { get; set; }
        }

        public class IaOwner
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class IaLinkBase
        {
            public int Id { get; set; }
            public int OwnerId { get; set; }
            [ForeignKey("OwnerId", "Id")] public virtual IaOwner Owner { get; set; }
        }

        // The override declares the base property's attribute again: it takes effect once.
        public class IaLink : IaLinkBase
        {
            [ForeignKey("OwnerId", "Id")] public override IaOwner Owner { get; set; }
        }

        // An override that declares a [ForeignKey] of its own replaces the base property's (the review of TASK-107: both
        // were taken, and the insert failed on two key maps); a further override that declares none inherits the nearest.
        public class IaRelinkBase
        {
            public int Id { get; set; }
            public int OwnerId { get; set; }
            public int OtherOwnerId { get; set; }
            [ForeignKey("OwnerId", "Id")] public virtual IaOwner Owner { get; set; }
        }

        public class IaRelink : IaRelinkBase
        {
            [ForeignKey("OtherOwnerId", "Id")] public override IaOwner Owner { get; set; }
        }

        public class IaRelinkLeaf : IaRelink
        {
            public override IaOwner Owner { get; set; }
        }

        // An override's own [Property] replaces the base property's whole: here it is indexed, and no autonumber.
        public abstract class IaNumberedBase
        {
            [PrimaryKey, Property(AutoNumber = true)] public virtual int Number { get; set; }
            public string Name { get; set; }
        }

        public class IaRenumbered : IaNumberedBase
        {
            [Property(Indexed = true)] public override int Number { get; set; }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_key_and_autonumber_on_the_base_property_apply_to_the_override(bool indexing)
        {
            string path = NewPath("key-" + indexing);
            using var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing });
            db.Insert(new IaItem { Code = "a", Name = "first" });
            db.Insert(new IaItem { Code = "b", Name = "second" });

            var rows = db.Select<IaItem>().OrderBy(r => r.Number).ToList();
            Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Number));
            Assert.Equal("second", db.Find<IaItem>(r => r.Number == 2)?.Name);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_unique_key_on_the_base_property_applies_to_the_override(bool indexing)
        {
            string path = NewPath("unique-" + indexing);
            using var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing });
            db.Insert(new IaItem { Code = "same", Name = "first" });
            Assert.ThrowsAny<XodException>(() => db.Insert(new IaItem { Code = "same", Name = "second" }));
            Assert.Single(db.Select<IaItem>());
        }

        [Fact]
        public void NotMapped_on_the_base_property_leaves_the_override_out()
        {
            // The override's type is one XOD can't store: registering the type refused it when [NotMapped] was missed.
            string path = NewPath("notmapped");
            using var db = new XodContext(path);
            db.Insert(new IaHidden { Name = "n", Where = new IaPoint { X = 1, Y = 2 } });
            var row = db.Find<IaHidden>(r => r.Id == 1);
            Assert.Equal("n", row?.Name);
            Assert.Equal(default, row.Where);
        }

        [Fact]
        public void An_override_s_own_foreign_key_replaces_the_base_property_s()
        {
            string path = NewPath("relink");
            using var db = new XodContext(path);
            db.Insert(new IaOwner { Name = "one" });
            db.Insert(new IaOwner { Name = "two" });
            db.Insert(new IaRelink { OwnerId = 1, OtherOwnerId = 2 });
            db.Insert(new IaRelinkLeaf { OwnerId = 1, OtherOwnerId = 2 });
            Assert.Equal("two", db.Find<IaRelink>(r => r.Id == 1, "*")?.Owner?.Name);
            Assert.Equal("two", db.Find<IaRelinkLeaf>(r => r.Id == 1, "*")?.Owner?.Name);
        }

        [Fact]
        public void An_override_s_own_property_attribute_replaces_the_base_property_s()
        {
            string path = NewPath("renumbered");
            using var db = new XodContext(path);
            Assert.Throws<MissingPrimaryKeyValueException>(() => db.Insert(new IaRenumbered { Name = "no number" }));
            db.Insert(new IaRenumbered { Number = 5, Name = "five" });
            Assert.Equal("five", db.Find<IaRenumbered>(r => r.Number == 5)?.Name);
        }

        [Fact]
        public void An_attribute_declared_again_on_the_override_takes_effect_once()
        {
            string path = NewPath("again");
            using var db = new XodContext(path);
            db.Insert(new IaOwner { Name = "owner" });
            db.Insert(new IaLink { OwnerId = 1 });
            var link = db.Find<IaLink>(r => r.Id == 1, "*");
            Assert.Equal("owner", link?.Owner?.Name);
        }
    }
}
