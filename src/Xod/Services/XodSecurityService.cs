using System;
using System.IO;
using Xod.Infra;

namespace Xod.Helpers
{
    /// <summary>
    /// Sets, changes and removes a database's password. Obsolete: use <see cref="XodContext.Secure"/>,
    /// <see cref="XodContext.Loose"/> and <see cref="XodContext.ChangePassword"/>, the one way the password
    /// changes. Each method here opens a context on <see cref="Path"/> and calls it, so it does what the
    /// context does: the AES256 key protected under the new password, and the open refused while another context
    /// has the database open. Until 2.0.1 these methods rewrote the files themselves and skipped all of that
    /// (ISS-069). To be removed in a major version.
    /// </summary>
    [Obsolete("Use XodContext.Secure, Loose and ChangePassword: the one way a database's password changes.")]
    public class XodSecurityService : IXodSecurityService
    {
        /// <summary>Raised before each change is attempted.</summary>
        public event EventHandler Changed;

        /// <summary>The password hash this object believes the database is under: set by a change made through
        /// it. The change itself checks the password given against the database.</summary>
        public string Password { get; set; }

        /// <summary>The database: its file, or its folder, as for a context.</summary>
        public string Path { get; set; }

        public XodSecurityService(string path, string password = null)
        {
            this.Path = path;
            this.Password = password;
        }

        public void Secure(string password) => Change(null, db => db.Secure(password), CryptoHelper.GetSHA256HashData(password));

        public void Loose(string password) => Change(password, db => db.Loose(password), null);

        public void ChangePassword(string currentPassword, string newPassword) =>
            Change(currentPassword, db => db.ChangePassword(currentPassword, newPassword), CryptoHelper.GetSHA256HashData(newPassword));

        // The database found as a context finds it (a folder, a database file; a new database file beside one is
        // refused, ISS-087). A database that isn't there has no files to change: only this object's password does,
        // as before.
        private void Change(string openWith, Action<XodContext> change, string passwordAfter)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            if (!string.IsNullOrEmpty(Path) && File.Exists(DatabaseFiles.Resolve(Path).File))
            {
                using var db = new XodContext(Path, openWith);
                change(db);
            }
            Password = passwordAfter;
        }
    }
}
