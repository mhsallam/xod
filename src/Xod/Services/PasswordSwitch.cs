using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Xod.Services
{
    /// <summary>
    /// Owns the password the database's files are under. Setting, changing and removing it go through one
    /// writer (<see cref="SwitchTo"/>) that is all or nothing, including across a process crash: every file
    /// is written under the new password beside the old one, a commit record is written, and only then are the
    /// files replaced. <see cref="RecoverSwitch"/>, run when the database opens, finishes a committed switch
    /// and discards an uncommitted one. (A power cut is another matter: the renames and the record's deletion
    /// aren't flushed to the device, as no write of this library is; see PLAN-046 section 9.) Internal: only the
    /// engine changes the password (XmlEngine.SwitchPassword), after its checks, its flush, the AES256 key and
    /// with the database to itself; the public XodSecurityService goes through a context (ISS-069).
    /// </summary>
    internal sealed class PasswordSwitch
    {
        // A file under the new password, written beside the one it replaces.
        internal const string StagedSuffix = ".xpwnew";
        // The commit record: its last line says the staged files replace the database's.
        internal const string JournalSuffix = ".xpwlog";
        private const string JournalHeader = "XOD password change v1";
        private const string JournalCommit = "commit";

        /// <summary>The password hash the database's files are under (null for none).</summary>
        internal string Password { get; private set; }

        // A switch committed by this context that couldn't replace every file yet (one was open elsewhere).
        private volatile bool switchPending;
        internal string Path { get; }

        internal PasswordSwitch(string path, string password = null)
        {
            this.Path = path;
            this.Password = password;
        }

        /// <summary>Throws unless a password set would accept this password (as the new password of
        /// a change, with <paramref name="replacingCurrent"/>). The engine asks first, so
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
            if (string.IsNullOrEmpty(this.Password) || this.Password != Helpers.CryptoHelper.GetSHA256HashData(password))
                throw new SecurityException("Input password doesn't match current database password.");
        }

        /// <summary>
        /// Puts every database file under <paramref name="newPassword"/> (a password hash; null for none).
        /// Throws, changing nothing, if a file can't be read or staged, or if a write that didn't finish is
        /// waiting for the next open. Once the commit record is written the change is made:
        /// <see cref="Password"/> is the new one, and if a file can't be replaced now (open elsewhere) this
        /// throws to say so; the next write in this context, or the next open, finishes it.
        /// </summary>
        internal void SwitchTo(string newPassword)
        {
            string root = System.IO.Path.GetDirectoryName(this.Path);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                this.Password = newPassword;
                return;
            }

            // A switch this context committed and couldn't finish: finished first, or this one is refused
            // (staging over its files would lose them).
            FinishPendingSwitch();
            RefuseOverUnfinishedWrites(root);

            string[] files = Directory.GetFiles(root).Where(Helpers.DatabaseFiles.IsOfDatabase).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            string journal = this.Path + JournalSuffix;

            // The record is created first and held with no sharing until the switch ends: another context's open
            // (RecoverSwitch) sees a switch in progress and stays away from its staged files. CreateNew: a record
            // already there is another context's switch.
            var record = new FileStream(journal, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var staged = new List<string>(files.Length);
            bool commitWritten = false;
            try
            {
                try
                {
                    WriteRecord(record, JournalHeader + "\n"); // not a commit yet

                    // 1. Stage: each file under the new password, beside it. The files themselves are untouched.
                    // The switch writes pages without the mutation hook that marks the indexes behind (ISS-043), and
                    // needs none: it runs alone, after a flush, and replaces every file, the indexes among them, from
                    // that state, so pages and indexes change together; a switch cut short is finished or undone
                    // whole (FinishPendingSwitch, RecoverSwitch).
                    foreach (string file in files)
                    {
                        byte[] content = File.ReadAllBytes(file);
                        byte[] plain = this.Password == null ? content : Helpers.FileCryptoHelper.DecryptBytes(content, this.Password);
                        byte[] output = newPassword == null ? plain : Helpers.FileCryptoHelper.EncryptBytes(plain, newPassword);
                        staged.Add(file + StagedSuffix);
                        WriteDurably(file + StagedSuffix, output);
                        FaultInjection.Step("staged", staged.Count, System.IO.Path.GetFileName(file));
                    }

                    // 2. Commit: the record names the files and ends with the commit line, so a record cut short
                    // reads as not committed.
                    var text = new StringBuilder().Append(JournalHeader).Append('\n');
                    foreach (string file in files)
                        text.Append(System.IO.Path.GetFileName(file)).Append('\n');
                    text.Append(JournalCommit).Append('\n');
                    commitWritten = true;
                    WriteRecord(record, text.ToString());
                }
                catch (Exception notCommitted)
                {
                    // The record goes first: staged files without a record are discarded at the next open, while a
                    // record that may hold the commit, left with some of its staged files, would be finished with
                    // only those. A record that may hold the commit and can't be deleted is a commit.
                    record.Dispose();
                    if (!TryDelete(journal) && commitWritten)
                    {
                        this.Password = newPassword;
                        switchPending = true;
                        throw Pending(notCommitted);
                    }
                    foreach (string file in staged)
                        TryDelete(file);
                    throw;
                }

                this.Password = newPassword;
                FaultInjection.Step("committed");

                // 3. Switch: each staged file replaces its original; every one must still be there.
                try
                {
                    SwitchStaged(root, files.Select(f => System.IO.Path.GetFileName(f)), expectAll: true);
                }
                catch (Exception e)
                {
                    switchPending = true;
                    throw Pending(e);
                }
            }
            finally
            {
                record.Dispose();
            }
            File.Delete(journal);
        }

        private static XodException Pending(Exception cause) =>
            new XodException("The password change is made, but a file could not be replaced yet (" + cause.Message +
                "). The next write in this context, or the next open, replaces it; until then this context can't read it.", cause);

        // A transaction journal or an atomic write cut short is put back by the next open, as it was written:
        // under the password it had. A switch now would end with those files under the old password.
        private static void RefuseOverUnfinishedWrites(string root)
        {
            string unfinished = Directory.GetFiles(root, "*.xwal").Concat(Directory.GetFiles(root, "*.tmp")).FirstOrDefault();
            if (unfinished != null)
                throw new XodException("A write that didn't finish (" + System.IO.Path.GetFileName(unfinished) +
                    ") is recovered when the database is next opened. Reopen the database before changing its password.");
        }

        private static void WriteRecord(FileStream record, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            record.SetLength(0);
            record.Position = 0;
            record.Write(bytes, 0, bytes.Length);
            record.Flush(flushToDisk: true);
        }

        // Replaces each named file by its staged copy. In a recovery a missing copy was already moved; in the
        // switch itself (expectAll) every copy is there unless something else deleted it, which is an error. A
        // file that can't be replaced doesn't stop the others; the first failure is thrown after.
        private static void SwitchStaged(string root, IEnumerable<string> fileNames, bool expectAll)
        {
            int switched = 0;
            Exception first = null;
            foreach (string name in fileNames)
            {
                string target = System.IO.Path.Combine(root, name);
                string source = target + StagedSuffix;
                if (!File.Exists(source))
                {
                    if (expectAll)
                        first ??= new XodException("The staged copy of " + name + " is missing.");
                    continue;
                }
                try
                {
                    FaultInjection.Step("switching", switched + 1, name);
                    File.Move(source, target, overwrite: true);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    first ??= e;
                    continue;
                }
                FaultInjection.Step("switched", ++switched, name);
            }
            if (first != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }

        /// <summary>
        /// Finishes a switch this context committed that couldn't replace every file then (one was open
        /// elsewhere); throws if it still can't. Run before any file is written (the engine's mutation hook) and
        /// before a new change reads the files: a file written now and replaced at the next open by its staged
        /// copy, from before the write, would lose the write.
        /// </summary>
        internal void FinishPendingSwitch()
        {
            if (!switchPending)
                return;
            RecoverSwitch(System.IO.Path.GetDirectoryName(this.Path));
            switchPending = false;
        }

        /// <summary>
        /// Run when the database opens, before any file is read: finishes a password change that was committed
        /// and discards one that wasn't. Throws if a committed change can't be finished (a file open elsewhere),
        /// or if another context is switching the password now: the database would otherwise open with files
        /// under two passwords.
        /// </summary>
        internal static void RecoverSwitch(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return;

            foreach (string journal in Directory.GetFiles(root, "*" + JournalSuffix))
            {
                string text;
                try
                {
                    using var fs = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    text = reader.ReadToEnd();
                }
                catch (IOException e) when (File.Exists(journal))
                {
                    // Held by the context switching the password: its staged files are live.
                    throw new XodException("A password change is in progress in another context or process; open the database when it ends.", e);
                }
                string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                bool committed = lines.Length >= 2 && lines[0] == JournalHeader && lines[^1] == JournalCommit;
                if (committed)
                {
                    try
                    {
                        SwitchStaged(root, lines.Skip(1).Take(lines.Length - 2), expectAll: false);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        throw new XodException("A password change made earlier can't be finished: " + e.Message +
                            " The database opens once that file is free.", e);
                    }
                }
                File.Delete(journal);
            }

            // Staged files no commit record names: a change that never committed.
            foreach (string staged in Directory.GetFiles(root, "*" + StagedSuffix))
                File.Delete(staged);
        }

        private static void WriteDurably(string file, byte[] content)
        {
            using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(content, 0, content.Length);
            fs.Flush(flushToDisk: true);
        }

        // Cleanup of a change that failed before its commit: what can't be deleted now is deleted at the next
        // open (staged files no record names). Returns whether the file is gone.
        private static bool TryDelete(string file)
        {
            try
            {
                File.Delete(file);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

    }
}
