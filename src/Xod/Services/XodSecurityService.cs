using System;
using System.IO;
using System.Linq;
using Xod.Infra;

namespace Xod.Helpers
{
    public class XodSecurityService : IXodSecurityService
    {
        public event EventHandler Changed;
        public string Password { get; set; }
        public string Path { get; set; }

        public XodSecurityService(string path, string password = null)
        {
            this.Path = path;
            this.Password = password;
        }

        /// <summary>Throws unless <see cref="Secure"/> would accept this password (as the new password of
        /// <see cref="ChangePassword"/>, with <paramref name="replacingCurrent"/>). The engine asks first, so
        /// that what it changes before the files (the AES256 key) isn't changed for nothing.</summary>
        internal void CheckCanSecure(string password, bool replacingCurrent = false)
        {
            if (string.IsNullOrEmpty(password) || password.Length > 256)
                throw new SecurityException("Password length should be between 1 and 256.");

            if (!replacingCurrent && !string.IsNullOrEmpty(this.Password))
                throw new SecurityException("The database is already protected with a password; to change it use ChangePassword.");
        }

        /// <summary>Throws unless <paramref name="password"/> is the database's current password.</summary>
        internal void CheckCurrentPassword(string password)
        {
            if (string.IsNullOrEmpty(this.Password) || this.Password != CryptoHelper.GetSHA256HashData(password))
                throw new SecurityException("Input password doesn't match current database password.");
        }

        public void Secure(string password)
        {
            CheckCanSecure(password);

            Changed?.Invoke(this, EventArgs.Empty);

            this.Password = CryptoHelper.GetSHA256HashData(password);
            var parentDir = Directory.GetParent(this.Path)?.FullName;
            if (parentDir == null || !Directory.Exists(parentDir))
                return;

            var dbfiles = Directory.GetFiles(parentDir).Where(IsDatabaseFile).ToArray();
            foreach (var file in dbfiles)
            {
                FileCryptoHelper.EncryptFile(file, file + ".lock", this.Password);
                File.Delete(file);
                File.Move(file + ".lock", file);
            }
        }

        public void Loose(string password)
        {
            CheckCurrentPassword(password);
            string hashedPassword = CryptoHelper.GetSHA256HashData(password);
            {
                Changed?.Invoke(this, EventArgs.Empty);

                var parentDir = Directory.GetParent(this.Path)?.FullName;
                if (parentDir != null && Directory.Exists(parentDir))
                {
                    var dbfiles = Directory.GetFiles(parentDir).Where(IsDatabaseFile).ToArray();
                    foreach (var file in dbfiles)
                    {
                        FileCryptoHelper.DecryptFile(file, file + ".lock", hashedPassword);
                        File.Delete(file);
                        File.Move(file + ".lock", file);
                    }
                }
                this.Password = null;
            }
        }

        public void ChangePassword(string currentPassword, string newPassword)
        {
            CheckCurrentPassword(currentPassword);
            CheckCanSecure(newPassword, replacingCurrent: true);
            Loose(currentPassword);
            Secure(newPassword);
        }

        private static bool IsDatabaseFile(string file)
        {
            return file.EndsWith(".xod", StringComparison.OrdinalIgnoreCase) ||
                   file.EndsWith(".xodj", StringComparison.OrdinalIgnoreCase) ||
                   file.EndsWith(".xodb", StringComparison.OrdinalIgnoreCase) ||
                   file.EndsWith(".xtab", StringComparison.OrdinalIgnoreCase) ||
                   file.EndsWith(".xpag", StringComparison.OrdinalIgnoreCase) ||
                   file.EndsWith(".xidx", StringComparison.OrdinalIgnoreCase);
        }
    }
}
