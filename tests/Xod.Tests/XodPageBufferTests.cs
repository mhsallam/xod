using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xod;
using Xod.Services;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-105, as reported: a page's bytes stay the page's while a reader holds them. A reader takes a page from the
    /// cache and reads its bytes after letting the cache's lock go. A read beside it that dropped the page from the cache
    /// (a <c>Select</c> turns a cached page into a document) gave the page's array back to the pool, until ISS-143
    /// (<c>XodPageBufferLifetimeTests</c>), and the next page loaded was read into it: the first reader read another
    /// page's bytes. Through the public calls, the reader held at the step between the two
    /// (<see cref="IOService.RowBytesHeldStep"/>). No page's array is pooled since ISS-212 (<see cref="RawPageCache"/>),
    /// so the test now guards that a dropped page's bytes stay with the reader holding them.
    /// </summary>
    [Collection(InProcessHookCollection.Name)]
    public class XodPageBufferTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-pagebuf-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class PbRow
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
        }

        public class PbOth
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
        }

        private static readonly AsyncLocal<bool> Held = new AsyncLocal<bool>();

        // A lookup by key through the index reads its row from the page's bytes by the row's code. The other table has
        // the same shape (a type name, a key and a name of the same lengths), so its page read into the dropped array
        // puts a row of the same form where the held row was: the fast parse reads it rather than failing over to a
        // read of the document, which would hide the overwrite.
        [Fact]
        public async Task A_page_a_reader_holds_keeps_its_bytes_when_a_read_beside_it_drops_the_page()
        {
            const bool indexing = true;
            string dir = Path.Combine(tempRoot, "indexed");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "pb.xod");
            var options = new DatabaseOptions { EnableIndexing = indexing };
            using (var seed = new XodContext(path, null, options))
            {
                for (int i = 1; i <= 50; i++)
                {
                    seed.Insert(new PbRow { Id = i, Name = "row " + i });
                    seed.Insert(new PbOth { Id = i, Name = "oth " + i });
                }
            }

            using var db = new XodContext(path, null, options);
            using var taken = new ManualResetEventSlim();
            using var go = new ManualResetEventSlim();
            int pauses = 0;
            Action<string> hook = step =>
            {
                if (Held.Value && step == IOService.RowBytesHeldStep && Interlocked.Increment(ref pauses) == 1)
                {
                    taken.Set();
                    go.Wait(TimeSpan.FromSeconds(30));
                }
            };
            FaultInjection.InProcess += hook;
            try
            {
                var reader = Task.Run(() =>
                {
                    Held.Value = true;
                    return db.Find<PbRow>(r => r.Id == 7);
                });
                Assert.True(taken.Wait(TimeSpan.FromSeconds(30)), "the reader never took the page");

                // Beside it: the same table read whole (its page turned into a document), then a row of another table
                // looked up by key, its page loaded into whatever array the pool hands out.
                Assert.Equal(50, db.Select<PbRow>().Count());
                Assert.Equal("oth 7", db.Find<PbOth>(o => o.Id == 7)?.Name);
                go.Set();

                var row = await reader;
                Assert.NotNull(row);
                Assert.Equal("row 7", row.Name);
            }
            finally
            {
                go.Set();
                FaultInjection.InProcess -= hook;
            }
        }
    }
}
