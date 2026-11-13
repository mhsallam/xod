// The stored-values fixture (ISS-060, PLAN-053; PLAN-046 section 7.2): rows whose values a read wouldn't give
// back if they were left out, written by the library in the tree: an empty string is an empty element or
// attribute, a null whose new instance holds something else is <Name nil="true" />, and a value a read gives
// back anyway stays out. Since ISS-097 a map's null value and a list's null item are marked the same way, and
// an empty map is stored. Each implementation's reader must read these forms; read.mjs checks this fixture with
// the others. Written by:
//   dotnet run --project conformance/Xod.Conformance -f net9.0 -- stored-values [root]
// into <root>/stored-values (root: conformance/data by default). Rewrite it only when its rows or the format
// change on purpose, and say why in PLAN-047.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace Xod.Conformance.Model.Format
{
    // Plain scalars: the fast row writer.
    public class Blank
    {
        public int Id { get; set; }
        [Property(Indexed = true)] public string Text { get; set; }
        public string Label { get; set; } = "unset";
        [Property(Indexed = true)] public string Area { get; set; } = "north";
        public Guid Token { get; set; } = new Guid("6f1c2b8e-1d4a-4c3b-9e2f-000000000001");
        public DateTime When { get; set; } = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    // The same through the document writer: an attribute and a DefaultValue keep a class off the fast writer.
    public class BlankDocument
    {
        public int Id { get; set; }
        public string Text { get; set; }
        public string Label { get; set; } = "unset";
        [Property(Position = ValuePosition.Attribute)] public string Tag { get; set; }
        [DefaultValue(5)] public int Count { get; set; }
    }

    // The value collections (ISS-097, PLAN-046 section 7.2): a map and lists of scalars.
    public class BlankCollections
    {
        public int Id { get; set; }
        public Dictionary<string, string> Names { get; set; }
        public List<string> Words { get; set; }
        public List<int?> Numbers { get; set; }
    }
}

namespace Xod.Conformance
{
    using Xod.Conformance.Model.Format;

    public static class StoredValuesFixture
    {
        public const string Name = "stored-values";

        public static List<Blank> Blanks() => new()
        {
            new Blank { Id = 1, Text = "", Label = null, Area = null, Token = Guid.Empty, When = default },
            new Blank { Id = 2, Text = null, Label = "set" },
            new Blank { Id = 3, Text = "x", Label = "", Area = "", Token = new Guid("6f1c2b8e-1d4a-4c3b-9e2f-000000000003"),
                When = new DateTime(2021, 6, 30, 12, 0, 0, DateTimeKind.Utc) },
        };

        public static List<BlankDocument> Documents() => new()
        {
            new BlankDocument { Id = 1, Text = "", Label = null, Tag = "", Count = 0 },
            new BlankDocument { Id = 2, Text = null, Label = "set", Tag = null, Count = 5 },
            new BlankDocument { Id = 3, Text = "y", Label = "", Tag = "t", Count = 7 },
        };

        public static List<BlankCollections> Collections() => new()
        {
            new BlankCollections
            {
                Id = 1,
                Names = new() { ["none"] = null, ["empty"] = "", ["one"] = "1" },
                Words = new() { "x", null, "" },
                Numbers = new() { 1, null, 0 },
            },
            new BlankCollections { Id = 2, Names = new(), Words = new(), Numbers = null },
        };

        /// <summary>Writes the fixture into <paramref name="folder"/>, which is emptied first.</summary>
        public static void Write(string folder)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "app.xod");
            using (var db = new XodContext(path))
            {
                foreach (var b in Blanks()) db.Insert(b);
                foreach (var d in Documents()) db.Insert(d);
                foreach (var c in Collections()) db.Insert(c);
            }
            Repo.DropLockFiles(folder);
        }
    }
}
