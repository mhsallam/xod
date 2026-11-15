// ISS-151: an encrypted database written by the 2017 XOD (xod.core) didn't open with its password, and the 1.x
// [Crypto] digests in it were hashed again by the first update of any other property of their row. The fixture,
// fixtures/xod.core-2017-encrypted, was written by the 2017 code (its README says how).
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xod;
using Xod.Helpers;
using Xunit;

// The fixture's type, by its stored name (XOD keys its files by Type.FullName). 1.x's bare [Crypto] was MD5;
// 2.x's is SHA256, so a 1.x model names MD5 to go on hashing as 1.x did (MIGRATION-GUIDE).
public class Legacy1xMember
{
    [PrimaryKey] public int Id { get; set; }
    public string Name { get; set; }
    [Crypto(CryptoMethod.MD5)] public string Pin { get; set; }
    [Crypto(CryptoMethod.SHA1)] public string Secret { get; set; }
}

namespace Xod.Tests
{
    public class XodLegacy1xCryptoTests : IDisposable
    {
        private const string Password = "legacy-pass";
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-legacy1x-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private static string FixtureDirectory(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "tests", "Xod.Tests", "fixtures", name);
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("fixture " + name + " not found above " + AppContext.BaseDirectory);
        }

        private string CopyFixture(string name, string fixture = "xod.core-2017-encrypted")
        {
            string target = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(FixtureDirectory(fixture)))
                if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            return Path.Combine(target, "legacy.xod");
        }

        // A 1.x digest, computed here as xod.core/src/Helpers/CryptoHelper.cs did, not by the code under test: the
        // decimal digits of each byte of the hash of the value's ASCII bytes, run together.
        private static string Digest1x(HashAlgorithm algorithm, string value) =>
            string.Concat(algorithm.ComputeHash(Encoding.ASCII.GetBytes(value)).Select(b => b.ToString()));

        private static string Md5Of1x(string value) { using var md5 = MD5.Create(); return Digest1x(md5, value); }
        private static string Sha1Of1x(string value) { using var sha1 = SHA1.Create(); return Digest1x(sha1, value); }

        [Fact]
        public void An_encrypted_1x_database_opens_with_its_password()
        {
            string path = CopyFixture("opens");
            using var db = new XodContext(path, Password);
            var members = db.Select<Legacy1xMember>().OrderBy(m => m.Id).ToList();
            Assert.Equal(Enumerable.Range(1, 12), members.Select(m => m.Id));
            Assert.All(members, m =>
            {
                Assert.Equal("member " + m.Id, m.Name);
                Assert.Equal(Md5Of1x("pin-" + m.Id), m.Pin);
                Assert.Equal(Sha1Of1x("secret " + m.Id), m.Secret);
            });
        }

        [Fact]
        public void An_encrypted_1x_database_refuses_another_password()
        {
            string path = CopyFixture("refuses");
            Assert.Throws<SecurityException>(() =>
            {
                using var db = new XodContext(path, "not-the-password");
                db.Count<Legacy1xMember>();
            });
        }

        [Fact]
        public void An_update_of_another_property_keeps_the_1x_digests()
        {
            string path = CopyFixture("update");
            using (var db = new XodContext(path, Password))
            {
                var member = db.Find<Legacy1xMember>(m => m.Id == 3);
                member.Name = "renamed";
                Assert.True(db.Update(member));
            }

            using (var db = new XodContext(path, Password))
            {
                var member = db.Find<Legacy1xMember>(m => m.Id == 3);
                Assert.Equal("renamed", member.Name);
                Assert.Equal(Md5Of1x("pin-3"), member.Pin);
                Assert.Equal(Sha1Of1x("secret 3"), member.Secret);
            }
        }

        // An encrypted database's pages aren't read as raw bytes, so the update above finds its row through the
        // table's pages. Without a password, the primary key index finds the row and the update reads it from its raw
        // page (the other update path).
        [Fact]
        public void An_update_by_the_primary_key_index_keeps_the_1x_digests()
        {
            string path = CopyFixture("update-indexed", "xod.core-2017-plain");
            using (var db = new XodContext(path))
            {
                var member = db.Find<Legacy1xMember>(m => m.Id == 6);
                member.Name = "renamed";
                Assert.True(db.Update(member));
            }

            using (var db = new XodContext(path))
            {
                var member = db.Find<Legacy1xMember>(m => m.Id == 6);
                Assert.Equal("renamed", member.Name);
                Assert.Equal(Md5Of1x("pin-6"), member.Pin);
                Assert.Equal(Sha1Of1x("secret 6"), member.Secret);
            }
        }

        // A value a row stores in plaintext under [Crypto] (a database of 2.0.1 or earlier, ISS-064, or a property given
        // [Crypto] later) is hashed by the next update of the row, as before ISS-151's fix: only a stored value of a 1.x
        // digest's form is kept (found by the review of the fix: equal to what the row stores was not enough).
        [Fact]
        public void A_plaintext_value_a_row_stores_is_hashed_by_an_update()
        {
            string path = CopyFixture("plaintext", "xod.core-2017-plain");
            string dir = Path.GetDirectoryName(path)!;
            string page = Directory.GetFiles(dir, "*.xpag").Single();
            string xml = File.ReadAllText(page);
            // 48 nines: digits in an MD5 1.x digest's length range, which don't read as 16 bytes (each would be 999).
            string nines = new string('9', 48);
            string pin2 = "<Pin>" + Md5Of1x("pin-2") + "</Pin>", secret10 = "<Secret>" + Sha1Of1x("secret 10") + "</Secret>";
            string pin5 = "<Pin>" + Md5Of1x("pin-5") + "</Pin>";
            Assert.Contains(pin2, xml);
            Assert.Contains(secret10, xml);
            Assert.Contains(pin5, xml);
            File.WriteAllText(page, xml.Replace(pin2, "<Pin>hunter2</Pin>").Replace(secret10, "<Secret>123456</Secret>")
                .Replace(pin5, "<Pin>" + nines + "</Pin>"));

            using (var db = new XodContext(path))
            {
                foreach (int id in new[] { 2, 5, 10 })
                {
                    var member = db.Find<Legacy1xMember>(m => m.Id == id);
                    member.Name = "renamed";
                    Assert.True(db.Update(member));
                }
            }

            using (var db = new XodContext(path))
            {
                Assert.Equal(CryptoHelper.GetMD5HashData("hunter2"), db.Find<Legacy1xMember>(m => m.Id == 2).Pin);
                Assert.Equal(CryptoHelper.GetSHA1HashData("123456"), db.Find<Legacy1xMember>(m => m.Id == 10).Secret);
                Assert.Equal(CryptoHelper.GetMD5HashData(nines), db.Find<Legacy1xMember>(m => m.Id == 5).Pin);
                Assert.Equal(Sha1Of1x("secret 2"), db.Find<Legacy1xMember>(m => m.Id == 2).Secret);
            }
        }

        // SQL's VERIFY_HASH verifies through CryptoHelper.VerifyHash (found by the review: it compared the hex form only).
        [Fact]
        public void Sql_verifies_a_1x_digest()
        {
            string path = CopyFixture("sql");
            using var db = new XodContext(path, Password);
            var pin = db.Sql("SELECT Id FROM Legacy1xMember WHERE VERIFY_HASH(Pin, 'pin-3')");
            Assert.Equal(new[] { 3L }, pin.Rows.Select(r => Convert.ToInt64(r[0])));
            var secret = db.Sql("SELECT Id FROM Legacy1xMember WHERE VERIFY_HASH(Secret, 'secret 11')");
            Assert.Equal(new[] { 11L }, secret.Rows.Select(r => Convert.ToInt64(r[0])));
        }

        [Fact]
        public void A_value_changed_on_a_1x_row_is_hashed()
        {
            string path = CopyFixture("changed");
            using (var db = new XodContext(path, Password))
            {
                var member = db.Find<Legacy1xMember>(m => m.Id == 4);
                member.Pin = "new pin";
                Assert.True(db.Update(member));
            }

            using (var db = new XodContext(path, Password))
            {
                var member = db.Find<Legacy1xMember>(m => m.Id == 4);
                Assert.True(CryptoHelper.VerifyHash("new pin", member.Pin, CryptoMethod.MD5));
                Assert.Equal(Sha1Of1x("secret 4"), member.Secret);
            }
        }

        [Fact]
        public void A_1x_digest_verifies_against_its_value()
        {
            string path = CopyFixture("verify");
            using var db = new XodContext(path, Password);
            var member = db.Find<Legacy1xMember>(m => m.Id == 5);
            Assert.True(CryptoHelper.VerifyHash("pin-5", member.Pin, CryptoMethod.MD5));
            Assert.True(CryptoHelper.VerifyHash("secret 5", member.Secret, CryptoMethod.SHA1));
            Assert.False(CryptoHelper.VerifyHash("pin-6", member.Pin, CryptoMethod.MD5));
            Assert.False(CryptoHelper.VerifyHash("secret 6", member.Secret, CryptoMethod.SHA1));
        }

        [Fact]
        public void A_1x_database_written_by_2x_opens_again_with_its_password()
        {
            string path = CopyFixture("written");
            using (var db = new XodContext(path, Password))
                db.Insert(new Legacy1xMember { Id = 13, Name = "member 13", Pin = "pin-13", Secret = "secret 13" });

            using (var db = new XodContext(path, Password))
            {
                Assert.Equal(13, db.Count<Legacy1xMember>());
                Assert.Equal(Md5Of1x("pin-1"), db.Find<Legacy1xMember>(m => m.Id == 1).Pin);
                Assert.True(CryptoHelper.VerifyHash("pin-13", db.Find<Legacy1xMember>(m => m.Id == 13).Pin, CryptoMethod.MD5));
            }
        }

        // 1.x hashed the password's ASCII bytes, each other character as '?': written under "pässwort", the database
        // opens with the form 1.x hashed (MIGRATION-GUIDE section 2.1).
        [Fact]
        public void A_1x_database_under_a_password_beyond_ascii_opens_with_the_form_1x_hashed()
        {
            string path = CopyFixture("nonascii", "xod.core-2017-nonascii");
            using (var db = new XodContext(path, "p?sswort"))
            {
                Assert.Equal(12, db.Count<Legacy1xMember>());
                Assert.Equal(Md5Of1x("pin-9"), db.Find<Legacy1xMember>(m => m.Id == 9).Pin);
            }
            Assert.Throws<SecurityException>(() =>
            {
                using var db = new XodContext(path, "pässwort");
                db.Count<Legacy1xMember>();
            });
        }

        // XodMigrator reads the files through the same reader as an open.
        [Fact]
        public void An_encrypted_1x_database_converts_with_its_digests()
        {
            string path = CopyFixture("convert");
            string target = Path.Combine(tempRoot, "converted");
            int converted = XodMigrator.ConvertDatabase(Path.GetDirectoryName(path)!, target, StorageFormat.Binary, Password);
            Assert.Equal(3, converted);

            using var db = new XodContext(Path.Combine(target, "legacy.xod"), Password);
            Assert.Equal(12, db.Count<Legacy1xMember>());
            var member = db.Find<Legacy1xMember>(m => m.Id == 8);
            Assert.Equal(Md5Of1x("pin-8"), member.Pin);
            Assert.Equal(Sha1Of1x("secret 8"), member.Secret);
        }

        [Fact]
        public void A_1x_database_takes_a_new_password()
        {
            string path = CopyFixture("newpassword");
            using (var db = new XodContext(path, Password))
                db.ChangePassword(Password, "new-pass");

            using (var db = new XodContext(path, "new-pass"))
            {
                Assert.Equal(12, db.Count<Legacy1xMember>());
                Assert.Equal(Md5Of1x("pin-7"), db.Find<Legacy1xMember>(m => m.Id == 7).Pin);
            }
            Assert.Throws<SecurityException>(() =>
            {
                using var db = new XodContext(path, Password);
                db.Count<Legacy1xMember>();
            });
        }
    }
}
