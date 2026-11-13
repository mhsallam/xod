using System;
using System.IO;
using System.Linq;
using Xod;
using Xod.Engines.Serializers;
using Xunit;

namespace Xod.Tests
{
    public class ConvertNote
    {
        public int Id { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// ISS-072: <see cref="XodMigrator.ConvertDatabase"/> wrote its target file by file, so a crash left a partial
    /// target; it mixed its files into a target folder that already held others; and it read a source another
    /// context had open, or one a crash had left half written (a commit's journal), copying a database no open
    /// would show. Now the target appears whole or not at all, a folder that isn't empty is refused, the source is
    /// held alone while it is read and recovered first, and a conversion in place replaces each file atomically.
    /// </summary>
    public class XodMigratorSafetyTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-migrate-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string PlainDatabase(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            using var db = new XodContext(Path.Combine(dir, "notes.xod"));
            for (int i = 1; i <= 20; i++)
                db.Insert(new ConvertNote { Text = "note " + i });
            return dir;
        }

        private static void HoldsNotes(string dir)
        {
            using var db = new XodContext(Path.Combine(dir, "notes.xod"));
            Assert.Equal(Enumerable.Range(1, 20).Select(i => "note " + i), db.Select<ConvertNote>().OrderBy(n => n.Id).Select(n => n.Text));
        }

        [Fact]
        public void A_conversion_cut_short_leaves_no_target_folder()
        {
            string dir = CrashDb.New(tempRoot, "source");
            string target = Path.Combine(tempRoot, "target");
            var crash = XodCrashSafetyTests.RunWriterProcess("convert", dir, ("XOD_TEST_TARGET", target), ("XOD_CRASH_AT_STEP", "convert-written:1"));
            Assert.DoesNotContain("CONVERTED", crash.Output);
            Assert.False(Directory.Exists(target), "a partial target was left: " + string.Join(", ", Directory.Exists(target) ? Directory.GetFiles(target) : Array.Empty<string>()));

            // The source is as it was, and the conversion runs again.
            CrashDb.HoldsBatches(dir, "first", 1);
            XodMigrator.ConvertDatabase(dir, target, StorageFormat.Binary, "first");
            CrashDb.HoldsBatches(target, "first", 1);
        }

        [Fact]
        public void Converting_into_a_folder_that_is_not_empty_is_refused()
        {
            string dir = PlainDatabase("source");
            string target = Path.Combine(tempRoot, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.txt"), "mine");

            Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, target, StorageFormat.Json));
            Assert.Equal(new[] { "keep.txt" }, Directory.GetFiles(target).Select(Path.GetFileName));
        }

        [Fact]
        public void Converting_into_an_empty_folder_works()
        {
            string dir = PlainDatabase("source");
            string target = Path.Combine(tempRoot, "target");
            Directory.CreateDirectory(target);

            XodMigrator.ConvertDatabase(dir, target, StorageFormat.Json);
            HoldsNotes(target);
            Assert.Equal(StorageFormat.Json, StorageSerializerFactory.DetectFormat(File.ReadAllBytes(Path.Combine(target, "notes.xod"))));
        }

