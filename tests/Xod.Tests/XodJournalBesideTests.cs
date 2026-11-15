using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-147: a commit's journal (<c>transaction.xwal</c>) is undone, by the next open that has the database to itself,
    /// whenever its commit didn't finish. While it is in the folder, anything another context writes there would be
    /// undone with it, after that context was told the write was made. So a context writes nothing beside another's
    /// journal: it waits for a commit under way (its context holds the journal open) and refuses beside one that won't
    /// finish (nobody holds it: cut short by a crash, or failed and not undone).
    /// </summary>
    public class XodJournalBesideTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-journal-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private static CrashCustomer Customer(string name) => new CrashCustomer { Name = name, Orders = new List<CrashOrder>() };

        private static string ReadUntil(Process writer, string prefix)
        {
            string line;
            while ((line = writer.StandardOutput.ReadLine()) != null && !line.StartsWith(prefix, StringComparison.Ordinal)) { }
            Assert.NotNull(line);
            return line;
        }

        private static string Resume(Process writer)
        {
            writer.StandardInput.WriteLine();
            writer.StandardInput.Flush();
            string rest = XodCrashSafetyTests.FinishWriter(writer);
            return rest;
        }

        // The review's probe (journal-undo): a context open while another's commit is cut short by a crash went on
        // inserting, closed without error, and the next open alone undid its rows with the commit.
        [Fact]
        public void A_write_beside_a_commit_a_crash_cut_short_is_refused_and_nothing_acknowledged_is_undone()
        {
            string dir = CrashDb.New(tempRoot, "beside-crashed");
            string journal = Path.Combine(dir, "transaction.xwal");
            using (var other = new XodContext(Path.Combine(dir, "pw.xod"), "first"))
            {
                var crash = XodCrashSafetyTests.RunWriterProcess("txcommit", dir, ("XOD_CRASH_AT_STEP", "commit-written"));
                Assert.DoesNotContain("COMMITTED", crash.Output);
                Assert.True(File.Exists(journal));

                var refused = Assert.ThrowsAny<XodException>(() => other.Insert(Customer("beside")));
                Assert.Contains("transaction.xwal", refused.Message);
                Assert.Contains("didn't finish", refused.Message);
                Assert.True(File.Exists(journal), "the journal went: " + refused.Message);
            }

            // The next open alone undoes the commit: the database is as it was before it, and takes writes again.
            CrashDb.HoldsBatches(dir, "first", 1);
            using (var db = new XodContext(Path.Combine(dir, "pw.xod"), "first"))
            {
                db.Insert(Customer("after"));
                Assert.Equal(26, db.Count<CrashCustomer>());
            }
        }

        // Reads go on beside a journal that won't finish. The crash also leaves the indexes untrusted, so the first
        // query by key rebuilds an index and saves it: an index file, which a journal never names and its undo drops,
        // so its save isn't a write the undo could take back (review of the ISS-147 fix: the query threw).
        [Fact]
        public void Reads_go_on_beside_a_commit_a_crash_cut_short()
        {
            string dir = CrashDb.New(tempRoot, "read-beside-crashed");
            using var other = new XodContext(Path.Combine(dir, "pw.xod"), "first");
            var crash = XodCrashSafetyTests.RunWriterProcess("txcommit", dir, ("XOD_CRASH_AT_STEP", "commit-written"));
            Assert.DoesNotContain("COMMITTED", crash.Output);
            Assert.True(File.Exists(Path.Combine(dir, "transaction.xwal")));

            Assert.NotNull(other.Find<CrashCustomer>(c => c.Id == 7));
            Assert.NotNull(other.Find<CrashOrder>(o => o.Id == 20));
        }

        // ISS-147's named debt, pinned: a buffered context's writes, acknowledged before another process's commit was
        // cut short, are flushed only at its close, which is then beside a journal that won't finish: the flush is
        // refused and the close says so, rather than writing what that journal's undo would take back.
        [Fact]
        public void A_buffered_close_beside_a_commit_a_crash_cut_short_refuses_its_unflushed_writes()
        {
            string dir = CrashDb.New(tempRoot, "buffered-beside-crashed");
            var other = new XodContext(Path.Combine(dir, "pw.xod"), "first", new DatabaseOptions { Durability = DurabilityMode.Buffered });
            other.Insert(new CrashMember { Id = 1, Email = "buffered" });

            var crash = XodCrashSafetyTests.RunWriterProcess("txcommit", dir, ("XOD_CRASH_AT_STEP", "commit-written"));
            Assert.DoesNotContain("COMMITTED", crash.Output);

            var refused = Assert.ThrowsAny<Exception>(() => other.Dispose());
            Assert.Contains("didn't finish", refused.ToString());
        }

        // A commit under way in another context: its journal is held, so a write beside it waits for it, for all of
        // TransactionTimeoutMs, and is refused as another's commit under way when it doesn't end in time; once the
        // commit has ended, a write goes in.
        [Fact]
        public void A_write_beside_another_context_s_commit_under_way_waits_for_it()
        {
            string dir = CrashDb.New(tempRoot, "beside-live");
            string path = Path.Combine(dir, "pw.xod");
            using var writer = XodCrashSafetyTests.StartWriterProcess("txcommit", dir, ("XOD_PAUSE_AT_STEP", "commit-written"));
            ReadUntil(writer, "PAUSED");
            Assert.True(File.Exists(Path.Combine(dir, "transaction.xwal")));

            // The context is opened before the commit ends, and writes after: another table than the commit's, so its
            // counters and cache, read before the commit ended, are still the disk's.
            using (var later = new XodContext(path, "first"))
            {
                using (var impatient = new XodContext(path, "first", new DatabaseOptions { TransactionTimeoutMs = 300 }))
                {
                    var waited = Stopwatch.StartNew();
                    var refused = Assert.ThrowsAny<XodException>(() => impatient.Insert(new CrashMember { Id = 1, Email = "impatient" }));
                    waited.Stop();
                    Assert.Contains("under way", refused.Message);
                    Assert.True(waited.ElapsedMilliseconds >= 300, "refused after " + waited.ElapsedMilliseconds + " ms, before its wait ran out");
                }

                Assert.Contains("COMMITTED", Resume(writer));
                later.Insert(new CrashMember { Id = 2, Email = "patient" });
            }

            CrashDb.HoldsBatches(dir, "first", 2);
            using var db = new XodContext(path, "first");
            Assert.NotNull(db.Find<CrashMember>(m => m.Email == "patient"));
            Assert.Null(db.Find<CrashMember>(m => m.Email == "impatient"));
        }

        // A commit that failed and couldn't be undone, in a process still running: its context writes nothing more,
        // and lets go of the journal, so the other contexts read it as one that won't finish rather than waiting for it.
        [Fact]
        public void A_commit_that_failed_and_wasn_t_undone_lets_go_of_its_journal()
        {
            string dir = CrashDb.New(tempRoot, "beside-faulted");
            using var writer = XodCrashSafetyTests.StartWriterProcess("txfail-hold", dir, ("XOD_FAIL_AT_STEP", "commit-written,undo"));
            ReadUntil(writer, "WAITING");
            Assert.True(File.Exists(Path.Combine(dir, "transaction.xwal")));

            using (var other = new XodContext(Path.Combine(dir, "pw.xod"), "first", new DatabaseOptions { TransactionTimeoutMs = 5000 }))
            {
                var refused = Assert.ThrowsAny<XodException>(() => other.Insert(new CrashMember { Id = 1, Email = "beside" }));
                Assert.Contains("didn't finish", refused.Message);
            }

            Resume(writer);
            CrashDb.HoldsBatches(dir, "first", 1);
        }
    }
}
