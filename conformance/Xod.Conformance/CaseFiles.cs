// The portable form of the cases (PLAN-047 section 3): one JSON file per group under conformance/cases,
// written from Cases.cs and the oracle. Other implementations read only these files and the fixtures.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Xod.Conformance
{
    public static class CaseFiles
    {
        public static string Folder => Path.Combine(Repo.Conformance, "cases");

        public static IEnumerable<string> Groups => Cases.All.Select(c => c.Group).Distinct();

        public static string PathOf(string group) => Path.Combine(Folder, group + ".json");

        /// <summary>The file text for one group, expected rows from the oracle.</summary>
        public static string Render(string group)
        {
            var oracle = new OracleSource();
            var sb = new StringBuilder();
            sb.Append("{\n  \"group\": ").Append(Str(group)).Append(",\n  \"cases\": [\n");
            var cases = Cases.All.Where(c => c.Group == group).ToList();
            for (int i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                sb.Append("    {\n");
                sb.Append("      \"id\": ").Append(Str(c.Id)).Append(",\n");
                sb.Append("      \"sql\": ").Append(Str(c.Sql)).Append(",\n");
                if (c.Error != null)
                {
                    sb.Append("      \"error\": ").Append(Str(c.Error));
                }
                else
                {
                    sb.Append("      \"columns\": [")
                      .Append(string.Join(", ", c.ColumnList.Select(col => $"{{\"name\": {Str(col.Name)}, \"type\": {Str(col.Type)}}}")))
                      .Append("],\n");
                    sb.Append("      \"ordered\": ").Append(c.Ordered ? "true" : "false");
                }
                if (c.Requires != null) sb.Append(",\n      \"requires\": ").Append(Str(c.Requires));
                if (c.Known != null) sb.Append(",\n      \"known\": ").Append(Str(c.Known));
                if (!c.RunOnXod && c.Error == null) sb.Append(",\n      \"csharp\": false");
                if (c.Note != null) sb.Append(",\n      \"note\": ").Append(Str(c.Note));
                if (c.Error == null)
                {
                    var rows = Values.EncodeRows(c, c.Rows(oracle));
                    sb.Append(",\n      \"rows\": [");
                    if (rows.Count > 0) sb.Append("\n        ").Append(string.Join(",\n        ", rows)).Append("\n      ");
                    sb.Append(']');
                }
                sb.Append("\n    }").Append(i < cases.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        /// <summary>Expected rows by case id, as written in the files.</summary>
        public static Dictionary<string, List<string>> ExpectedRows()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Folder, "*.json"))
            {
                var root = JsonNode.Parse(File.ReadAllText(file));
                foreach (var c in root["cases"].AsArray())
                {
                    var rows = c["rows"]?.AsArray();
                    if (rows != null) result[c["id"].GetValue<string>()] = rows.Select(r => r.ToJsonString(Values.Json)).ToList();
                }
            }
            return result;
        }

        public static void WriteAll()
        {
            Directory.CreateDirectory(Folder);
            foreach (var stale in Directory.GetFiles(Folder, "*.json")) File.Delete(stale);
            foreach (var g in Groups) File.WriteAllText(PathOf(g), Render(g));
        }

        private static string Str(string s) => JsonValue.Create(s).ToJsonString(Values.Json);
    }
}
