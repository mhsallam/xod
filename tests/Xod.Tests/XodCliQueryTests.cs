// xod query (PLAN-060 section 2.5): SQL from the command line, reading tables through their manifest
// (SqlSchemaSource.Recorded) without the application's classes, and never writing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;

namespace Xod.Tests
{
    public class XodCliQueryTests : IDisposable
    {
        private readonly List<string> dirs = new();

        public void Dispose()
        {
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private string Seeded(string name, string password = null)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-cli", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            dirs.Add(dir);
            string path = Path.Combine(dir, "app.xod");
            using var db = new XodContext(path, password);
            db.Insert(new SqProduct
            {
                Name = "Kettle, \"steel\"",
                Category = "kitchen",
                Price = 1.50m,
                Weight = null,
                Tier = SqTier.Gold,
                Tags = new List<string> { "a", "b" },
                Stock = new Dictionary<string, int> { ["y"] = 2, ["x"] = 1 },
            });
            db.Insert(new SqProduct { Name = "Mug", Category = "kitchen", Price = 3m, Weight = 0.25, Tags = new List<string>() });
            return path;
        }

        private static (int Code, string Out, string Err) Run(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int code = Xod.Cli.Program.Run(args, output, error);
            return (code, output.ToString().Replace("\r\n", "\n"), error.ToString().Replace("\r\n", "\n"));
        }

        [Fact]
        public void Query_prints_a_table_with_its_row_count()
        {
            string path = Seeded("table");
            var (code, output, error) = Run("query", path, "SELECT Id, Name, Weight FROM SqProduct ORDER BY Id");

            Assert.Equal(0, code);
            Assert.Equal("", error);
            var lines = output.TrimEnd('\n').Split('\n');
            Assert.Matches(@"^Id\s+\|\s+Name\s+\|\s+Weight$", lines[0].TrimEnd());
            Assert.Matches(@"^-+\+-+\+-+$", lines[1]);
            Assert.Contains("Kettle, \"steel\"", lines[2]);
            Assert.EndsWith("NULL", lines[2].TrimEnd());
            Assert.Contains("0.25", lines[3]);
            Assert.Equal("(2 rows)", lines[^1]);
        }

        [Fact]
        public void Query_json_writes_values_as_the_conformance_set_encodes_them()
        {
            string path = Seeded("json");
            var (code, output, _) = Run("query", path, "SELECT Id, Name, Price, Weight, Tier, Tags, Stock FROM SqProduct WHERE Id = 1", "--format", "json");

            Assert.Equal(0, code);
            Assert.Equal(
                "{\"columns\":[{\"name\":\"Id\",\"type\":\"INTEGER\"},{\"name\":\"Name\",\"type\":\"TEXT\"},{\"name\":\"Price\",\"type\":\"DECIMAL\"}," +
                "{\"name\":\"Weight\",\"type\":\"DOUBLE\"},{\"name\":\"Tier\",\"type\":\"TEXT\"},{\"name\":\"Tags\",\"type\":\"ARRAY\"},{\"name\":\"Stock\",\"type\":\"MAP\"}]," +
                "\"rows\":[[1,\"Kettle, \\\"steel\\\"\",\"1.50\",null,\"Gold\",[\"a\",\"b\"],{\"x\":1,\"y\":2}]],\"warnings\":[]}",
                output.Trim());
        }

        [Fact]
        public void Query_csv_quotes_what_needs_it_and_leaves_NULL_empty()
        {
            string path = Seeded("csv");
            var (code, output, _) = Run("query", path, "SELECT Id, Name, Weight FROM SqProduct ORDER BY Id", "--format", "csv");

            Assert.Equal(0, code);
            Assert.Equal("Id,Name,Weight\n1,\"Kettle, \"\"steel\"\"\",\n2,Mug,0.25\n", output);
        }

        [Fact]
        public void A_query_error_is_its_code_on_stderr_and_exit_code_1()
        {
            string path = Seeded("error");
            var (code, output, error) = Run("query", path, "SELECT Nope FROM SqProduct");

            Assert.Equal(1, code);
            Assert.Equal("", output);
            Assert.StartsWith("XQ004: ", error);
        }

        [Fact]
        public void Warnings_go_to_stderr()
        {
            string path = Seeded("warn");
            string table = Path.Combine(Path.GetDirectoryName(path), typeof(SqProduct).FullName + ".xtab");
            var doc = System.Xml.Linq.XDocument.Load(table);
            doc.Root.Element("Schema").Remove();
            doc.Save(table);

            var (code, output, error) = Run("query", path, "SELECT Price FROM SqProduct ORDER BY Id");
            Assert.Equal(0, code);
            Assert.Contains("1.50", output);
            Assert.StartsWith("XQ013: ", error);
        }

        [Fact]
        public void Query_reads_a_database_with_its_password_and_writes_nothing()
        {
            string path = Seeded("password", "cli-pw");
            string dir = Path.GetDirectoryName(path);
            Dictionary<string, string> Snapshot() => Directory.GetFiles(dir).ToDictionary(
                f => Path.GetFileName(f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
            var before = Snapshot();

            var (code, output, _) = Run("query", path, "SELECT COUNT(*) AS n FROM SqProduct", "--password", "cli-pw", "--format", "csv");
            Assert.Equal(0, code);
            Assert.Equal("n\n2\n", output);
            // xod.lock is held while open and never deleted; nothing else changes.
            Assert.Equal(before.Where(kv => kv.Key != "xod.lock"), Snapshot().Where(kv => kv.Key != "xod.lock"));

            Assert.Equal(1, Run("query", path, "SELECT 1").Code);
            Assert.Equal(1, Run("query", Path.Combine(dir, "missing.xod"), "SELECT 1").Code);
            Assert.False(File.Exists(Path.Combine(dir, "missing.xod")));
        }

        [Fact]
        public void Query_without_its_arguments_prints_its_usage()
        {
            var (code, _, error) = Run("query");
            Assert.Equal(1, code);
            Assert.Contains("xod query <db-path> \"SELECT ...\"", error);
        }

        // Output sent to a file or a pipe is UTF-8, whatever the console's code page: text outside it (Arabic here)
        // would otherwise become '?'. Run as its own process, so the console's encoding is the one in play.
        [Fact]
        public async System.Threading.Tasks.Task Redirected_output_is_UTF8_text_whatever_the_console_code_page()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-cli", "utf8-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            dirs.Add(dir);
            string path = Path.Combine(dir, "app.xod");
            const string name = "إبريق — café";
            using (var db = new XodContext(path))
                db.Insert(new SqProduct { Name = name, Category = "k", Price = 1m });

            var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            psi.ArgumentList.Add(typeof(Xod.Cli.Program).Assembly.Location);
            psi.ArgumentList.Add("query");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("SELECT Name FROM SqProduct");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("csv");
            using var process = System.Diagnostics.Process.Start(psi)!;
            var errorRead = process.StandardError.ReadToEndAsync();
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await errorRead;
            await process.WaitForExitAsync();

            Assert.True(process.ExitCode == 0, error);
            Assert.Equal("Name\n" + name + "\n", output.Replace("\r\n", "\n"));
        }
    }
}