        [Fact]
        public void Converting_a_database_another_context_has_open_is_refused()
        {
            string dir = PlainDatabase("source");
            string target = Path.Combine(tempRoot, "target");
            using (var open = new XodContext(Path.Combine(dir, "notes.xod")))
            {
                Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, target, StorageFormat.Json));
                Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, dir, StorageFormat.Json));
                open.Insert(new ConvertNote { Text = "still writing" });
            }
            Assert.False(Directory.Exists(target));
            using var db = new XodContext(Path.Combine(dir, "notes.xod"));
            Assert.Equal(21, db.Count<ConvertNote>());
        }

        // ISS-084: the conversion's exclusive hold asked for write access, so a read-only lock file failed it with
        // UnauthorizedAccessException, which the conversion took for a folder nothing can write: it went on with no
        // hold, past the open context, and in place rewrote the files under it.
        [Fact]
        public void A_read_only_lock_file_does_not_let_a_conversion_past_an_open_context()
        {
            string dir = PlainDatabase("source");
            string target = Path.Combine(tempRoot, "target");
            string lockFile = Path.Combine(dir, "notes.xod.lock");
            using (var open = new XodContext(Path.Combine(dir, "notes.xod")))
            {
                File.SetAttributes(lockFile, FileAttributes.ReadOnly);
                try
                {
                    Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, target, StorageFormat.Json));
                    Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, dir, StorageFormat.Json));
                    open.Insert(new ConvertNote { Text = "still writing" });
                }
                finally
                {
                    File.SetAttributes(lockFile, FileAttributes.Normal);
                }
            }
            Assert.False(Directory.Exists(target));
            using var db = new XodContext(Path.Combine(dir, "notes.xod"));
            Assert.Equal(21, db.Count<ConvertNote>());
        }

        // ISS-084, with a commit under way in another process: the conversion's recovery, run without a hold,
        // undid that live commit and deleted its journal; the commit then reported success with its rows gone.
        [Fact]
        public void A_read_only_lock_file_does_not_let_a_conversion_undo_a_live_commit()
        {
            string dir = CrashDb.New(tempRoot, "source");
            string target = Path.Combine(tempRoot, "target");
            string lockFile = Path.Combine(dir, "pw.xod.lock");
            using var writer = XodCrashSafetyTests.StartWriterProcess("txcommit", dir, ("XOD_PAUSE_AT_STEP", "commit-written"));
            string line;
            while ((line = writer.StandardOutput.ReadLine()) != null && !line.StartsWith("PAUSED", StringComparison.Ordinal)) { }
            Assert.NotNull(line);

            File.SetAttributes(lockFile, FileAttributes.ReadOnly);
            try
            {
                Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, target, StorageFormat.Json, "first"));
            }
            finally
            {
                File.SetAttributes(lockFile, FileAttributes.Normal);
                writer.StandardInput.WriteLine();
                writer.StandardInput.Flush();
            }
            string rest = writer.StandardOutput.ReadToEnd() + writer.StandardError.ReadToEnd();
            Assert.True(writer.WaitForExit(120_000));
            Assert.Contains("COMMITTED", rest);
            Assert.False(Directory.Exists(target));
            CrashDb.HoldsBatches(dir, "first", 2);
        }

        // ISS-085: a folder holds one database, but each database file has its own lock file: a context opened
        // through a second database file in the folder wrote to the shared tables while a conversion held the
        // first. A folder with two database files is refused.
        [Fact]
        public void A_folder_with_two_database_files_is_refused()
        {
            string dir = PlainDatabase("source");
            File.Copy(Path.Combine(dir, "notes.xod"), Path.Combine(dir, "other.xod"));
            string target = Path.Combine(tempRoot, "target");
            var refused = Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, target, StorageFormat.Json));
            Assert.Contains("other.xod", refused.Message);
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public void Opening_the_source_during_a_conversion_is_refused()
        {
            string dir = CrashDb.New(tempRoot, "source");
            string target = Path.Combine(tempRoot, "target");
            using var writer = XodCrashSafetyTests.StartWriterProcess("convert", dir, ("XOD_TEST_TARGET", target), ("XOD_PAUSE_AT_STEP", "convert-written:1"));
            string line;
            while ((line = writer.StandardOutput.ReadLine()) != null && !line.StartsWith("PAUSED", StringComparison.Ordinal)) { }
            Assert.NotNull(line);

            Assert.ThrowsAny<XodException>(() =>
            {
                using var other = new XodContext(Path.Combine(dir, "pw.xod"), "first", new DatabaseOptions { LockRetryTimeoutMs = 200 });
                other.Insert(new CrashCustomer { Name = "during" });
            });

            writer.StandardInput.WriteLine();
            writer.StandardInput.Flush();
            string rest = writer.StandardOutput.ReadToEnd() + writer.StandardError.ReadToEnd();
            Assert.True(writer.WaitForExit(120_000));
            Assert.Contains("CONVERTED", rest);
            CrashDb.HoldsBatches(target, "first", 1);
        }

        [Fact]
        public void A_source_a_crash_left_mid_commit_is_recovered_before_it_is_converted()
        {
            string dir = CrashDb.New(tempRoot, "source");
            var crash = XodCrashSafetyTests.RunWriterProcess("txcommit", dir, ("XOD_CRASH_AT_STEP", "commit-written"));
            Assert.DoesNotContain("COMMITTED", crash.Output);
            Assert.True(File.Exists(Path.Combine(dir, "transaction.xwal")));

            string target = Path.Combine(tempRoot, "target");
            XodMigrator.ConvertDatabase(dir, target, StorageFormat.Binary, "first");

            // The commit that didn't finish is undone, in the target as in the source.
            CrashDb.HoldsBatches(target, "first", 1);
            Assert.False(File.Exists(Path.Combine(dir, "transaction.xwal")));
            CrashDb.HoldsBatches(dir, "first", 1);
        }

        [Fact]
        public void An_in_place_conversion_cut_short_leaves_a_database_that_opens()
        {
            foreach (int replaced in new[] { 1, 3 })
            {
                string dir = CrashDb.New(tempRoot, "inplace-" + replaced);
                var crash = XodCrashSafetyTests.RunWriterProcess("convert", dir, ("XOD_CRASH_AT_STEP", "convert-written:" + replaced));
                Assert.DoesNotContain("CONVERTED", crash.Output);
                CrashDb.HoldsBatches(dir, "first", 1);
            }
        }

        [Fact]
        public void The_source_folder_named_another_way_is_converted_in_place()
        {
            string dir = PlainDatabase("source");
            XodMigrator.ConvertDatabase(dir, dir + Path.DirectorySeparatorChar, StorageFormat.Json);
            Assert.All(Directory.GetFiles(dir).Where(f => !f.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".xidx", StringComparison.OrdinalIgnoreCase)),
                f => Assert.Equal(StorageFormat.Json, StorageSerializerFactory.DetectFormat(File.ReadAllBytes(f))));
            Assert.DoesNotContain(Directory.GetFiles(dir), f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
            HoldsNotes(dir);
        }

        [Fact]
        public void A_folder_without_a_database_file_is_refused()
        {
            string dir = Path.Combine(tempRoot, "empty-source");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a database");
            Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, Path.Combine(tempRoot, "target"), StorageFormat.Json));
            Assert.False(Directory.Exists(Path.Combine(tempRoot, "target")));
        }

        // The review of TASK-071: the conversion checked the folder to hold one database before its hold, and not
        // again, then converted every file of the folder. Here a second database file is put in the folder (a copy,
        // or XOD 2.0.x, which takes no folder hold) after the conversion's first check: under its hold it checks
        // again and is refused, converting nothing, where it converted both into its target.
        [Fact]
        public void A_conversion_checks_the_folder_again_under_its_hold()
        {
            string dir = Path.Combine(tempRoot, "second-file");
            var setup = XodCrashSafetyTests.RunWriterProcess("pwsetup", dir);
            Assert.True(setup.ExitCode == 0, setup.Output);
            string target = Path.Combine(tempRoot, "second-file-target");
            using var convert = XodCrashSafetyTests.StartWriterProcess("convert", dir, ("XOD_PAUSE_AT_STEP", "open-checked"), ("XOD_TEST_TARGET", target));
            string line;
            while ((line = convert.StandardOutput.ReadLine()) != null && !line.StartsWith("PAUSED", StringComparison.Ordinal)) { }
            Assert.NotNull(line);

            File.Copy(Path.Combine(dir, "pw.xod"), Path.Combine(dir, "copy.xod"));
            convert.StandardInput.WriteLine();
            convert.StandardInput.Flush();
            string rest = convert.StandardOutput.ReadToEnd() + convert.StandardError.ReadToEnd();
            Assert.True(convert.WaitForExit(120_000));
            Assert.Contains("more than one database file", rest);
            Assert.DoesNotContain("CONVERTED", rest);
            Assert.False(Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any());
        }
    }
}
