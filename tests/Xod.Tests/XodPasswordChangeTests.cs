using System;
using System.IO;
using System.Linq;
using System.Text;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class PwItem
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class PwTag
    {
        public int Id { get; set; }
        public string Label { get; set; }
    }

    /// <summary>
    /// ISS-065: setting, changing or removing a database's password is all or nothing. A change that fails,
    /// or a process that dies part-way, leaves every file under the old password or every file under the new
    /// one (finished when the database is next opened), never a mix that neither password opens.
    ///
    /// Faults land at exact steps through the crash writer (tests/Xod.CrashWriter) and XOD_CRASH_AT_STEP,
    /// XOD_FAIL_AT_STEP (one IOException, as from a file briefly open elsewhere) or XOD_PAUSE_AT_STEP; see
    /// src/Xod/Services/FaultInjection.cs. They behave the same on every OS.
    /// </summary>
    public class XodPasswordChangeTests : IDisposable
    {
        private static readonly string[] DatabaseExtensions = { ".xod", ".xodj", ".xodb", ".xtab", ".xpag", ".xidx" };
        private static readonly byte[] EnvelopeHeader = Encoding.ASCII.GetBytes("XODENC2\0");

        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-password-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        // ---- in-process: a database of two tables, 30 rows each ----

        private string NewDatabase(string name, StorageFormat format, string password, bool lockDatabase = false)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "pw.xod");
            using var db = new XodContext(path, password, Options(format, lockDatabase));
            for (int i = 1; i <= 30; i++)
            {
                db.Insert(new PwItem { Name = "item " + i });
                db.Insert(new PwTag { Label = "tag " + i });
            }
            return path;
        }

        private static DatabaseOptions Options(StorageFormat format, bool lockDatabase = false) =>
            new DatabaseOptions { StorageFormat = format, LockDatabase = lockDatabase };

        // In the order the switch takes them.
        private static string[] DatabaseFiles(string dir) =>
            Directory.GetFiles(dir).Where(f => DatabaseExtensions.Contains(Path.GetExtension(f))).OrderBy(f => f, StringComparer.Ordinal).ToArray();

        private static void Reads(string path, string password, StorageFormat format)
        {
            using var db = new XodContext(path, password, Options(format));
            Assert.Equal(30, db.Select<PwItem>().Count());
            Assert.Equal(30, db.Select<PwTag>().Count());
            Assert.Equal("item 30", db.Find<PwItem>(i => i.Id == 30).Name);
        }

        private static void DoesNotOpen(string path, string password, StorageFormat format)
        {
            Assert.ThrowsAny<Exception>(() =>
            {
                using var db = new XodContext(path, password, Options(format));
                db.Select<PwItem>().ToList();
                db.Select<PwTag>().ToList();
            });
        }

        // Nothing besides the database's own files (and the lock files: the database file's, the folder's "xod.lock",
        // the gate "xod.gate.lock", PLAN-046 section 9, and the commit gate "xod.commit.lock", ISS-164) is left in the folder.
        private static void NothingLeftOver(string dir)
        {
            var extra = Directory.GetFiles(dir)
                .Where(f => !DatabaseExtensions.Contains(Path.GetExtension(f)) && !f.EndsWith(".xod.lock", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFileName(f), "xod.lock", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFileName(f), "xod.gate.lock", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFileName(f), "xod.commit.lock", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .ToList();
            Assert.Empty(extra);
        }

        private static bool Enveloped(string file)
        {
            byte[] head = new byte[EnvelopeHeader.Length];
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return fs.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(EnvelopeHeader);
        }

        // A file the change can't read (held open elsewhere with no sharing), after files it has already staged:
        // the change is refused and nothing changes. (A table file: the AES256 key's scan reads only pages, so
        // the failure is the staging's.)
        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        [InlineData(StorageFormat.Binary)]
        public void A_password_change_that_cannot_read_a_file_changes_nothing(StorageFormat format)
        {
            string path = NewDatabase("unreadable-" + format, format, "first");
            string dir = Path.GetDirectoryName(path);
            string[] files = DatabaseFiles(dir);
            string held = files.Last(f => f.EndsWith(".xtab", StringComparison.Ordinal));
            Assert.True(Array.IndexOf(files, held) > 0);

            using (var db = new XodContext(path, "first", Options(format)))
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var e = Assert.ThrowsAny<IOException>(() => db.ChangePassword("first", "second"));
                Assert.Contains(Path.GetFileName(held), e.Message);
            }

            Reads(path, "first", format);
            DoesNotOpen(path, "second", format);
            NothingLeftOver(dir);

            using (var db = new XodContext(path, "first", Options(format)))
                db.ChangePassword("first", "second");
            Reads(path, "second", format);
        }

        // The same for setting a password and removing it: one writer for all three.
        [Fact]
        public void Setting_and_removing_a_password_that_cannot_read_a_file_change_nothing()
        {
            string path = NewDatabase("secure-loose", StorageFormat.Xml, null);
            string dir = Path.GetDirectoryName(path);
            string held = DatabaseFiles(dir).Last(f => f.EndsWith(".xtab", StringComparison.Ordinal));

            using (var db = new XodContext(path, null, Options(StorageFormat.Xml)))
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => db.Secure("first"));
            Reads(path, null, StorageFormat.Xml);
            Assert.DoesNotContain(DatabaseFiles(dir), Enveloped);

            using (var db = new XodContext(path, null, Options(StorageFormat.Xml)))
                db.Secure("first");
            Assert.All(DatabaseFiles(dir), f => Assert.True(Enveloped(f), f));

            using (var db = new XodContext(path, "first", Options(StorageFormat.Xml)))
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => db.Loose("first"));
            Reads(path, "first", StorageFormat.Xml);
            Assert.All(DatabaseFiles(dir), f => Assert.True(Enveloped(f), f));
            NothingLeftOver(dir);
        }

        // With LockDatabase the database holds "<name>.xod.lock" open; a password change must not need that name.
        [Fact]
        public void A_locked_database_can_be_secured()
        {
            string path = NewDatabase("locked", StorageFormat.Xml, null, lockDatabase: true);
            using (var db = new XodContext(path, null, Options(StorageFormat.Xml, lockDatabase: true)))
                db.Secure("first");
            Reads(path, "first", StorageFormat.Xml);
        }

        // A transaction's rollback restores files as they were; after a password change that would put them
        // back under the old password among files under the new one.
        [Fact]
        public void A_password_change_inside_a_transaction_is_refused()
        {
            string path = NewDatabase("transaction", StorageFormat.Xml, "first");
            using (var db = new XodContext(path, "first", Options(StorageFormat.Xml)))
            {
                using (var tx = db.BeginTransaction())
                {
                    db.Insert(new PwItem { Name = "in the transaction" });
                    Assert.ThrowsAny<XodException>(() => db.ChangePassword("first", "second"));
                }
            }
            Reads(path, "first", StorageFormat.Xml);
        }

        // A commit that didn't finish leaves its undo journal, and the next open puts the files back as they were,
        // under the password they had. A change of password over it would end with files under both.
        [Fact]
        public void A_password_change_over_an_unfinished_commit_is_refused()
        {
            string path = NewDatabase("unfinished-commit", StorageFormat.Xml, "first");
            string dir = Path.GetDirectoryName(path);
            string page = DatabaseFiles(dir).First(f => f.EndsWith(".xpag", StringComparison.Ordinal));

            using (var db = new XodContext(path, "first", Options(StorageFormat.Xml)))
            {
                // What a commit leaves when it fails after its journal (PLAN-046 section 9).
                File.WriteAllText(Path.Combine(dir, "transaction.xwal"),
                    "<TransactionJournal id=\"" + Guid.NewGuid().ToString("N") + "\" timestampUtc=\"2026-11-12T00:00:00.0000000Z\">" +
                    "<Snapshot file=\"" + Path.GetFileName(page) + "\">" + Convert.ToBase64String(File.ReadAllBytes(page)) + "</Snapshot>" +
                    "</TransactionJournal>");
                Assert.ThrowsAny<XodException>(() => db.ChangePassword("first", "second"));
            }

            Reads(path, "first", StorageFormat.Xml);
            NothingLeftOver(dir);
        }

        // Likewise an atomic write cut short: the next open renames "<file>.tmp" into place, as it was written.
        [Fact]
        public void A_password_change_over_an_unfinished_write_is_refused()
        {
            string path = NewDatabase("unfinished-write", StorageFormat.Xml, "first");
            string dir = Path.GetDirectoryName(path);
            using (var db = new XodContext(path, "first", Options(StorageFormat.Xml)))
            {
                File.WriteAllBytes(Path.Combine(dir, "a1b2c3d4e.xpag.tmp"), new byte[] { 1, 2, 3 });
                Assert.ThrowsAny<XodException>(() => db.ChangePassword("first", "second"));
            }
            Reads(path, "first", StorageFormat.Xml);
        }

        // ISS-149: likewise a file a transaction set aside (deleted, "<file>.xdel") when its process died before the
        // commit: the next open alone puts it back, as it was written. A switch converted only the database's files and
        // refused only over journals and ".tmp" files, so the page came back in plaintext under a secured database, and
        // its table couldn't be read. The switch now refuses over everything the open recovers.
        [Fact]
        public void A_password_change_over_a_file_a_transaction_set_aside_is_refused()
        {
            string path = NewDatabase("set-aside", StorageFormat.Xml, null);
            string dir = Path.GetDirectoryName(path);
            string page = DatabaseFiles(dir).First(f => f.EndsWith(".xpag", StringComparison.Ordinal));
            using (var db = new XodContext(path, null, Options(StorageFormat.Xml)))
            {
                File.Move(page, page + ".xdel");
                Assert.ThrowsAny<XodException>(() => db.Secure("second"));
            }
            Reads(path, null, StorageFormat.Xml);
            NothingLeftOver(dir);
        }

        // Opening a folder picks the database file, not a file a password change or a write left beside it.
        [Fact]
        public void Opening_a_folder_picks_the_database_file()
        {
            string path = NewDatabase("folder-open", StorageFormat.Xml, "first");
            string dir = Path.GetDirectoryName(path);
            File.WriteAllBytes(Path.Combine(dir, "a.xod.xpwnew"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(dir, "a.xod.tmp"), new byte[0]);
            using var db = new XodContext(dir, "first");
            Assert.Equal("pw.xod", Path.GetFileName(db.GetStatistics().DatabasePath));
            Assert.Equal(30, db.Select<PwItem>().Count());
        }

        // Buffered durability under a password: Flush puts the index on disk with the pages. (Under a password
        // the index is saved through the buffered files; a flush that wrote those first left it queued.)
        [Fact]
        public void Flush_under_a_password_with_buffered_durability_puts_the_index_on_disk()
        {
            string path = NewDatabase("buffered-flush", StorageFormat.Xml, "first");
            string index = Path.Combine(Path.GetDirectoryName(path), "Xod.Tests.PwItem.xidx");
            byte[] before = File.ReadAllBytes(index);

            var options = Options(StorageFormat.Xml);
            options.Durability = DurabilityMode.Buffered;
            using var db = new XodContext(path, "first", options);
            for (int i = 31; i <= 40; i++)
                db.Insert(new PwItem { Name = "item " + i });
            db.Flush();
            Assert.False(before.AsSpan().SequenceEqual(File.ReadAllBytes(index)), "Flush left the index file as it was");
        }

        // ---- the crash writer: 25 customers with 3 orders each, under the password "first" ----

        // A process killed at each step of a change: before the change is committed the database is the old
        // password's; from the commit on it is the new one's, finished at the next open. No file is ever
        // written in plain on the way.
        [Theory]
        [InlineData("staged:1", false)]
        [InlineData("staged:3", false)]
        [InlineData("committed", true)]
        [InlineData("switched:1", true)]
        [InlineData("switched:3", true)]
        public void A_crash_during_a_password_change_leaves_one_password(string step, bool changed)
        {
            string dir = CrashDb.New(tempRoot, "crash-" + step.Replace(':', '-'));

            var change = XodCrashSafetyTests.RunWriterProcess("pwchange", dir, ("XOD_CRASH_AT_STEP", step));
            Assert.NotEqual(0, change.ExitCode);
            Assert.DoesNotContain("CHANGED", change.Output);

            // Before anything reopens it: nothing on disk is in plain (the commit record holds only names).
            Assert.All(Directory.GetFiles(dir).Where(f => !f.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".xpwlog", StringComparison.OrdinalIgnoreCase)),
                f => Assert.True(Enveloped(f), Path.GetFileName(f) + " is in plain"));

            CrashDb.HoldsBatches(dir, changed ? "second" : "first", 1);
            CrashDb.DoesNotOpen(dir, changed ? "first" : "second");
            NothingLeftOver(dir);
        }

        // A commit record cut short by a crash while it was written (no commit line) is not a commit.
        [Fact]
        public void A_commit_record_cut_short_is_not_a_commit()
        {
            string dir = CrashDb.New(tempRoot, "torn-record");
            Assert.NotEqual(0, XodCrashSafetyTests.RunWriterProcess("pwchange", dir, ("XOD_CRASH_AT_STEP", "staged:3")).ExitCode);

            var staged = Directory.GetFiles(dir, "*.xpwnew").Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
            Assert.Equal(3, staged.Count);
            File.WriteAllText(Path.Combine(dir, "pw.xod.xpwlog"), "XOD password change v1\n" + string.Join("\n", staged) + "\n");

            CrashDb.HoldsBatches(dir, "first", 1);
            NothingLeftOver(dir);
        }

        // A file that can't be replaced once the change is committed: the change stands, and the database opens
        // with the new password (finished at the next open).
        [Theory]
        [InlineData("pw.xod")]
        [InlineData("CrashCustomer.xtab")]
        public void A_change_that_cannot_replace_a_file_finishes_when_the_database_is_next_opened(string file)
        {
            string dir = CrashDb.New(tempRoot, "unreplaceable-" + file);
            var change = XodCrashSafetyTests.RunWriterProcess("pwchange", dir, ("XOD_FAIL_AT_STEP", "switching:" + file));
            Assert.Contains("PENDING", change.Output);

            CrashDb.HoldsBatches(dir, "second", 1);
            CrashDb.DoesNotOpen(dir, "first");
            NothingLeftOver(dir);
        }

        // Then another change in the same context: the first is finished before anything reads the files for
        // the second (the database file holds the AES256 key, read first), not lost under the second's staging.
        [Theory]
        [InlineData("pw.xod")]
        [InlineData("CrashCustomer.xtab")]
        public void A_change_after_one_that_did_not_finish_finishes_it_first(string file)
        {
            string dir = CrashDb.New(tempRoot, "unfinished-then-change-" + file);
            var run = XodCrashSafetyTests.RunWriterProcess("pwchange-twice", dir, ("XOD_FAIL_AT_STEP", "switching:" + file));
            Assert.Contains("PENDING", run.Output);
            Assert.Contains("CHANGED", run.Output);

            CrashDb.HoldsBatches(dir, "third", 1);
            CrashDb.DoesNotOpen(dir, "second");
            NothingLeftOver(dir);
        }

        // Or writes in the same context: they finish the change first, so the next open doesn't put a staged
        // copy from before them over what they wrote. (The index file: written without being read first.)
        [Fact]
        public void Writes_after_a_change_that_did_not_finish_are_kept()
        {
            string dir = CrashDb.New(tempRoot, "unfinished-then-write");
            var run = XodCrashSafetyTests.RunWriterProcess("pwchange-then-write", dir, ("XOD_FAIL_AT_STEP", "switching:CrashCustomer.xidx"));
            Assert.Contains("PENDING", run.Output);
            Assert.Contains("WROTE", run.Output);

            CrashDb.HoldsBatches(dir, "second", 2);
            NothingLeftOver(dir);
        }

        // Another context opening the database while a change is staging: the open is refused, and the change
        // (whose staged files a recovery would otherwise delete) completes.
        [Fact]
        public void Opening_the_database_during_a_change_is_refused_and_the_change_completes()
        {
            string dir = CrashDb.New(tempRoot, "open-during-change");
            using var writer = XodCrashSafetyTests.StartWriterProcess("pwchange", dir, ("XOD_PAUSE_AT_STEP", "staged:2"));
            string line;
            while ((line = writer.StandardOutput.ReadLine()) != null && !line.StartsWith("PAUSED", StringComparison.Ordinal)) { }
            Assert.NotNull(line);

            Assert.ThrowsAny<XodException>(() =>
            {
                using var other = new XodContext(Path.Combine(dir, "pw.xod"), "first");
                other.Count<CrashCustomer>();
            });

            writer.StandardInput.WriteLine();
            writer.StandardInput.Flush();
            string rest = XodCrashSafetyTests.FinishWriter(writer);
            Assert.True(writer.ExitCode == 0, rest);
            Assert.Contains("CHANGED", rest);

            CrashDb.HoldsBatches(dir, "second", 1);
            NothingLeftOver(dir);
        }

        // And a password change sees everything the context holds: rows not yet flushed, and their index.
        [Fact]
        public void A_password_change_with_buffered_durability_keeps_rows_not_yet_flushed()
        {
            string dir = CrashDb.New(tempRoot, "buffered-change");
            var run = XodCrashSafetyTests.RunWriterProcess("pwchange-buffered", dir, ("XOD_CRASH_AT_STEP", "committed"));
            Assert.NotEqual(0, run.ExitCode);

            CrashDb.HoldsBatches(dir, "second", 2);
            NothingLeftOver(dir);
        }
    }
}
