using System;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-070: a commit that fails part-way is undone at once, in the context and on disk, so nothing the
    /// context writes afterwards is undone by the next open. ISS-071: a password change needs the database to
    /// itself, so no other context goes on writing under the old password.
    /// </summary>
    public class XodSharedAccessTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-shared-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        // ---- ISS-070 ----

        // The batch's files are written, then the commit fails: the context sees the database as before the
        // transaction, and what it writes next is kept.
        [Fact]
        public void A_commit_that_fails_part_way_is_undone_at_once()
        {
            string dir = CrashDb.New(tempRoot, "commit-fails");
            var run = XodCrashSafetyTests.RunWriterProcess("txfail", dir, ("XOD_FAIL_AT_STEP", "commit-written"));
            Assert.Contains("COMMIT FAILED", run.Output);
            Assert.Contains("COUNT 25", run.Output);
            Assert.Contains("WROTE", run.Output);

            Assert.False(File.Exists(Path.Combine(dir, "transaction.xwal")), run.Output);
            CrashDb.HoldsBatches(dir, "first", 2);
        }

        // And when the undo fails too, the context writes nothing more (the next open undoes the commit from its
        // journal, and would undo those writes with it).
        [Fact]
        public void A_commit_that_fails_and_cannot_be_undone_refuses_writes_until_reopened()
        {
            string dir = CrashDb.New(tempRoot, "commit-and-undo-fail");
            var run = XodCrashSafetyTests.RunWriterProcess("txfail", dir, ("XOD_FAIL_AT_STEP", "commit-written,undo"));
            Assert.Contains("COMMIT FAILED", run.Output);
            Assert.Contains(run.Output.Split(Environment.NewLine), l => l.StartsWith("REFUSED", StringComparison.Ordinal) && l.Contains("couldn't be undone"));

            CrashDb.HoldsBatches(dir, "first", 1);
            Assert.False(File.Exists(Path.Combine(dir, "transaction.xwal")));
        }

        // A journal the open can't write back stays, and the open fails, rather than opening a commit half done
        // and deleting the record of how to undo it.
        [Fact]
        public void A_recovery_that_fails_keeps_the_journal_and_stops_the_open()
        {
            string dir = CrashDb.New(tempRoot, "recovery-fails");
            var crash = XodCrashSafetyTests.RunWriterProcess("txcommit", dir, ("XOD_CRASH_AT_STEP", "commit-written"));
            Assert.DoesNotContain("COMMITTED", crash.Output);
            string journal = Path.Combine(dir, "transaction.xwal");
            Assert.True(File.Exists(journal));

            var open = XodCrashSafetyTests.RunWriterProcess("pwopen", dir, ("XOD_FAIL_AT_STEP", "recover-restoring:1"));
            Assert.Contains("OPEN FAILED", open.Output);
            Assert.True(File.Exists(journal), "the journal was deleted: " + open.Output);

            CrashDb.HoldsBatches(dir, "first", 1);
            Assert.False(File.Exists(journal));
        }

        // ISS-110: a commit undone after it wrote its index files, one of them held open by another program at the
        // undo (held while the commit is paused once its indexes are written: it has the commit's key). The undo
        // can't drop it, and leaves the indexes marked behind, so no index load trusts it: the context goes on
        // writing, and the undone commit's key isn't taken.
        [Fact]
        public void A_held_index_file_with_an_undone_commit_s_keys_isn_t_trusted()
        {
            if (!OperatingSystem.IsWindows())
                return; // a file held open blocks its deletion on Windows only
            string dir = Path.Combine(tempRoot, "undo-held-keys");
            Directory.CreateDirectory(dir);
            using var writer = XodCrashSafetyTests.StartWriterProcess("txmember", dir,
                ("XOD_PAUSE_AT_STEP", "commit-indexed"), ("XOD_FAIL_AT_STEP", "commit-indexed"));
            string line;
            while ((line = writer.StandardOutput.ReadLine()) != null && !line.StartsWith("PAUSED", StringComparison.Ordinal)) { }
            Assert.NotNull(line);
            string memberIndex = Path.Combine(dir, typeof(CrashMember).FullName + ".xidx");
            var undone = new System.Text.StringBuilder();
            using (new FileStream(memberIndex, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                writer.StandardInput.WriteLine();
                writer.StandardInput.Flush();
                while ((line = writer.StandardOutput.ReadLine()) != null && !line.StartsWith("UNDONE", StringComparison.Ordinal))
                    undone.AppendLine(line);
                Assert.NotNull(line);
            }
            Assert.Contains("COMMIT FAILED", undone.ToString());
            Assert.True(File.Exists(memberIndex)); // held: it couldn't be dropped
            // Without the pause the undo restores the pages within milliseconds of the index save, which the guess by
            // time at an index load (a page more than 50 ms newer than the index) doesn't see: only the marker keeps
            // the file untrusted. The pause gave the restored pages more, so the index file's time is brought level.
            DateTime newest = Directory.GetFiles(dir, "*.xpag").Concat(Directory.GetFiles(dir, "*.xtab")).Max(File.GetLastWriteTimeUtc);
            File.SetLastWriteTimeUtc(memberIndex, newest);
            writer.StandardInput.WriteLine();
            writer.StandardInput.Flush();
            string rest = XodCrashSafetyTests.FinishWriter(writer);
            Assert.Contains("WROTE", rest); // the context goes on writing, and the undone commit's key isn't taken

            using var db = new XodContext(Path.Combine(dir, "crash.xod"));
            Assert.Equal(3, db.Find<CrashMember>(m => m.Email == "tx")?.Id);
            Assert.Equal(new[] { 1, 3 }, db.Select<CrashMember>().Select(m => m.Id).OrderBy(i => i).ToArray());
        }

        // With LockDatabase the same, and the failed open lets go of the database: the next one recovers.
        [Fact]
        public void A_locked_open_whose_recovery_fails_lets_go_of_the_database()
        {
            string dir = CrashDb.New(tempRoot, "locked-recovery-fails");
            Assert.DoesNotContain("COMMITTED", XodCrashSafetyTests.RunWriterProcess("txcommit", dir, ("XOD_CRASH_AT_STEP", "commit-written")).Output);

            var open = XodCrashSafetyTests.RunWriterProcess("pwopen-locked", dir, ("XOD_FAIL_AT_STEP", "recover-restoring:1"));
            Assert.Contains("OPEN 1 FAILED", open.Output);
            Assert.Contains("OPEN 2 COUNT 25", open.Output);
        }

        // ---- ISS-071 ----

        private string NewDatabase(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "pw.xod");
            using var db = new XodContext(path, "first");
            for (int i = 1; i <= 10; i++)
                db.Insert(new PwItem { Name = "item " + i });
            return path;
        }

        // A context opened with LockDatabase has the database to itself, whether the other opens with it or not.
        [Fact]
        public void A_database_locked_by_one_context_cannot_be_opened_by_another()
        {
            string path = NewDatabase("locked");
            var locked = new DatabaseOptions { LockDatabase = true };

            using (var a = new XodContext(path, "first", locked))
                Assert.Throws<DatabaseLockedException>(() => new XodContext(path, "first"));

            using (var b = new XodContext(path, "first"))
                Assert.Throws<DatabaseLockedException>(() => new XodContext(path, "first", locked));

            using (var a = new XodContext(path, "first", locked))
            {
                a.ChangePassword("first", "second");
                Assert.Equal(10, a.Select<PwItem>().Count());
            }
        }

        // An open that fails after taking hold of the database (a wrong password) lets go of it.
        [Fact]
        public void A_failed_open_lets_go_of_the_database()
        {
            string path = NewDatabase("failed-open");
            var locked = new DatabaseOptions { LockDatabase = true };
            Assert.ThrowsAny<XodException>(() => new XodContext(path, "wrong", locked));
            using (var a = new XodContext(path, "first", locked))
                Assert.Equal(10, a.Select<PwItem>().Count());

            Assert.ThrowsAny<XodException>(() => new XodContext(path, "wrong"));
            using (var b = new XodContext(path, "first"))
                b.ChangePassword("first", "second");
        }

        // The lock file is never deleted, by any context: on Unix a context that opened it just before a deletion
        // would hold a file no longer in the folder, and the next would create another, so the two wouldn't see
        // each other (on Windows an open racing the deletion fails). It doesn't stop a later open.
        [Fact]
        public void The_lock_file_stays_and_does_not_block_a_later_lock()
        {
            string path = NewDatabase("lock-file");
            using (var a = new XodContext(path, "first"))
                Assert.True(File.Exists(path + ".lock"));
            Assert.True(File.Exists(path + ".lock"));

            using (var c = new XodContext(path, "first", new DatabaseOptions { LockDatabase = true }))
                Assert.Equal(10, c.Select<PwItem>().Count());
            Assert.True(File.Exists(path + ".lock"));

            using (var d = new XodContext(path, "first"))
                Assert.Equal(10, d.Select<PwItem>().Count());
        }

        // ISS-084, at the open: with the lock file read-only, the open's exclusive try failed with
        // UnauthorizedAccessException, which it took for a folder nothing can write, and it recovered without a
        // hold: what it deleted (a staged write) or undid (a journal) may be another context's, under way. Now that
        // open is refused for the other's hold (PLAN-064), and recovers nothing.
        [Fact]
        public void An_open_recovers_nothing_while_another_context_holds_the_database_even_with_a_read_only_lock_file()
        {
            string path = NewDatabase("read-only-lock");
            string dir = Path.GetDirectoryName(path)!;
            using var first = new XodContext(path, "first");
            string staged = Directory.GetFiles(dir, "*.xpag").First() + ".tmp";
            File.WriteAllText(staged, "another context's write, under way");
            File.SetAttributes(path + ".lock", FileAttributes.ReadOnly);
            try
            {
                Assert.Throws<DatabaseLockedException>(() => new XodContext(path, "first"));
                Assert.True(File.Exists(staged));
                Assert.Equal(10, first.Count<PwItem>());
            }
            finally
            {
                File.SetAttributes(path + ".lock", FileAttributes.Normal);
            }
        }

        // ISS-084 for a lock file this user can't read (another user's, on Unix 0600): no hold can be taken, so
        // nothing tells whether what is pending is another context's write under way. The open is refused, and
        // recovers nothing; it used to recover anyway, taking this for a folder nothing could write.
        [Fact]
        public void An_open_that_cannot_take_a_hold_recovers_nothing_and_is_refused_while_something_is_pending()
        {
            if (!OperatingSystem.IsWindows())
                return; // the lock file is made unreadable with a Windows access rule

            string path = NewDatabase("unreadable-lock");
            string dir = Path.GetDirectoryName(path)!;
            string staged = Directory.GetFiles(dir, "*.xpag").First() + ".tmp";
            File.WriteAllText(staged, "another context's write, under way");
            var info = new FileInfo(path + ".lock");
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
                System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(deny);
            info.SetAccessControl(acl);
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(path + ".lock"));
                Assert.ThrowsAny<XodException>(() => { using var db = new XodContext(path, "first"); });
                Assert.True(File.Exists(staged));
                Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, Path.Combine(tempRoot, "unreadable-target"), StorageFormat.Json, "first"));
                Assert.True(File.Exists(staged));
            }
            finally
            {
                acl = info.GetAccessControl();
                acl.RemoveAccessRule(deny);
                info.SetAccessControl(acl);
            }
            using (var db = new XodContext(path, "first"))
                Assert.Equal(10, db.Count<PwItem>());
            Assert.False(File.Exists(staged));
        }

        // ISS-085: a folder holds one database: every database file in it shares its tables, but each has its own
        // lock file, so contexts opened through two of them didn't see each other. A new database file beside
        // one is refused (a mistyped name, a case that differs), and so is a folder that holds two.
        [Fact]
        public void A_second_database_file_in_a_folder_is_refused()
        {
            string path = NewDatabase("second-file");
            string dir = Path.GetDirectoryName(path)!;
            string other = Path.Combine(dir, "other.xod");

            var refused = Assert.ThrowsAny<XodException>(() => { using var db = new XodContext(other, "first"); });
            Assert.Contains("pw.xod", refused.Message);
            Assert.False(File.Exists(other));

            File.Copy(path, other);
            Assert.ThrowsAny<XodException>(() => { using var db = new XodContext(path, "first"); });
            Assert.ThrowsAny<XodException>(() => { using var db = new XodContext(dir, "first"); });

            File.Delete(other);
            using (var db = new XodContext(dir, "first"))
                Assert.Equal(10, db.Count<PwItem>());
        }

        // ISS-096: the one-database check ran before the open's hold, and the holds were per database file, so two
        // opens naming different new database files in one empty folder both passed it and both wrote the tables.
        // Here one open is paused once it has checked the folder (in the crash writer's process) while another
        // database is created there and written. Going on, it checks again under its hold, before it recovers or
        // creates anything: it is refused, creates no database file, and every file of the other database is as it
        // was. The first fix (review of TASK-071) checked again only once its own file was on disk: the open had
        // run the folder's recovery by then, dropping the other database's indexes (their marker held and behind),
        // and left its file, so the folder refused every open after, the other database's own included.
        [Fact]
        public void Two_opens_creating_different_databases_in_one_folder_don_t_both_go_on()
        {
            string dir = Path.Combine(tempRoot, "two-new-files");
            Directory.CreateDirectory(dir);
            using var writer = XodCrashSafetyTests.StartWriterProcess("open-second", dir, ("XOD_PAUSE_AT_STEP", "open-checked"));
            ReadUntil(writer, "PAUSED");

            using (var first = new XodContext(Path.Combine(dir, "first.xod")))
            {
                first.Insert(new PwItem { Id = 1, Name = "first" });
                first.Flush();
                // Its indexes behind the pages again: its marker is held and not empty.
                first.Insert(new PwItem { Id = 2, Name = "second" });
                var before = FolderContent(dir);
                Assert.Contains(before.Keys, f => f.EndsWith(".xidx", StringComparison.Ordinal));

                writer.StandardInput.WriteLine();
                writer.StandardInput.Flush();
                string rest = XodCrashSafetyTests.FinishWriter(writer);
                // Refused for the hold the first context has (PLAN-064: one context at a time).
                Assert.Contains("REFUSED", rest);
                Assert.Contains("locked", rest);
                Assert.DoesNotContain("OPENED", rest);
                Assert.False(File.Exists(Path.Combine(dir, "second.xod")));

                var after = FolderContent(dir);
                foreach (var (file, content) in before)
                {
                    Assert.True(after.ContainsKey(file), file + " is gone");
                    Assert.True(content == null || content.SequenceEqual(after[file]), file + " changed");
                }
                first.Insert(new PwItem { Id = 3, Name = "after" });
                Assert.Equal(3, first.Count<PwItem>());
            }
            using (var again = new XodContext(Path.Combine(dir, "first.xod")))
                Assert.Equal(3, again.Count<PwItem>());

            // With the first database closed, the check under the hold refuses the second file: one database per folder.
            var later = XodCrashSafetyTests.RunWriterProcess("open-second", dir);
            Assert.Contains("REFUSED", later.Output);
            Assert.Contains("first.xod", later.Output);
            Assert.False(File.Exists(Path.Combine(dir, "second.xod")));
        }

        // ISS-096, the other order: an open creating a new database holds the folder alone, its check passed, until
        // its file is written. Meanwhile an open naming another new file, or the same one, is refused (as by a
        // context holding the database alone) and creates nothing; then the first goes on. Created under a shared
        // hold, as the first fix did, the other open passed its check (no file yet) and created its file too.
        [Fact]
        public void An_open_creating_a_database_holds_its_folder_until_its_file_is_written()
        {
            string dir = Path.Combine(tempRoot, "creating");
            Directory.CreateDirectory(dir);
            using var writer = XodCrashSafetyTests.StartWriterProcess("open-second", dir, ("XOD_PAUSE_AT_STEP", "creating"));
            ReadUntil(writer, "PAUSED");

            string rest;
            try
            {
                Assert.Throws<DatabaseLockedException>(() => { using var other = new XodContext(Path.Combine(dir, "first.xod")); });
                Assert.Throws<DatabaseLockedException>(() => { using var same = new XodContext(Path.Combine(dir, "second.xod")); });
                Assert.Empty(DatabaseFilesIn(dir));
            }
            finally
            {
                writer.StandardInput.WriteLine();
                writer.StandardInput.Flush();
                rest = XodCrashSafetyTests.FinishWriter(writer);
            }
            Assert.Contains("OPENED", rest);
            Assert.Equal(new[] { "second.xod" }, DatabaseFilesIn(dir));
            using (var db = new XodContext(Path.Combine(dir, "second.xod")))
            {
                db.Insert(new PwItem { Id = 1, Name = "after" });
                Assert.Equal(1, db.Count<PwItem>());
            }
        }

        // A new database's file is created only with the folder held alone, or under a shared hold's check that finds
        // the database another context created meanwhile. Here another context holds the folder while its database
        // file is gone (deleted while it was open), so the check finds no database file: an open naming a new file
        // there is refused and creates nothing, where it would have created its file beside the one that context
        // writes again, and so would any number of opens doing the same.
        [Fact]
        public void A_new_database_file_isn_t_created_while_another_context_holds_the_folder()
        {
            string dir = Path.Combine(tempRoot, "held-without-file");
            string first = Path.Combine(dir, "first.xod");
            using (var a = new XodContext(first))
            {
                a.Insert(new PwItem { Id = 1, Name = "first" });
                a.Flush();
                File.Delete(first);
                Assert.Throws<DatabaseLockedException>(() => { using var b = new XodContext(Path.Combine(dir, "second.xod")); });
                Assert.Empty(DatabaseFilesIn(dir));
            }
        }

        // ISS-096, the review of TASK-071's second run: an open the check would refuse ran the folder's recovery first.
        // With the hold covering the folder, that undid nothing another context of this release had under way; but
        // XOD 2.0.1 holds only its database file's lock. Here a commit's journal is in the folder (its process
        // crashed) while a context holds the database as 2.0.1 does with LockDatabase, and an open naming a second,
        // new database file goes on from its first check: it is refused before it recovers anything.
        [Fact]
        public void An_open_the_folder_refuses_recovers_nothing()
        {
            string dir = Path.Combine(tempRoot, "refused-recovers-nothing");
            Directory.CreateDirectory(dir);
            using var opener = XodCrashSafetyTests.StartWriterProcess("open-second", dir, ("XOD_PAUSE_AT_STEP", "open-checked"));
            ReadUntil(opener, "PAUSED");

            var setup = XodCrashSafetyTests.RunWriterProcess("pwsetup", dir);
            Assert.True(setup.ExitCode == 0, setup.Output);
            var crash = XodCrashSafetyTests.RunWriterProcess("txcommit", dir, ("XOD_CRASH_AT_STEP", "commit-written"));
            string journal = Path.Combine(dir, "transaction.xwal");
            Assert.True(File.Exists(journal), crash.Output);

            string opened;
            using (new FileStream(Path.Combine(dir, "pw.xod.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                opener.StandardInput.WriteLine();
                opener.StandardInput.Flush();
                opened = XodCrashSafetyTests.FinishWriter(opener);
                Assert.True(File.Exists(journal), "the refused open recovered the folder: " + opened);
            }
            Assert.Contains("REFUSED", opened);
            Assert.Contains("pw.xod", opened);
            Assert.False(File.Exists(Path.Combine(dir, "second.xod")));
            // The database's own next open recovers it: the commit cut short is undone.
            CrashDb.HoldsBatches(dir, "first", 1);
            Assert.False(File.Exists(journal));
        }

        // ISS-096, the review of TASK-071's first run: an open naming a new database file checked an empty folder,
        // then another process created a database there and began a commit. The open, going on, ran the folder's
        // recovery as if it had the database to itself (it held its own file's lock alone), undid that commit under
        // way (its journal, pages and tables), and only then found the other database file and was refused. The
        // commit then reported success with its rows gone.
        [Fact]
        public void An_open_naming_a_second_database_doesn_t_undo_a_commit_under_way()
        {
            string dir = Path.Combine(tempRoot, "second-beside-commit");
            Directory.CreateDirectory(dir);
            using var opener = XodCrashSafetyTests.StartWriterProcess("open-second", dir, ("XOD_PAUSE_AT_STEP", "open-checked"));
            string line;
            while ((line = opener.StandardOutput.ReadLine()) != null && !line.StartsWith("PAUSED", StringComparison.Ordinal)) { }
            Assert.NotNull(line);

            var setup = XodCrashSafetyTests.RunWriterProcess("pwsetup", dir);
            Assert.True(setup.ExitCode == 0, setup.Output);
            using var committer = XodCrashSafetyTests.StartWriterProcess("txcommit", dir, ("XOD_PAUSE_AT_STEP", "commit-written"));
            while ((line = committer.StandardOutput.ReadLine()) != null && !line.StartsWith("PAUSED", StringComparison.Ordinal)) { }
            Assert.NotNull(line);

            string opened;
            try
            {
                opener.StandardInput.WriteLine();
                opener.StandardInput.Flush();
                opened = XodCrashSafetyTests.FinishWriter(opener);
            }
            finally
            {
                committer.StandardInput.WriteLine();
                committer.StandardInput.Flush();
            }
            string committed = XodCrashSafetyTests.FinishWriter(committer);
            // Refused for the committer's hold (PLAN-064: one context at a time), before any check or recovery.
            Assert.Contains("REFUSED", opened);
            Assert.Contains("locked", opened);
            Assert.Contains("COMMITTED", committed);
            Assert.False(File.Exists(Path.Combine(dir, "second.xod")));
            CrashDb.HoldsBatches(dir, "first", 2);
        }

        // The database files in a folder (.xod, .xodj, .xodb), by name.
        private static string[] DatabaseFilesIn(string dir) =>
            Directory.GetFiles(dir).Select(Path.GetFileName)
                .Where(f => f.EndsWith(".xod", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xodj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xodb", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();

        // Reads a process's output up to a line starting with prefix, and returns what it read, that line included.
        private static string ReadUntil(System.Diagnostics.Process p, string prefix)
        {
            var read = new System.Text.StringBuilder();
            string line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                read.AppendLine(line);
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    return read.ToString();
            }
            throw new InvalidOperationException(prefix + " never came: " + read + XodCrashSafetyTests.FinishWriter(p));
        }

        // Each file of the folder and its bytes; null for one another handle holds without sharing (a live marker).
        private static Dictionary<string, byte[]> FolderContent(string dir)
        {
            var content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".lock", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    content[name] = copy.ToArray();
                }
                catch (IOException)
                {
                    content[name] = null;
                }
            }
            return content;
        }

        // A folder the process can't write to, holding a database without its lock file (from 2.0.1, or copied
        // without it): the context opens for reading, and refuses writes with the reason. The open used to fail.
        [Fact]
        public void A_database_in_a_folder_that_cannot_be_written_opens_for_reading()
        {
            if (!OperatingSystem.IsWindows())
                return; // the folder is made read-only with a Windows access rule

            string path = NewDatabase("read-only");
            File.Delete(path + ".lock");
            string dir = Path.GetDirectoryName(path)!;
            var info = new DirectoryInfo(dir);
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.CreateDirectories,
                System.Security.AccessControl.AccessControlType.Deny);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(deny);
            info.SetAccessControl(acl);
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(dir, "probe.txt"), "x"));
                using var db = new XodContext(path, "first");
                Assert.Equal(10, db.Select<PwItem>().Count());
                var refused = Assert.ThrowsAny<XodException>(() => db.Insert(new PwItem { Name = "refused" }));
                Assert.Contains("hold", refused.Message);
            }
            finally
            {
                acl = info.GetAccessControl();
                acl.RemoveAccessRule(deny);
                info.SetAccessControl(acl);
            }
        }

        // ISS-110: the open undoes a commit a crash cut short, from its journal, and the index files must go (they
        // may hold its keys). The journal's undo deleted them itself, and an index file another program held (a
        // backup tool, sharing reading) failed the undo and the open until it was let go. The journal now leaves
        // them to the marker step, which drops what it can and keeps the marker, so the rest stay untrusted. The
        // journal says so itself: the crashed context's marker may not be there (XOD 2.0.0 and 2.0.1 write none).
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_commit_undone_at_the_open_isn_t_stopped_by_an_index_file_held_open(bool markerLeft)
        {
            if (!OperatingSystem.IsWindows())
                return; // a file held open blocks its deletion on Windows only
            string dir = CrashDb.New(tempRoot, "undo-held-index-" + markerLeft);
            var crash = XodCrashSafetyTests.RunWriterProcess("txcommit", dir, ("XOD_CRASH_AT_STEP", "commit-written"));
            Assert.DoesNotContain("COMMITTED", crash.Output);
            if (!markerLeft)
            {
                foreach (string marker in Directory.GetFiles(dir, "*.xidxdirty"))
                    File.Delete(marker);
            }
            string journal = Path.Combine(dir, "transaction.xwal");
            Assert.True(File.Exists(journal));
            string index = Assert.Single(Directory.GetFiles(dir, typeof(CrashCustomer).FullName + ".xidx"));

            using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var open = XodCrashSafetyTests.RunWriterProcess("pwopen", dir);
                Assert.Contains("COUNT 25", open.Output);
                Assert.False(File.Exists(journal), open.Output);
                Assert.NotEmpty(Directory.GetFiles(dir, "*.xidxdirty")); // the index files stay untrusted
            }

            CrashDb.HoldsBatches(dir, "first", 1);
            Assert.Empty(Directory.GetFiles(dir, "*.xidxdirty"));
        }
    }
}
