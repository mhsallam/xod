// ISS-098: a page write of a batch or of the buffer (DurabilityMode.Buffered) that failed left the set of writes
// still to make before it was made, so the next flush wrote nothing and the close called the context's writes
// done: the disk never got them, though the context went on showing them, and its indexes, saved, named rows the
// pages didn't hold. A write now stays to be made until it succeeds: the next flush (Flush, the next transaction,
// the close) makes it, or a rollback drops it.
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Xod.Tests
{
    public class FailedWriteItem
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class XodFailedWriteTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-failed-write-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string NewDatabase(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "data.xod");
        }

        // Read by a scan of the pages: an index may name rows the pages don't hold.
        private static string[] NamesOnThePages(string path)
        {
            using var db = new XodContext(path);
            return db.QueryDelegate<FailedWriteItem>(i => i.Name != null).Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        private static FileStream HoldPage(string path)
        {
            string page = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag"));
            return new FileStream(page, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        [Fact]
        public void A_buffered_write_that_failed_is_made_by_the_next_flush()
        {
            string path = NewDatabase("buffered");
            var options = new DatabaseOptions { InitialCreate = true, Durability = DurabilityMode.Buffered };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new FailedWriteItem { Name = "first" });
                db.Flush();
                db.Insert(new FailedWriteItem { Name = "second" });
                Exception failure;
                using (HoldPage(path))
                    failure = Record.Exception(() => db.Flush());
                if (OperatingSystem.IsWindows())
                    Assert.NotNull(failure); // the page held open fails the write there
                db.Flush();
            }
            Assert.Equal(new[] { "first", "second" }, NamesOnThePages(path));
        }

        [Fact]
        public void A_range_whose_page_write_failed_is_made_by_the_next_flush()
        {
            string path = NewDatabase("range");
            using (var db = new XodContext(path, options: new DatabaseOptions { InitialCreate = true }))
            {
                db.Insert(new FailedWriteItem { Name = "a" });
                Exception failure;
                using (HoldPage(path))
                {
                    failure = Record.Exception(() => db.InsertRange(new[] { "b", "c", "d" }
                        .Select(n => new FailedWriteItem { Name = n }).ToList()));
                }
                if (OperatingSystem.IsWindows())
                    Assert.NotNull(failure);
                // The context shows what it was told to write, and the disk gets it.
                Assert.Equal(4, db.QueryDelegate<FailedWriteItem>(i => i.Name != null).Count());
                db.Flush();
            }
            Assert.Equal(new[] { "a", "b", "c", "d" }, NamesOnThePages(path));
        }

        [Fact]
        public void A_buffered_write_that_failed_is_made_by_the_close()
        {
            string path = NewDatabase("buffered-close");
            var options = new DatabaseOptions { InitialCreate = true, Durability = DurabilityMode.Buffered };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new FailedWriteItem { Name = "first" });
                db.Flush();
                db.Insert(new FailedWriteItem { Name = "second" });
                using (HoldPage(path))
                    _ = Record.Exception(() => db.Flush());
            }
            Assert.Equal(new[] { "first", "second" }, NamesOnThePages(path));
        }

        // A page that fills during a batch is written on its own while the batch goes on, and counted as written
        // when its write began: one whose write failed was never written again.
        [Fact]
        public void A_full_page_a_range_failed_to_write_is_made_by_the_next_flush()
        {
            string path = NewDatabase("full-page");
            var options = new DatabaseOptions { InitialCreate = true, PageSize = 2048 };
            using (var db = new XodContext(path, options: options))
            {
                db.Insert(new FailedWriteItem { Name = "item 000" });
                Exception failure;
                using (HoldPage(path))
                {
                    failure = Record.Exception(() => db.InsertRange(Enumerable.Range(1, 100)
                        .Select(i => new FailedWriteItem { Name = "item " + i.ToString("D3") }).ToList()));
                }
                if (OperatingSystem.IsWindows())
                    Assert.NotNull(failure);
                db.Flush();
            }
            Assert.Equal(Enumerable.Range(0, 101).Select(i => "item " + i.ToString("D3")), NamesOnThePages(path));
        }
    }
}
