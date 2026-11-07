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

        public void Secure(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 1 || password.Length > 256)
                throw new SecurityException("Password length should be between 1 and 256.");

            if (!string.IsNullOrEmpty(this.Password))
                throw new SecurityException("The database is already protected with a password; to change it use ChangePassword.");

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
            string hashedPassword = CryptoHelper.GetSHA256HashData(password);
            if (!string.IsNullOrEmpty(this.Password) && this.Password == hashedPassword)
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
            else
            {
                throw new SecurityException("Input password doesn't match current database password.");
            }
        }

        public void ChangePassword(string currentPassword, string newPassword)
        {
            string hashedPassword = CryptoHelper.GetSHA256HashData(currentPassword);
            if (!string.IsNullOrEmpty(this.Password) && this.Password == hashedPassword)
            {
                Loose(currentPassword);
                Secure(newPassword);
            }
            else
            {
                throw new SecurityException("Input password doesn't match current database password.");
            }
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
