using System;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-123: a property named <c>Id</c> with no <c>[PrimaryKey]</c> is the key and an autonumber by convention when
    /// it is a number or a <c>Guid</c>. A nullable number was (the check unwraps <c>Nullable</c>), a <c>Guid?</c> wasn't,
    /// so rows inserted with a null <c>Id</c> all kept a null key, though a declared
    /// <c>[Property(AutoNumber = true)] Guid?</c> is one.
    /// </summary>
    public class XodNullableGuidIdTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-guidid-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class NgRow
        {
            public Guid? Id { get; set; }
            public string Name { get; set; }
        }

        [Fact]
        public void A_nullable_Guid_Id_is_an_autonumber_by_convention()
        {
            Directory.CreateDirectory(tempRoot);
            string path = Path.Combine(tempRoot, "ng.xod");
            var a = new NgRow { Name = "a" };
            var b = new NgRow { Name = "b" };
            using (var db = new XodContext(path))
            {
                db.Insert(a);
                db.Insert(b);
            }

            Assert.NotNull(a.Id);
            Assert.NotNull(b.Id);
            Assert.NotEqual(Guid.Empty, a.Id.Value);
            Assert.NotEqual(a.Id, b.Id);

            using (var db = new XodContext(path))
            {
                Assert.Equal(2, db.Select<NgRow>().Count());
                Assert.Equal("b", db.Find<NgRow>(r => r.Id == b.Id)?.Name);
            }
        }
    }
}
