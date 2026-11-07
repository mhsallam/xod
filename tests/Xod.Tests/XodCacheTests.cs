using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class CacheRow
    {
        public int Id { get; set; }
        public int Bucket { get; set; }
        public string Payload { get; set; }
    }

    /// <summary>
    /// The parsed-page cache is bounded by bytes as well as by entry count, so pages are evicted and
    /// reloaded constantly on a database larger than the budget. These tests make that happen on
    /// purpose and then check the data is still what was written: an eviction that loses a pending
    /// write or serves a stale row is a data bug, not a performance detail.
    /// </summary>
    public class XodCacheTests : IDisposable
    {
        private readonly string tempRoot;

        public XodCacheTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodCache_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        private string GetDbPath(string name) => Path.Combine(tempRoot, name, "data.xod");

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        [InlineData(StorageFormat.Binary)]
        public void Evicted_Pages_Reload_To_The_Same_Data(StorageFormat format)
        {
            string dbPath = GetDbPath("evict_" + format);
            const int rows = 20_000;
            string payload = new string('x', 256);

            using (var db = new XodContext(dbPath, options: new DatabaseOptions { StorageFormat = format, PageSize = 4096 }))
            {
                db.InsertRange(Enumerable.Range(1, rows).Select(i => new CacheRow { Id = i, Bucket = i % 50, Payload = $"{i:D6}:{payload}" }));
                Assert.Equal(rows, db.Count<CacheRow>());
            }

            // Walk the table far past any cache budget, then mutate rows the first pass has evicted.
            using (var db = new XodContext(dbPath, options: new DatabaseOptions { StorageFormat = format, PageSize = 4096 }))
            {
                int seen = 0;
                foreach (var row in db.Select<CacheRow>())
                {
                    Assert.Equal($"{row.Id:D6}:{payload}", row.Payload);
                    seen++;
                }
                Assert.Equal(rows, seen);

                foreach (int id in new[] { 1, 999, 5_000, 10_000, 19_999 })
                {
                    var row = db.Find<CacheRow>(r => r.Id == id);
                    row.Bucket = 999;
                    Assert.True(db.Update(row));
                }

                foreach (int id in new[] { 2, 1_001, 10_001 })
                    Assert.True(db.Delete(new CacheRow { Id = id }));
            }

            using (var db = new XodContext(dbPath, options: new DatabaseOptions { StorageFormat = format, PageSize = 4096 }))
            {
                Assert.Equal(rows - 3, db.Count<CacheRow>());
                foreach (int id in new[] { 1, 999, 5_000, 10_000, 19_999 })
                    Assert.Equal(999, db.Find<CacheRow>(r => r.Id == id).Bucket);
                foreach (int id in new[] { 2, 1_001, 10_001 })
                    Assert.Null(db.Find<CacheRow>(r => r.Id == id));
                Assert.Equal(7, db.Find<CacheRow>(r => r.Id == 7).Bucket);
            }
        }
    }
}
