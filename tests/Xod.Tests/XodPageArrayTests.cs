using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-212: a page read from disk takes an array of its own size. Every load took at least 528 KB from a pool that
    /// a cached page never refilled (a reader may hold its array, ISS-105), so the floor only cost memory: a page of a
    /// few rows took half a megabyte on the large object heap.
    /// </summary>
    public class XodPageArrayTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-pagearray-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class PaRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [Fact]
        public void A_small_page_read_from_disk_takes_an_array_of_its_size()
        {
            string dir = Path.Combine(tempRoot, "small");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "pa.xod");
            using (var db = new XodContext(path))
                db.InsertRange(Enumerable.Range(1, 5).Select(i => new PaRow { Name = "r" + i }).ToList());
            string page = Directory.GetFiles(dir, "*.xpag").Single();

            using (var db = new XodContext(path))
            {
                object engine = db.Engine;
                object io = engine.GetType().GetField("ioService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
                object raw = io.GetType().GetMethod("RawPageForRead", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(io, new object[] { Path.GetFileName(page) })!;
                byte[] bytes = (byte[])raw.GetType().GetField("Bytes")!.GetValue(raw)!;
                Assert.Equal(new FileInfo(page).Length, bytes.Length);
                Assert.Equal(5, db.Count<PaRow>());
            }
        }

        // A page the raw writer appends to row by row moves once, to a full page's array; doubling from the page's own
        // size moved it again and again on its way to full.
        [Fact]
        public void A_page_appended_to_moves_to_a_full_page_s_array_once()
        {
            string dir = Path.Combine(tempRoot, "append");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "pa.xod");
            var options = new DatabaseOptions { EnableIndexing = true };
            using (var db = new XodContext(path, null, options))
                db.InsertRange(Enumerable.Range(1, 5).Select(i => new PaRow { Name = "r" + i }).ToList());
            string page = Path.GetFileName(Directory.GetFiles(dir, "*.xpag").Single());

            using (var db = new XodContext(path, null, options))
            {
                object engine = db.Engine;
                object io = engine.GetType().GetField("ioService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
                MethodInfo read = io.GetType().GetMethod("RawPageForRead", BindingFlags.NonPublic | BindingFlags.Instance)!;
                byte[] Array() { object raw = read.Invoke(io, new object[] { page })!; return (byte[])raw.GetType().GetField("Bytes")!.GetValue(raw)!; }

                var arrays = new System.Collections.Generic.List<byte[]>();
                for (int i = 0; i < 1500; i++)
                {
                    db.Insert(new PaRow { Name = "appended row " + i });
                    byte[] now = Array();
                    if (arrays.Count == 0 || !ReferenceEquals(arrays[^1], now))
                        arrays.Add(now);
                }
                Assert.Single(Directory.GetFiles(dir, "*.xpag"));
                Assert.Single(arrays);
            }
        }
    }
}
