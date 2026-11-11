// An index must never change an answer (PLAN-045 principle 3): Query<T>(predicate) on an indexed
// property must return exactly what the same predicate returns over every row. Each test stores values
// at the edges of a type, then compares the two, before and after reopening (index from memory, then
// from the .xidx file). ISS-053, ISS-054, ISS-056 and ISS-057.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Xunit;

namespace Xod.Tests
{
    public enum IndexedTier { Basic = 0, Silver = 1, Gold = 2, Platinum = 10 }

    public class IndexedValues
    {
        public int Id { get; set; }
        [Property(Indexed = true)] public IndexedTier Tier { get; set; }
        [Property(Indexed = true)] public char Code { get; set; }
        [Property(Indexed = true)] public decimal Amount { get; set; }
        [Property(Indexed = true)] public double Measure { get; set; }
        [Property(Indexed = true)] public float Ratio { get; set; }
        [Property(Indexed = true)] public long Big { get; set; }
        [Property(Indexed = true)] public ulong Huge { get; set; }
        [Property(Indexed = true)] public TimeSpan Span { get; set; }
        [Property(Indexed = true)] public DateTime When { get; set; }
        [Property(Indexed = true)] public int? Maybe { get; set; }
    }

    public class UniqueAmount
    {
        public int Id { get; set; }
        [UniqueKey] public decimal Amount { get; set; }
    }

