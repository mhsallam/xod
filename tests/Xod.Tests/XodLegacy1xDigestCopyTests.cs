using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xod;
using Xod.Helpers;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-171: a XOD 1.x <c>[Crypto]</c> digest written where no stored row says it is one. An update kept a 1.x digest
    /// only when the row it replaces stores it (ISS-151): any other write took the digits for a value and hashed them,
    /// and the digest no longer verified. Read from a database said to hold 1.x digests
    /// (<see cref="DatabaseOptions.Legacy1xDigests"/>), a digest is now known for what it is wherever it is written
    /// (another row, another database, a keyless child its parent writes again); a value the caller typed, digits or
    /// not, is hashed as before. Without that word, a plaintext of digits, which reads the same, is hashed.
    /// </summary>
    public class XodLegacy1xDigestCopyTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-1xcopy-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class PdNote
        {
            public string Text { get; set; }
            [Crypto(CryptoMethod.MD5)] public string Pin { get; set; }
        }

        public class PdBox
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
            public List<PdNote> Notes { get; set; }
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

        // The 2017 XOD's database without a password: twelve Legacy1xMember rows with their 1.x digests.
        private string CopyPlainFixture(string name)
        {
            string target = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(FixtureDirectory("xod.core-2017-plain")))
                if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            return Path.Combine(target, "legacy.xod");
        }

        private static DatabaseOptions Holds1x() => new DatabaseOptions { Legacy1xDigests = true };

        private string NewDatabase(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "copy.xod");
        }

        // As xod.core/src/Helpers/CryptoHelper.cs did, not by the code under test.
        private static string Md5Of1x(string value)
        {
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(Encoding.ASCII.GetBytes(value)).Select(b => b.ToString()));
        }

        // A row read from a 1.x database and inserted into another: its digests go as they are, and verify there.
        [Fact]
        public void A_1x_digest_read_and_inserted_into_another_database_stays_the_digest()
        {
            Legacy1xMember member;
            using (var legacy = new XodContext(CopyPlainFixture("insert"), null, Holds1x()))
                member = legacy.Find<Legacy1xMember>(m => m.Id == 3);
            Assert.Equal(Md5Of1x("pin-3"), member.Pin);

            string path = NewDatabase("insert-target");
            using (var db = new XodContext(path))
                db.Insert(member);

            using (var db = new XodContext(path))
            {
                var copy = db.Find<Legacy1xMember>(m => m.Id == 3);
                Assert.Equal(Md5Of1x("pin-3"), copy.Pin);
                Assert.True(CryptoHelper.VerifyHash("pin-3", copy.Pin, CryptoMethod.MD5));
            }
        }

        // One row's digest, read, given to another row by an update: the other row stores it as it is.
        [Fact]
        public void A_1x_digest_read_and_given_to_another_row_stays_the_digest()
        {
            string path = CopyPlainFixture("other-row");
            using (var db = new XodContext(path, null, Holds1x()))
            {
                var three = db.Find<Legacy1xMember>(m => m.Id == 3);
                var four = db.Find<Legacy1xMember>(m => m.Id == 4);
                four.Pin = three.Pin;
                Assert.True(db.Update(four));
            }

            using (var db = new XodContext(path))
                Assert.True(CryptoHelper.VerifyHash("pin-3", db.Find<Legacy1xMember>(m => m.Id == 4).Pin, CryptoMethod.MD5));
        }

        // A keyless child is written again by every update of its parent (ISS-191), with no stored row of its own to
        // say its digest is one: it keeps it.
        [Fact]
        public void A_keyless_child_s_1x_digest_stays_when_its_parent_is_updated()
        {
            string path = NewDatabase("keyless");
            using (var db = new XodContext(path))
                db.Insert(new PdBox { Id = 1, Name = "box", Notes = new() { new PdNote { Text = "n1", Pin = "plain" } } });

            // The note as XOD 1.x stored it: its digest in 1.x's form.
            string digest = Md5Of1x("pin-n1");
            string dir = Path.GetDirectoryName(path)!;
            string hex = CryptoHelper.GetMD5HashData("plain");
            string page = Directory.GetFiles(dir, "*.xpag").Single(f => File.ReadAllText(f).Contains(hex));
            File.WriteAllText(page, File.ReadAllText(page).Replace(hex, digest));

            using (var db = new XodContext(path, null, Holds1x()))
            {
                var box = db.Find<PdBox>(b => b.Id == 1);
                Assert.Equal(digest, box.Notes.Single().Pin);
                box.Name = "renamed";
                Assert.True(db.Update(box));
            }

            using (var db = new XodContext(path))
            {
                var box = db.Find<PdBox>(b => b.Id == 1);
                Assert.Equal("renamed", box.Name);
                Assert.Equal(digest, box.Notes.Single().Pin);
                Assert.True(CryptoHelper.VerifyHash("pin-n1", box.Notes.Single().Pin, CryptoMethod.MD5));
            }
        }

        // A value the caller typed is hashed, even with the digits of a digest a read gave back: only the value the
        // read gave is known to be one.
        [Fact]
        public void A_value_typed_with_a_1x_digest_s_digits_is_hashed()
        {
            Legacy1xMember member;
            using (var legacy = new XodContext(CopyPlainFixture("typed"), null, Holds1x()))
                member = legacy.Find<Legacy1xMember>(m => m.Id == 3);
            string typed = new string(member.Pin.ToCharArray());

            string path = NewDatabase("typed-target");
            using (var db = new XodContext(path))
                db.Insert(new Legacy1xMember { Id = 3, Name = "typed", Pin = typed, Secret = "s" });

            using (var db = new XodContext(path))
                Assert.Equal(CryptoHelper.GetMD5HashData(typed), db.Find<Legacy1xMember>(m => m.Id == 3).Pin);
        }

        // A row read by SQL with its columns read by the schema the table records (SqlSchemaSource.Recorded):
        // its digest is known as a digest read back is, and an insert elsewhere keeps it.
        [Fact]
        public void A_1x_digest_read_by_sql_and_inserted_into_another_database_stays_the_digest()
        {
            string path = NewDatabase("sql");
            using (var db = new XodContext(path))
                db.Insert(new Legacy1xMember { Id = 1, Name = "m", Pin = "p-plain", Secret = "s" });

            string digest = Md5Of1x("pin-1");
            string hex = CryptoHelper.GetMD5HashData("p-plain");
            string page = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag").Single(f => File.ReadAllText(f).Contains(hex));
            File.WriteAllText(page, File.ReadAllText(page).Replace(hex, digest));

            Legacy1xMember row;
            using (var db = new XodContext(path, null, new DatabaseOptions { SqlSchema = SqlSchemaSource.Recorded, Legacy1xDigests = true }))
                row = db.Sql<Legacy1xMember>("SELECT * FROM Legacy1xMember WHERE Id = 1").Single();
            Assert.Equal(digest, row.Pin);

            string target = NewDatabase("sql-target");
            using (var db = new XodContext(target))
                db.Insert(row);
            using (var db = new XodContext(target))
                Assert.True(CryptoHelper.VerifyHash("pin-1", db.Find<Legacy1xMember>(m => m.Id == 1).Pin, CryptoMethod.MD5));
        }

        // The default: a database not said to hold 1.x digests. A value it stores in plaintext under [Crypto] (an
        // attribute written by 2.0.1 or earlier, a property given [Crypto] later) with the digits of a 1.x digest, such
        // as a card number under MD5, is hashed when a row read from it is inserted elsewhere: the stored text can't say
        // which it is, and only the database's owner can.
        [Fact]
        public void A_plaintext_of_digits_read_from_a_database_not_said_to_hold_1x_digests_is_hashed_when_copied()
        {
            const string card = "4111111111111111";
            Assert.True(CryptoHelper.IsLegacy1xDigest(card, CryptoMethod.MD5));

            string path = NewDatabase("plain-digits");
            using (var db = new XodContext(path))
                db.Insert(new Legacy1xMember { Id = 1, Name = "m", Pin = "p-plain", Secret = "s" });
            string hex = CryptoHelper.GetMD5HashData("p-plain");
            string page = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag").Single(f => File.ReadAllText(f).Contains(hex));
            File.WriteAllText(page, File.ReadAllText(page).Replace(hex, card));

            Legacy1xMember row;
            using (var db = new XodContext(path))
                row = db.Find<Legacy1xMember>(m => m.Id == 1);
            Assert.Equal(card, row.Pin);

            string target = NewDatabase("plain-digits-target");
            using (var db = new XodContext(target))
                db.Insert(row);
            using (var db = new XodContext(target))
                Assert.Equal(CryptoHelper.GetMD5HashData(card), db.Find<Legacy1xMember>(m => m.Id == 1).Pin);
        }

        // As xod.core did for SHA1.
        private static string Sha1Of1x(string value)
        {
            using var sha1 = SHA1.Create();
            return string.Concat(sha1.ComputeHash(Encoding.ASCII.GetBytes(value)).Select(b => b.ToString()));
        }

        // A digest read under one method, written to a property of another: it isn't that property's digest, and is
        // hashed. Here an SHA1 1.x digest given to an MD5 property, one whose digits an MD5 1.x digest could have too
        // (16 to 48 digits that cut into 16 byte values), so only the method it was read under tells.
        [Fact]
        public void A_1x_digest_read_under_another_method_is_hashed()
        {
            string digest = Enumerable.Range(0, 100000).Select(i => Sha1Of1x("w" + i))
                .First(d => CryptoHelper.IsLegacy1xDigest(d, CryptoMethod.MD5));
            Assert.True(CryptoHelper.IsLegacy1xDigest(digest, CryptoMethod.SHA1));

            string path = NewDatabase("method");
            using (var db = new XodContext(path))
                db.Insert(new Legacy1xMember { Id = 1, Name = "m", Pin = "p", Secret = "s-plain" });

            // The row as XOD 1.x stored it: its SHA1 digest in 1.x's form.
            string hex = CryptoHelper.GetSHA1HashData("s-plain");
            string page = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag").Single(f => File.ReadAllText(f).Contains(hex));
            File.WriteAllText(page, File.ReadAllText(page).Replace(hex, digest));

            using (var db = new XodContext(path, null, Holds1x()))
            {
                var one = db.Find<Legacy1xMember>(m => m.Id == 1);
                Assert.Equal(digest, one.Secret);
                db.Insert(new Legacy1xMember { Id = 2, Name = "copy", Pin = one.Secret, Secret = one.Secret });
            }

            using (var db = new XodContext(path))
            {
                var copy = db.Find<Legacy1xMember>(m => m.Id == 2);
                Assert.Equal(CryptoHelper.GetMD5HashData(digest), copy.Pin);
                Assert.Equal(digest, copy.Secret);
            }
        }
    }
}
