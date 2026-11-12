using System;
using System.IO;
using System.Linq;
using Xod;
using Xod.Helpers;
using Xunit;

#pragma warning disable CS0618 // the obsolete public security service is what these tests exercise

namespace Xod.Tests
{
    /// <summary>
    /// ISS-069: the public <see cref="XodSecurityService"/> was a second way to change a database's password. It
    /// skipped what the context's change does: the AES256 key's protection by the new password (so encrypted values
    /// became unreadable), the flush, and the refusal while another context has the database open. Its methods now go
    /// through a context, so there is one way to change the password.
    /// </summary>
    public class XodSecurityServiceTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-securitysvc-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string NewDatabase(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "app.xod");
            using var db = new XodContext(path, "first");
            for (int i = 1; i <= 5; i++)
                db.Insert(new SecretMemo { Text = "secret " + i });
            return path;
        }

        [Fact]
        public void A_change_through_the_public_service_keeps_encrypted_values_readable()
        {
            string path = NewDatabase("change");
            var service = new XodSecurityService(path, CryptoHelper.GetSHA256HashData("first"));
            service.ChangePassword("first", "second");
            Assert.Equal(CryptoHelper.GetSHA256HashData("second"), service.Password);

            using var db = new XodContext(path, "second");
            Assert.Equal(Enumerable.Range(1, 5).Select(i => "secret " + i), db.Select<SecretMemo>().OrderBy(m => m.Id).Select(m => m.Text));
        }

        [Fact]
        public void Removing_and_setting_the_password_through_the_public_service_keeps_encrypted_values_readable()
        {
            string path = NewDatabase("loose-secure");
            var service = new XodSecurityService(path, CryptoHelper.GetSHA256HashData("first"));
            // Removing the password needs something else to protect the AES256 key: refused, nothing changed.
            Assert.ThrowsAny<XodException>(() => service.Loose("first"));
            using (var db = new XodContext(path, "first"))
                Assert.Equal(5, db.Select<SecretMemo>().Count(m => m.Text.StartsWith("secret ")));
        }

        [Fact]
        public void A_change_through_the_public_service_is_refused_while_a_context_has_the_database_open()
        {
            string path = NewDatabase("refused");
            using (var open = new XodContext(path, "first"))
            {
                var service = new XodSecurityService(path, CryptoHelper.GetSHA256HashData("first"));
                Assert.ThrowsAny<XodException>(() => service.ChangePassword("first", "second"));
                open.Insert(new SecretMemo { Text = "still writing" });
            }

            using var db = new XodContext(path, "first");
            Assert.Equal(6, db.Count<SecretMemo>());
        }

        [Fact]
        public void A_service_for_a_database_not_created_yet_only_records_the_password()
        {
            string dir = Path.Combine(tempRoot, "none");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "app.xod");
            var service = new XodSecurityService(path);
            service.Secure("first");
            Assert.Equal(CryptoHelper.GetSHA256HashData("first"), service.Password);
            Assert.Empty(Directory.GetFiles(dir));
        }

        // ISS-087: given the database's folder, which a context accepts, the service changed nothing and reported
        // nothing (it acted only on a path that was a file). It now finds the database as a context does.
        [Fact]
        public void A_change_through_the_database_folder_changes_the_database()
        {
            string path = NewDatabase("folder");
            var service = new XodSecurityService(Path.GetDirectoryName(path)!, CryptoHelper.GetSHA256HashData("first"));
            service.ChangePassword("first", "second");

            using var db = new XodContext(path, "second");
            Assert.Equal(5, db.Count<SecretMemo>());
        }

        // A database file named wrongly, in a folder that holds the database, is refused rather than taken for a
        // database not created yet (which changes nothing).
        [Fact]
        public void A_mistyped_database_file_beside_the_database_is_refused()
        {
            string path = NewDatabase("mistyped");
            var service = new XodSecurityService(Path.Combine(Path.GetDirectoryName(path)!, "ap.xod"), CryptoHelper.GetSHA256HashData("first"));
            Assert.ThrowsAny<XodException>(() => service.ChangePassword("first", "second"));
            using var db = new XodContext(path, "first");
            Assert.Equal(5, db.Count<SecretMemo>());
        }

        [Fact]
        public void The_public_service_refuses_a_wrong_current_password()
        {
            string path = NewDatabase("wrong");
            var service = new XodSecurityService(path, CryptoHelper.GetSHA256HashData("first"));
            Assert.ThrowsAny<XodException>(() => service.ChangePassword("wrong", "second"));
            using var db = new XodContext(path, "first");
            Assert.Equal(5, db.Count<SecretMemo>());
        }
    }
}
