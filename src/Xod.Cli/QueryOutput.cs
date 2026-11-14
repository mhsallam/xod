using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Xod.Cli
{
    /// <summary>
    /// The three forms of xod query's answer. Values are written by their SQL type as the conformance set encodes
    /// them (PLAN-047 section 4.2): an INTEGER beyond 2^53 and every DECIMAL as text, so no JSON reader loses digits;
    /// a TIMESTAMP in round-trip form (<c>Z</c> for UTC); a MAP's keys in ordinal order.
    /// </summary>
    internal static class QueryOutput
    {
        private const long MaxExactInJson = 9007199254740992; // 2^53

        public static void WriteTable(TextWriter output, SqlResult result)
        {
            int n = result.Columns.Count;
            var cells = result.Rows.Select(r => r.Select(Text).ToArray()).ToList();
            var widths = new int[n];
            for (int i = 0; i < n; i++)
                widths[i] = Math.Max(result.Columns[i].Name.Length, cells.Count == 0 ? 0 : cells.Max(c => c[i].Length));
            bool[] right = result.Columns.Select(c => c.Type is "INTEGER" or "DOUBLE" or "DECIMAL").ToArray();

            output.WriteLine(string.Join(" | ", result.Columns.Select((c, i) => c.Name.PadRight(widths[i]))).TrimEnd());
            output.WriteLine(string.Join("-+-", widths.Select(w => new string('-', Math.Max(w, 1)))));
            foreach (var row in cells)
                output.WriteLine(string.Join(" | ", row.Select((v, i) => right[i] ? v.PadLeft(widths[i]) : v.PadRight(widths[i]))).TrimEnd());
            output.WriteLine(result.Rows.Count == 1 ? "(1 row)" : $"({result.Rows.Count} rows)");
        }

        /// <summary>RFC 4180: a header row, a value quoted when it holds a comma, a quote or a line break (its quotes
        /// doubled), NULL as an empty field.</summary>
        public static void WriteCsv(TextWriter output, SqlResult result)
        {
            output.WriteLine(string.Join(",", result.Columns.Select(c => Csv(c.Name))));
            foreach (var row in result.Rows)
                output.WriteLine(string.Join(",", row.Select(v => v == null ? "" : Csv(Text(v)))));
        }

        private static string Csv(string s) =>
            s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || (s.Length > 0 && (s[0] == ' ' || s[^1] == ' '))
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;

        public static void WriteJson(TextWriter output, SqlResult result)
        {
            var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                json.WriteStartObject();
                json.WriteStartArray("columns");
                foreach (var c in result.Columns)
                {
                    json.WriteStartObject();
                    json.WriteString("name", c.Name);
                    json.WriteString("type", c.Type);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("rows");
                foreach (var row in result.Rows)
                {
                    json.WriteStartArray();
                    foreach (var v in row)
                        WriteValue(json, v);
                    json.WriteEndArray();
                }
                json.WriteEndArray();
                json.WriteStartArray("warnings");
                foreach (var w in result.Warnings)
                {
                    json.WriteStartObject();
                    json.WriteString("code", w.Code);
                    json.WriteString("message", w.Message);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            output.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        // Each SQL type has one .NET type in a result (SqlResult), so the value's own type says how it is written.
        private static void WriteValue(Utf8JsonWriter json, object v)
        {
            switch (v)
            {
                case null:
                    json.WriteNullValue();
                    break;
                case long l:
                    if (l >= -MaxExactInJson && l <= MaxExactInJson) json.WriteNumberValue(l);
                    else json.WriteStringValue(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) json.WriteStringValue(d.ToString(CultureInfo.InvariantCulture));
                    else json.WriteNumberValue(d);
                    break;
                case bool b:
                    json.WriteBooleanValue(b);
                    break;
                case IReadOnlyDictionary<string, object> map:
                    json.WriteStartObject();
                    foreach (var kv in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        json.WritePropertyName(kv.Key);
                        WriteValue(json, kv.Value);
                    }
                    json.WriteEndObject();
                    break;
                case IEnumerable items when v is not string:
                    json.WriteStartArray();
                    foreach (var item in items)
                        WriteValue(json, item);
                    json.WriteEndArray();
                    break;
                default:
                    json.WriteStringValue(Text(v));
                    break;
            }
        }

        /// <summary>A value as text: the forms of PLAN-047 section 4.2, a MAP or an ARRAY as its JSON, NULL as
        /// <c>NULL</c>.</summary>
        private static string Text(object v)
        {
            switch (v)
            {
                case null: return "NULL";
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case decimal m: return m.ToString(CultureInfo.InvariantCulture);
                case Guid g: return g.ToString("D");
                case DateTime t: return t.ToString("O", CultureInfo.InvariantCulture);
                case DateTimeOffset o: return o.ToString("O", CultureInfo.InvariantCulture);
                case DateOnly d: return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case TimeOnly t: return t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                case TimeSpan s: return s.ToString("c", CultureInfo.InvariantCulture);
            }
            var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                WriteValue(json, v);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
