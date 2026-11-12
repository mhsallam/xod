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

        // Another context opening the database while a commit is under way doesn't undo that commit: its
        // journal is live, not left by a crash.
        [Fact]
        public void An_open_during_another_context_s_commit_leaves_the_commit_alone()
        {
            string dir = CrashDb.New(tempRoot, "open-during-commit");
            using var writer = XodCrashSafetyTests.StartWriterProcess("txcommit", dir, ("XOD_PAUSE_AT_STEP", "commit-written"));
            string line;
            while ((line = writer.StandardOutput.ReadLine()) != null && !line.StartsWith("PAUSED", StringComparison.Ordinal)) { }
            Assert.NotNull(line);
            Assert.True(File.Exists(Path.Combine(dir, "transaction.xwal")));

            using (var other = new XodContext(Path.Combine(dir, "pw.xod"), "first"))
                Assert.True(File.Exists(Path.Combine(dir, "transaction.xwal")), "the open undid a live commit");

            writer.StandardInput.WriteLine();
            writer.StandardInput.Flush();
            string rest = writer.StandardOutput.ReadToEnd() + writer.StandardError.ReadToEnd();
            Assert.True(writer.WaitForExit(120_000));
            Assert.Contains("COMMITTED", rest);

            CrashDb.HoldsBatches(dir, "first", 2);
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

        // Another context open on the database: the change is refused, and that context goes on as before.
        [Fact]
        public void A_password_change_is_refused_while_another_context_has_the_database_open()
        {
            string path = NewDatabase("other-open");
            using (var a = new XodContext(path, "first"))
            {
                using (var b = new XodContext(path, "first"))
                {
                    Assert.ThrowsAny<XodException>(() => a.ChangePassword("first", "second"));
                    b.Insert(new PwItem { Name = "from b" });
                    Assert.Equal(11, b.Select<PwItem>().Count());
                }

                a.ChangePassword("first", "second");
                Assert.Equal(11, a.Select<PwItem>().Count());
            }

            using (var c = new XodContext(path, "second"))
                Assert.Equal(11, c.Select<PwItem>().Count());
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
        // each other (on Windows an open racing the deletion fails). It doesn't stop a later LockDatabase open.
        [Fact]
        public void The_lock_file_stays_and_does_not_block_a_later_lock()
        {
            string path = NewDatabase("lock-file");
            using (var a = new XodContext(path, "first"))
            using (var b = new XodContext(path, "first"))
                Assert.True(File.Exists(path + ".lock"));

            using (var c = new XodContext(path, "first", new DatabaseOptions { LockDatabase = true }))
                Assert.Equal(10, c.Select<PwItem>().Count());
            Assert.True(File.Exists(path + ".lock"));

            using (var d = new XodContext(path, "first"))
                Assert.Equal(10, d.Select<PwItem>().Count());
        }

        // A context that lost its hold on the database (it couldn't take it back after a password change) writes
        // nothing more: it no longer knows whether others have the database, so a change or an open's recovery could
        // run under it. It used to go on writing, and to make the next password change alone.
        [Fact]
        public void A_context_that_lost_its_hold_refuses_writes()
        {
            string dir = CrashDb.New(tempRoot, "lost-hold");
            var run = XodCrashSafetyTests.RunWriterProcess("pwchange-then-write", dir, ("XOD_FAIL_AT_STEP", "retaking-hold"));
            Assert.Contains("PENDING", run.Output);
            Assert.Contains(run.Output.Split(Environment.NewLine), l => l.StartsWith("REFUSED", StringComparison.Ordinal) && l.Contains("no longer holds"));
            CrashDb.HoldsBatches(dir, "second", 1);
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
    }
}
