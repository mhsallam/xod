// [Crypto(AES256)] (ISS-050, ISS-058, ISS-063, ISS-064). A database's AES256 values are encrypted with
// one random key of its own, stored in the database file and protected by the database password or by
// DatabaseOptions.PropertyEncryptionKey. With neither, a value can't be written: it would be readable by
// anyone with XOD. Changing the password protects the same key again, so the values stay readable.
// Values written by 2.0 (Xod.Conformance's KnownDefectTests read the committed fixtures) stay readable.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Xod.Tests
{
    public class SecretNote
    {
        public int Id { get; set; }
        public string Title { get; set; }
        [Crypto(CryptoMethod.AES256)] public string Body { get; set; }
        [Crypto(CryptoMethod.AES256), Property(Position = ValuePosition.Attribute)] public string Pin { get; set; }
        [Crypto(CryptoMethod.SHA256), Property(Position = ValuePosition.Attribute)] public string Digest { get; set; }
    }

    public class IndexedSecret
    {
        public int Id { get; set; }
        [Crypto(CryptoMethod.AES256), UniqueKey] public string Token { get; set; }
    }

    public class IndexedHash
    {
        public int Id { get; set; }
        [Crypto(CryptoMethod.SHA256), Property(Indexed = true)] public string Email { get; set; }
    }

    public class SecretById
    {
        [Crypto(CryptoMethod.AES256)] public string Id { get; set; } // the key by convention
        public string Name { get; set; }
    }

    public class SecretMemo
    {
        public int Id { get; set; }
        [Crypto(CryptoMethod.AES256)] public string Text { get; set; }
    }

    public class SecretList
    {
        public int Id { get; set; }
        [Crypto(CryptoMethod.AES256)] public List<string> Codes { get; set; }
    }

    public class XodPropertyCryptoTests
    {
        private const string Plain = "PLAINTEXT-7f3a";
        private const string PinPlain = "PIN-55a1";
        private const string AppKey = "application-secret";

        public static IEnumerable<object[]> Formats() => new[]
        {
            new object[] { StorageFormat.Xml },
            new object[] { StorageFormat.Json },
            new object[] { StorageFormat.Binary },
        };

        private static string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-crypto", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static DatabaseOptions Options(StorageFormat format, string appKey = null) =>
            new DatabaseOptions { StorageFormat = format, PropertyEncryptionKey = appKey };

        private static SecretNote Note() => new SecretNote { Title = "t", Body = Plain, Pin = PinPlain, Digest = "d" };

        /// <summary>No file of the database holds the plaintext (files are read as bytes; with no
        /// password, every page is plain UTF-8).</summary>
        private static void NoFileHolds(string dbPath, params string[] secrets)
        {
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(dbPath)!))
            {
                string text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                foreach (string secret in secrets)
                    Assert.False(text.Contains(secret), $"{Path.GetFileName(file)} holds {secret}");
            }
        }

        // ISS-050: with no password and no application key there is nothing to protect the value with.
        [Theory]
        [MemberData(nameof(Formats))]
        public void An_aes256_value_with_no_key_to_protect_it_is_refused(StorageFormat format)
        {
            string path = NewDatabase("nokey-" + format);
            using (var db = new XodContext(path, null, Options(format)))
            {
                Assert.Throws<SecurityException>(() => db.Insert(Note()));
                db.Insert(new SecretNote { Title = "no secret" }); // a null AES256 value needs no key
            }
            using (var db = new XodContext(path, null, Options(format)))
                Assert.Equal(new[] { "no secret" }, db.Select<SecretNote>().Select(n => n.Title));
        }

        // ISS-050 and ISS-064: with an application key the values are encrypted in an element and in an
        // attribute alike; nothing on disk holds them; without the key they can't be read.
        [Theory]
        [MemberData(nameof(Formats))]
        public void An_application_key_protects_the_values(StorageFormat format)
        {
            string path = NewDatabase("appkey-" + format);
            using (var db = new XodContext(path, null, Options(format, AppKey)))
                db.Insert(Note());
            NoFileHolds(path, Plain, PinPlain);

            using (var db = new XodContext(path, null, Options(format, AppKey)))
            {
                var note = db.Select<SecretNote>().Single();
                Assert.Equal(Plain, note.Body);
                Assert.Equal(PinPlain, note.Pin);
                Assert.True(Xod.Helpers.CryptoHelper.VerifyHash("d", note.Digest, CryptoMethod.SHA256));
            }
            using (var db = new XodContext(path, null, Options(format)))
                Assert.Throws<SecurityException>(() => db.Select<SecretNote>().ToList());
            using (var db = new XodContext(path, null, Options(format, "another-secret")))
                Assert.Throws<SecurityException>(() => db.Select<SecretNote>().ToList());
        }

        // ISS-058: the values stay readable when the password is added, changed or removed.
        [Theory]
        [MemberData(nameof(Formats))]
        public void Values_stay_readable_when_the_password_changes(StorageFormat format)
        {
            string path = NewDatabase("change-" + format);
            using (var db = new XodContext(path, "first", Options(format)))
                db.Insert(Note());
            using (var db = new XodContext(path, "first", Options(format)))
                db.ChangePassword("first", "second");
            using (var db = new XodContext(path, "second", Options(format)))
            {
                Assert.Equal(Plain, db.Select<SecretNote>().Single().Body);
                db.Insert(Note());
                db.ChangePassword("second", "third");
            }
            using (var db = new XodContext(path, "third", Options(format)))
                Assert.Equal(new[] { Plain, Plain }, db.Select<SecretNote>().Select(n => n.Body));
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void Values_protected_by_an_application_key_survive_securing_the_database(StorageFormat format)
        {
            string path = NewDatabase("secure-" + format);
            using (var db = new XodContext(path, null, Options(format, AppKey)))
            {
                db.Insert(Note());
                db.Secure("pw");
            }
            using (var db = new XodContext(path, "pw", Options(format, AppKey)))
            {
                Assert.Equal(Plain, db.Select<SecretNote>().Single().Body);
                db.Loose("pw");
            }
            using (var db = new XodContext(path, null, Options(format, AppKey)))
                Assert.Equal(Plain, db.Select<SecretNote>().Single().Body);
            NoFileHolds(path, Plain, PinPlain);
        }

        // Removing the password would leave nothing protecting the key: refused, unless the application
        // supplies a key, which then protects it.
        [Theory]
        [MemberData(nameof(Formats))]
        public void Removing_the_password_needs_another_key_for_the_values(StorageFormat format)
        {
            string path = NewDatabase("loose-" + format);
            using (var db = new XodContext(path, "pw", Options(format)))
            {
                db.Insert(Note());
                Assert.Throws<SecurityException>(() => db.Loose("pw"));
                Assert.Equal(Plain, db.Select<SecretNote>().Single().Body);
            }
            using (var db = new XodContext(path, "pw", Options(format, AppKey)))
                db.Loose("pw");
            using (var db = new XodContext(path, null, Options(format, AppKey)))
                Assert.Equal(Plain, db.Select<SecretNote>().Single().Body);
            NoFileHolds(path, Plain, PinPlain);

            // The database file is no longer encrypted: the protection by the old password must be gone, or
            // anyone who knew that password could open the key from it.
            if (format != StorageFormat.Binary)
                NoFileHolds(path, "password");
        }

        // Once no value uses the key, removing the password needs no other key: the unused key goes with it.
        [Fact]
        public void Removing_the_password_drops_a_key_no_value_uses()
        {
            string path = NewDatabase("loose-unused");
            using (var db = new XodContext(path, "pw"))
            {
                var note = db.Insert(Note());
                Assert.True(db.Delete(note));
                db.Loose("pw");
            }
            using (var db = new XodContext(path))
            {
                Assert.Empty(db.Select<SecretNote>());
                Assert.Throws<SecurityException>(() => db.Insert(Note())); // nothing protects a new key
            }
        }

        // A wrong current password changes nothing, the key included.
        [Fact]
        public void A_wrong_current_password_changes_nothing()
        {
            string path = NewDatabase("wrong");
            using (var db = new XodContext(path, "pw"))
            {
                db.Insert(Note());
                Assert.Throws<SecurityException>(() => db.ChangePassword("not-it", "other"));
                Assert.Throws<SecurityException>(() => db.Loose("not-it"));
            }
            using (var db = new XodContext(path, "pw"))
                Assert.Equal(Plain, db.Select<SecretNote>().Single().Body);
        }

        // A key created inside a transaction that rolls back is gone from the file: the next write must not
        // use a copy kept in memory, or its values would be unreadable after reopening.
        [Fact]
        public void A_key_made_in_a_rolled_back_transaction_is_not_used_afterwards()
        {
            string path = NewDatabase("rollback");
            using (var db = new XodContext(path, null, Options(StorageFormat.Xml, AppKey)))
            {
                using (var tx = db.BeginTransaction())
                {
                    db.Insert(Note());
                    tx.Rollback();
                }
                db.Insert(Note());
            }
            using (var db = new XodContext(path, null, Options(StorageFormat.Xml, AppKey)))
                Assert.Equal(new[] { Plain }, db.Select<SecretNote>().Select(n => n.Body));
        }

        // ISS-063: an index of a [Crypto] property would hold its plaintext (or disagree with the rows),
        // including the key a property named Id becomes by convention.
        [Fact]
        public void A_crypto_property_cannot_be_indexed()
        {
            string path = NewDatabase("indexed");
            using (var db = new XodContext(path, null, Options(StorageFormat.Xml, AppKey)))
            {
                Assert.Throws<IndexDataTypeException>(() => db.Insert(new IndexedSecret { Token = Plain }));
                Assert.Throws<IndexDataTypeException>(() => db.Insert(new IndexedHash { Email = Plain }));
                Assert.Throws<IndexDataTypeException>(() => db.Insert(new SecretById { Id = Plain, Name = "n" }));
            }
            NoFileHolds(path, Plain);
        }

        // [Crypto] protects a string. On anything else (a list, a dictionary) 2.0 ignored it and stored the
        // plaintext; it is refused instead.
        [Fact]
        public void Crypto_on_a_collection_is_refused()
        {
            string path = NewDatabase("collection");
            using (var db = new XodContext(path, null, Options(StorageFormat.Xml, AppKey)))
                Assert.Throws<CryptoDataTypeException>(() => db.Insert(new SecretList { Codes = new() { Plain } }));
            NoFileHolds(path, Plain);
        }

        // A password change that fails after the key has its new protection, but before the files change
        // (the first file it rewrites is held open), leaves the key openable with the password that still
        // opens the files; and the change can be made again. (A change that fails after some files have
        // switched leaves a database neither password opens, AES256 or not: ISS-065.)
        [Fact]
        public void A_password_change_that_fails_before_the_files_switch_keeps_the_values_readable()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-crypto", "failed-change-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "zz.xod"); // the database file sorts last
            using (var db = new XodContext(path, "first"))
                db.Insert(Note());

            // The file the password change rewrites first, in the order it rewrites them.
            string first = Directory.GetFiles(dir)
                .First(f => new[] { ".xod", ".xtab", ".xpag", ".xidx" }.Contains(Path.GetExtension(f)));
            using (var db = new XodContext(path, "first"))
            {
                using (new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.ThrowsAny<IOException>(() => db.ChangePassword("first", "second"));
            }

            using (var db = new XodContext(path, "first"))
            {
                Assert.Equal(Plain, db.Select<SecretNote>().Single().Body);
                db.ChangePassword("first", "third");
            }
            using (var db = new XodContext(path, "third"))
                Assert.Equal(Plain, db.Select<SecretNote>().Single().Body);
        }

        // Values not yet on disk (buffered durability) count: removing the password must not drop their key.
        [Fact]
        public void Removing_the_password_sees_values_not_yet_on_disk()
        {
            string path = NewDatabase("buffered");
            var buffered = new DatabaseOptions { Durability = DurabilityMode.Buffered };
            using (var db = new XodContext(path, "pw", buffered))
            {
                db.Insert(Note());
                Assert.Throws<SecurityException>(() => db.Loose("pw"));
            }
            using (var db = new XodContext(path, "pw", buffered))
                Assert.Equal(Plain, db.Select<SecretNote>().Single().Body);
        }

        // A context opened before another made the key must use that key, not make a second one. (Each
        // writes its own table: two contexts writing one table need DatabaseOptions.LockDatabase anyway.)
        [Fact]
        public void A_context_opened_earlier_uses_the_key_another_made()
        {
            string path = NewDatabase("two-contexts");
            var options = Options(StorageFormat.Xml, AppKey);
            using (var early = new XodContext(path, null, options))
            {
                early.Insert(new SecretNote { Title = "warm-up" });
                using (var other = new XodContext(path, null, options))
                    other.Insert(new SecretMemo { Text = Plain });
                early.Insert(Note());
            }
            using (var db = new XodContext(path, null, options))
            {
                Assert.Equal(Plain, db.Select<SecretMemo>().Single().Text);
                Assert.Equal(new[] { Plain }, db.Select<SecretNote>().Where(n => n.Body != null).Select(n => n.Body));
            }
        }
    }
}
