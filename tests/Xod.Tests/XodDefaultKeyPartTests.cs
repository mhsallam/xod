using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-188: a key part at its type's default is no value (decided 2026-11-18). The index looked every key part up
    /// literally, a <c>0</c> included, while the example matcher and the primary-key check took a part at its default as
    /// unset: with a composite key and indexing off, <c>Update(new T { A = 1, B = 0 })</c> rewrote every row with
    /// <c>A = 1</c>, where indexing on changed one; and rows were stored with a key part at <c>0</c>, never kept
    /// unique. Now a row isn't written with a key part at its default (an autonumber is given its value first), a
    /// lookup by key needs every part, and an example that sets some key parts but not all is refused, the same with
    /// and without indexing. An example that sets no key part names no row of a keyed type, as before.
    /// </summary>
    public class XodDefaultKeyPartTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-keypart-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class KpPair
        {
            [PrimaryKey] public int A { get; set; }
            [PrimaryKey] public int B { get; set; }
            public string V { get; set; }
        }

        public class KpCode
        {
            [PrimaryKey] public int Code { get; set; }
            public string V { get; set; }
        }

        public class KpAuto
        {
            public int Id { get; set; }
            public string V { get; set; }
        }

        private XodContext Open(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return new XodContext(Path.Combine(dir, "kp.xod"), null, new DatabaseOptions { EnableIndexing = indexing });
        }

        private static void Seed(XodContext db)
        {
            for (int b = 1; b <= 5; b++)
                db.Insert(new KpPair { A = 1, B = b, V = "v" + b });
            db.Insert(new KpPair { A = 2, B = 1, V = "w1" });
        }

        private static string[] Rows(XodContext db) =>
            db.Select<KpPair>().Select(p => $"{p.A},{p.B},{p.V}").OrderBy(s => s, StringComparer.Ordinal).ToArray();

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_row_with_a_key_part_at_its_default_is_not_written(bool indexing)
        {
            using var db = Open("insert", indexing);
            Assert.Throws<MissingPrimaryKeyValueException>(() => db.Insert(new KpCode { Code = 0, V = "zero" }));
            Assert.Throws<MissingPrimaryKeyValueException>(() => db.Insert(new KpPair { A = 1, B = 0, V = "half" }));
            Assert.Throws<MissingPrimaryKeyValueException>(() => db.InsertRange(new[] { new KpCode { Code = 1 }, new KpCode { Code = 0 } }));
            Assert.Equal(0, db.Count<KpCode>());
            Assert.Equal(0, db.Count<KpPair>());

            // An autonumber is given its value before the check.
            db.Insert(new KpAuto { V = "a" });
            db.Insert(new KpCode { Code = 7, V = "seven" });
            Assert.Equal(1, db.Count<KpAuto>());
            Assert.Equal("seven", db.Find<KpCode>(c => c.Code == 7).V);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_key_with_a_part_at_its_default_names_no_row(bool indexing)
        {
            using var db = Open("bykey", indexing);
            Seed(db);
            string[] before = Rows(db);

            Assert.Throws<MissingPrimaryKeyValueException>(() => db.Update(new KpPair { A = 1, B = 0, V = "x" }));
            Assert.Throws<MissingPrimaryKeyValueException>(() => db.Delete(new KpPair { A = 1, B = 0 }));
            Assert.Throws<MissingPrimaryKeyValueException>(() => db.InsertOrUpdate(new KpPair { A = 1, B = 0, V = "x" }));
            Assert.Throws<MissingPrimaryKeyValueException>(() => db.Update(new KpPair { A = 1 }, new KpPair { A = 1, B = 2, V = "x" }));
            Assert.Throws<MissingPrimaryKeyValueException>(() => db.Update(new KpCode { Code = 0, V = "x" }));

            Assert.Equal(before, Rows(db));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_update_that_writes_a_key_part_at_its_default_is_refused(bool indexing)
        {
            using var db = Open("rekey", indexing);
            Seed(db);
            string[] before = Rows(db);

            Assert.Throws<MissingPrimaryKeyValueException>(() => db.Update(new KpPair { A = 1, B = 1 }, new KpPair { A = 1, B = 0, V = "y" }));

            Assert.Equal(before, Rows(db));
        }

        // A guard: a full key and an example without a key part work as before.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_full_key_and_an_example_without_one_work_as_before(bool indexing)
        {
            using var db = Open("guard", indexing);
            Seed(db);

            Assert.True(db.Update(new KpPair { A = 1, B = 3, V = "v3b" }));
            Assert.True(db.Delete(new KpPair { A = 1, B = 5 }));
            // A keyed type is updated and deleted by its key: an example that sets no key part names no row, as before.
            Assert.False(db.Update(new KpPair { V = "w1" }, new KpPair { A = 2, B = 1, V = "w1b" }));
            Assert.False(db.Delete(new KpPair { V = "v4" }));
            db.InsertOrUpdate(new KpPair { A = 1, B = 1, V = "v1b" });
            db.InsertOrUpdate(new KpPair { A = 3, B = 1, V = "n" });
            db.InsertOrUpdate(new KpAuto { V = "new" });

            Assert.Equal(new[] { "1,1,v1b", "1,2,v2", "1,3,v3b", "1,4,v4", "2,1,w1", "3,1,n" }, Rows(db));
            Assert.Equal(1, db.Count<KpAuto>());
        }

        public class KpOrder
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Children] public List<KpLine> Lines { get; set; }
        }

        public class KpLine
        {
            [PrimaryKey] public int KpOrderId { get; set; }
            [PrimaryKey] public int LineNo { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(KpOrderId))] public KpOrder Order { get; set; }
        }

        public class KpStudent { public int Id { get; set; } public string Name { get; set; } }
        public class KpCourse { public int Id { get; set; } public string Name { get; set; } }

        public class KpEnrolment
        {
            [PrimaryKey] public int StudentId { get; set; }
            [PrimaryKey] public int CourseId { get; set; }
            [ForeignKey(nameof(StudentId))] public KpStudent Student { get; set; }
            [ForeignKey(nameof(CourseId))] public KpCourse Course { get; set; }
        }

        // A key part a relation gives ([ParentKey], [ForeignKey]) is set by the row's write: the check runs after it.
        // It ran before, so such a row was refused, and before ISS-188 never checked for uniqueness.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_key_part_a_relation_gives_is_set_before_the_check(bool indexing)
        {
            using var db = Open("relation", indexing);
            db.Insert(new KpOrder { Name = "o" });
            var order = db.Find<KpOrder>(o => o.Name == "o");
            db.Insert(new KpLine { Order = order, LineNo = 2, Text = "two" });
            Assert.Throws<ReservedPrimaryKeyException>(() => db.Insert(new KpLine { Order = order, LineNo = 2, Text = "again" }));
            Assert.Equal(new[] { "two" }, db.Select<KpLine>().Select(l => l.Text).ToArray());
            Assert.Equal(order.Id, db.Select<KpLine>().Single().KpOrderId);

            db.Insert(new KpStudent { Name = "s" });
            db.Insert(new KpCourse { Name = "c" });
            var student = db.Find<KpStudent>(x => x.Name == "s");
            var course = db.Find<KpCourse>(x => x.Name == "c");
            db.Insert(new KpEnrolment { Student = student, Course = course });
            Assert.Throws<ReservedPrimaryKeyException>(() => db.Insert(new KpEnrolment { Student = student, Course = course }));
            var e = db.Select<KpEnrolment>().Single();
            Assert.Equal((student.Id, course.Id), (e.StudentId, e.CourseId));
        }

        // A row 2.0.x stored with a key part at its default: an example without a key names it with indexing on and
        // off alike, that is not at all. The delete by key looked the default up literally in the index.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_stored_row_with_a_key_part_at_its_default_is_named_by_no_example(bool indexing)
        {
            string dir = Path.Combine(tempRoot, "legacy" + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "kp.xod");
            using (var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing }))
                db.Insert(new KpCode { Code = 7, V = "seven" });
            foreach (var f in Directory.GetFiles(dir, "*.xpag"))
                File.WriteAllText(f, File.ReadAllText(f).Replace("<Code>7</Code>", "<Code>0</Code>"));
            foreach (var f in Directory.GetFiles(dir, "*.xidx"))
                File.Delete(f);

            using (var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing }))
            {
                Assert.False(db.Delete(new KpCode { V = "seven" }));
                Assert.False(db.Update(new KpCode { V = "seven" }, new KpCode { Code = 8, V = "eight" }));
                Assert.Equal(new[] { "0,seven" }, db.Select<KpCode>().Select(c => $"{c.Code},{c.V}").ToArray());
                Assert.Equal(1, db.DeleteWhere<KpCode>(c => c.V == "seven"));
            }
        }
    }
}
