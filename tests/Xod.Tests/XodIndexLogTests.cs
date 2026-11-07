using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class IndexLogRow
    {
        [PrimaryKey, Property(AutoNumber = true)] public int Id { get; set; }
        [UniqueKey] public string Sku { get; set; }
        [Property(Indexed = true)] public int Bucket { get; set; }
        public string Payload { get; set; }
    }

    public class IndexLogKeylessRow
    {
        [UniqueKey] public string Code { get; set; }
        [Property(Indexed = true)] public int Bucket { get; set; }
        public string Payload { get; set; }
    }

    /// <summary>
    /// The .xidx file is a snapshot followed by a log of delta records. Appending to the log is cheap; a
    /// snapshot costs the size of the whole index. These tests pin the policy that keeps an insert's cost
    /// independent of the table's size: one flush appends however many deltas it carries, the log is folded
    /// into a new snapshot only once it would outgrow the snapshot it extends, and a clean close folds a
    /// log larger than the floor so that the next open does not replay it.
    /// </summary>
    public class XodIndexLogTests : IDisposable
    {
        // IndexService.DeltaLog.FloorBytes: a log under this size is never worth a snapshot.
        private const long LogFloorBytes = 256 * 1024;

        private static readonly byte[] Xidx2Header = Encoding.UTF8.GetBytes("XIDX2:<TableIndex>\n");

        private readonly string tempRoot;

        public XodIndexLogTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodIndexLog_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string DbPath(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "data.xod");
        }

        private static string IndexPath(string dbPath) =>
            Path.Combine(Path.GetDirectoryName(dbPath)!, typeof(IndexLogRow).FullName + ".xidx");

        private static IndexLogRow Row(int i) => new()
        {
            Sku = $"SKU-{i:D8}",
            Bucket = i % 50,
            Payload = "p" + i,
        };

        private static List<IndexLogRow> Rows(int start, int count) =>
            Enumerable.Range(start, count).Select(Row).ToList();

        /// <summary>Reads where the snapshot of a framed (-3) .xidx file ends; the rest is the delta log.</summary>
        private static (long SnapshotEnd, long Length) ReadLayout(string xidxPath)
        {
            byte[] bytes = File.ReadAllBytes(xidxPath);
            using var ms = new MemoryStream(bytes);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            Assert.True(r.ReadBytes(Xidx2Header.Length).AsSpan().SequenceEqual(Xidx2Header));
            Assert.Equal(-3, r.ReadInt32()); // framed snapshot whose log flushes are closed
            int props = r.ReadInt32();
            for (int p = 0; p < props; p++)
            {
                r.ReadString();          // property name
                r.ReadInt32();           // entry count
                r.ReadString();          // min key
                r.ReadString();          // max key
                int blockLength = r.ReadInt32();
                ms.Position += blockLength;
            }
            return (ms.Position, bytes.Length);
        }

        /// <summary>Byte offsets where each delta record after the snapshot starts.</summary>
        private static List<long> RecordStarts(string xidxPath)
        {
            long snapshotEnd = ReadLayout(xidxPath).SnapshotEnd;
            var starts = new List<long>();
            using var fs = File.OpenRead(xidxPath);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            fs.Position = snapshotEnd;
            while (fs.Position < fs.Length)
            {
                starts.Add(fs.Position);
                r.ReadByte();
                r.ReadString();
                r.ReadString();
                r.ReadString();
            }
            return starts;
        }

        // Files written before flush-end records existed (marker -2) cannot tell a cut log from a whole
        // one, so nothing is appended to them: their first save writes a snapshot in the new layout.
        [Fact]
        public void An_index_file_from_before_flush_end_records_is_rewritten_before_it_is_appended_to()
        {
            string path = DbPath("legacy-marker");
            string xidx = IndexPath(path);
            using (var db = new XodContext(path))
                db.InsertRange(Rows(0, 5_000));

            int markerAt = Xidx2Header.Length;
            byte[] bytes = File.ReadAllBytes(xidx);
            BitConverter.GetBytes(-2).CopyTo(bytes, markerAt); // the layouts are otherwise identical
            File.WriteAllBytes(xidx, bytes);

            using (var db = new XodContext(path))
                db.InsertRange(Rows(5_000, 1_500));

            var layout = ReadLayout(xidx); // asserts marker -3
            Assert.Equal(layout.Length, layout.SnapshotEnd);

            using (var db = new XodContext(path))
                Assert.NotNull(db.Find<IndexLogRow>(r => r.Sku == "SKU-00006499"));
        }

        // A crash during an append can cut the log anywhere, including exactly between two records; the
        // cut-off rows are in the pages but not in the index.
        [Fact]
        public void A_log_cut_between_two_records_is_not_mistaken_for_a_whole_one()
        {
            string path = DbPath("torn");
            string xidx = IndexPath(path);
            using (var db = new XodContext(path))
            {
                db.InsertRange(Rows(0, 5_000));
                db.InsertRange(Rows(5_000, 1_500));
            }

            var starts = RecordStarts(xidx);
            using (var fs = new FileStream(xidx, FileMode.Open, FileAccess.Write))
                fs.SetLength(starts[starts.Count - 31]); // the last ten rows' records, and anything after

            using (var db = new XodContext(path))
            {
                for (int i = 6_480; i < 6_500; i++)
                    Assert.NotNull(db.Find<IndexLogRow>(r => r.Sku == $"SKU-{i:D8}"));
                Assert.Throws<ReservedUniqueKeyException>(() => db.Insert(Row(6_499)));
            }
        }

        [Fact]
        public void A_flush_of_many_deltas_extends_the_log_instead_of_rewriting_the_snapshot()
        {
            string path = DbPath("append");
            string xidx = IndexPath(path);

            using (var db = new XodContext(path))
            {
                db.InsertRange(Rows(0, 5_000));
                byte[] before = File.ReadAllBytes(xidx);
                Assert.Equal(before.Length, ReadLayout(xidx).SnapshotEnd);

                // 1,500 rows x 3 indexed properties = 4,500 deltas in one flush.
                db.InsertRange(Rows(5_000, 1_500));
                byte[] after = File.ReadAllBytes(xidx);

                Assert.True(after.Length > before.Length);
                Assert.True(after.AsSpan(0, before.Length).SequenceEqual(before),
                    "the batch rewrote the index snapshot instead of appending its deltas");

                for (int i = 6_000; i < 6_010; i++)
                    Assert.True(db.Delete(db.Find<IndexLogRow>(r => r.Sku == $"SKU-{i:D8}")));
            }

            // A cold open replays the log: every index answers, removes included.
            using (var db = new XodContext(path))
            {
                Assert.Equal(6_490, db.Count<IndexLogRow>());
                for (int i = 0; i < 6_500; i += 97)
                {
                    var found = db.Find<IndexLogRow>(r => r.Sku == $"SKU-{i:D8}");
                    bool deleted = i >= 6_000 && i < 6_010;
                    Assert.Equal(deleted, found == null);
                    if (!deleted)
                    {
                        Assert.Equal(i + 1, found.Id);
                        Assert.Equal(found.Sku, db.Find<IndexLogRow>(r => r.Id == i + 1).Sku);
                    }
                }
                Assert.Equal(129, db.Query<IndexLogRow>(r => r.Bucket == 7).Count()); // 6,007 was deleted
                Assert.Throws<ReservedUniqueKeyException>(() => db.Insert(Row(6_400)));
                db.Insert(Row(6_005)); // its key was released by the delete
            }
        }

        // A delete on a type without a primary key finds its row by scanning the pages and never loads
        // the index, so the index service receives a change for an index it does not hold.
        [Fact]
        public void A_change_to_an_index_this_context_never_loaded_keeps_every_other_key()
        {
            string path = DbPath("unloaded-delete");
            using (var db = new XodContext(path))
            {
                db.InsertRange(Enumerable.Range(0, 100)
                    .Select(i => new IndexLogKeylessRow { Code = $"K{i:D4}", Bucket = i % 5, Payload = "p" })
                    .ToList());
            }

            using (var db = new XodContext(path))
                Assert.True(db.Delete(new IndexLogKeylessRow { Code = "K0003" }));

            using (var db = new XodContext(path))
            {
                Assert.Equal(99, db.Count<IndexLogKeylessRow>());
                Assert.Null(db.Find<IndexLogKeylessRow>(r => r.Code == "K0003"));
                Assert.NotNull(db.Find<IndexLogKeylessRow>(r => r.Code == "K0050"));
                Assert.Throws<ReservedUniqueKeyException>(() =>
                    db.Insert(new IndexLogKeylessRow { Code = "K0050", Bucket = 1 }));
                db.Insert(new IndexLogKeylessRow { Code = "K0003", Bucket = 1 }); // released by the delete
            }
        }

        // With indexing off the engine never loads the index, but its writes still reach the index service.
        [Fact]
        public void A_session_with_indexing_off_keeps_the_index_whole()
        {
            string path = DbPath("indexing-off");
            using (var db = new XodContext(path))
                db.InsertRange(Rows(0, 100));

            using (var db = new XodContext(path, options: new DatabaseOptions { EnableIndexing = false }))
                db.Insert(Row(100));

            using (var db = new XodContext(path))
            {
                Assert.Equal(101, db.Count<IndexLogRow>());
                Assert.NotNull(db.Find<IndexLogRow>(r => r.Sku == "SKU-00000050"));
                Assert.NotNull(db.Find<IndexLogRow>(r => r.Sku == "SKU-00000100"));
                Assert.Throws<ReservedUniqueKeyException>(() => db.Insert(Row(50)));
            }
        }

        [Fact]
        public void The_log_never_outgrows_its_snapshot_and_a_clean_close_folds_it_back()
        {
            string path = DbPath("bounded");
            string xidx = IndexPath(path);

            const int Total = 32_000;
            using (var db = new XodContext(path))
            {
                db.InsertRange(Rows(0, 2_000));

                // Each batch flushes its 3,000 deltas to the log on its own.
                for (int start = 2_000; start < Total; start += 1_000)
                {
                    db.InsertRange(Rows(start, 1_000));
                    var layout = ReadLayout(xidx);
                    long log = layout.Length - layout.SnapshotEnd;
                    Assert.True(log <= Math.Max(LogFloorBytes, layout.SnapshotEnd),
                        $"log {log:N0} bytes over a snapshot of {layout.SnapshotEnd:N0} bytes at {start + 1_000:N0} rows");
                }
            }

            var closed = ReadLayout(xidx);
            Assert.True(closed.Length - closed.SnapshotEnd <= LogFloorBytes,
                $"a clean close left a log of {closed.Length - closed.SnapshotEnd:N0} bytes");

            using (var db = new XodContext(path))
            {
                Assert.Equal(Total, db.Count<IndexLogRow>());
                for (int i = 0; i < Total; i += 101)
                    Assert.Equal(i + 1, db.Find<IndexLogRow>(r => r.Sku == $"SKU-{i:D8}").Id);
                Assert.Equal(Total / 50, db.Query<IndexLogRow>(r => r.Bucket == 7).Count());
            }
        }
    }
}
