using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class VolumeRow
    {
        [PrimaryKey, Property(AutoNumber = true)] public int Id { get; set; }
        [UniqueKey] public string Sku { get; set; }
        [Property(Indexed = true)] public int Bucket { get; set; }
        public double Amount { get; set; }
        public string Payload { get; set; }
    }

    /// <summary>
    /// Gate layer 8 of TEST-PLAN-2.0: the engine must survive volume - page splits, a large delete, a
    /// cold reopen - not just thousands of rows. The workload is 1,000,000 rows of ~1 KB payload (about
    /// 1 GB on disk), then 200,000 deletes, then a reopen with counts and spot checks.
    ///
    /// It is heavy (tens of seconds and about a gigabyte of temp space), so it only runs when
    /// XOD_VOLUME_TEST=1 is set. The gate evidence is the run recorded in TEST-PLAN-2.0; the test is the
    /// guard that can be repeated.
    /// </summary>
    public class XodVolumeTests : IDisposable
    {
        private const int Rows = 1_000_000;
        private const int Deletes = 200_000;
        private const int Batch = 10_000;

        private readonly string tempRoot;

        public XodVolumeTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodVolume_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private static VolumeRow MakeRow(int i, string payload) => new()
        {
            Sku = $"SKU-{i:D8}",
            Bucket = i % 1_000,
            Amount = (i % 500) * 1.25,
            Payload = payload,
        };

        [Fact]
        public void One_million_rows_survive_page_splits_a_large_delete_and_a_cold_reopen()
        {
            if (Environment.GetEnvironmentVariable("XOD_VOLUME_TEST") != "1")
                return;

            string dir = Path.Combine(tempRoot, "volume");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "volume.xod");
            string payload = new string('x', 1_000);

            var options = new DatabaseOptions
            {
                InitialCreate = true,
                EnableIndexing = true,
                StorageFormat = StorageFormat.Xml,
                Durability = DurabilityMode.Buffered,
            };

            var insert = Stopwatch.StartNew();
            using (var db = new XodContext(path, options: options))
            {
                for (int start = 0; start < Rows; start += Batch)
                {
                    var chunk = new List<VolumeRow>(Batch);
                    for (int i = start; i < start + Batch; i++)
                        chunk.Add(MakeRow(i + 1, payload));
                    db.InsertRange(chunk);
                }

                db.Flush();
            }

            insert.Stop();

            long bytes = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Assert.True(bytes > 900L * 1024 * 1024, $"database is only {bytes / (1024 * 1024)} MB");

            var rng = new Random(11);
            using (var db = new XodContext(path, options: options))
            {
                Assert.Equal(Rows, db.Count<VolumeRow>());

                for (int n = 0; n < 200; n++)
                {
                    int id = rng.Next(1, Rows + 1);
                    var row = db.Find<VolumeRow>(r => r.Id == id);
                    Assert.NotNull(row);
                    Assert.Equal(1_000, row.Payload.Length);
                }

                // a page split that lost index entries would show up here: 20 indexed queries over
                // the whole file, each expecting the exact bucket size
                var expectedPerBucket = Rows / 1_000;
                for (int b = 0; b < 20; b++)
                {
                    int bucket = b * 37 % 1_000;
                    int count = db.Query<VolumeRow>(r => r.Bucket == bucket).Count();
                    Assert.Equal(expectedPerBucket, count);
                }

                var delete = Stopwatch.StartNew();
                for (int start = 1; start <= Deletes; start += Batch)
                {
                    for (int id = start; id < start + Batch; id++)
                        db.Delete(new VolumeRow { Id = id });
                    db.Flush();
                }

                delete.Stop();
                Assert.Equal(Rows - Deletes, db.Count<VolumeRow>());
            }

            using (var reopened = new XodContext(path, options: options))
            {
                Assert.Equal(Rows - Deletes, reopened.Count<VolumeRow>());
                Assert.Null(reopened.Find<VolumeRow>(r => r.Id == 1));
                Assert.NotNull(reopened.Find<VolumeRow>(r => r.Id == Deletes + 1));
                Assert.NotNull(reopened.Find<VolumeRow>(r => r.Id == Rows));
            }
        }
    }
}
