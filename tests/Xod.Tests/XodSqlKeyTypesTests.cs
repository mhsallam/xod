// A path whose keys are of SQL types that compare but don't hash alike (a DOUBLE key against an INTEGER one: 1.0
// equals 1, but they hash apart) is looked up by the row's stored link, as any path without keys is; looked up by
// hashed keys it found nothing (review of TASK-072).
using System.Linq;
using Xunit;

namespace Xod.Tests
{
    public class SqKeyParent
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class SqKeyChild
    {
        public int Id { get; set; }
        public double ParentRef { get; set; }
        [ForeignKey(nameof(ParentRef), nameof(SqKeyParent.Id))] public SqKeyParent Parent { get; set; }
    }

    public class XodSqlKeyTypesTests
    {
        [Fact]
        public void A_path_whose_keys_compare_but_hash_apart_finds_its_row()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xod-sql-keys", System.Guid.NewGuid().ToString("N")[..8]);
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                using var db = new XodContext(System.IO.Path.Combine(dir, "app.xod"));
                var parent = db.Insert(new SqKeyParent { Name = "p" });
                db.Insert(new SqKeyChild { ParentRef = parent.Id, Parent = parent });
                Assert.Equal("p", db.Select<SqKeyChild>().Single().Parent?.Name);

                var r = db.Sql("SELECT c.Parent.Name FROM SqKeyChild c");
                Assert.Equal("p", r.Rows.Single()[0]);
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { }
            }
        }
    }
}
