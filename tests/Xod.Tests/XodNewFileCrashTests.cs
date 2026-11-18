using System;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-134: a file written whole goes beside its file as <c>.tmp</c> and is moved over it, so a crash leaves the
    /// old file or the new one. A new page was written straight to its name, and a crash halfway left half a page, which
    /// the next open read and failed on. And a <c>.tmp</c> with no file beside it was moved into place at the next open,
    /// though nothing says it was written to its end.
    /// </summary>
    public class XodNewFileCrashTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-newfile-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        // A table's first row: its page, its table file and its index file are new. The writer dies with part of one
        // of them on disk. (The index is written first, under the mark of ISS-043, which drops it at the next open.)
        [Theory]
        [InlineData("*.xpag")]
        [InlineData("*.xtab")]
        [InlineData("*.xidx")]
        public void A_crash_partway_through_a_new_file_leaves_the_database_readable(string file)
        {
            string dir = Path.Combine(tempRoot, file.Substring(2));
            var crash = XodCrashSafetyTests.RunWriterProcess("firstrow", dir, ("XOD_CRASH_AT_STEP", "file-partly-written:" + file));
            Assert.Contains("XOD_CRASH_AT_STEP", crash.Output);
            Assert.DoesNotContain("INSERTED", crash.Output);
            Assert.NotEqual(0, crash.ExitCode);

            using (var db = new XodContext(Path.Combine(dir, "crash.xod")))
            {
                Assert.Empty(db.QueryDelegate<CrashMember>(m => m.Email != null));
                db.Insert(new CrashMember { Id = 1, Email = "e1" });
            }

            using (var db = new XodContext(Path.Combine(dir, "crash.xod")))
                Assert.Equal(new[] { "e1" }, db.QueryDelegate<CrashMember>(m => m.Email != null).Select(m => m.Email).ToArray());
        }

        public class NfRow
        {
            [PrimaryKey] public int Id { get; set; }
            public string Text { get; set; }
        }

        // The recovery alone: a .tmp whose file isn't there may be cut short anywhere, so it is deleted, never put in
        // the file's place. (Every writer moves its .tmp over the file, which is never missing in between.)
        [Fact]
        public void A_tmp_with_no_file_beside_it_is_deleted_at_the_next_open()
        {
            string dir = Path.Combine(tempRoot, "tmp-alone");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "nf.xod");
            using (var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = false }))
                db.Insert(new NfRow { Id = 1, Text = "one" });

            string page = Directory.GetFiles(dir, "*.xpag").Single();
            byte[] bytes = File.ReadAllBytes(page);
            string halfPage = Path.Combine(dir, "half.xpag");
            File.WriteAllBytes(halfPage + ".tmp", bytes.Take(bytes.Length / 2).ToArray());

            using (var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = false }))
                Assert.Equal("one", db.Find<NfRow>(r => r.Id == 1).Text);

            Assert.False(File.Exists(halfPage + ".tmp"));
            Assert.False(File.Exists(halfPage));
        }
    }
}
