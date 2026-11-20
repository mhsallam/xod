using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Xod.Services;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-196: a failed statement's undo put its files back in no particular order, so a crash in the middle of it
    /// could leave a table naming a page the undo had deleted already (a page the statement made) or not put back yet
    /// (a page the statement deleted). The undo now puts back the pages it found, then the tables, then deletes the
    /// files it made, pages last: at each step every table names only pages on disk.
    /// </summary>
    [Collection(InProcessHookCollection.Name)]
    public class XodStatementUndoOrderTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "xod-undo-order-" + Guid.NewGuid().ToString("N")[..8]);

        public XodStatementUndoOrderTests() => Directory.CreateDirectory(dir);

        public void Dispose()
        {
            FaultInjection.InProcess = null;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        private static byte[] Table(params string[] pages) =>
            System.Text.Encoding.UTF8.GetBytes(new XElement("Table", new XElement("Pages",
                pages.Select(p => new XElement("Page", new XAttribute("file", p))))).ToString());

        private List<string> MissingPages()
        {
            var missing = new List<string>();
            foreach (string table in Directory.GetFiles(dir, "*.xtab"))
                foreach (var page in XDocument.Load(table).Descendants("Page"))
                    if (!File.Exists(Path.Combine(dir, page.Attribute("file").Value)))
                        missing.Add(page.Attribute("file").Value);
            return missing;
        }

        // The statement made page b and deleted page c; its table named a and c, and names a and b. The undo's notes
        // are in the order the statement made them: the pages, then the table.
        [Fact]
        public void The_undo_keeps_every_table_naming_pages_on_disk()
        {
            string table = Path.Combine(dir, "T.xtab");
            string a = Path.Combine(dir, "a.xpag"), b = Path.Combine(dir, "b.xpag"), c = Path.Combine(dir, "c.xpag");
            File.WriteAllText(a, "<Page/>");
            File.WriteAllText(c, "<Page/>");
            byte[] before = Table("a.xpag", "c.xpag");
            File.WriteAllBytes(table, before);

            var savepoint = new StatementSavepoint();
            savepoint.BeforeWrite(b, 0);            // made by the statement
            File.WriteAllText(b, "<Page/>");
            savepoint.Changed(b);
            savepoint.KeepWhole(c, File.ReadAllBytes(c)); // deleted by the statement
            File.Delete(c);
            savepoint.Changed(c);
            savepoint.KeepWhole(table, before);
            File.WriteAllBytes(table, Table("a.xpag", "b.xpag"));
            savepoint.Changed(table);
            Assert.Empty(MissingPages());

            var seen = new List<string>();
            var thread = Thread.CurrentThread;
            FaultInjection.InProcess = step =>
            {
                if (Thread.CurrentThread == thread && step.StartsWith("undo-file"))
                    seen.AddRange(MissingPages().Select(p => step + ": " + p));
            };
            savepoint.Undo();
            FaultInjection.InProcess = null;

            Assert.Empty(seen);
            Assert.Empty(MissingPages());
            Assert.Equal(before, File.ReadAllBytes(table));
            Assert.True(File.Exists(c));
            Assert.False(File.Exists(b));
        }

        // Putting back the page the statement deleted fails: the table isn't put back after it (it would name that
        // page), and the page the statement made stays (the table it wrote names it). The failure is thrown.
        [Fact]
        public void An_undo_that_fails_to_put_a_page_back_leaves_the_tables_as_written()
        {
            string table = Path.Combine(dir, "T.xtab");
            string a = Path.Combine(dir, "a.xpag"), b = Path.Combine(dir, "b.xpag"), c = Path.Combine(dir, "c.xpag");
            File.WriteAllText(a, "<Page/>");
            File.WriteAllText(c, "<Page/>");
            byte[] before = Table("a.xpag", "c.xpag");
            File.WriteAllBytes(table, before);

            var savepoint = new StatementSavepoint();
            savepoint.BeforeWrite(b, 0);
            File.WriteAllText(b, "<Page/>");
            savepoint.Changed(b);
            savepoint.KeepWhole(c, File.ReadAllBytes(c));
            File.Delete(c);
            savepoint.Changed(c);
            savepoint.KeepWhole(table, before);
            byte[] written = Table("a.xpag", "b.xpag");
            File.WriteAllBytes(table, written);
            savepoint.Changed(table);

            var thread = Thread.CurrentThread;
            int steps = 0;
            FaultInjection.InProcess = step =>
            {
                if (Thread.CurrentThread == thread && step.StartsWith("undo-file") && ++steps == 1)
                    throw new IOException("held by another program");
            };
            Assert.Throws<IOException>(() => savepoint.Undo());
            FaultInjection.InProcess = null;

            Assert.Equal(1, steps);
            Assert.Empty(MissingPages());
            Assert.Equal(written, File.ReadAllBytes(table));
            Assert.True(File.Exists(b));
        }
    }
}
