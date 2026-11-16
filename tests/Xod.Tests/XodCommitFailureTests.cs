using System;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// A commit that fails, or a rollback, leaves the context and the disk as they were before the transaction,
    /// whatever the context still had to write (buffered durability) and wherever the commit failed (its journal,
    /// its files, its indexes). Found by the independent review of PLAN-051. Each runs the crash writer's
    /// "txwrite" modes: batch 1 written outside the transaction, batch 2 inside it, then the next batch after
    /// what the context sees; the database holds batch 0 from setup.
    /// </summary>
    public class XodCommitFailureTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-commitfail-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private static readonly (string, string) Buffered = ("XOD_TEST_DURABILITY", "Buffered");

        private static void FailedAndCarriedOn(string output)
        {
            Assert.Contains("COMMIT FAILED", output);
            Assert.Contains("COUNT 50", output); // batches 0 and 1: not the transaction's batch 2
            Assert.Contains("WROTE", output);
        }

        // With buffered durability the batch written before the transaction was still in memory; the failed
        // commit's files used to come back at the next flush, and the rollback's too.
        [Fact]
        public void A_failed_commit_with_buffered_writes_is_not_written_later()
        {
            string dir = CrashDb.New(tempRoot, "buffered-commit");
            var run = XodCrashSafetyTests.RunWriterProcess("txwrite-commit", dir, Buffered, ("XOD_FAIL_AT_STEP", "commit-written"));
            FailedAndCarriedOn(run.Output);
            CrashDb.HoldsBatches(dir, "first", 3);
        }

        [Fact]
        public void A_rollback_with_buffered_writes_is_not_written_later()
        {
            string dir = CrashDb.New(tempRoot, "buffered-rollback");
            var run = XodCrashSafetyTests.RunWriterProcess("txwrite-rollback", dir, Buffered);
            Assert.Contains("ROLLED BACK", run.Output);
            Assert.Contains("COUNT 50", run.Output);
            Assert.Contains("WROTE", run.Output);
            CrashDb.HoldsBatches(dir, "first", 3);
        }

        // A journal that can't be written: nothing of the batch reached the disk, and the context used to keep the
        // batch open for good, showing the commit and losing every later write.
        [Fact]
        public void A_commit_whose_journal_cannot_be_written_is_undone()
        {
            string dir = CrashDb.New(tempRoot, "journal-fails");
            var run = XodCrashSafetyTests.RunWriterProcess("txwrite-commit", dir, ("XOD_FAIL_AT_STEP", "journal-writing"));
            FailedAndCarriedOn(run.Output);
            CrashDb.HoldsBatches(dir, "first", 3);
        }

        // A commit that fails after writing its indexes: they hold its rows, so the undo drops them (rebuilt from
        // the pages); otherwise the next insert of those keys is refused, and lookups find rows that aren't there.
        [Fact]
        public void A_commit_that_fails_after_its_indexes_drops_them()
        {
            string dir = CrashDb.New(tempRoot, "indexed-fails");
            var run = XodCrashSafetyTests.RunWriterProcess("txwrite-commit", dir, ("XOD_FAIL_AT_STEP", "commit-indexed"));
            FailedAndCarriedOn(run.Output);
            CrashDb.HoldsBatches(dir, "first", 3);
        }

        // The same, checked on the files: the undo deletes every index file, since the failed commit wrote them
        // after its journal (the staleness check at load, ISS-043, can't be relied on to catch them).
        [Fact]
        public void A_commit_that_fails_after_its_indexes_leaves_no_index_file()
        {
            string dir = CrashDb.New(tempRoot, "indexed-fails-files");
            Assert.NotEmpty(Directory.GetFiles(dir, "*.xidx"));
            var run = XodCrashSafetyTests.RunWriterProcess("txcommit", dir, ("XOD_FAIL_AT_STEP", "commit-indexed"));
            Assert.Contains("COMMIT FAILED", run.Output);
            Assert.Empty(Directory.GetFiles(dir, "*.xidx"));
            CrashDb.HoldsBatches(dir, "first", 1);
        }

        // With buffered durability under a password, the commit saves its indexes into the buffer (after its batch):
        // the undo forgets them, or they would be written later, holding the rows of a commit the disk doesn't hold.
        [Fact]
        public void A_commit_that_fails_after_buffering_its_indexes_does_not_write_them_later()
        {
            string dir = CrashDb.New(tempRoot, "indexed-fails-buffered");
            var run = XodCrashSafetyTests.RunWriterProcess("txcommit", dir, Buffered, ("XOD_TEST_SINGLE", "1"), ("XOD_FAIL_AT_STEP", "commit-indexed"));
            Assert.Contains("COMMIT FAILED", run.Output);
            Assert.Empty(Directory.GetFiles(dir, "*.xidx"));
            CrashDb.HoldsBatches(dir, "first", 1);
        }

        // A context whose close fails (its last flush) still lets go of the database: the next open in the same
        // process used to be refused until the garbage collector ran (with LockDatabase then; every open holds it now).
        [Fact]
        public void A_close_that_fails_lets_go_of_the_database()
        {
            string dir = CrashDb.New(tempRoot, "close-fails");
            var run = XodCrashSafetyTests.RunWriterProcess("txwrite-commit", dir, Buffered, ("XOD_FAIL_AT_STEP", "closing"));
            Assert.Contains("DISPOSE FAILED", run.Output);
            Assert.Contains("REOPENED", run.Output);
        }

        // ---- dropping the cache inside a transaction ----

        // ClearCache inside a transaction used to drop the transaction's pages that waited for the commit (and its
        // index changes), so the commit wrote only part of it.
        [Fact]
        public void Clearing_the_cache_inside_a_transaction_keeps_its_changes()
        {
            string dir = Path.Combine(tempRoot, "clear-in-tx");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "app.xod");
            using (var db = new XodContext(path))
            {
                db.Insert(new NumberedItem { Name = "before" });
                using (var tx = db.BeginTransaction())
                {
                    for (int i = 0; i < 50; i++)
                        db.Insert(new NumberedItem { Name = "in " + i });
                    db.ClearCache();
                    db.Insert(new NumberedItem { Name = "after the clear" });
                    tx.Commit();
                }
                Assert.Equal(52, db.Count<NumberedItem>());
            }

            using (var db = new XodContext(path))
            {
                Assert.Equal(52, db.Count<NumberedItem>());
                for (int id = 1; id <= 52; id++)
                    Assert.True(db.Find<NumberedItem>(x => x.Id == id) != null, "item " + id + " not found");
            }
        }

        // A journal cut short before its rename means a commit that hadn't started: the next open applies none of its
        // before-images and deletes it. (With one context at a time nothing writes between the crash and that open;
        // the case of another context writing meanwhile went with PLAN-064.)
        [Fact]
        public void A_journal_that_was_never_renamed_into_place_is_dropped_by_the_next_open()
        {
            string dir = CrashDb.New(tempRoot, "staged-journal");
            var crash = XodCrashSafetyTests.RunWriterProcess("txwrite-commit", dir, ("XOD_CRASH_AT_STEP", "journal-staged"));
            Assert.DoesNotContain("COMMITTED", crash.Output);
            Assert.True(File.Exists(Path.Combine(dir, "transaction.xwal.tmp")));

            // Batch 1, written outside the transaction before the crash; the transaction's batch never reached the pages.
            CrashDb.HoldsBatches(dir, "first", 2);
            Assert.False(File.Exists(Path.Combine(dir, "transaction.xwal.tmp")));
            Assert.False(File.Exists(Path.Combine(dir, "transaction.xwal")));
        }

        // A commit that drops a table and makes it again deletes the old pages and writes new ones; failed once its
        // files are written, its undo puts the deleted pages back, and the old table is there again for the context
        // and the next open.
        [Fact]
        public void A_failed_commit_puts_back_the_pages_it_deleted()
        {
            string dir = Path.Combine(tempRoot, "drop-fail");
            Directory.CreateDirectory(dir);
            var run = XodCrashSafetyTests.RunWriterProcess("txdrop-fail", dir, ("XOD_FAIL_AT_STEP", "commit-written"));
            Assert.Contains("COMMIT FAILED", run.Output);
            Assert.DoesNotContain("couldn't be undone", run.Output);
            Assert.Contains("SEEN 1:note 1,2:note 2,3:note 3", run.Output);
            Assert.Contains("REOPENED 1:note 1,2:note 2,3:note 3", run.Output);
            Assert.False(File.Exists(Path.Combine(dir, "transaction.xwal")), run.Output);
        }

        // A commit whose page another program holds (a virus scanner, a backup tool) fails on that page and is undone.
        // The undo writes back only what the commit wrote: the page it never replaced is left alone, so the undo
        // doesn't fail on it too, the context goes on writing, and no journal is left. It used to write every page
        // the transaction changed back, and on the held one failed and refused every write until the next open.
        // Windows only: elsewhere a file another process opens doesn't keep a rename out.
        [Theory]
        [InlineData(FileShare.None)]
        [InlineData(FileShare.Read)]
        public void A_commit_that_fails_on_a_page_another_program_holds_is_undone_and_the_context_carries_on(FileShare share)
        {
            if (!OperatingSystem.IsWindows())
                return;
            string dir = Path.Combine(tempRoot, "held-" + share);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "notes.xod");
            var options = new DatabaseOptions { InitialCreate = true };
            using (var db = new XodContext(path, options: options))
            {
                for (int id = 1; id <= 3; id++)
                    db.Insert(new HeldPageNote { Id = id, Text = "note " + id });
            }

            using (var db = new XodContext(path, options: options))
            {
                using (var tx = db.BeginTransaction())
                {
                    var note = db.Find<HeldPageNote>(n => n.Id == 1);
                    note.Text = "from the transaction";
                    Assert.True(db.Update(note));
                    string page = Assert.Single(Directory.GetFiles(dir, "*.xpag"));
                    using (new FileStream(page, FileMode.Open, FileAccess.Read, share))
                    {
                        var e = Assert.ThrowsAny<Exception>(() => tx.Commit());
                        Assert.DoesNotContain("couldn't be undone", e.Message);
                    }
                }
                Assert.False(File.Exists(Path.Combine(dir, "transaction.xwal")));
                Assert.Equal("note 1", db.Find<HeldPageNote>(n => n.Id == 1).Text);
                db.Insert(new HeldPageNote { Id = 4, Text = "after" });
            }

            using var reopened = new XodContext(path, options: options);
            Assert.Equal("note 1", reopened.Find<HeldPageNote>(n => n.Id == 1).Text);
            Assert.Equal(4, reopened.Count<HeldPageNote>());
        }
    }

    public class HeldPageNote
    {
        [PrimaryKey]
        public int Id { get; set; }
        public string Text { get; set; }
    }
}
