using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-209: a page file no table names. A page reaches the disk before the table that names it and leaves after
    /// the last table naming it is written without it (ISS-196), so a crash between the two leaves a page no table
    /// names: no read reaches it, and nothing removed it. A context names such a page in its marker before the window
    /// opens, and an open that holds the database alone deletes the pages a dead context's marker names that no table
    /// names, once every table file has been read; a table it can't read leaves every page where it is, and a page no
    /// marker names is never deleted.
    /// </summary>
    public class XodUnnamedPageTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-unnamed-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class UpPerson
        {
            [PrimaryKey] public int Id { get; set; }
            public string Name { get; set; }
        }

        public class UpItem
        {
            [PrimaryKey] public int Id { get; set; }
            public int UpBoxId { get; set; }
            public string Text { get; set; }
            [ParentKey(nameof(UpBoxId))] public UpBox Box { get; set; }
        }

        public class UpBox
        {
            [PrimaryKey] public int Id { get; set; }
            [Children] public List<UpItem> Items { get; set; }
        }

        private static string[] Pages(string dir) =>
            Directory.GetFiles(dir, "*.xpag").Where(f => f.EndsWith(".xpag", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // The pages the table files name, read as text (an XML database without a password).
        private static HashSet<string> NamedPages(string dir) =>
            Directory.GetFiles(dir, "*.xtab")
                .SelectMany(t => System.Xml.Linq.XDocument.Load(t).Descendants().Select(e => e.Attribute("file")?.Value))
                .Where(f => f != null).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A table's first row: its page is written, and the process dies before its table file is. The page is named
        // by no table, as there is none yet.
        [Fact]
        public void A_crash_before_a_new_table_is_written_leaves_no_page_after_the_next_open()
        {
            string dir = Path.Combine(tempRoot, "first-row");
            var crash = XodCrashSafetyTests.RunWriterProcess("firstrow", dir, ("XOD_CRASH_AT_STEP", "file-write:*.xtab"));
            Assert.Contains("XOD_CRASH_AT_STEP", crash.Output);
            Assert.NotEqual(0, crash.ExitCode);
            Assert.NotEmpty(Pages(dir));
            Assert.Empty(NamedPages(dir));

            using (var db = new XodContext(Path.Combine(dir, "crash.xod")))
            {
                Assert.Empty(Pages(dir));
                Assert.Equal(0, db.Count<CrashMember>());
                db.Insert(new CrashMember { Id = 1, Email = "e1" });
                Assert.Equal(1, db.Count<CrashMember>());
            }
            Assert.Equal(NamedPages(dir).OrderBy(n => n, StringComparer.Ordinal), Pages(dir));
        }

        // Every row deleted: the table is written without its pages, and the process dies before it deletes them.
        [Fact]
        public void A_crash_before_emptied_pages_are_deleted_leaves_none_after_the_next_open()
        {
            string dir = Path.Combine(tempRoot, "emptied");
            var setup = XodCrashSafetyTests.RunWriterProcess("members", dir);
            Assert.True(setup.ExitCode == 0, setup.Output);
            var crash = XodCrashSafetyTests.RunWriterProcess("deletemembers", dir, ("XOD_CRASH_AT_STEP", "file-deleting:*.xpag"));
            Assert.Contains("XOD_CRASH_AT_STEP", crash.Output);
            Assert.NotEqual(0, crash.ExitCode);
            Assert.Contains(Pages(dir), p => !NamedPages(dir).Contains(p));

            using (var db = new XodContext(Path.Combine(dir, "crash.xod")))
            {
                Assert.Equal(0, db.Count<CrashMember>());
                Assert.Null(db.Find<CrashMember>(m => m.Email == "e1"));
            }
            Assert.Empty(Pages(dir));
        }

        // A context that wrote many pages over its life, statement by statement, dies with every statement written.
        // Its table file then missing (a copy not finished) costs no page: each page's record lasted only until its
        // statement was written. The review of TASK-104: records lasted until the indexes caught up, which this
        // context never reached, so the open deleted every page it had made, and the table, back, named them.
        [Fact]
        public void A_crash_after_its_statements_costs_no_page_when_a_table_is_missing()
        {
            string dir = Path.Combine(tempRoot, "long-lived");
            var crash = XodCrashSafetyTests.RunWriterProcess("longlived", dir);
            Assert.Contains("INSERTED", crash.Output);
            Assert.NotEqual(0, crash.ExitCode);
            string[] pages = Pages(dir);
            Assert.True(pages.Length > 5, pages.Length + " pages");

            string table = Path.Combine(dir, typeof(CrashNote).FullName + ".xtab");
            string aside = Path.Combine(tempRoot, "long-lived-aside.xtab");
            File.Move(table, aside);
            using (new XodContext(Path.Combine(dir, "crash.xod")))
                Assert.Equal(pages, Pages(dir));
            File.Move(aside, table);

            using var db = new XodContext(Path.Combine(dir, "crash.xod"));
            Assert.Equal(400, db.Count<CrashNote>());
            Assert.Equal(400, db.Select<CrashNote>().Count());
        }

        // A trigger's flush in the middle of a statement, after the statement chose a new page and before the page is
        // pending: the record stays (the review of TASK-104: the flush cleared it, so the page, written before its
        // table and then left by the crash, survived every open).
        [Theory]
        [InlineData("Direct")]
        [InlineData("Buffered")]
        public void A_trigger_s_flush_in_a_statement_keeps_the_record_of_the_page_it_chose(string durability)
        {
            string dir = Path.Combine(tempRoot, "trigger-" + durability);
            var setup = XodCrashSafetyTests.RunWriterProcess("emptytables", dir);
            Assert.True(setup.ExitCode == 0, setup.Output);
            var crash = XodCrashSafetyTests.RunWriterProcess("triggerflush", dir,
                ("XOD_CRASH_AT_STEP", "file-write:" + typeof(CrashCustomer).FullName + ".xtab"), ("XOD_TEST_DURABILITY", durability));
            Assert.Contains("XOD_CRASH_AT_STEP", crash.Output);
            Assert.Contains(Pages(dir), p => !NamedPages(dir).Contains(p));

            using (new XodContext(Path.Combine(dir, "crash.xod")))
                Assert.DoesNotContain(Pages(dir), p => !NamedPages(dir).Contains(p));
        }

        // A statement whose batch was written and which then failed: its undo puts the files back, and the process
        // dies in the middle of it. The records stay through the undo (the review of TASK-104: the batch's end cleared
        // them, so the page the undo hadn't taken back yet survived every open).
        [Fact]
        public void A_crash_in_the_undo_of_a_written_batch_leaves_no_page_after_the_next_open()
        {
            string dir = Path.Combine(tempRoot, "range-undo");
            var crash = XodCrashSafetyTests.RunWriterProcess("rangeundo", dir, ("XOD_FAIL_AT_STEP", "batch-written"), ("XOD_CRASH_AT_STEP", "undo-file:2"));
            Assert.Contains("XOD_CRASH_AT_STEP", crash.Output);
            Assert.Contains(Pages(dir), p => !NamedPages(dir).Contains(p));

            using (var db = new XodContext(Path.Combine(dir, "crash.xod")))
            {
                Assert.DoesNotContain(Pages(dir), p => !NamedPages(dir).Contains(p));
                Assert.Equal(0, db.Count<CrashNote>());
            }
        }

        // A conversion into another folder after such a crash: the converted database's first open deletes the page.
        [Fact]
        public void A_conversion_after_a_crash_takes_the_record_and_the_converted_database_drops_the_page()
        {
            string dir = Path.Combine(tempRoot, "convert");
            var crash = XodCrashSafetyTests.RunWriterProcess("firstrow", dir, ("XOD_CRASH_AT_STEP", "file-write:*.xtab"));
            Assert.Contains("XOD_CRASH_AT_STEP", crash.Output);
            Assert.NotEmpty(Pages(dir));

            string target = Path.Combine(tempRoot, "converted");
            XodMigrator.ConvertDatabase(dir, target, StorageFormat.Binary);
            using (var db = new XodContext(Directory.GetFiles(target, "*.xodb").Concat(Directory.GetFiles(target, "*.xod")).First()))
            {
                Assert.Empty(Pages(target));
                Assert.Equal(0, db.Count<CrashMember>());
            }
        }

        // A database of two tables (a standalone one, on raw pages, and a parent with its children), and a copy of
        // each of its pages under a name no table names.
        private static (string Path, string[] Kept, string[] Copies) WithCopies(string dir, string file, string password)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, file);
            using (var db = new XodContext(path, password))
            {
                for (int i = 1; i <= 3; i++)
                    db.Insert(new UpPerson { Id = i, Name = "p" + i });
                db.Insert(new UpBox { Id = 1, Items = new() { new UpItem { Id = 1, Text = "a" }, new UpItem { Id = 2, Text = "b" } } });
            }
            string[] kept = Pages(dir);
            Assert.NotEmpty(kept);
            var copies = new List<string>();
            foreach (string page in kept)
            {
                string copy = "0copy" + copies.Count + Path.GetFileNameWithoutExtension(page).Substring(1) + ".xpag";
                File.Copy(Path.Combine(dir, page), Path.Combine(dir, copy));
                copies.Add(copy);
            }
            return (path, kept, copies.ToArray());
        }

        private static void ReadsAsWritten(XodContext db)
        {
            Assert.Equal(3, db.Count<UpPerson>());
            Assert.Equal(new[] { "a", "b" }, db.Find<UpBox>(b => b.Id == 1).Items.Select(i => i.Text).OrderBy(t => t));
        }

        // The marker a context that died leaves (IOService.NotePageInFlight): its first byte, then a line per page.
        private static void LeaveMarker(string path, params string[] pages) =>
            File.WriteAllBytes(path + ".deadbeef" + Xod.Services.IOService.IndexesBehindSuffix,
                new byte[] { 0 }.Concat(System.Text.Encoding.UTF8.GetBytes(string.Concat(pages.Select(n => "\n" + n)))).ToArray());

        // The pages a dead context's marker names and no table names are deleted, in every format and under a
        // password; a page no marker names stays, and so does every page a table names; the marker goes.
        [Theory]
        [InlineData("u.xod", null)]
        [InlineData("u.xodj", null)]
        [InlineData("u.xodb", null)]
        [InlineData("u.xod", "secret")]
        public void An_open_deletes_the_pages_a_crash_named_that_no_table_names(string file, string password)
        {
            string dir = Path.Combine(tempRoot, "marked-" + Path.GetExtension(file).Substring(1) + (password == null ? "" : "-pw"));
            var (path, kept, copies) = WithCopies(dir, file, password);
            // The marker names one copy left unnamed and a page a table names (a page a crash left named after all).
            LeaveMarker(path, copies[0], kept[0], "0gone0000.xpag");

            using (var db = new XodContext(path, password))
            {
                Assert.Equal(kept.Concat(copies.Skip(1)).OrderBy(n => n, StringComparer.Ordinal), Pages(dir));
                ReadsAsWritten(db);
            }
            Assert.Empty(Directory.GetFiles(dir, "*.deadbeef" + Xod.Services.IOService.IndexesBehindSuffix));
        }

        // No crash names them: pages no table names are kept (a file put there by something else, or a table file
        // missing for a while: a copy, a sync or a restore not finished). The review of TASK-104: the first version
        // deleted every page no table named, and a table that came back named deleted pages.
        [Fact]
        public void Pages_no_crash_named_are_kept_when_no_table_names_them()
        {
            string dir = Path.Combine(tempRoot, "unmarked");
            var (path, kept, copies) = WithCopies(dir, "u.xod", null);
            using (var db = new XodContext(path))
                ReadsAsWritten(db);
            Assert.Equal(kept.Concat(copies).OrderBy(n => n, StringComparer.Ordinal), Pages(dir));

            string aside = Path.Combine(tempRoot, "aside");
            Directory.CreateDirectory(aside);
            string[] tables = Directory.GetFiles(dir, "*.xtab");
            foreach (string t in tables)
                File.Move(t, Path.Combine(aside, Path.GetFileName(t)));
            using (new XodContext(path))
                Assert.Equal(kept.Concat(copies).OrderBy(n => n, StringComparer.Ordinal), Pages(dir));
            foreach (string t in tables)
                File.Move(Path.Combine(aside, Path.GetFileName(t)), t);

            using (var db = new XodContext(path))
                ReadsAsWritten(db);
        }

        // A table file the open can't read: which pages it names isn't known, so every page stays. The rest of the
        // database reads as before.
        [Fact]
        public void A_table_the_open_can_t_read_leaves_every_page()
        {
            string dir = Path.Combine(tempRoot, "unreadable");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "u.xod");
            using (var db = new XodContext(path))
                db.Insert(new UpPerson { Id = 1, Name = "p1" });
            string page = Pages(dir).Single();
            File.Copy(Path.Combine(dir, page), Path.Combine(dir, "0orphan.xpag"));
            LeaveMarker(path, "0orphan.xpag");
            File.WriteAllText(Path.Combine(dir, "Some.Other.Type.xtab"), "not a table");

            using (var db = new XodContext(path))
            {
                Assert.Contains("0orphan.xpag", Pages(dir));
                Assert.Equal(1, db.Count<UpPerson>());
            }
            // The marker goes all the same: kept, every open would distrust every index file (the review of TASK-104).
            Assert.Empty(Directory.GetFiles(dir, "*.deadbeef" + Xod.Services.IOService.IndexesBehindSuffix));
        }
    }
}
