// Defects found while writing the file-format spec (PLAN-046 section 11) that a query case can't show.
// Each test states the correct behaviour and expects it to be missing today: it passes while the defect
// is open and fails the day it is fixed, so the marker can't outlive the bug. On that day, turn the test
// into an ordinary assertion and close the issue.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xod.Helpers;
using Xunit;
using Xunit.Sdk;

namespace Xod.Conformance
{
    public class KnownDefectTests
    {
        private static void StillOpen(string issue, Func<bool> behavesCorrectly)
        {
            if (behavesCorrectly())
                throw new XunitException($"{issue} behaves correctly now: make this an ordinary test and close the issue.");
        }

        private static string NewFolder(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-known", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.xod");
        }

        public class Parent { public int Id { get; set; } public string Text { get; set; } [Children] public List<Kid> Kids { get; set; } }
        public class Kid { public int Id { get; set; } public int ParentId { get; set; } }
        public class Item { public int Id { get; set; } public string Name { get; set; } }
        public class Reading { public int Id { get; set; } [Property(Indexed = true)] public double Value { get; set; } }
        public class Secret { public int Id { get; set; } [Crypto(CryptoMethod.AES256)] public string Key { get; set; } }

        // ISS-051, fixed: one XML character policy (XmlText). Xod.Tests/XodTextRoundTripTests covers the rest.
        [Fact]
        public void ISS_051_an_xml_page_with_a_control_character_reads_back()
        {
            string path = NewFolder("iss051");
            const string text = "a\u0001b";
            using (var db = new XodContext(path)) db.Insert(new Parent { Text = text, Kids = new() { new Kid() } });
            using (var db = new XodContext(path))
            {
                Assert.Equal(text, db.Find<Parent>(p => p.Id == 1)?.Text);
                Assert.Equal(1, db.Count<Parent>());
            }
        }

        // ISS-052, fixed: the table records its highest autonumber when a row is deleted.
        // Xod.Tests/XodAutonumberTests covers the rest.
        [Fact]
        public void ISS_052_a_deleted_id_is_not_reused_after_reopening()
        {
            string path = NewFolder("iss052");
            using (var db = new XodContext(path))
            {
                for (int i = 0; i < 5; i++) db.Insert(new Item { Name = "i" + i });
                db.Delete(db.Find<Item>(x => x.Id == 5));
            }
            using (var db = new XodContext(path)) Assert.Equal(6, db.Insert(new Item { Name = "new" }).Id);
        }

        // ISS-054, fixed: keys v2. Xod.Tests/XodIndexSemanticsTests covers the edges.
        [Fact]
        public void ISS_054_an_indexed_double_beyond_decimal_range_can_be_stored()
        {
            using var db = new XodContext(NewFolder("iss054"));
            db.Insert(new Reading { Value = 1e30 });
            Assert.Single(db.Query<Reading>(r => r.Value == 1e30));
        }

        [Fact]
        public void ISS_050_an_aes256_value_without_a_password_cannot_be_read_with_the_library_constants() => StillOpen("ISS-050", () =>
        {
            string path = NewFolder("iss050");
            using (var db = new XodContext(path)) db.Insert(new Secret { Key = "sk-test-value" });
            string stored = Directory.GetFiles(Path.GetDirectoryName(path), "*.xpag")
                .Select(f => Regex.Match(File.ReadAllText(f), "enc:aes256:[A-Za-z0-9+/=]+"))
                .First(m => m.Success).Value;
            // DecryptProperty with no passphrase uses the built-in key: if that recovers the value, so can anyone.
            try { return CryptoHelper.DecryptProperty(stored, null) != "sk-test-value"; }
            catch (SecurityException) { return true; }
        });
    }
}
