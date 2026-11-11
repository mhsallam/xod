// The two XML writers, the fast row writer (flat classes) and the document writer (everything else, and
// rows rewritten in place), must write the same text for the same thing (PLAN-046 sections 7.1 and 7.5,
// ISS-055), and the table file holds no per-page key ranges.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Xod.Tests
{
    public class FlagRow
    {
        public int Id { get; set; }
        public bool On { get; set; }
        public bool Off { get; set; }
    }

    public class FlagHolder
    {
        public int Id { get; set; }
        public bool On { get; set; }
        [Children] public List<FlagPart> Parts { get; set; }
    }

    public class FlagPart
    {
        public int Id { get; set; }
        public int FlagHolderId { get; set; }
    }

    public class XodWriterAgreementTests
    {
        private static string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-writers", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static string PageText<T>(string dbPath)
        {
            string dir = Path.GetDirectoryName(dbPath)!;
            string table = File.ReadAllText(Path.Combine(dir, typeof(T).FullName + ".xtab"));
            string page = Regex.Match(table, "file=\"([^\"]+)\"").Groups[1].Value;
            return File.ReadAllText(Path.Combine(dir, page));
        }

        [Fact]
        public void Both_writers_write_booleans_and_a_new_page_the_same_way()
        {
            string path = NewDatabase("agree");
            using (var db = new XodContext(path))
            {
                db.Insert(new FlagRow { On = true, Off = false });          // the fast row writer
                var rewritten = db.Insert(new FlagRow { On = false, Off = true });
                rewritten.On = true;
                rewritten.Off = false;
                db.Update(rewritten);                                        // rewritten in place by the document writer
                db.Insert(new FlagHolder { On = true, Parts = new() { new FlagPart() } }); // the document writer
            }

            string fast = PageText<FlagRow>(path);
            string document = PageText<FlagHolder>(path);

            var flat = Regex.Matches(fast, "<(On|Off)>([^<]*)</").Select(m => m.Groups[1].Value + "=" + m.Groups[2].Value).ToList();
            Assert.Equal(new[] { "On=True", "Off=False", "On=True", "Off=False" }, flat);
            Assert.Contains("<On>True</On>", document);

            string Header(string page) => Regex.Match(page, "^.*?<Rows[^>]*>").Value;
            Assert.Equal(Header(document), Header(fast));
        }

        [Fact]
        public void The_table_file_holds_no_page_key_ranges()
        {
            string path = NewDatabase("ranges");
            using (var db = new XodContext(path))
            {
                for (int i = 0; i < 3; i++) db.Insert(new FlagRow { On = i % 2 == 0 });
                db.Insert(new FlagHolder { On = true, Parts = new() { new FlagPart() } });
            }
            string dir = Path.GetDirectoryName(path)!;
            foreach (string table in Directory.GetFiles(dir, "*.xtab"))
                Assert.DoesNotContain("<Index ", File.ReadAllText(table));
        }
    }
}