    public class XodIndexSemanticsTests
    {
        private static readonly IndexedValues[] Rows =
        {
            new() { Tier = IndexedTier.Basic, Code = 'A', Amount = 0.000000001m, Measure = 1e30, Ratio = 0.1f, Big = long.MinValue, Huge = ulong.MaxValue, Span = TimeSpan.FromDays(10), When = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Maybe = null },
            new() { Tier = IndexedTier.Gold, Code = 'Z', Amount = 0.000000002m, Measure = -1e30, Ratio = 0.25f, Big = -3_000_000_000_000_000_000, Huge = 10_000_000_000_000_000_000UL, Span = TimeSpan.FromDays(2), When = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), Maybe = 3 },
            new() { Tier = IndexedTier.Platinum, Code = 'a', Amount = 12.50m, Measure = 1e25, Ratio = -0.5f, Big = -2_000_000_000_000_000_000, Huge = 5, Span = TimeSpan.FromHours(-1), When = new DateTime(2026, 1, 1, 3, 0, 0, DateTimeKind.Utc), Maybe = -7 },
            new() { Tier = IndexedTier.Silver, Code = '0', Amount = -12.5m, Measure = double.NaN, Ratio = 1e30f, Big = 0, Huge = 0, Span = TimeSpan.FromHours(8.5), When = new DateTime(2025, 12, 31, 22, 0, 0, DateTimeKind.Utc), Maybe = 0 },
            new() { Tier = IndexedTier.Gold, Code = 'm', Amount = 0m, Measure = -0.0, Ratio = 0f, Big = 3_000_000_000_000_000_000, Huge = 9_000_000_000_000_000_000UL, Span = TimeSpan.Zero, When = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), Maybe = 3 },
            new() { Tier = IndexedTier.Basic, Code = '~', Amount = 1_000_000_000_000_000_000m, Measure = 0.1, Ratio = 0.5f, Big = long.MaxValue, Huge = 1, Span = TimeSpan.FromDays(-3), When = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1), Maybe = null },
            new() { Tier = (IndexedTier)7, Code = 'é', Amount = -79228162514264337593543950335m, Measure = double.PositiveInfinity, Ratio = 2f, Big = -900_000_000_000_000_001, Huge = 2, Span = TimeSpan.FromTicks(1), When = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local), Maybe = 1000 },
        };

        private static readonly IndexedTier gold = IndexedTier.Gold;

        // Each predicate as C# means it. Constants and captured variables both, because the expression
        // tree differs (a captured enum arrives through a closure, a literal one as a converted number).
        private static readonly (string Name, Expression<Func<IndexedValues, bool>> Predicate)[] Predicates =
        {
            ("enum == literal (ISS-056)", x => x.Tier == IndexedTier.Gold),
            ("enum == captured", x => x.Tier == gold),
            ("enum > (by value, not name)", x => x.Tier > IndexedTier.Silver),
            ("enum undefined value", x => x.Tier == (IndexedTier)7),
            ("enum <= with AND", x => x.Tier <= IndexedTier.Gold && x.Code != 'A'),
            ("char ==", x => x.Code == 'Z'),
            ("char < (by code point)", x => x.Code < 'a'),
            ("decimal == past 8 places (ISS-053)", x => x.Amount == 0.000000001m),
            ("decimal == with another scale", x => x.Amount == 12.5m),
            ("decimal >= 1e18", x => x.Amount >= 1_000_000_000_000_000_000m),
            ("decimal < 0", x => x.Amount < 0m),
            ("decimal > tiny", x => x.Amount > 0.0000000015m),
            ("double > 1e20 (ISS-054)", x => x.Measure > 1e20),
            ("double < -1e20", x => x.Measure < -1e20),
            ("double == 0 matches -0", x => x.Measure == 0.0),
            ("double == NaN matches nothing", x => x.Measure == double.NaN),
            ("double == 0.1", x => x.Measure == 0.1),
            ("double <= +inf", x => x.Measure <= double.PositiveInfinity),
            ("float == 0.1 (a double)", x => x.Ratio == 0.1),
            ("float == 0.1f", x => x.Ratio == 0.1f),
            ("float > 1", x => x.Ratio > 1f),
            ("long < -2.5e18", x => x.Big < -2_500_000_000_000_000_000),
            ("long > 2e18", x => x.Big > 2_000_000_000_000_000_000),
            ("long == MinValue", x => x.Big == long.MinValue),
            ("long < -9e17", x => x.Big < -900_000_000_000_000_000),
            ("ulong > 2^63", x => x.Huge > 9_300_000_000_000_000_000UL),
            ("ulong == MaxValue", x => x.Huge == ulong.MaxValue),
            ("TimeSpan < 3 days", x => x.Span < TimeSpan.FromDays(3)),
            ("TimeSpan > 1 day", x => x.Span > TimeSpan.FromDays(1)),
            ("TimeSpan < 0", x => x.Span < TimeSpan.Zero),
            ("DateTime == unspecified literal (ISS-057)", x => x.When == new DateTime(2026, 1, 1)),
            ("DateTime < unspecified literal", x => x.When < new DateTime(2026, 1, 1, 2, 0, 0)),
            ("DateTime >= utc literal", x => x.When >= new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ("int? == 3", x => x.Maybe == 3),
            ("int? > -10", x => x.Maybe > -10),
            ("int? == null", x => x.Maybe == null),
        };

        private static string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-index-semantics", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static List<string> Check(XodContext db)
        {
            var all = db.Select<IndexedValues>().ToList();
            var wrong = new List<string>();
            foreach (var (name, predicate) in Predicates)
            {
                var want = all.Where(predicate.Compile()).Select(x => x.Id).OrderBy(i => i).ToList();
                List<int> got;
                try { got = db.Query(predicate).Select(x => x.Id).OrderBy(i => i).ToList(); }
                catch (Exception e) { wrong.Add($"{name}: threw {e.GetType().Name}: {e.Message}"); continue; }
                if (!want.SequenceEqual(got)) wrong.Add($"{name}: got [{string.Join(",", got)}], every-row answer [{string.Join(",", want)}]");
            }
            return wrong;
        }

        [Theory]
        [InlineData(StorageFormat.Xml, null)]
        [InlineData(StorageFormat.Json, null)]
        [InlineData(StorageFormat.Binary, null)]
        [InlineData(StorageFormat.Xml, "pw")]
        public void An_index_never_changes_the_answer(StorageFormat format, string password)
        {
            string path = NewDatabase(format + (password ?? ""));
            var options = new DatabaseOptions { StorageFormat = format };
            var wrong = new List<string>();
            using (var db = new XodContext(path, password, options))
            {
                foreach (var row in Rows) db.Insert(Clone(row));
                wrong.AddRange(Check(db).Select(w => "in memory: " + w));
            }
            using (var db = new XodContext(path, password, options))
                wrong.AddRange(Check(db).Select(w => "reopened: " + w));

            // The index rebuilt from the pages (as after an upgrade or a crash recovery) must agree too.
            foreach (string xidx in Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xidx"))
                File.Delete(xidx);
            using (var db = new XodContext(path, password, options))
                wrong.AddRange(Check(db).Select(w => "rebuilt: " + w));

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        public class Defaults
        {
            public int Id { get; set; }
            [Property(Indexed = true)] public TimeSpan Span { get; set; }
            [Property(Indexed = true)] public string Name { get; set; }
            [Property(Indexed = true)] public Guid Ref { get; set; }
            [Property(Indexed = true)] public DateTime When { get; set; }
        }

        // The writers leave some values out of the row (TimeSpan.Zero, "", Guid.Empty, a default
        // DateTime); the index must key what a read gives back: as inserted, after an update, and rebuilt.
        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        public void Values_the_writers_leave_out_are_keyed_as_they_read_back(StorageFormat format)
        {
            string path = NewDatabase("defaults-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            var wrong = new List<string>();
            void CheckAll(XodContext db, string when)
            {
                var all = db.Select<Defaults>().ToList();
                var predicates = new (string, Expression<Func<Defaults, bool>>)[]
                {
                    ("Span == 0", x => x.Span == TimeSpan.Zero),
                    ("Span < 3 days", x => x.Span < TimeSpan.FromDays(3)),
                    ("Name == \"\"", x => x.Name == ""),
                    ("Name == null", x => x.Name == null),
                    ("Ref == empty", x => x.Ref == Guid.Empty),
                    ("When == default", x => x.When == default(DateTime)),
                    ("When < 2000", x => x.When < new DateTime(2000, 1, 1)),
                };
                foreach (var (name, p) in predicates)
                {
                    var want = all.Where(p.Compile()).Select(x => x.Id).OrderBy(i => i).ToList();
                    var got = db.Query(p).Select(x => x.Id).OrderBy(i => i).ToList();
                    if (!want.SequenceEqual(got)) wrong.Add($"{when}: {name}: got [{string.Join(",", got)}], every-row answer [{string.Join(",", want)}]");
                }
            }
            using (var db = new XodContext(path, null, options))
            {
                db.Insert(new Defaults { Span = TimeSpan.Zero, Name = "", Ref = Guid.Empty });
                var moved = db.Insert(new Defaults { Span = TimeSpan.Zero, Name = "", Ref = Guid.Empty });
                db.Insert(new Defaults { Span = TimeSpan.FromDays(9), Name = "x", Ref = Guid.NewGuid(), When = new DateTime(2026, 1, 1) });
                moved.Span = TimeSpan.FromDays(5);
                moved.Name = "five";
                moved.Ref = Guid.NewGuid();
                moved.When = new DateTime(2026, 2, 2);
                db.Update(moved);
                CheckAll(db, "same context");
            }
            using (var db = new XodContext(path, null, options))
                CheckAll(db, "reopened");
            foreach (string xidx in Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xidx"))
                File.Delete(xidx);
            using (var db = new XodContext(path, null, options))
                CheckAll(db, "rebuilt");
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        [Fact]
        public void Unique_decimals_that_differ_past_the_eighth_place_are_different_keys()
        {
            using var db = new XodContext(NewDatabase("unique-decimal"));
            db.Insert(new UniqueAmount { Amount = 0.000000001m });
            db.Insert(new UniqueAmount { Amount = 0.000000002m }); // threw ReservedUniqueKeyException
            Assert.Throws<ReservedUniqueKeyException>(() => db.Insert(new UniqueAmount { Amount = 0.0000000010m }));
            Assert.Equal(2, db.Count<UniqueAmount>());
        }

        private static IndexedValues Clone(IndexedValues r) => new()
        {
            Tier = r.Tier, Code = r.Code, Amount = r.Amount, Measure = r.Measure, Ratio = r.Ratio, Big = r.Big,
            Huge = r.Huge, Span = r.Span, When = r.When, Maybe = r.Maybe,
        };
    }
}
