using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class RekeyRow
    {
        [PrimaryKey, Property(AutoNumber = true)] public int Id { get; set; }
        [UniqueKey] public string Sku { get; set; }
        [Property(Indexed = true)] public int Bucket { get; set; }
        public string Payload { get; set; }
    }

    public class RekeyHolder
    {
        [PrimaryKey, Property(AutoNumber = true)] public int Id { get; set; }
        [UniqueKey] public string Code { get; set; }
        [Property(Indexed = true)] public int Group { get; set; }
        public string Note { get; set; }
        [Children] public List<RekeyPart> Parts { get; set; }
    }

    public class RekeyPart
    {
        [PrimaryKey, Property(AutoNumber = true)] public int Id { get; set; }
        public int RekeyHolderId { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// An update changes a row's index keys only where a key's value changed (PLAN-056, ISS-044). Every update
    /// used to remove and add every key of the row, changed or not: the index log grew with each one, and once
    /// it outgrew its snapshot a flush wrote the whole index again, holding the engine's write lock. And a
    /// read of a page after one of its rows grew used to rebuild the page's whole row map.
    /// </summary>
    public class XodRowKeysTests : IDisposable
    {
        private readonly string tempRoot;

        public XodRowKeysTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodRowKeys_" + Guid.NewGuid().ToString("N"));
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

        private static string IndexPath(string dbPath, Type type) =>
            Path.Combine(Path.GetDirectoryName(dbPath)!, type.FullName + ".xidx");

        private static RekeyRow Row(int i) => new() { Sku = $"SKU-{i:D6}", Bucket = i % 10, Payload = "p" + i };

        private static void Seed(XodContext db, int count) =>
            db.InsertRange(Enumerable.Range(1, count).Select(Row).ToList());

        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        [InlineData(StorageFormat.Binary)]
        public void An_update_that_changes_no_indexed_value_adds_nothing_to_the_index_file(StorageFormat format)
        {
            string path = DbPath("unchanged-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            using (var db = new XodContext(path, options: options))
            {
                Seed(db, 200);
                db.Flush();
                long before = new FileInfo(IndexPath(path, typeof(RekeyRow))).Length;

                for (int id = 1; id <= 50; id++)
                {
                    var row = db.Find<RekeyRow>(r => r.Id == id);
                    row.Payload = "changed-" + id;
                    Assert.True(db.Update(row));
                }
                db.Flush();

                Assert.Equal(before, new FileInfo(IndexPath(path, typeof(RekeyRow))).Length);
                Assert.Equal("changed-7", db.Find<RekeyRow>(r => r.Id == 7).Payload);
                Assert.Equal("changed-7", db.Find<RekeyRow>(r => r.Sku == "SKU-000007").Payload);
            }

            using (var db = new XodContext(path, options: options))
            {
                Assert.Equal("changed-50", db.Find<RekeyRow>(r => r.Sku == "SKU-000050").Payload);
                Assert.Equal(20, db.Query<RekeyRow>(r => r.Bucket == 3).Count());
            }
        }

        [Fact]
        public void An_update_of_a_related_type_that_changes_no_indexed_value_adds_nothing_to_the_index_file()
        {
            string path = DbPath("related");
            using (var db = new XodContext(path))
            {
                for (int i = 1; i <= 20; i++)
                    db.Insert(new RekeyHolder { Code = "H" + i, Group = i % 3, Note = "n" + i, Parts = new() { new RekeyPart { Text = "t" + i } } });
                db.Flush();
                long before = new FileInfo(IndexPath(path, typeof(RekeyHolder))).Length;

                var holder = db.Find<RekeyHolder>(h => h.Code == "H5");
                holder.Note = "changed";
                Assert.True(db.Update(holder));
                db.Flush();

                Assert.Equal(before, new FileInfo(IndexPath(path, typeof(RekeyHolder))).Length);
                Assert.Equal("changed", db.Find<RekeyHolder>(h => h.Code == "H5").Note);

                holder = db.Find<RekeyHolder>(h => h.Code == "H5");
                holder.Group = 7;
                Assert.True(db.Update(holder));
                Assert.Equal(new[] { "H5" }, db.Query<RekeyHolder>(h => h.Group == 7).Select(h => h.Code));
                Assert.DoesNotContain("H5", db.Query<RekeyHolder>(h => h.Group == 2).Select(h => h.Code));
            }

            using (var db = new XodContext(path))
            {
                Assert.Equal(new[] { "H5" }, db.Query<RekeyHolder>(h => h.Group == 7).Select(h => h.Code));
                Assert.Equal(6, db.Query<RekeyHolder>(h => h.Group == 2).Count());
            }
        }

        // The key that changed moves; the others stay. After a reopen the index's blocks are read only when
        // needed: the bucket's block isn't read before the update here, so taking the old key out of it needs
        // the whole block (a block read later merges only additions).
        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        [InlineData(StorageFormat.Binary)]
        public void An_update_that_changes_one_key_moves_that_key_and_keeps_the_others(StorageFormat format)
        {
            string path = DbPath("one-key-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            using (var db = new XodContext(path, options: options))
            {
                Seed(db, 200);
            }

            using (var db = new XodContext(path, options: options))
            {
                var row = db.Find<RekeyRow>(r => r.Id == 7);
                Assert.Equal(7, row.Bucket);
                row.Bucket = 42;
                Assert.True(db.Update(row));
                AssertMoved(db);
            }

            using (var db = new XodContext(path, options: options))
            {
                AssertMoved(db);

                var row = db.Find<RekeyRow>(r => r.Id == 8);
                row.Sku = "NEW-8";
                Assert.True(db.Update(row));
                Assert.Null(db.Find<RekeyRow>(r => r.Sku == "SKU-000008"));
                Assert.Equal(8, db.Find<RekeyRow>(r => r.Sku == "NEW-8").Id);
                Assert.Contains(8, db.Query<RekeyRow>(r => r.Bucket == 8).Select(r => r.Id));
                Assert.Throws<ReservedUniqueKeyException>(() => db.Insert(new RekeyRow { Sku = "NEW-8", Bucket = 1, Payload = "x" }));
                db.Insert(new RekeyRow { Sku = "SKU-000008", Bucket = 1, Payload = "reused" });
            }

            using (var db = new XodContext(path, options: options))
            {
                Assert.Equal(8, db.Find<RekeyRow>(r => r.Sku == "NEW-8").Id);
                Assert.Equal("reused", db.Find<RekeyRow>(r => r.Sku == "SKU-000008").Payload);
                Assert.Contains(8, db.Query<RekeyRow>(r => r.Bucket == 8).Select(r => r.Id));
            }

            static void AssertMoved(XodContext db)
            {
                Assert.DoesNotContain(7, db.Query<RekeyRow>(r => r.Bucket == 7).Select(r => r.Id));
                Assert.Equal(19, db.Query<RekeyRow>(r => r.Bucket == 7).Count());
                Assert.Equal(new[] { 7 }, db.Query<RekeyRow>(r => r.Bucket == 42).Select(r => r.Id));
                Assert.Equal(7, db.Find<RekeyRow>(r => r.Sku == "SKU-000007").Id);
                Assert.Equal(42, db.Find<RekeyRow>(r => r.Id == 7).Bucket);
            }
        }

        [Theory]
        [InlineData(StorageFormat.Xml)]
        [InlineData(StorageFormat.Json)]
        public void A_row_whose_key_moved_leaves_no_key_when_deleted(StorageFormat format)
        {
            string path = DbPath("moved-deleted-" + format);
            var options = new DatabaseOptions { StorageFormat = format };
            using (var db = new XodContext(path, options: options))
            {
                Seed(db, 50);
                var row = db.Find<RekeyRow>(r => r.Id == 9);
                row.Sku = "MOVED-9";
                Assert.True(db.Update(row));
                Assert.True(db.Delete(db.Find<RekeyRow>(r => r.Id == 9)));

                Assert.Null(db.Find<RekeyRow>(r => r.Sku == "MOVED-9"));
                Assert.Null(db.Find<RekeyRow>(r => r.Sku == "SKU-000009"));
                Assert.DoesNotContain(9, db.Query<RekeyRow>(r => r.Bucket == 9).Select(r => r.Id));
                db.Insert(new RekeyRow { Sku = "MOVED-9", Bucket = 9, Payload = "again" });
            }

            using (var db = new XodContext(path, options: options))
            {
                Assert.Equal("again", db.Find<RekeyRow>(r => r.Sku == "MOVED-9").Payload);
                Assert.Equal(5, db.Query<RekeyRow>(r => r.Bucket == 9).Count());
            }
        }

        // An update that leaves indexed properties out (an update filter) keeps their keys as stored: they compare
        // equal, so nothing is recorded for them; a key the filter lets through still moves.
        [Fact]
        public void An_update_filter_moves_only_the_keys_it_lets_through()
        {
            string path = DbPath("filter");
            using (var db = new XodContext(path))
            {
                Seed(db, 100);
                db.Flush();
                long before = new FileInfo(IndexPath(path, typeof(RekeyRow))).Length;

                var payloadOnly = new UpdateFilter { Behavior = UpdateFilterBehavior.Target, Properties = new[] { "Payload" } };
                var stored = db.Find<RekeyRow>(r => r.Id == 7);
                Assert.True(db.Update(stored, new RekeyRow { Id = 7, Sku = "IGNORED", Bucket = 99, Payload = "filtered" }, payloadOnly));
                db.Flush();
                Assert.Equal(before, new FileInfo(IndexPath(path, typeof(RekeyRow))).Length);

                var bucketToo = new UpdateFilter { Behavior = UpdateFilterBehavior.Target, Properties = new[] { "Payload", "Bucket" } };
                stored = db.Find<RekeyRow>(r => r.Id == 8);
                Assert.True(db.Update(stored, new RekeyRow { Id = 8, Sku = "IGNORED", Bucket = 42, Payload = "moved" }, bucketToo));
            }

            using (var db = new XodContext(path))
            {
                Assert.Equal("filtered", db.Find<RekeyRow>(r => r.Sku == "SKU-000007").Payload);
                Assert.Null(db.Find<RekeyRow>(r => r.Sku == "IGNORED"));
                Assert.Empty(db.Query<RekeyRow>(r => r.Bucket == 99));
                Assert.Equal(new[] { 8 }, db.Query<RekeyRow>(r => r.Bucket == 42).Select(r => r.Id));
                Assert.Equal("moved", db.Find<RekeyRow>(r => r.Sku == "SKU-000008").Payload);
                Assert.DoesNotContain(8, db.Query<RekeyRow>(r => r.Bucket == 8).Select(r => r.Id));
            }
        }

        // Another context replaces the index file (it rebuilt the index) after this one read only the primary key's
        // block. A change here that then reads another block finds the file replaced, and the index is loaded again
        // from the new file. Everything the change records must land in what was loaded: it used to keep writing to
        // the log it held before the load, which no flush writes, so the keys it moved came back after a reopen.
        private string IndexReplacedByAnotherContext(string name)
        {
            string path = DbPath(name);
            using (var db = new XodContext(path))
                Seed(db, 100);
            File.Delete(IndexPath(path, typeof(RekeyRow)));
            using (var db = new XodContext(path))
                Assert.NotNull(db.Find<RekeyRow>(r => r.Id == 1)); // rebuilt, saved as a snapshot of blocks
            return path;
        }

        private static void ReplaceIndexFrom(string path)
        {
            using var other = new XodContext(path);
            File.Delete(IndexPath(path, typeof(RekeyRow)));
            Assert.NotNull(other.Find<RekeyRow>(r => r.Id == 1)); // rebuilt; a new file, a new generation, at its close
        }

        [Fact]
        public void Keys_an_update_moves_after_the_index_file_was_replaced_are_saved()
        {
            string path = IndexReplacedByAnotherContext("replaced-update");
            using (var db = new XodContext(path))
            {
                Assert.NotNull(db.Find<RekeyRow>(r => r.Id == 5)); // only the primary key's block read
                ReplaceIndexFrom(path);

                var row = db.Find<RekeyRow>(r => r.Id == 7);
                row.Sku = "ZZZ-MOVED"; // past the block's largest key: the unique check doesn't read the block
                row.Bucket = 42;
                Assert.True(db.Update(row));
                Assert.Equal(7, db.Find<RekeyRow>(r => r.Sku == "ZZZ-MOVED").Id);
                Assert.Equal("ZZZ-MOVED", db.Find<RekeyRow>(r => r.Id == 7).Sku); // the row written, not only its keys (ISS-113)
            }

            using (var db = new XodContext(path))
            {
                Assert.Equal(7, db.Find<RekeyRow>(r => r.Sku == "ZZZ-MOVED")?.Id);
                Assert.Equal("ZZZ-MOVED", db.Find<RekeyRow>(r => r.Id == 7).Sku);
                Assert.Null(db.Find<RekeyRow>(r => r.Sku == "SKU-000007"));
                Assert.Equal(new[] { 7 }, db.Query<RekeyRow>(r => r.Bucket == 42).Select(r => r.Id));
                Assert.DoesNotContain(7, db.Query<RekeyRow>(r => r.Bucket == 7).Select(r => r.Id));
                Assert.ThrowsAny<Exception>(() => db.Insert(new RekeyRow { Sku = "ZZZ-MOVED", Bucket = 1, Payload = "twin" }));
            }
        }

        // The primary and unique keys' blocks were read before the file was replaced; the update's read of the third
        // block then loads the index again, in the middle of the update's reads. The unique key's old value must still come out: the load reads every block, so the
        // one read before it is read again.
        [Fact]
        public void A_key_whose_block_was_read_before_the_index_file_was_replaced_is_still_taken_out()
        {
            string path = IndexReplacedByAnotherContext("replaced-read-before");
            using (var db = new XodContext(path))
            {
                Assert.NotNull(db.Find<RekeyRow>(r => r.Id == 3)); // the primary key's block read, which the update
                Assert.NotNull(db.Find<RekeyRow>(r => r.Sku == "SKU-000003")); // looks the row up by; and the unique key's
                ReplaceIndexFrom(path);

                var row = db.Find<RekeyRow>(r => r.Sku == "SKU-000007");
                row.Sku = "ZZZ-MOVED";
                row.Bucket = 42; // its block is read now, from the replaced file: the index is loaded again
                Assert.True(db.Update(row));
                Assert.Equal(42, db.Find<RekeyRow>(r => r.Id == 7).Bucket); // the row written (ISS-113)

                db.Insert(new RekeyRow { Sku = "SKU-000007", Bucket = 7, Payload = "reused" });
                Assert.Equal("reused", db.Find<RekeyRow>(r => r.Sku == "SKU-000007").Payload);
            }

            using (var db = new XodContext(path))
            {
                Assert.Equal("reused", db.Find<RekeyRow>(r => r.Sku == "SKU-000007").Payload);
                Assert.Equal(7, db.Find<RekeyRow>(r => r.Sku == "ZZZ-MOVED").Id);
            }
        }

        [Fact]
        public void Keys_a_delete_frees_after_the_index_file_was_replaced_stay_free()
        {
            string path = IndexReplacedByAnotherContext("replaced-delete");
            using (var db = new XodContext(path))
            {
                Assert.NotNull(db.Find<RekeyRow>(r => r.Id == 5));
                ReplaceIndexFrom(path);
                Assert.True(db.Delete(db.Find<RekeyRow>(r => r.Id == 7)));
            }

            using (var db = new XodContext(path))
            {
                Assert.Null(db.Find<RekeyRow>(r => r.Sku == "SKU-000007"));
                Assert.Equal(9, db.Query<RekeyRow>(r => r.Bucket == 7).Count());
                db.Insert(new RekeyRow { Sku = "SKU-000007", Bucket = 7, Payload = "again" });
                Assert.Equal("again", db.Find<RekeyRow>(r => r.Sku == "SKU-000007").Payload);
            }
        }

        // A row that grows moves the rows after it in its page. The page's row map follows them, as it does for
        // a row written in place, deleted or appended, instead of being dropped: so the next read of the page
        // doesn't rebuild the map, a dictionary and a string for each of the page's rows.
        [Fact]
        public void A_read_after_a_row_grew_does_not_rebuild_the_page_s_row_map()
        {
            string path = DbPath("row-map");
            string payload = new string('x', 300);
            using var db = new XodContext(path);
            db.InsertRange(Enumerable.Range(1, 1500).Select(i => new RekeyRow { Sku = $"SKU-{i:D6}", Bucket = i % 10, Payload = payload }).ToList());
            for (int id = 1; id <= 1500; id++)
                Assert.NotNull(db.Find<RekeyRow>(r => r.Id == id)); // every page read once: its row map built

            int probe = 20;
            Assert.NotNull(db.Find<RekeyRow>(r => r.Id == probe)); // the read below is warm

            var grown = db.Find<RekeyRow>(r => r.Id == 10);
            grown.Payload = payload + "-longer";
            Assert.True(db.Update(grown));

            long before = GC.GetAllocatedBytesForCurrentThread();
            var read = db.Find<RekeyRow>(r => r.Id == probe);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(payload, read.Payload);
            // A read allocates its object and a few small ones (about 2 KB here); rebuilding the map of a page of
            // over a thousand rows allocates tens of times that.
            Assert.True(allocated < 32 * 1024, $"the read allocated {allocated:N0} bytes");
        }

        // The page's row map, kept through rows growing anywhere in the page (first, middle, last, next to each
        // other) and through one that outgrows the page's buffer, still finds every row where it is.
        [Fact]
        public void Rows_read_back_as_written_after_updates_that_lengthen_rows_anywhere_in_a_page()
        {
            string path = DbPath("lengthen");
            string payload = new string('y', 300);
            var expected = new Dictionary<int, string>();
            using (var db = new XodContext(path))
            {
                db.InsertRange(Enumerable.Range(1, 1500).Select(i => new RekeyRow { Sku = $"SKU-{i:D6}", Bucket = i % 10, Payload = payload }).ToList());
                for (int id = 1; id <= 1500; id++)
                {
                    Assert.Equal(payload, db.Find<RekeyRow>(r => r.Id == id).Payload);
                    expected[id] = payload;
                }

                int[] grow = { 1, 2, 3, 600, 601, 1199, 1200, 1201, 1499, 1500, 2, 600 };
                for (int n = 0; n < grow.Length; n++)
                {
                    int id = grow[n];
                    var row = db.Find<RekeyRow>(r => r.Id == id);
                    row.Payload = expected[id] + new string('z', 5 + n * 7);
                    Assert.True(db.Update(row));
                    expected[id] = row.Payload;
                    foreach (int check in new[] { id - 1, id, id + 1, 1, 1500 })
                        if (expected.ContainsKey(check))
                            Assert.Equal(expected[check], db.Find<RekeyRow>(r => r.Id == check).Payload);
                }

                // One row outgrows its page's buffer, which is then replaced by a larger one.
                var big = db.Find<RekeyRow>(r => r.Id == 300);
                big.Payload = new string('b', 700_000);
                Assert.True(db.Update(big));
                expected[300] = big.Payload;

                foreach (var (id, text) in expected)
                    Assert.Equal(text, db.Find<RekeyRow>(r => r.Id == id).Payload);
            }

            using (var db = new XodContext(path))
            {
                foreach (var (id, text) in expected)
                    Assert.Equal(text, db.Find<RekeyRow>(r => r.Id == id).Payload);
            }
        }
    }
}
