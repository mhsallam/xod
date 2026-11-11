// A row that fails to build must leave its table as it was (ISS-061). The document writer used to
// register a new page in the table before building the row; when building threw (a child that fails
// validation, a refused value), the page stayed registered in memory with nothing left to save the
// table, so every later row of that table was lost when the database was reopened.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Xod.Tests
{
    public class RefusedParent
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Children] public List<RefusedChild> Children { get; set; }
    }

    public class RefusedChild
    {
        public int Id { get; set; }
        public int RefusedParentId { get; set; }
        [Required] public string Label { get; set; }
    }

    public class XodRefusedRowTests
    {
        public static IEnumerable<object[]> Formats() => XodTextRoundTripTests.Formats();

        [Theory]
        [MemberData(nameof(Formats))]
        public void A_row_whose_child_is_refused_leaves_the_table_as_it_was(StorageFormat format, string password)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-refused", format + (password ?? "") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "app.xod");
            var options = new DatabaseOptions { StorageFormat = format };

            using (var db = new XodContext(path, password, options))
            {
                // The table's first row fails: its child has no Label.
                Assert.ThrowsAny<XodException>(() => db.Insert(new RefusedParent
                {
                    Name = "refused",
                    Children = new() { new RefusedChild() }
                }));
                db.Insert(new RefusedParent { Name = "kept", Children = new() { new RefusedChild { Label = "c" } } });
                Assert.Equal(new[] { "kept" }, db.Select<RefusedParent>().Select(p => p.Name));
            }

            using (var db = new XodContext(path, password, options))
            {
                var parents = db.Select<RefusedParent>().ToList();
                Assert.Equal(new[] { "kept" }, parents.Select(p => p.Name));
                Assert.Equal(new[] { "c" }, parents.Single().Children.Select(c => c.Label));
            }
        }
    }
}
