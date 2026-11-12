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

        // A context whose close fails (its last flush) still lets go of the database: with LockDatabase, the next
        // open in the same process used to be refused until the garbage collector ran.
        [Fact]
        public void A_close_that_fails_lets_go_of_the_database()
        {
            string dir = CrashDb.New(tempRoot, "close-fails");
            var run = XodCrashSafetyTests.RunWriterProcess("txwrite-commit", dir, Buffered, ("XOD_TEST_LOCK", "1"), ("XOD_FAIL_AT_STEP", "closing"));
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

        // ---- the journal of a commit that didn't finish, while other contexts have the database open ----

        // A dead journal waits for an open that has the database to itself. A commit in the meantime used to write
        // its own journal over it and delete it, so the half-done commit stayed for good.
        [Fact]
        public void A_commit_is_refused_while_another_commit_s_journal_is_there()
        {
            string dir = CrashDb.New(tempRoot, "dead-journal");
            string journal = Path.Combine(dir, "transaction.xwal");
            using (var other = new XodContext(Path.Combine(dir, "pw.xod"), "first"))
            {
                var crash = XodCrashSafetyTests.RunWriterProcess("txwrite-commit", dir, ("XOD_CRASH_AT_STEP", "commit-written"));
                Assert.DoesNotContain("COMMITTED", crash.Output);
                Assert.True(File.Exists(journal));
                byte[] dead = File.ReadAllBytes(journal);

                var next = XodCrashSafetyTests.RunWriterProcess("txcommit", dir, ("XOD_TEST_BATCH", "3"));
                Assert.Contains("COMMIT FAILED TransactionException", next.Output);
                Assert.Equal(dead, File.ReadAllBytes(journal));
            }

            // Alone, the open undoes the dead commit: batch 0, and batch 1 the crashed process wrote outside it.
            CrashDb.HoldsBatches(dir, "first", 2);
            Assert.False(File.Exists(journal));
        }

        // A journal cut short before its rename means a commit that hadn't started. With others open it isn't
        // recovered at once; the open that later recovers used to make it the journal and write its before-images
        // back over what the others wrote since.
        [Fact]
        public void A_journal_that_was_never_renamed_into_place_is_not_applied()
        {
            string dir = CrashDb.New(tempRoot, "staged-journal");
            using (var other = new XodContext(Path.Combine(dir, "pw.xod"), "first"))
            {
                var crash = XodCrashSafetyTests.RunWriterProcess("txwrite-commit", dir, ("XOD_CRASH_AT_STEP", "journal-staged"));
                Assert.DoesNotContain("COMMITTED", crash.Output);
                Assert.True(File.Exists(Path.Combine(dir, "transaction.xwal.tmp")));

                // Batch 1, which the crashed process wrote outside its transaction, and then a customer of this one.
                Assert.Equal(50, other.Count<CrashCustomer>());
                other.Insert(new CrashCustomer { Id = 1000, Name = "written after the crash" });
            }

            using (var db = new XodContext(Path.Combine(dir, "pw.xod"), "first"))
            {
                Assert.NotNull(db.Find<CrashCustomer>(c => c.Id == 1000));
                Assert.Equal(51, db.Count<CrashCustomer>());
            }
            Assert.False(File.Exists(Path.Combine(dir, "transaction.xwal.tmp")));
        }
    }
}
