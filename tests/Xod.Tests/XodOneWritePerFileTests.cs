// TASK-068 (found by its suite): a file waiting to be written was known by the object holding it, not by its name, so
// a second object for the same file (an encrypted index is saved as a new document each time) made two writes of one
// file, made side by side when enough files waited: they collided on the file's .tmp ("being used by another
// process"), or the older document could land last and leave the index behind its pages. The test counts the writes a
// statement makes of an index file, through the hook every file write passes first (IOService.BeforeFileMutate).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Xod.Tests
{
    public class XodOneWritePerFileTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-one-write-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string NewDatabase(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "data.xod");
        }

        // The names of the files the context writes from now on, in order.
        private static List<string> RecordWrites(XodContext context)
        {
            object engine = context.Engine;
            object io = engine.GetType().GetField("ioService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
            PropertyInfo hook = io.GetType().GetProperty("BeforeFileMutate", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var inner = (Action<string>)hook.GetValue(io)!;
            var written = new List<string>();
            hook.SetValue(io, (Action<string>)(file =>
            {
                lock (written)
                    written.Add(Path.GetFileName(file));
                inner(file);
            }));
            return written;
        }

        // An insert that adds a page saves its table's index (XmlEngine.AddToPage). With small pages, a parent with
        // many children adds several pages of the children's table in one statement: several documents of one index
        // file (an encrypted index is saved as a new document each time), all saved by the statement, which used to
        // write each when it ended (IOService.WriteSaved), side by side from 4 files. Each file a write of saved
        // files makes passes the hook once (IOService.FlushFilesList).
        [Fact]
        public void A_statement_writes_an_index_it_saved_several_times_once()
        {
            string path = NewDatabase("several");
            var options = new DatabaseOptions { InitialCreate = true, EnableIndexing = true, PageSize = 2048 };
            string index = typeof(TrustKid).FullName + ".xidx";
            var kids = Enumerable.Range(1, 40).Select(i => new TrustKid { Id = i, Name = new string('k', 100) + i }).ToList();
            using (var db = new XodContext(path, "pw", options))
            {
                // The tables and their indexes exist and are loaded before the statement measured.
                db.Insert(new TrustParent { Id = 1, Code = "first", Kids = new List<TrustKid> { new TrustKid { Id = 1000, Name = "k" } } });
                List<string> written = RecordWrites(db);
                db.Insert(new TrustParent { Id = 2, Code = "many", Kids = kids });
                Assert.True(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag").Length >= 4);
                Assert.Equal(new[] { index }, written.Where(f => f == index));
                Assert.Equal(written.Distinct().Count(), written.Count);
            }

            using (var db = new XodContext(path, "pw", options))
            {
                Assert.Equal(41, db.Select<TrustKid>().Count());
                foreach (var kid in kids)
                    Assert.NotNull(db.Find<TrustKid>(k => k.Id == kid.Id));
                Assert.Throws<ReservedPrimaryKeyException>(() => db.Insert(new TrustKid { Id = 40, Name = "again" }));
            }
        }
    }
}
