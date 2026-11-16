using System;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// PLAN-064: a database is open in one context at a time. Every context holds it alone from its open to its
    /// disposal, whatever <see cref="DatabaseOptions.LockDatabase"/> says; any other open, in this process or another,
    /// is refused with <see cref="DatabaseLockedException"/> (after <see cref="DatabaseOptions.LockRetryTimeoutMs"/>).
    /// </summary>
    public class XodOneContextTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-one-context-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class OneItem
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        private string NewDatabase(string name)
        {
            string path = Path.Combine(tempRoot, name, "one.xod");
            using (var db = new XodContext(path))
                db.Insert(new OneItem { Name = "first" });
            return path;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_second_context_on_an_open_database_is_refused(bool lockDatabase)
        {
            string path = NewDatabase("second");
            using var first = new XodContext(path, options: new DatabaseOptions { LockDatabase = lockDatabase });
            Assert.Throws<DatabaseLockedException>(() => new XodContext(path, options: new DatabaseOptions { LockDatabase = lockDatabase }));

            // The first context is untouched by the refused open.
            first.Insert(new OneItem { Name = "second" });
            Assert.Equal(2, first.Count<OneItem>());
        }

        [Fact]
        public void A_database_opens_again_once_its_context_is_disposed()
        {
            string path = NewDatabase("again");
            new XodContext(path).Dispose();
            using var db = new XodContext(path);
            Assert.Equal(1, db.Count<OneItem>());
        }

        // XOD 2.0.2 holds the folder's lock file shared while it has the database open: a context of this version
        // doesn't open beside it.
        [Fact]
        public void A_shared_hold_of_an_older_version_keeps_a_new_open_out()
        {
            string path = NewDatabase("older");
            string folderLock = Path.Combine(Path.GetDirectoryName(path)!, "xod.lock");
            using (new FileStream(folderLock, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
                Assert.Throws<DatabaseLockedException>(() => new XodContext(path));

            using var db = new XodContext(path);
            Assert.Equal(1, db.Count<OneItem>());
        }

        // A context in another process has the database open (paused in its commit): an open here is refused, and
        // the other's commit goes on untouched.
        [Fact]
        public void A_database_another_process_has_open_is_refused()
        {
            string dir = CrashDb.New(tempRoot, "process");
            using var writer = XodCrashSafetyTests.StartWriterProcess("txcommit", dir, ("XOD_PAUSE_AT_STEP", "commit-written"));
            string line;
            while ((line = writer.StandardOutput.ReadLine()) != null && !line.StartsWith("PAUSED", StringComparison.Ordinal)) { }
            Assert.NotNull(line);

            Assert.Throws<DatabaseLockedException>(() => new XodContext(Path.Combine(dir, "pw.xod"), "first"));
            Assert.True(File.Exists(Path.Combine(dir, "transaction.xwal")), "the refused open undid a live commit");

            writer.StandardInput.WriteLine();
            writer.StandardInput.Flush();
            Assert.Contains("COMMITTED", XodCrashSafetyTests.FinishWriter(writer));
            CrashDb.HoldsBatches(dir, "first", 2);
        }

        // The CLI's reads open the database as a context does: refused while an application has it open.
        [Fact]
        public void A_read_only_use_is_refused_too()
        {
            string path = NewDatabase("reader");
            using var app = new XodContext(path);
            Assert.Throws<DatabaseLockedException>(() => new XodContext(path, options: new DatabaseOptions { InitialCreate = false }));
            Assert.Single(app.Select<OneItem>());
        }
    }
}
