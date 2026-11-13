// ISS-042: an index file's blocks are read when first needed, at the byte offsets found when the file was loaded.
// Another context that replaced the file in between (a snapshot: a rebuild, a fold at its close) left those offsets
// pointing at other bytes, so the read failed or gave wrong keys. And an index file another program held open failed
// what touched it: a query, whose rebuilt index couldn't be saved over it, and a write, whose index couldn't be
// deleted when it couldn't be loaded.
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Xod.Tests
{
    public class ReplacedIndexItem
    {
        [PrimaryKey] public int Id { get; set; }
        [UniqueKey] public string Code { get; set; }
        public string Name { get; set; }
    }

    public class ReplacedIndexTagged
    {
        [PrimaryKey] public int Id { get; set; }
        [Property(Indexed = true)] public string Tag { get; set; }
    }

    public class XodIndexReplacedTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-index-replaced-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private static string DatabasePath(string dir) => Path.Combine(dir, "data.xod");

        private static string IndexFile(string dir) => Path.Combine(dir, typeof(ReplacedIndexItem).FullName + ".xidx");

        private static string Code(int i) => "c" + i.ToString("D4");

        private string Seeded(string name, int rows)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            using (var db = new XodContext(DatabasePath(dir), options: new DatabaseOptions { InitialCreate = true }))
                db.InsertRange(Enumerable.Range(1, rows).Select(i => new ReplacedIndexItem { Id = i, Code = Code(i), Name = "n" + i }).ToList());
            // The index written as a snapshot of its blocks (rebuilt from the pages and saved whole), not as a log
            // replayed into memory: the blocks are then read when first needed.
            File.Delete(IndexFile(dir));
            using (var db = new XodContext(DatabasePath(dir)))
                Assert.NotNull(db.Find<ReplacedIndexItem>(i => i.Id == 1));
            return dir;
        }

        [Fact]
        public void A_snapshot_another_context_wrote_doesn_t_spoil_blocks_not_yet_read()
        {
            string dir = Seeded("replaced", 100);
            using var reader = new XodContext(DatabasePath(dir));
            // Loads the index and reads the primary key's block only.
            Assert.NotNull(reader.Find<ReplacedIndexItem>(i => i.Id == 5));

            // Another context adds rows, then replaces the file: it rebuilds the index from the pages and writes it
            // whole, so every block moves.
            using (var writer = new XodContext(DatabasePath(dir)))
            {
                writer.InsertRange(Enumerable.Range(101, 400)
                    .Select(i => new ReplacedIndexItem { Id = i, Code = Code(i) + new string('x', 40), Name = "n" + i }).ToList());
                writer.ClearCache();
                File.Delete(IndexFile(dir));
                Assert.NotNull(writer.Find<ReplacedIndexItem>(i => i.Id == 1));
            }

            for (int i = 1; i <= 100; i++)
            {
                string code = Code(i);
                Assert.Equal(i, reader.Find<ReplacedIndexItem>(x => x.Code == code)?.Id);
            }
        }

        // A context whose index has changes not yet saved (buffered) can't take the replacing file, which lacks them:
        // it builds the index from the pages, which hold them, and saves it whole at its next flush.
        [Fact]
        public void A_replaced_snapshot_doesn_t_lose_changes_not_yet_saved()
        {
            string dir = Path.Combine(tempRoot, "unsaved");
            Directory.CreateDirectory(dir);
            string tagIndex = Path.Combine(dir, typeof(ReplacedIndexTagged).FullName + ".xidx");
            using (var db = new XodContext(DatabasePath(dir), options: new DatabaseOptions { InitialCreate = true }))
                db.InsertRange(Enumerable.Range(1, 100).Select(i => new ReplacedIndexTagged { Id = i, Tag = "t" + (i % 10) }).ToList());
            File.Delete(tagIndex);
            using (var db = new XodContext(DatabasePath(dir)))
                Assert.NotNull(db.Find<ReplacedIndexTagged>(i => i.Id == 1));

            using (var reader = new XodContext(DatabasePath(dir), options: new DatabaseOptions { Durability = DurabilityMode.Buffered }))
            {
                Assert.NotNull(reader.Find<ReplacedIndexTagged>(i => i.Id == 5));
                // Recorded against the Tag block, which isn't read yet; held in memory until a flush.
                reader.Insert(new ReplacedIndexTagged { Id = 1000, Tag = "new" });

                // Another context replaces the file with a snapshot of the pages on disk, without the row above.
                using (var writer = new XodContext(DatabasePath(dir)))
                {
                    File.Delete(tagIndex);
                    Assert.NotNull(writer.Find<ReplacedIndexTagged>(i => i.Id == 1));
                }

                Assert.Equal(1000, Assert.Single(reader.Query<ReplacedIndexTagged>(x => x.Tag == "new")).Id);
                Assert.Equal(10, reader.Query<ReplacedIndexTagged>(x => x.Tag == "t3").Count());
            }

            using var again = new XodContext(DatabasePath(dir));
            Assert.Equal(1000, Assert.Single(again.Query<ReplacedIndexTagged>(x => x.Tag == "new")).Id);
            Assert.Equal(10, again.Query<ReplacedIndexTagged>(x => x.Tag == "t3").Count());
        }

        // Another context replaces the file: it rebuilds the index from the pages and writes it whole.
        private static void ReplaceIndex(string dir)
        {
            using var writer = new XodContext(DatabasePath(dir));
            File.Delete(IndexFile(dir));
            Assert.NotNull(writer.Find<ReplacedIndexItem>(i => i.Id == 1));
        }

        // A flush whose log has outgrown its snapshot writes the snapshot, reading every block first. A replaced file
        // made that read load the type again, after the flush had taken the type out of the set still to save, so the
        // changes it was saving were dropped: the new rows' keys were missing, and a second row with one was accepted.
        [Fact]
        public void A_flush_that_meets_a_replaced_file_keeps_the_changes_it_saves()
        {
            string dir = Seeded("flush", 100);
            using (var db = new XodContext(DatabasePath(dir)))
            {
                Assert.NotNull(db.Find<ReplacedIndexItem>(i => i.Id == 5)); // reads the primary key's block only
                ReplaceIndex(dir);
                db.InsertRange(Enumerable.Range(1001, 6000).Select(i => new ReplacedIndexItem { Id = i, Code = "z" + i, Name = "n" + i }).ToList());
                Assert.Equal(1001, db.Find<ReplacedIndexItem>(i => i.Code == "z1001")?.Id);
                Assert.Equal(7000, db.Find<ReplacedIndexItem>(i => i.Code == "z7000")?.Id);
                Assert.Equal(7, db.Find<ReplacedIndexItem>(i => i.Code == Code(7))?.Id);
            }

            using var again = new XodContext(DatabasePath(dir));
            Assert.Equal(1001, again.Find<ReplacedIndexItem>(i => i.Code == "z1001")?.Id);
            Assert.Throws<ReservedUniqueKeyException>(() => again.Insert(new ReplacedIndexItem { Id = 99999, Code = "z1001" }));
        }

        // A delete takes the row's key out of every index, reading each block it hasn't yet. A replaced file made that
        // read load the type again in the middle of the delete, from the other context's file, which still had the
        // keys the delete had already taken out, and the delete went on recording into the log it had dropped.
        [Fact]
        public void A_delete_after_a_replaced_snapshot_is_kept()
        {
            string dir = Seeded("delete", 100);
            using (var db = new XodContext(DatabasePath(dir)))
            {
                var seven = db.Find<ReplacedIndexItem>(i => i.Id == 7); // reads the primary key's block only
                Assert.NotNull(seven);
                ReplaceIndex(dir);
                Assert.True(db.Delete(seven));
                Assert.Null(db.Find<ReplacedIndexItem>(i => i.Id == 7));
                Assert.Null(db.Find<ReplacedIndexItem>(i => i.Code == Code(7)));
                db.Insert(new ReplacedIndexItem { Id = 7, Code = "fresh7" });
            }

            using var again = new XodContext(DatabasePath(dir));
            Assert.Equal("fresh7", again.Find<ReplacedIndexItem>(i => i.Id == 7)?.Code);
            Assert.Null(again.Find<ReplacedIndexItem>(i => i.Code == Code(7)));
            again.Insert(new ReplacedIndexItem { Id = 500, Code = Code(7) });
        }

        // An update finds its row's offset in the cached page bytes, checks the new keys, changes the index, then
        // writes the row at that offset. A block read in between that found the file replaced built the unread blocks
        // from the pages, opening each as a document, which took it out of the raw page cache; the write then found no
        // page and returned, and Update returned true with nothing changed (ISS-113). With the same rows in the new
        // file the first version passed by chance (the old offsets still held); with more rows it threw.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_update_after_a_replaced_snapshot_is_kept(bool moreRows)
        {
            string dir = Seeded("update-" + moreRows, 100);
            using (var db = new XodContext(DatabasePath(dir)))
            {
                var seven = db.Find<ReplacedIndexItem>(i => i.Id == 7); // reads the primary key's block only
                Assert.NotNull(seven);
                if (moreRows)
                {
                    using var writer = new XodContext(DatabasePath(dir));
                    writer.InsertRange(Enumerable.Range(101, 400)
                        .Select(i => new ReplacedIndexItem { Id = i, Code = Code(i) + new string('x', 40), Name = "n" + i }).ToList());
                    writer.ClearCache();
                    File.Delete(IndexFile(dir));
                    Assert.NotNull(writer.Find<ReplacedIndexItem>(i => i.Id == 1));
                }
                else
                {
                    ReplaceIndex(dir);
                }

                seven.Code = "zz-new";
                seven.Name = "renamed";
                Assert.True(db.Update(seven));
                Assert.Equal("renamed", db.Find<ReplacedIndexItem>(i => i.Id == 7)?.Name);
                Assert.Equal(7, db.Find<ReplacedIndexItem>(i => i.Code == "zz-new")?.Id);
                Assert.Null(db.Find<ReplacedIndexItem>(i => i.Code == Code(7)));
            }

            using var again = new XodContext(DatabasePath(dir));
            Assert.Equal("renamed", again.Find<ReplacedIndexItem>(i => i.Id == 7)?.Name);
            Assert.Equal(7, again.Find<ReplacedIndexItem>(i => i.Code == "zz-new")?.Id);
            Assert.Null(again.Find<ReplacedIndexItem>(i => i.Code == Code(7)));
            again.Insert(new ReplacedIndexItem { Id = 1000, Code = Code(7) });
        }

        // A context records an index file it couldn't delete, and never trusts it; a deletion that succeeds clears the
        // record. In a transaction that deletion is undone by a rollback, which put the file back with the record
        // cleared, so the file, missing a row, was trusted after the close and a second row with that row's key was
        // accepted. The record changes only outside a transaction. (On Unix an open file can be deleted, so this
        // case doesn't arise there.)
        [Fact]
        public void A_rollback_doesn_t_bring_back_an_index_file_as_trusted()
        {
            string dir = Seeded("rollback", 1);
            var hold = new FileStream(IndexFile(dir), FileMode.Open, FileAccess.Read, FileShare.None);
            try
            {
                using var db = new XodContext(DatabasePath(dir), options: new DatabaseOptions { EnableIndexing = false });
                db.Insert(new ReplacedIndexItem { Id = 2, Code = Code(2), Name = "n2" }); // can't load the file, nor delete it
                var one = db.Find<ReplacedIndexItem>(i => i.Id == 1);
                var two = db.Find<ReplacedIndexItem>(i => i.Id == 2);
                var tx = db.BeginTransaction();
                hold.Dispose();
                Assert.True(db.Delete(one));
                Assert.True(db.Delete(two)); // the table's last row: its index file is deleted
                tx.Rollback();
            }
            finally
            {
                hold.Dispose();
            }

            using var again = new XodContext(DatabasePath(dir));
            Assert.Equal(2, again.Find<ReplacedIndexItem>(i => i.Code == Code(2))?.Id);
            Assert.Throws<ReservedUniqueKeyException>(() => again.Insert(new ReplacedIndexItem { Id = 3, Code = Code(2) }));
        }

        // A file held by another program that shares nothing can't be loaded, nor replaced by the rebuilt index; the
        // rebuilt index is a cache, so the query goes on without writing it, and leaves no half-done write beside it
        // (a .tmp the next open would move into place if the file were gone by then).
        [Fact]
        public void A_query_isn_t_failed_by_an_index_file_it_can_t_load_or_replace()
        {
            string dir = Seeded("held", 50);
            using (new FileStream(IndexFile(dir), FileMode.Open, FileAccess.Read, FileShare.None))
            using (var db = new XodContext(DatabasePath(dir)))
            {
                Assert.Equal(7, db.Find<ReplacedIndexItem>(i => i.Code == Code(7))?.Id);
                Assert.False(File.Exists(IndexFile(dir) + ".tmp"));
            }
        }

        // Deleting a table's last row deletes its index file too. One another program held failed the delete, after the
        // row was gone; it now stays, untrusted, as a file the context couldn't delete does (below).
        [Fact]
        public void Deleting_a_table_s_last_row_isn_t_failed_by_an_index_file_held_open()
        {
            string dir = Seeded("last-row", 1);
            using (var db = new XodContext(DatabasePath(dir)))
            {
                var one = db.Find<ReplacedIndexItem>(i => i.Id == 1);
                Exception failure;
                using (new FileStream(IndexFile(dir), FileMode.Open, FileAccess.Read, FileShare.Read))
                    failure = Record.Exception(() => db.Delete(one));
                Assert.Null(failure);
                Assert.Null(db.Find<ReplacedIndexItem>(i => i.Id == 1));
            }

            using var again = new XodContext(DatabasePath(dir));
            Assert.Empty(again.Select<ReplacedIndexItem>());
            Assert.Null(again.Find<ReplacedIndexItem>(i => i.Code == Code(1)));
            again.Insert(new ReplacedIndexItem { Id = 1, Code = Code(1) });
        }

        // A write to a table whose index isn't loaded (indexing off) loads it to keep it current, and deletes a file it
        // can't load. A file it can't delete failed the write; it now stays, untrusted, until an open alone drops it.
        [Fact]
        public void A_write_isn_t_failed_by_an_index_file_it_can_t_delete()
        {
            string dir = Seeded("abandoned", 50);
            using (new FileStream(IndexFile(dir), FileMode.Open, FileAccess.Read, FileShare.None))
            using (var db = new XodContext(DatabasePath(dir), options: new DatabaseOptions { EnableIndexing = false }))
                db.Insert(new ReplacedIndexItem { Id = 51, Code = Code(51), Name = "n51" });

            using var again = new XodContext(DatabasePath(dir));
            Assert.Equal(51, again.Find<ReplacedIndexItem>(i => i.Code == Code(51))?.Id);
            Assert.Throws<ReservedUniqueKeyException>(() => again.Insert(new ReplacedIndexItem { Id = 52, Code = Code(51) }));
        }

        // The file left behind misses the row written while it was held, so the context that couldn't delete it
        // never loads it, even once it can read it again (held now by a program that shares reading, not deletion)
        // and the caches are cleared. Its time is set after the pages', as when they were written within the 50 ms
        // the load's guess by time allows: what keeps the file out is the context's own record, not that guess.
        // (On Unix an open file can be deleted, so this case doesn't arise there.)
        [Fact]
        public void An_index_file_that_couldn_t_be_deleted_isn_t_loaded_later()
        {
            string dir = Seeded("undeleted", 50);
            using (var db = new XodContext(DatabasePath(dir), options: new DatabaseOptions { EnableIndexing = false }))
            {
                using (new FileStream(IndexFile(dir), FileMode.Open, FileAccess.Read, FileShare.None))
                    db.Insert(new ReplacedIndexItem { Id = 51, Code = Code(51), Name = "n51" });
                if (File.Exists(IndexFile(dir)))
                    File.SetLastWriteTimeUtc(IndexFile(dir), DateTime.UtcNow.AddMinutes(1));
                using (new FileStream(IndexFile(dir), FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read))
                {
                    db.ClearCache();
                    db.Insert(new ReplacedIndexItem { Id = 52, Code = Code(52), Name = "n52" });
                    db.Dispose();
                }
            }

            using var again = new XodContext(DatabasePath(dir));
            Assert.Equal(51, again.Find<ReplacedIndexItem>(i => i.Code == Code(51))?.Id);
            Assert.Equal(52, again.Find<ReplacedIndexItem>(i => i.Code == Code(52))?.Id);
            Assert.Throws<ReservedUniqueKeyException>(() => again.Insert(new ReplacedIndexItem { Id = 53, Code = Code(51) }));
        }

        // ISS-110: a commit that fails after its pages are written is undone: the pages go back and the index files
        // are dropped (they may hold its keys). The undo deleted every index file itself, and one another program
        // held failed it, so the context refused writes until reopened. One that can't be dropped is now left
        // untrusted, with a marker that says the indexes are behind, until an open alone drops it: the undo
        // finishes and the context goes on writing. (On Unix an open file can be deleted: the case doesn't arise.)
        [Fact]
        public void A_commit_undone_isn_t_failed_by_an_index_file_held_open()
        {
            if (!OperatingSystem.IsWindows())
                return;
            string dir = Seeded("undo-held", 3);
            using (var db = new XodContext(DatabasePath(dir)))
            {
                Assert.NotNull(db.Find<ReplacedIndexItem>(i => i.Code == Code(1)));
                Exception commit;
                using (new FileStream(IndexFile(dir), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using var tx = db.BeginTransaction();
                    db.Insert(new ReplacedIndexItem { Id = 4, Code = Code(4), Name = "n4" });
                    commit = Record.Exception(() => tx.Commit()); // its index can't be written: undone
                    Assert.NotNull(commit);
                    Assert.Null(db.Find<ReplacedIndexItem>(i => i.Code == Code(4)));
                    db.Insert(new ReplacedIndexItem { Id = 5, Code = Code(5), Name = "n5" });
                }
                db.Insert(new ReplacedIndexItem { Id = 4, Code = Code(4), Name = "again" });
            }
            // The file it couldn't drop stays untrusted after the close, until an open alone drops it.
            Assert.NotEmpty(Directory.GetFiles(dir, "*.xidxdirty"));

            using var again = new XodContext(DatabasePath(dir));
            for (int i = 1; i <= 5; i++)
            {
                string code = Code(i);
                Assert.Equal(i, again.Find<ReplacedIndexItem>(x => x.Code == code)?.Id);
            }
            Assert.Equal("again", again.Find<ReplacedIndexItem>(x => x.Id == 4).Name);
        }

        // ISS-110, through the transaction's own images: deleting a table's last row inside a transaction removes its
        // index, and the index file's delete took an image of it. A commit then undone wrote that image back over the
        // file, and one another program held failed the undo, so the context refused writes. A transaction keeps no
        // image of an index file: the undo drops them all.
        [Fact]
        public void A_commit_undone_writes_no_index_file_back()
        {
            if (!OperatingSystem.IsWindows())
                return; // a file held open blocks replacing it on Windows only
            string dir = Seeded("undo-image", 3);
            string tagIndex = Path.Combine(dir, typeof(ReplacedIndexTagged).FullName + ".xidx");
            using (var db = new XodContext(DatabasePath(dir)))
            {
                db.Insert(new ReplacedIndexTagged { Id = 1, Tag = "lone" });
                db.Flush();
                Assert.True(File.Exists(tagIndex));
                using (new FileStream(tagIndex, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (new FileStream(IndexFile(dir), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using var tx = db.BeginTransaction();
                    Assert.True(db.Delete(new ReplacedIndexTagged { Id = 1 })); // its table's last row: its index goes
                    db.Insert(new ReplacedIndexItem { Id = 4, Code = Code(4), Name = "n4" });
                    Assert.NotNull(Record.Exception(() => tx.Commit())); // the item index can't be written: undone
                }
                db.Insert(new ReplacedIndexItem { Id = 5, Code = Code(5), Name = "n5" }); // not refused
            }

            using var again = new XodContext(DatabasePath(dir));
            Assert.Equal("lone", again.Find<ReplacedIndexTagged>(x => x.Id == 1)?.Tag);
            Assert.Null(again.Find<ReplacedIndexItem>(x => x.Code == Code(4)));
            Assert.Equal(5, again.Find<ReplacedIndexItem>(x => x.Code == Code(5))?.Id);
        }

        // ISS-115: InsertRange writes its rows, then saves its index. Another context reading the index file (a load
        // or a block read keeps it open, sharing reading) kept that save from replacing or appending to it on Windows,
        // and InsertRange threw though its rows were on the pages: a retry met their keys. The save is a derived
        // cache's, left to the next flush, and the index files stay untrusted until it is made.
        [Fact]
        public void An_insert_range_isn_t_failed_by_another_context_reading_its_index_file()
        {
            string dir = Seeded("reading", 2);
            using (var db = new XodContext(DatabasePath(dir)))
            {
                Assert.NotNull(db.Find<ReplacedIndexItem>(i => i.Code == Code(1)));
                using (new FileStream(IndexFile(dir), FileMode.Open, FileAccess.Read, FileShare.Read))
                    db.InsertRange(Enumerable.Range(3, 500).Select(i => new ReplacedIndexItem { Id = i, Code = Code(i), Name = "n" + i }).ToList());
                Assert.Equal(300, db.Find<ReplacedIndexItem>(i => i.Code == Code(300))?.Id);
                db.Flush(); // the reader gone, the next flush saves it
            }

            using var again = new XodContext(DatabasePath(dir));
            Assert.Equal(502, again.Find<ReplacedIndexItem>(i => i.Code == Code(502))?.Id);
            Assert.Throws<ReservedUniqueKeyException>(() => again.Insert(new ReplacedIndexItem { Id = 9999, Code = Code(300) }));
        }
    }
}
