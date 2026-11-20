using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-211: a new page's name is drawn at random (unique within a process, not across processes), and was never
    /// checked against the folder: a name another process had drawn made two tables share one page file. A name already
    /// on disk is drawn again. The draws are injected, so the clash is certain.
    /// </summary>
    public class XodPageNameTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-pagename-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class PnRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        private static void DrawPagesFrom(XodContext db, params string[] codes)
        {
            object engine = db.Engine;
            object io = engine.GetType().GetField("ioService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
            var queue = new Queue<string>(codes);
            io.GetType().GetField("pageCodes", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(io, (Func<string>)(() => queue.Dequeue()));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_page_name_already_on_disk_is_drawn_again(bool indexing)
        {
            string dir = Path.Combine(tempRoot, indexing ? "indexed" : "unindexed");
            Directory.CreateDirectory(dir);
            string taken = Path.Combine(dir, "aaaaaaaaa.xpag");
            File.WriteAllText(taken, "another table's page");

            using (var db = new XodContext(Path.Combine(dir, "pn.xod"), null, new DatabaseOptions { EnableIndexing = indexing }))
            {
                DrawPagesFrom(db, "aaaaaaaaa", "bbbbbbbbb", "ccccccccc");
                db.Insert(new PnRow { Name = "one" });
                Assert.Equal("one", db.Find<PnRow>(r => r.Id == 1)?.Name);
            }

            Assert.Equal("another table's page", File.ReadAllText(taken));
            Assert.True(File.Exists(Path.Combine(dir, "bbbbbbbbb.xpag")));
            using (var db = new XodContext(Path.Combine(dir, "pn.xod"), null, new DatabaseOptions { EnableIndexing = indexing }))
                Assert.Equal("one", db.Find<PnRow>(r => r.Id == 1)?.Name);
        }
    }
}
