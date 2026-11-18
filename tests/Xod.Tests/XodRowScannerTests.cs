using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-143: the readers of a page's bytes found a row by the byte patterns <c>&lt;Row code="</c> and
    /// <c>&lt;/Row&gt;</c>, so a <c>[Markup]</c> value holding either, or a property named <c>Row</c>, ended a row early
    /// or started a forged one: <c>Select(skip, take)</c> dropped rows, <c>Last</c> returned a row made of a value's text,
    /// <c>Find</c> read the properties after a <c>Row</c> property as null and an update wrote the loss back. ISS-144: a
    /// row that failed to parse read as absent, and the fast read of a row of scalars read a value it didn't follow as
    /// the wrong value.
    /// </summary>
    public class XodRowScannerTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-rowscan-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class RsNote
        {
            public int Id { get; set; }
            [Markup] public string Body { get; set; }
            public string Tail { get; set; }
        }

        // A type of scalars alone: read by the fast paths that parse a row's bytes into the object directly.
        public class RsPlain
        {
            public int Id { get; set; }
            public string Row { get; set; }
            public string Tail { get; set; }
        }

        public class RsCount
        {
            public int Id { get; set; }
            public int Count { get; set; }
            public string Name { get; set; }
        }

        public class RsTag
        {
            public string Name { get; set; }
        }

        // Not a type of scalars alone: read through the row's XML.
        public class RsHolder
        {
            public int Id { get; set; }
            public string Row { get; set; }
            public string Tail { get; set; }
            public List<RsTag> Tags { get; set; }
        }

        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Tail { get; set; }
        }

        private string NewPath(string name, bool indexing)
        {
            string dir = Path.Combine(tempRoot, name + (indexing ? "-indexed" : "-unindexed"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "rs.xod");
        }

        private static XodContext Open(string path, bool indexing) =>
            new XodContext(path, null, new DatabaseOptions { EnableIndexing = indexing });

        private static readonly string[] Bodies =
        {
            "plain",
            "<p>ends a row early</Row> here</p>",
            "<Row code=\"999\"><RsNote><Id>77</Id></RsNote></Row>",
            "<b>last</b></Row>",
        };

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Markup_holding_the_row_markers_reads_back_whole(bool indexing)
        {
            string path = NewPath("markup", indexing);
            using (var db = Open(path, indexing))
            {
                foreach (var body in Bodies)
                    db.Insert(new RsNote { Body = body, Tail = "t" + body.Length });
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(Bodies, db.Select<RsNote>(0, 10).Select(n => n.Body).ToArray());
                Assert.Equal(new[] { Bodies[1], Bodies[2] }, db.Select<RsNote>(1, 2).Select(n => n.Body).ToArray());
                Assert.Equal(4, db.Count<RsNote>());
                var last = db.Last<RsNote>();
                Assert.Equal(4, last.Id);
                Assert.Equal(Bodies[3], last.Body);
                for (int id = 1; id <= 4; id++)
                {
                    var note = db.Find<RsNote>(n => n.Id == id);
                    Assert.Equal(Bodies[id - 1], note.Body);
                    Assert.Equal("t" + Bodies[id - 1].Length, note.Tail);
                }

                var second = db.Find<RsNote>(n => n.Id == 2);
                second.Tail = "changed";
                Assert.True(db.Update(second));
            }

            using (var db = Open(path, indexing))
            {
                var second = db.Find<RsNote>(n => n.Id == 2);
                Assert.Equal(Bodies[1], second.Body);
                Assert.Equal("changed", second.Tail);
                Assert.Equal(Bodies, db.Select<RsNote>(0, 10).Select(n => n.Body).ToArray());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_property_named_Row_keeps_the_properties_after_it(bool indexing)
        {
            string path = NewPath("prop", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 3; i++)
                {
                    db.Insert(new RsPlain { Row = "r" + i, Tail = "t" + i });
                    db.Insert(new RsHolder { Row = "r" + i, Tail = "t" + i, Tags = new() { new RsTag { Name = "g" + i } } });
                }
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(new[] { "t1", "t2", "t3" }, db.Select<RsPlain>(0, 10).Select(p => p.Tail).ToArray());
                Assert.Equal(new[] { "t2" }, db.Select<RsPlain>(1, 1).Select(p => p.Tail).ToArray());
                Assert.Equal("t3", db.Last<RsPlain>().Tail);
                Assert.Equal("t2", db.Find<RsPlain>(p => p.Id == 2).Tail);

                Assert.Equal(new[] { "t1", "t2", "t3" }, db.Select<RsHolder>(0, 10).Select(p => p.Tail).ToArray());
                Assert.Equal("t3", db.Last<RsHolder>().Tail);
                var holder = db.Find<RsHolder>(p => p.Id == 2);
                Assert.Equal("t2", holder.Tail);
                Assert.Equal("g2", holder.Tags.Single().Name);
                holder.Row = "r2b";
                Assert.True(db.Update(holder));
            }

            using (var db = Open(path, indexing))
            {
                var holder = db.Find<RsHolder>(p => p.Id == 2);
                Assert.Equal("r2b", holder.Row);
                Assert.Equal("t2", holder.Tail);
                Assert.Equal("g2", holder.Tags.Single().Name);
            }
        }

        // A guard: a class named Row wrote <Row> inside each row, which the old readers already read right.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_class_named_Row_reads_back_whole(bool indexing)
        {
            string path = NewPath("class", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 3; i++)
                    db.Insert(new Row { Name = "n" + i, Tail = "t" + i });
            }

            using (var db = Open(path, indexing))
            {
                Assert.Equal(new[] { "t1", "t2", "t3" }, db.Select<Row>(0, 10).Select(r => r.Tail).ToArray());
                Assert.Equal(new[] { "t2", "t3" }, db.Select<Row>(1, 5).Select(r => r.Tail).ToArray());
                Assert.Equal("t3", db.Last<Row>().Tail);
                var second = db.Find<Row>(r => r.Id == 2);
                Assert.Equal("t2", second.Tail);
                second.Name = "n2b";
                Assert.True(db.Update(second));
                Assert.Equal(3, db.Count<Row>());
            }

            using (var db = Open(path, indexing))
            {
                var second = db.Find<Row>(r => r.Id == 2);
                Assert.Equal("n2b", second.Name);
                Assert.Equal("t2", second.Tail);
            }
        }

        private static void DamagePage(string path, string typeElement, Func<string, string> change)
        {
            int changed = 0;
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag"))
            {
                string text = File.ReadAllText(file);
                if (!text.Contains("<" + typeElement + ">"))
                    continue;
                string damaged = change(text);
                Assert.NotEqual(text, damaged);
                File.WriteAllText(file, damaged);
                changed++;
            }
            Assert.Equal(1, changed);
        }

        // ISS-144: a row that doesn't parse is an error, not an absent row.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_row_that_does_not_parse_is_an_error_not_an_absent_row(bool indexing)
        {
            string path = NewPath("damaged", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 3; i++)
                {
                    db.Insert(new RsPlain { Row = "r" + i, Tail = "t" + i });
                    db.Insert(new RsNote { Body = "b" + i, Tail = "t" + i });
                }
            }

            // The second row's closing tag lost its '<': the row no longer closes where it should.
            DamagePage(path, "RsPlain", t => t.Replace("<Tail>t2</Tail>", "<Tail>t2/Tail>"));
            DamagePage(path, "RsNote", t => t.Replace("<Tail>t2</Tail>", "<Tail>t2/Tail>"));

            using (var db = Open(path, indexing))
            {
                Assert.Throws<DatabaseFileException>(() => db.Find<RsPlain>(p => p.Id == 2));
                Assert.Throws<DatabaseFileException>(() => db.Select<RsPlain>(0, 10).ToList());
                Assert.Throws<DatabaseFileException>(() => db.Find<RsNote>(p => p.Id == 2));
                Assert.Throws<DatabaseFileException>(() => db.Select<RsNote>(0, 10).ToList());
            }
        }

        // A row whose elements balance but whose XML doesn't parse: found in the page, then refused by the parse.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_row_whose_xml_does_not_parse_is_an_error_not_an_absent_row(bool indexing)
        {
            string path = NewPath("misnamed", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 3; i++)
                    db.Insert(new RsNote { Body = "b" + i, Tail = "t" + i });
            }

            DamagePage(path, "RsNote", t => t.Replace("<Tail>t2</Tail>", "<Tail>t2</Tial>"));

            using (var db = Open(path, indexing))
            {
                Assert.Throws<DatabaseFileException>(() => db.Find<RsNote>(p => p.Id == 2));
                Assert.Throws<DatabaseFileException>(() => db.Select<RsNote>(0, 10).ToList());
                Assert.Throws<DatabaseFileException>(() => db.Select<RsNote>(1, 1).ToList());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_cut_page_is_an_error_not_a_shorter_table(bool indexing)
        {
            string path = NewPath("cut", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 3; i++)
                    db.Insert(new RsNote { Body = "b" + i, Tail = "t" + i });
            }

            DamagePage(path, "RsNote", t => t.Substring(0, t.IndexOf("<Tail>t3</Tail>", StringComparison.Ordinal)));

            using (var db = Open(path, indexing))
            {
                Assert.Throws<DatabaseFileException>(() => db.Find<RsNote>(p => p.Id == 3));
                Assert.Throws<DatabaseFileException>(() => db.Select<RsNote>(0, 10).ToList());
                Assert.Throws<DatabaseFileException>(() => db.Count<RsNote>());
                Assert.Throws<DatabaseFileException>(() => db.Last<RsNote>());
            }
        }

        // A value the fast read of a row of scalars can't parse was left at its default (0), where the XML read of the
        // same row throws: the fast read now leaves the row to it.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_value_that_does_not_parse_is_an_error_not_a_default(bool indexing)
        {
            string path = NewPath("value", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 3; i++)
                    db.Insert(new RsCount { Count = i * 5, Name = "n" + i });
            }

            DamagePage(path, "RsCount", t => t.Replace("<Count>10</Count>", "<Count>10x</Count>"));

            using (var db = Open(path, indexing))
            {
                // The slice first: a Find without the index opens the page as a document, and the slice read that.
                Assert.Throws<FormatException>(() => db.Select<RsCount>(0, 10).ToList());
                Assert.Throws<FormatException>(() => db.Find<RsCount>(p => p.Id == 2));
                // By its key a row past the bad one reads; a scan meets the bad row first.
                if (indexing)
                    Assert.Equal(15, db.Find<RsCount>(p => p.Id == 3).Count);
            }
        }

        private static string[] Plain(IEnumerable<RsPlain> rows) =>
            rows.Select(p => $"{p.Id}|{p.Row}|{p.Tail}").ToArray();

        // Valid XML that XOD doesn't write, in a page of a type of scalars: the fast read declines what it doesn't
        // follow and the XML read reads it, so every read agrees. It read <![CDATA[ as text, a value after a CDATA
        // holding "</" as its default, every Id as 0 after a comment, and a quoted '>' as the end of a tag.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Xml_the_fast_read_does_not_follow_is_read_as_xml(bool indexing)
        {
            string path = NewPath("valid", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 5; i++)
                    db.Insert(new RsPlain { Row = "r" + i, Tail = "t" + i });
            }

            DamagePage(path, "RsPlain", t => t
                .Replace("<Id>1</Id>", "<!-- c --><Id>1</Id>")
                .Replace("<Row>r2</Row>", "<Row><![CDATA[r</2]]></Row>")
                .Replace("<Tail>t3</Tail>", "<Tail z=\"q>r\">t3</Tail>")
                .Replace("<Tail>t4</Tail>", "<Tail>t4</Tail ><?pi x?>")
                .Replace("<Row>r5</Row>", "<Row><![CDATA[r5]]></Row>"));
            string[] expected = { "1|r1|t1", "2|r</2|t2", "3|r3|t3", "4|r4|t4", "5|r5|t5" };

            using (var db = Open(path, indexing))
            {
                Assert.Equal(expected, Plain(db.Select<RsPlain>(0, 10)));
                Assert.Equal(expected.Skip(1).Take(2), Plain(db.Select<RsPlain>(1, 2)));
                Assert.Equal(expected[4], Plain(new[] { db.Last<RsPlain>() })[0]);
                for (int id = 1; id <= 5; id++)
                    Assert.Equal(expected[id - 1], Plain(new[] { db.Find<RsPlain>(p => p.Id == id) })[0]);
                Assert.Equal(expected, Plain(db.Select<RsPlain>()));
            }
        }

        // A close tag that doesn't name its element: the XML read refuses the row, and so does the fast read, which took
        // it as the property's end.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_misnamed_close_tag_in_a_row_of_scalars_is_an_error(bool indexing)
        {
            string path = NewPath("misnamed-plain", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 3; i++)
                    db.Insert(new RsPlain { Row = "r" + i, Tail = "t" + i });
            }

            DamagePage(path, "RsPlain", t => t.Replace("<Tail>t3</Tail>", "<Tail>t3</Tial>"));

            using (var db = Open(path, indexing))
            {
                Assert.Throws<DatabaseFileException>(() => db.Select<RsPlain>(0, 10).ToList());
                Assert.Throws<DatabaseFileException>(() => db.Last<RsPlain>());
            }
        }

        // Two rows with one code, as a page XOD 1.x wrote can hold (its codes were random): every read of the page's
        // rows has both, as the document read does. The row map kept the second, and a read of the rows dropped the first.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Two_rows_with_one_code_are_both_read(bool indexing)
        {
            string path = NewPath("twins", indexing);
            using (var db = Open(path, indexing))
            {
                for (int i = 1; i <= 3; i++)
                    db.Insert(new RsPlain { Row = "r" + i, Tail = "t" + i });
            }

            DamagePage(path, "RsPlain", t =>
            {
                var codes = System.Text.RegularExpressions.Regex.Matches(t, "<Row code=\"([^\"]+)\">").Select(m => m.Groups[1].Value).ToArray();
                Assert.Equal(3, codes.Length);
                return t.Replace("<Row code=\"" + codes[2] + "\">", "<Row code=\"" + codes[0] + "\">");
            });
            string[] all = { "1|r1|t1", "2|r2|t2", "3|r3|t3" };

            using (var db = Open(path, indexing))
            {
                Assert.Equal(all, Plain(db.Select<RsPlain>(0, 10)));
                Assert.Equal(all.Skip(2), Plain(db.Select<RsPlain>(2, 5)));
                Assert.Equal(all[2], Plain(new[] { db.Last<RsPlain>() })[0]);
                Assert.Equal(3, db.Count<RsPlain>());
            }
            using (var db = Open(path, indexing))
                Assert.Equal(all, Plain(db.Select<RsPlain>()));
        }

        // A query that reads many pages reads them side by side; a damaged one is thrown as itself, as when it reads
        // fewer. Read with four or more, it came out as an AggregateException around the DatabaseFileException.
        [Fact]
        public void A_damaged_page_read_beside_others_is_thrown_as_itself()
        {
            string path = NewPath("many", true);
            using (var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = true, PageSize = 4096 }))
                db.InsertRange(Enumerable.Range(1, 1000).Select(i => new RsCount { Count = i % 3, Name = "n" + i }).ToList());

            var pages = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag")
                .Where(f => File.ReadAllText(f).Contains("<RsCount>")).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.True(pages.Length >= 8, "pages: " + pages.Length);
            var page = pages[pages.Length / 2];
            string text = File.ReadAllText(page);
            int cut = text.IndexOf("</Rows>", StringComparison.Ordinal);
            File.WriteAllText(page, text.Substring(0, text.LastIndexOf("<Row code=", cut, StringComparison.Ordinal) + 12));

            using (var db = new XodContext(path, null, new DatabaseOptions { EnableIndexing = true, PageSize = 4096 }))
            {
                var thrown = Record.Exception(() => db.Query<RsCount>(c => c.Id > 0).ToList());
                Assert.IsType<DatabaseFileException>(thrown);
            }
        }
    }
}
