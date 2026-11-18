// A stored string must read back exactly as written, whatever its characters (PLAN-046 section 4.2,
// ISS-051): XML can't carry a control character or a CR as a raw character, and a conforming parser
// changes CR LF to LF, drops whitespace-only text, and refuses a reference to U+0001. Each value goes
// through both XML writers (the fast row writer for a flat class; the document writer for a class with
// children or attributes), every storage format, and both readers (before and after reopening).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Xod.Tests
{
    public class FlatText
    {
        public int Id { get; set; }
        public string Value { get; set; }
    }

    public class TextHolder
    {
        public int Id { get; set; }
        public string Value { get; set; }
        [Property(Position = ValuePosition.Attribute)] public string Tag { get; set; }
        [Markup] public string Html { get; set; }
        [Children] public List<TextPart> Parts { get; set; }
    }

    public class TextPart
    {
        public int Id { get; set; }
        public int TextHolderId { get; set; }
        public string Value { get; set; }
    }

    // ISS-140: a char, flat (the fast row writer and reader) and in a document (with an attribute), and indexed.
    public class FlatChar
    {
        public int Id { get; set; }
        [Property(Indexed = true)] public char Letter { get; set; }
        public char? Maybe { get; set; }
    }

    public class CharHolder
    {
        public int Id { get; set; }
        public char Letter { get; set; }
        [Property(Position = ValuePosition.Attribute)] public char Tag { get; set; }
        [Children] public List<CharPart> Parts { get; set; }
    }

    public class CharPart
    {
        public int Id { get; set; }
        public int CharHolderId { get; set; }
        public string Value { get; set; }
    }

    public class XodTextRoundTripTests
    {
        public static readonly string[] Values =
        {
            "a\u0001b",          // a control character (ISS-051)
            "\u001F",
            "nul\u0000here",
            "line1\r\nline2",    // CR LF: a parser makes it LF
            "cr\ronly",
            "lf\nonly",
            "tab\there",
            "  ",                // whitespace only: a parser may drop it
            " padded ",
            "\t\r\n",
            "<&>\"'",
            "� and \U0001F600",
            "]]>",
        };

        public static IEnumerable<object[]> Formats() => new[]
        {
            new object[] { StorageFormat.Xml, null },
            new object[] { StorageFormat.Json, null },
            new object[] { StorageFormat.Binary, null },
            new object[] { StorageFormat.Xml, "pw" },
        };

        private static string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-text", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static string Show(string s) =>
            s == null ? "null" : string.Concat(s.Select(c => c < 0x20 || c == 0x7F || char.IsSurrogate(c) ? $"\\u{(int)c:X4}" : c.ToString()));

        [Theory]
        [MemberData(nameof(Formats))]
        public void Every_string_reads_back_exactly(StorageFormat format, string password)
        {
            string path = NewDatabase(format + (password ?? ""));
            var options = new DatabaseOptions { StorageFormat = format };
            var wrong = new List<string>();

            void Check(XodContext db, string when)
            {
                List<FlatText> flats, paged; List<TextHolder> holders;
                try
                {
                    flats = db.Select<FlatText>().OrderBy(f => f.Id).ToList();
                    holders = db.Select<TextHolder>().OrderBy(h => h.Id).ToList();
                    paged = db.Select<FlatText>(0, Values.Length).OrderBy(f => f.Id).ToList();
                }
                catch (Exception e) { wrong.Add($"{when}: reading threw {e.GetType().Name}: {e.Message}"); return; }

                for (int i = 0; i < Values.Length; i++)
                {
                    string want = Values[i];
                    void Same(string what, string got)
                    {
                        if (got != want) wrong.Add($"{when}: {what} [{Show(want)}] read back as [{Show(got)}]");
                    }
                    Same("flat", flats.ElementAtOrDefault(i)?.Value);
                    Same("document element", holders.ElementAtOrDefault(i)?.Value);
                    Same("document attribute", holders.ElementAtOrDefault(i)?.Tag);
                    Same("markup", holders.ElementAtOrDefault(i)?.Html);
                    Same("child", holders.ElementAtOrDefault(i)?.Parts?.SingleOrDefault()?.Value);
                    if (!wrong.Any(w => w.Contains("flat")) && db.Find<FlatText>(f => f.Id == i + 1)?.Value != want)
                        wrong.Add($"{when}: flat by Find [{Show(want)}]");
                    Same("flat by page (fast row reader)", paged.ElementAtOrDefault(i)?.Value);
                }
            }

            using (var db = new XodContext(path, password, options))
            {
                foreach (string v in Values)
                {
                    try { db.Insert(new FlatText { Value = v }); }
                    catch (Exception e) { wrong.Add($"insert flat [{Show(v)}] threw {e.GetType().Name}: {e.Message}"); }
                    try { db.Insert(new TextHolder { Value = v, Tag = v, Html = v, Parts = new() { new TextPart { Value = v } } }); }
                    catch (Exception e) { wrong.Add($"insert document [{Show(v)}] threw {e.GetType().Name}: {e.Message}"); }
                }
                Check(db, "same context");
            }
            using (var db = new XodContext(path, password, options))
                Check(db, "reopened");

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        // ISS-140: a char reads back as written, whitespace too, in every format and by every reader, and the index
        // keys it alike from the object and from the row: a whitespace char used to read back '\0' (a char? null),
        // and an index rebuilt from the rows keyed it 0, so a query by it found the row only until the rebuild.
        public static readonly char[] Chars = { ' ', '\u00A0', '\t', '\u2003', '\r', '\n', 'x', '<' };

        [Theory]
        [MemberData(nameof(Formats))]
        public void Every_char_reads_back_exactly(StorageFormat format, string password)
        {
            string path = NewDatabase("char-" + format + (password ?? ""));
            var options = new DatabaseOptions { StorageFormat = format };
            var wrong = new List<string>();
            string U(char? c) => c == null ? "null" : $"U+{(int)c.Value:X4}";

            void Check(XodContext db, string when)
            {
                var flats = db.Select<FlatChar>().OrderBy(f => f.Id).ToList();
                var paged = db.Select<FlatChar>(0, Chars.Length).OrderBy(f => f.Id).ToList();
                var holders = db.Select<CharHolder>().OrderBy(h => h.Id).ToList();
                for (int i = 0; i < Chars.Length; i++)
                {
                    char want = Chars[i];
                    void Same(string what, char? got)
                    {
                        if (got != want) wrong.Add($"{when}: {what} {U(want)} read back as {U(got)}");
                    }
                    Same("flat", flats.ElementAtOrDefault(i)?.Letter);
                    Same("flat char?", flats.ElementAtOrDefault(i)?.Maybe);
                    Same("flat by page (fast row reader)", paged.ElementAtOrDefault(i)?.Letter);
                    Same("document element", holders.ElementAtOrDefault(i)?.Letter);
                    Same("document attribute", holders.ElementAtOrDefault(i)?.Tag);
                    int found = db.Query<FlatChar>(f => f.Letter == want).Count();
                    if (found != 1) wrong.Add($"{when}: an indexed query by {U(want)} found {found}");
                }
                if (db.Query<FlatChar>(f => f.Letter == '\0').Any())
                    wrong.Add($"{when}: an indexed query by U+0000 found a row");
            }

            using (var db = new XodContext(path, password, options))
            {
                foreach (char c in Chars)
                {
                    db.Insert(new FlatChar { Letter = c, Maybe = c });
                    db.Insert(new CharHolder { Letter = c, Tag = c, Parts = new() { new CharPart { Value = "p" } } });
                }
                Check(db, "same context");
            }
            using (var db = new XodContext(path, password, options))
                Check(db, "reopened");
            foreach (var index in Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xidx"))
                File.Delete(index);
            using (var db = new XodContext(path, password, options))
                Check(db, "reopened, the index rebuilt from the rows");

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        // A lone surrogate isn't text UTF-8 can carry. It must be refused, never stored as U+FFFD in its
        // place, in every format, by both writers and on update, and nothing of the row may be left behind.
        [Theory]
        [MemberData(nameof(Formats))]
        public void A_string_with_a_lone_surrogate_is_refused_not_changed(StorageFormat format, string password)
        {
            string path = NewDatabase("surrogate-" + format + (password ?? ""));
            var options = new DatabaseOptions { StorageFormat = format };
            var outcomes = new List<string>();
            using (var db = new XodContext(path, password, options))
            {
                foreach (string bad in new[] { "a\uD800b", "\uDC00", "end\uD83D" })
                {
                    try { db.Insert(new FlatText { Value = bad }); outcomes.Add($"flat [{Show(bad)}] stored"); }
                    catch (InvalidTextException) { }
                    catch (Exception e) { outcomes.Add($"flat [{Show(bad)}] threw {e.GetType().Name}"); }
                    try { db.Insert(new TextHolder { Value = bad, Parts = new() { new TextPart { Value = "p" } } }); outcomes.Add($"document [{Show(bad)}] stored"); }
                    catch (InvalidTextException) { }
                    catch (Exception e) { outcomes.Add($"document [{Show(bad)}] threw {e.GetType().Name}"); }
                }
                db.Insert(new FlatText { Value = "ok" });
                var holder = db.Insert(new TextHolder { Value = "kept", Parts = new() { new TextPart { Value = "p" } } });
                foreach (string bad in new[] { "a\uD800b", "\uDC00" })
                {
                    var flat = db.Find<FlatText>(f => f.Value == "ok");
                    flat.Value = bad;
                    try { db.Update(flat); outcomes.Add($"flat update [{Show(bad)}] stored"); }
                    catch (InvalidTextException) { }
                    catch (Exception e) { outcomes.Add($"flat update [{Show(bad)}] threw {e.GetType().Name}"); }
                    holder.Value = bad;
                    try { db.Update(holder); outcomes.Add($"document update [{Show(bad)}] stored"); }
                    catch (InvalidTextException) { }
                    catch (Exception e) { outcomes.Add($"document update [{Show(bad)}] threw {e.GetType().Name}"); }
                }
            }
            using (var db = new XodContext(path, password, options))
            {
                Assert.Equal(new[] { "ok" }, db.Select<FlatText>().Select(f => f.Value));
                Assert.Equal(new[] { "kept" }, db.Select<TextHolder>().Select(h => h.Value));
            }
            Assert.True(outcomes.Count == 0, string.Join("\n", outcomes));
        }

        // XOD never writes a lone surrogate, but a page written elsewhere may reference one. Every read must
        // give the same value: that UTF-16 unit. (The fast row reader falls back to the document reader on
        // any exception, so this held before XmlText.Unescape learned the case; it now answers without the
        // fallback.)
        [Fact]
        public void Both_readers_read_a_reference_to_a_surrogate_alike()
        {
            string path = NewDatabase("surrogate-reference");
            using (var db = new XodContext(path))
            {
                db.Insert(new FlatText { Value = "MARK" });
                db.Insert(new TextHolder { Value = "MARK", Parts = new() { new TextPart { Value = "p" } } });
            }
            foreach (string page in Directory.GetFiles(Path.GetDirectoryName(path)!, "*.xpag"))
                File.WriteAllText(page, File.ReadAllText(page).Replace("MARK", "a&#xD800;b"));

            using var reopened = new XodContext(path);
            Assert.Equal(Show("a\uD800b"), Show(reopened.Select<FlatText>().Single().Value));
            Assert.Equal(Show("a\uD800b"), Show(reopened.Find<FlatText>(f => f.Id == 1).Value));
            Assert.Equal(Show("a\uD800b"), Show(reopened.Select<TextHolder>().Single().Value));
            Assert.Equal(Show("a\uD800b"), Show(reopened.Select<FlatText>(0, 1).Single().Value)); // the fast row reader
            Assert.Equal(Show("a\uD800b"), Show(reopened.Last<FlatText>().Value));
        }

        // An update rewrites a row in place: the same rules hold for the rewritten row.
        [Theory]
        [MemberData(nameof(Formats))]
        public void An_updated_string_reads_back_exactly(StorageFormat format, string password)
        {
            string path = NewDatabase("update-" + format + (password ?? ""));
            var options = new DatabaseOptions { StorageFormat = format };
            var wrong = new List<string>();
            using (var db = new XodContext(path, password, options))
            {
                foreach (string v in Values)
                {
                    var flat = db.Insert(new FlatText { Value = "x" });
                    flat.Value = v;
                    db.Update(flat);
                    var holder = db.Insert(new TextHolder { Value = "x", Tag = "x", Parts = new() { new TextPart { Value = "x" } } });
                    holder.Value = v;
                    holder.Tag = v;
                    holder.Html = v;
                    db.Update(holder);
                }
            }
            using (var db = new XodContext(path, password, options))
            {
                var flats = db.Select<FlatText>().OrderBy(f => f.Id).ToList();
                var holders = db.Select<TextHolder>().OrderBy(h => h.Id).ToList();
                for (int i = 0; i < Values.Length; i++)
                {
                    if (flats[i].Value != Values[i]) wrong.Add($"flat [{Show(Values[i])}] read back as [{Show(flats[i].Value)}]");
                    if (holders[i].Value != Values[i]) wrong.Add($"document element [{Show(Values[i])}] read back as [{Show(holders[i].Value)}]");
                    if (holders[i].Tag != Values[i]) wrong.Add($"document attribute [{Show(Values[i])}] read back as [{Show(holders[i].Tag)}]");
                    if (holders[i].Html != Values[i]) wrong.Add($"markup [{Show(Values[i])}] read back as [{Show(holders[i].Html)}]");
                }
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }
    }
}
