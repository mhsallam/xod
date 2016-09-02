using System;
using System.IO;
using System.Linq;

namespace Xod.Helpers
{
    public class XodSecurityService : Xod.Infra.IXodSecurityService
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
            if (!string.IsNullOrEmpty(password) && (password.Length < 1 || password.Length > 256))
                throw new SecurityException("Password length should be between 1 and 256.");

            if (string.IsNullOrEmpty(this.Password))
            {
                if (Changed != null)
                    Changed(this, EventArgs.Empty);

                this.Password = CryptoHelper.GetSHA256HashData(password);
                var dbfiles = Directory.GetFiles(Directory.GetParent(this.Path).FullName).Where(s =>
                    s.EndsWith(".xod") ||
                    s.EndsWith(".xtab") ||
                    s.EndsWith(".xpag"));

                foreach (var file in dbfiles)
                {
                    FileCryptoHelper.EncryptFile(file, file + ".lock", this.Password);
                    File.Delete(file);
                    File.Move(file + ".lock", file);
                }
            }
            else
                throw new SecurityException("The database is already protected with a password, if you want to change it use ChangePassword method.");
        }

        public void Loose(string password)
        {
            string hashedPassword = CryptoHelper.GetSHA256HashData(password);
            if (!string.IsNullOrEmpty(this.Password) && this.Password == hashedPassword)
            {
                if (Changed != null)
                    Changed(this, EventArgs.Empty);

                var dbfiles = Directory.GetFiles(Directory.GetParent(this.Path).FullName).Where(s =>
                    s.EndsWith(".xod") ||
                    s.EndsWith(".xtab") ||
                    s.EndsWith(".xpag"));

                foreach (var file in dbfiles)
                {
                    FileCryptoHelper.DecryptFile(file, file + ".lock", hashedPassword);
                    File.Delete(file);
                    File.Move(file + ".lock", file);
                }
                this.Password = null;
            }
            else
                throw new SecurityException("Input password doesn't match current database password.");
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
                throw new SecurityException("Input password doesn't match current database password.");
        }

    }
}
