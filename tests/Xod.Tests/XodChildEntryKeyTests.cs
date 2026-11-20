using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-204: a list entry's key attributes are written as the key is stored, and an entry that names no row is taken
    /// over by a child with its key whichever form the entry holds. They were written by <c>XAttribute</c>'s own
    /// conversion (<c>PT1H</c> for one hour) and compared with the key's <c>ToString</c> (<c>01:00:00</c>), so for a
    /// <c>TimeSpan</c>, a <c>bool</c> or a <c>DateTime</c> key no entry ever matched, and the child got a second entry
    /// beside the one left behind. An entry names no row once its child's table is dropped (by design, ISS-210).
    /// </summary>
    public class XodChildEntryKeyTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-entrykey-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class EkSlot
        {
            [PrimaryKey] public TimeSpan At { get; set; }
            public int EkDayId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(EkDayId))] public EkDay Day { get; set; }
        }

        public class EkDay
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [Children] public List<EkSlot> Slots { get; set; }
        }

        private static XElement StoredList(string dir) =>
            Directory.GetFiles(dir, "*.xpag")
                .Select(f => XDocument.Load(f))
                .SelectMany(d => d.Descendants(nameof(EkDay)))
                .Single()
                .Element(nameof(EkDay.Slots));

        private string Seeded(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            using var db = new XodContext(Path.Combine(dir, "ek.xod"), null, new DatabaseOptions { EnableIndexing = indexing });
            db.Insert(new EkDay { Name = "d", Slots = new() { new EkSlot { At = TimeSpan.FromHours(1), Text = "first" } } });
            Assert.True(db.Drop<EkSlot>());
            return dir;
        }

        private static void InsertAgain(string dir, bool indexing)
        {
            using var db = new XodContext(Path.Combine(dir, "ek.xod"), null, new DatabaseOptions { EnableIndexing = indexing });
            var day = db.Find<EkDay>(d => d.Name == "d");
            db.Insert(new EkSlot { At = TimeSpan.FromHours(1), Day = day, Text = "again" });
            Assert.Equal(new[] { "again" }, db.Find<EkDay>(d => d.Name == "d").Slots.Select(s => s.Text).ToArray());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_child_takes_over_the_entry_its_key_left(bool indexing)
        {
            string dir = Seeded("new", indexing);
            Assert.Equal("01:00:00", StoredList(dir).Elements().Single().Attribute(nameof(EkSlot.At))!.Value);

            InsertAgain(dir, indexing);
            Assert.Single(StoredList(dir).Elements());
        }

        // An entry an earlier version wrote holds XAttribute's form of the key; it is taken over too.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_child_takes_over_an_entry_written_in_the_earlier_form(bool indexing)
        {
            string dir = Seeded("earlier", indexing);
            foreach (string page in Directory.GetFiles(dir, "*.xpag"))
            {
                var doc = XDocument.Load(page);
                var entry = doc.Descendants(nameof(EkDay)).SingleOrDefault()?.Element(nameof(EkDay.Slots))?.Elements().Single();
                if (entry == null)
                    continue;
                entry.SetAttributeValue(nameof(EkSlot.At), "PT1H");
                doc.Save(page);
            }

            InsertAgain(dir, indexing);
            Assert.Single(StoredList(dir).Elements());
        }
    }
}
