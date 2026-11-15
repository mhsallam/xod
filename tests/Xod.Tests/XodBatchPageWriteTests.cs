// ISS-129: a page that filled during a batch outside a transaction was written by a task of its own, from the page's
// pooled bytes, while the batch went on. With indexing off every insert's key check opens pages as documents, which
// gives a page's bytes back to the pool (IOService.OpenFileOrCreate); the next new page rented them and wrote its rows
// into them, and the task wrote that: the InsertRange returned success and the table no longer opened. A page now
// waits with the rest of the batch and is written at its end by the batch's one writer (IOService.FlushPending),
// under the lock every release of page bytes takes. A full page kept as a document (an encrypted database) is still
// written early (IOService.FlushFullPageInBatch, PLAN-019's memory bound), by this context, under the lock, from the
// document it owns; the encrypted case covers that path, but can't tell the old engine from this one.
//
// The unindexed case was red against the unfixed engine in every run here (8 of 8 in this suite, and 24 of 24 fresh
// processes by the review of the fix), though the overall review once saw 36 runs pass: the race depends on timing.
// Held until the batch's writer was done, the old background write broke the table in every run (PLAN-061 section 5).
// What keeps it from coming back is that page bytes have one writer, which no test can prove by itself.
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Xod.Tests
{
    public class BatchPageRow
    {
        [PrimaryKey] public int Id { get; set; }
        public string Text { get; set; }
    }

    public class XodBatchPageWriteTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-batch-page-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private static string TextOf(int id) => new string((char)('a' + id % 26), 5000) + id;

        // The review's probe P9: 400 rows, each larger than a 4 KB page, so each fills a page of its own.
        [Theory]
        [InlineData(false, null)]
        [InlineData(true, null)]
        [InlineData(false, "batch-pass")]
        public void An_insert_range_that_fills_pages_writes_each_page_with_its_own_rows(bool indexing, string password)
        {
            string dir = Path.Combine(tempRoot, (indexing ? "indexed" : "unindexed") + (password == null ? "" : "-encrypted"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "p9.xod");
            var rows = Enumerable.Range(1, 400).Select(i => new BatchPageRow { Id = i, Text = TextOf(i) }).ToList();

            using (var db = new XodContext(path, password, new DatabaseOptions { InitialCreate = true, EnableIndexing = indexing, PageSize = 4096 }))
                db.InsertRange(rows);

            using (var db = new XodContext(path, password, new DatabaseOptions { EnableIndexing = indexing, PageSize = 4096 }))
            {
                var back = db.Select<BatchPageRow>().OrderBy(r => r.Id).ToList();
                Assert.Equal(Enumerable.Range(1, 400), back.Select(r => r.Id));
                Assert.All(back, r => Assert.Equal(TextOf(r.Id), r.Text));
            }
        }
    }
}
