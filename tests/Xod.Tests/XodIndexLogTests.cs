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

    public class IndexLogDatedRow
    {
        [PrimaryKey, Property(AutoNumber = true)] public int Id { get; set; }
        [Property(Indexed = true)] public DateTime When { get; set; }
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

        /// <summary>Reads where the snapshot of a framed (-5) .xidx file ends; the rest is the delta log.</summary>
        private static (long SnapshotEnd, long Length) ReadLayout(string xidxPath)
        {
            byte[] bytes = File.ReadAllBytes(xidxPath);
            using var ms = new MemoryStream(bytes);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            Assert.True(r.ReadBytes(Xidx2Header.Length).AsSpan().SequenceEqual(Xidx2Header));
            Assert.Equal(-5, r.ReadInt32()); // framed snapshot, closed log flushes, keys v2, a generation
            Assert.NotEqual(0, r.ReadInt64()); // the snapshot's generation (ISS-042)
            r.ReadString();                  // zone stamp
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

        /// <summary>The item code the index snapshot holds for a Sku.</summary>
        private static string CodeOf(string dbPath, string sku) => CodeIn(IndexPath(dbPath), nameof(IndexLogRow.Sku), sku);

        /// <summary>The item code an index snapshot holds for a property's key.</summary>
        private static string CodeIn(string xidxPath, string property, string sku)
        {
            using var r = new BinaryReader(File.OpenRead(xidxPath), Encoding.UTF8);
            r.ReadBytes(Xidx2Header.Length);
            r.ReadInt32(); // marker
            r.ReadInt64(); // generation
            r.ReadString(); // zone stamp
            int props = r.ReadInt32();
            for (int p = 0; p < props; p++)
            {
                string name = r.ReadString();
                int entries = r.ReadInt32();
                r.ReadString();
                r.ReadString();
                int blockLength = r.ReadInt32();
                if (name != property)
                {
                    r.BaseStream.Position += blockLength;
                    continue;
                }
                for (int e = 0; e < entries; e++)
                {
                    string key = r.ReadString();
                    int codes = r.ReadInt32();
                    var held = Enumerable.Range(0, codes).Select(_ => r.ReadString()).ToList();
                    if (key == sku) return held.Single();
                }
            }
            throw new InvalidOperationException(property + " " + sku + " is not in the index");
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

        // Files from 2.0 (marker -2, before flush-end records; -3, with them) hold keys v1, which are
        // rounded and misordered (ISS-053, ISS-054, ISS-056). They are never loaded or appended to: the
        // index is rebuilt from the pages and written in the current layout. A planted wrong key proves the
        // old file was not read.
        [Theory]
        [InlineData(-2)]
        [InlineData(-3)]
        public void An_index_file_with_v1_keys_is_rebuilt_from_the_pages(int oldMarker)
        {
            string path = DbPath("legacy-marker" + oldMarker);
            string xidx = IndexPath(path);
            using (var db = new XodContext(path))
                db.InsertRange(Rows(0, 5_000));

            string planted = CodeOf(path, "SKU-00000007");
            int markerAt = Xidx2Header.Length;
            byte[] bytes = File.ReadAllBytes(xidx);
            BitConverter.GetBytes(oldMarker).CopyTo(bytes, markerAt); // a file of the old layout as far as the marker goes
            File.WriteAllBytes(xidx, bytes);
            using (var fs = new FileStream(xidx, FileMode.Append, FileAccess.Write))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                // A log record that removes row SKU-00000007 from the index, closed as a whole flush.
                w.Write((byte)2); w.Write(string.Empty); w.Write(string.Empty); w.Write(planted);
                w.Write((byte)3); w.Write(string.Empty); w.Write(string.Empty); w.Write(string.Empty);
            }

            using (var db = new XodContext(path))
            {
                Assert.NotNull(db.Find<IndexLogRow>(r => r.Sku == "SKU-00000007"));
                db.InsertRange(Rows(5_000, 1_500));
            }

            ReadLayout(xidx); // asserts marker -5

            using (var db = new XodContext(path))
            {
                Assert.NotNull(db.Find<IndexLogRow>(r => r.Sku == "SKU-00006499"));
                Assert.NotNull(db.Find<IndexLogRow>(r => r.Sku == "SKU-00000007"));
            }
        }

        // With a password the index is an encrypted document. 2.0 wrote it with a TableIndex root and keys
        // v1; such a document is never loaded. The planted removal proves the old document was not read.
        [Fact]
        public void An_encrypted_index_document_with_v1_keys_is_rebuilt_from_the_pages()
        {
            const string password = "index-pw";
            string path = DbPath("legacy-document");
            string xidx = IndexPath(path);
            using (var db = new XodContext(path, password))
                db.InsertRange(Rows(0, 50));

            var doc = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(Envelope.Open(File.ReadAllBytes(xidx), password)));
            Assert.Equal("TableIndex2", doc.Root!.Name.LocalName);
            doc.Root.Name = "TableIndex";
            doc.Root.Descendants("Entry").Single(e => (string)e.Attribute("key") == "SKU-00000007").Remove();
            File.WriteAllBytes(xidx, Envelope.Seal(Encoding.UTF8.GetBytes(doc.ToString()), password));

            using (var db = new XodContext(path, password))
                Assert.NotNull(db.Find<IndexLogRow>(r => r.Sku == "SKU-00000007"));

            doc = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(Envelope.Open(File.ReadAllBytes(xidx), password)));
            Assert.Equal("TableIndex2", doc.Root!.Name.LocalName);
        }

        // A local DateTime reads back in the reading machine's zone, so an index of one is valid only in the
        // zone that built it (ISS-057). The index records that zone; a different stamp is never trusted. The
        // planted removal proves the old index was not read.
        [Fact]
        public void An_index_built_in_another_zone_is_rebuilt_from_the_pages()
        {
            string path = DbPath("zone");
            string xidx = Path.Combine(Path.GetDirectoryName(path)!, typeof(IndexLogDatedRow).FullName + ".xidx");
            var start = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local);
            using (var db = new XodContext(path))
                db.InsertRange(Enumerable.Range(0, 20).Select(i => new IndexLogDatedRow { When = start.AddDays(i) }).ToList());

            byte[] bytes = File.ReadAllBytes(xidx);
            using var ms = new MemoryStream(bytes);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            r.ReadBytes(Xidx2Header.Length);
            Assert.Equal(-5, r.ReadInt32());
            r.ReadInt64(); // generation
            int zoneAt = (int)ms.Position;
            string zone = r.ReadString();
            Assert.Equal(16, zone.Length); // a type with an indexed DateTime is stamped
            string other = zone[0] == '0' ? "1" + zone[1..] : "0" + zone[1..];
            Encoding.UTF8.GetBytes(other).CopyTo(bytes, zoneAt + 1);
            File.WriteAllBytes(xidx, bytes);
            string code = CodeIn(xidx, nameof(IndexLogDatedRow.Id), "09223372036854775809"); // Id 1, keys v2
            using (var fs = new FileStream(xidx, FileMode.Append, FileAccess.Write))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                // Remove row 1 from the index everywhere: a stale index would no longer find it.
                w.Write((byte)2); w.Write(string.Empty); w.Write(string.Empty); w.Write(code);
                w.Write((byte)3); w.Write(string.Empty); w.Write(string.Empty); w.Write(string.Empty);
            }

            using (var db = new XodContext(path))
                Assert.Equal(1, db.Query<IndexLogDatedRow>(x => x.When == start).Single().Id);

            using var after = new BinaryReader(File.OpenRead(xidx), Encoding.UTF8);
            after.ReadBytes(Xidx2Header.Length);
            after.ReadInt32();
            after.ReadInt64();
            Assert.Equal(zone, after.ReadString()); // rebuilt, and stamped with this machine's zone again
        }

        [Fact]
        public void An_encrypted_index_built_in_another_zone_is_rebuilt_from_the_pages()
        {
            const string password = "index-pw";
            string path = DbPath("zone-document");
            string xidx = Path.Combine(Path.GetDirectoryName(path)!, typeof(IndexLogDatedRow).FullName + ".xidx");
            var start = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local);
            using (var db = new XodContext(path, password))
                db.InsertRange(Enumerable.Range(0, 20).Select(i => new IndexLogDatedRow { When = start.AddDays(i) }).ToList());

            var doc = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(Envelope.Open(File.ReadAllBytes(xidx), password)));
            string zone = (string)doc.Root!.Attribute("zone");
            Assert.Equal(16, zone?.Length);
            doc.Root.SetAttributeValue("zone", "0000000000000000" == zone ? "1111111111111111" : "0000000000000000");
            doc.Root.Descendants("Entry").Where(e => e.Parent!.Attribute("name")!.Value == nameof(IndexLogDatedRow.When)).First().Remove();
            File.WriteAllBytes(xidx, Envelope.Seal(Encoding.UTF8.GetBytes(doc.ToString()), password));

            using (var db = new XodContext(path, password))
                Assert.Equal(20, Enumerable.Range(0, 20).Count(i => db.Query<IndexLogDatedRow>(x => x.When == start.AddDays(i)).Count() == 1));

            doc = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(Envelope.Open(File.ReadAllBytes(xidx), password)));
            Assert.Equal(zone, (string)doc.Root!.Attribute("zone"));
        }

        /// <summary>The file envelope as PLAN-046 section 5 states it, written from the spec.</summary>
        private static class Envelope
        {
            private static readonly byte[] Magic = Encoding.ASCII.GetBytes("XODENC2\0");

            private static byte[] Key(string password, byte[] salt)
            {
                string passphrase = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
                return System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(passphrase), salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
            }

            internal static byte[] Open(byte[] file, string password)
            {
                Assert.True(file.AsSpan(0, 8).SequenceEqual(Magic));
                byte[] salt = file[8..24], nonce = file[24..36], tag = file[36..52], cipher = file[52..];
                byte[] plain = new byte[cipher.Length];
                using var gcm = new System.Security.Cryptography.AesGcm(Key(password, salt), 16);
                gcm.Decrypt(nonce, cipher, tag, plain, Magic);
                return plain;
            }

            internal static byte[] Seal(byte[] plain, string password)
            {
                byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                byte[] nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
                byte[] tag = new byte[16], cipher = new byte[plain.Length];
                using (var gcm = new System.Security.Cryptography.AesGcm(Key(password, salt), 16))
                    gcm.Encrypt(nonce, plain, cipher, tag, Magic);
                return Magic.Concat(salt).Concat(nonce).Concat(tag).Concat(cipher).ToArray();
            }
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
