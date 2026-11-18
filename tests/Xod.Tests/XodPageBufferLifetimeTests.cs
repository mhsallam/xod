using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-143, found by the review of the row scanner: a reader takes a page from the cache and reads its bytes outside
    /// the cache's lock, while another reader beside it, under the same shared read lock, drops that page from the
    /// cache (a document read of the page, or an eviction by a load). The drop gave the page's array back to the pool
    /// and set its bytes to null, so the first reader threw (a parallel <c>Last</c> did), or, holding the span already,
    /// read whatever page rented the array next at its own page's offsets. A page that leaves the cache now keeps its
    /// bytes. The steps run one after another on one thread, the order the race takes.
    /// </summary>
    public class XodPageBufferLifetimeTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-pagebuf-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class PbOne
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class PbTwo
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        private static object Io(XodContext db)
        {
            object engine = db.Engine;
            return engine.GetType().GetField("ioService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        }

        private static object RawPageForRead(object io, string pageFile) =>
            io.GetType().GetMethod("RawPageForRead", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(io, new object[] { pageFile })!;

        private static byte[] Bytes(object rawPage) => (byte[])rawPage.GetType().GetField("Bytes")!.GetValue(rawPage);
        private static int Length(object rawPage) => (int)rawPage.GetType().GetField("Length")!.GetValue(rawPage)!;

        private static string PageOf(string dir, string table) =>
            Directory.GetFiles(dir, "*.xpag").Single(f => File.ReadAllText(f).Contains("<" + table + ">"));

        [Fact]
        public void A_page_dropped_from_the_cache_keeps_its_bytes_for_the_reader_that_holds_it()
        {
            string dir = Path.Combine(tempRoot, "drop");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "pb.xod");
            using (var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = true }))
            {
                db.InsertRange(Enumerable.Range(1, 50).Select(i => new PbOne { Name = "one" + i }).ToList());
                db.InsertRange(Enumerable.Range(1, 50).Select(i => new PbTwo { Name = "two" + i }).ToList());
            }
            string onePage = Path.GetFileName(PageOf(dir, nameof(PbOne)));
            string twoPage = Path.GetFileName(PageOf(dir, nameof(PbTwo)));

            using (var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = true }))
            {
                object io = Io(db);

                // A reader holds the first page.
                object held = RawPageForRead(io, onePage);
                byte[] heldBytes = Bytes(held);
                int heldLength = Length(held);
                byte[] expected = heldBytes.AsSpan(0, heldLength).ToArray();

                // Another reader opens it as a document, which drops it from the cache; then a load of another page.
                io.GetType().GetMethod("OpenFileOrCreate", BindingFlags.NonPublic | BindingFlags.Instance,
                        new[] { typeof(string), typeof(Type), typeof(bool), typeof(bool) })!
                    .Invoke(io, new object[] { onePage, null, true, false });
                RawPageForRead(io, twoPage);

                Assert.Same(heldBytes, Bytes(held));
                Assert.True(expected.AsSpan().SequenceEqual(Bytes(held).AsSpan(0, Length(held))),
                    "the held page's bytes were changed by the page loaded after it");
                Assert.Contains("<PbOne>", System.Text.Encoding.UTF8.GetString(Bytes(held), 0, Length(held)));
            }
        }
    }
}
