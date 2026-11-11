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

        // ISS-050, fixed: an AES256 value needs a key to protect it, and its key is the database's own,
        // never a constant. Xod.Tests/XodPropertyCryptoTests covers the rest.
        [Fact]
        public void ISS_050_an_aes256_value_is_never_encrypted_with_the_library_constants()
        {
            string path = NewFolder("iss050");
            using (var db = new XodContext(path))
                Assert.Throws<SecurityException>(() => db.Insert(new Secret { Key = "sk-test-value" }));
            using (var db = new XodContext(path, null, new DatabaseOptions { PropertyEncryptionKey = "app-secret" }))
                db.Insert(new Secret { Key = "sk-test-value" });
            string stored = Directory.GetFiles(Path.GetDirectoryName(path), "*.xpag")
                .Select(f => Regex.Match(File.ReadAllText(f), "enc:aes256[a-z0-9]*:[A-Za-z0-9+/=]+"))
                .First(m => m.Success).Value;
            // The 2.0 helper with no passphrase used the built-in key; it must not recover the value.
            string recovered;
#pragma warning disable CS0618 // the 2.0 helper, on purpose
            try { recovered = CryptoHelper.DecryptProperty(stored, null); }
#pragma warning restore CS0618
            catch (SecurityException) { recovered = null; }
            Assert.NotEqual("sk-test-value", recovered);
        }

        // ISS-058, fixed: values 2.0 wrote stay readable when the password is added, changed or removed.
        // The committed fixtures hold them: with no password under 2.0's built-in key, with the password
        // under a key derived from it.
        private static string CopyFixture(string fixture)
        {
            string target = Path.Combine(Path.GetTempPath(), "xod-known", "legacy-" + fixture + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(Path.Combine(Repo.Conformance, "data", fixture)))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            return Path.Combine(target, "app.xod");
        }

        private static string[] ApiKeys(XodContext db) =>
            db.Select<Model.Integration>().OrderBy(i => i.Id).Select(i => i.ApiKey).ToArray();

        private static readonly string[] LegacyApiKeys = { "sk-billing-0001", "sk-mail-0002", null };

        [Fact]
        public void ISS_058_values_2_0_wrote_under_a_password_survive_changing_it()
        {
            string path = CopyFixture("password");
            using (var db = new XodContext(path, Dataset.Password))
            {
                Assert.Equal(LegacyApiKeys, ApiKeys(db));
                db.ChangePassword(Dataset.Password, "changed");
            }
            using (var db = new XodContext(path, "changed"))
            {
                Assert.Equal(LegacyApiKeys, ApiKeys(db));
                db.ChangePassword("changed", "again");
            }
            using (var db = new XodContext(path, "again", new DatabaseOptions { PropertyEncryptionKey = "app-secret" }))
            {
                Assert.Equal(LegacyApiKeys, ApiKeys(db));
                db.Loose("again");
            }
            using (var db = new XodContext(path, null, new DatabaseOptions { PropertyEncryptionKey = "app-secret" }))
                Assert.Equal(LegacyApiKeys, ApiKeys(db));
        }

        // Removing the password would leave 2.0's values with no key at all: refused without an application
        // key, and nothing changes.
        [Fact]
        public void ISS_058_removing_a_password_that_2_0_values_need_is_refused_without_an_application_key()
        {
            string path = CopyFixture("password");
            using (var db = new XodContext(path, Dataset.Password))
            {
                Assert.Throws<SecurityException>(() => db.Loose(Dataset.Password));
                Assert.Equal(LegacyApiKeys, ApiKeys(db));
            }
            using (var db = new XodContext(path, Dataset.Password))
                Assert.Equal(LegacyApiKeys, ApiKeys(db));
        }

        // Once an application key protects the database's key, a context without it can't keep 2.0's key
        // through a password change: refused, and nothing changes.
        [Fact]
        public void ISS_058_a_password_change_that_would_lose_2_0_values_is_refused()
        {
            string path = CopyFixture("password");
            var withKey = new DatabaseOptions { PropertyEncryptionKey = "app-secret" };
            using (var db = new XodContext(path, Dataset.Password, withKey))
                db.ChangePassword(Dataset.Password, "second");
            using (var db = new XodContext(path, "second"))
                Assert.Throws<SecurityException>(() => db.ChangePassword("second", "third"));
            using (var db = new XodContext(path, "second", withKey))
            {
                Assert.Equal(LegacyApiKeys, ApiKeys(db));
                db.ChangePassword("second", "third");
            }
            using (var db = new XodContext(path, "third", withKey))
                Assert.Equal(LegacyApiKeys, ApiKeys(db));
        }

        [Fact]
        public void ISS_058_values_2_0_wrote_without_a_password_survive_securing_it()
        {
            string path = CopyFixture("xml");
            using (var db = new XodContext(path))
            {
                Assert.Equal(LegacyApiKeys, ApiKeys(db));
                db.Secure("added");
            }
            using (var db = new XodContext(path, "added"))
                Assert.Equal(LegacyApiKeys, ApiKeys(db));
        }
    }
}
