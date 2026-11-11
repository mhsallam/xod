// Runs the conformance set against the C# library (PLAN-047 section 5).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Xod.Conformance
{
    public class ConformanceTests
    {
        private readonly ITestOutputHelper output;
        public ConformanceTests(ITestOutputHelper output) => this.output = output;

        [Fact]
        public void The_case_files_are_what_the_definitions_and_the_oracle_give()
        {
            var stale = new List<string>();
            foreach (var group in CaseFiles.Groups)
            {
                string path = CaseFiles.PathOf(group);
                string onDisk = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
                if (onDisk != CaseFiles.Render(group)) stale.Add(group);
            }
            var orphans = Directory.GetFiles(CaseFiles.Folder, "*.json")
                .Select(Path.GetFileNameWithoutExtension).Except(CaseFiles.Groups).ToList();
            Assert.True(stale.Count == 0 && orphans.Count == 0,
                $"case files out of date: {string.Join(", ", stale.Concat(orphans))}. Run: dotnet run --project conformance/Xod.Conformance -f net9.0 -- cases");
        }

        [Fact]
        public void There_are_at_least_a_hundred_cases_and_every_group_has_some()
        {
            Assert.True(Cases.All.Count >= 100, $"{Cases.All.Count} cases");
            Assert.All(CaseFiles.Groups, g => Assert.True(Cases.All.Count(c => c.Group == g) > 0));
        }

        [Theory]
        [InlineData("xml")]
        [InlineData("json")]
        [InlineData("binary")]
        [InlineData("password")]
        public void The_library_gives_the_expected_answers(string fixture)
        {
            var expected = CaseFiles.ExpectedRows();
            string dir = CopyFixture(fixture);
            var failures = new List<string>();
            int passed = 0, known = 0, skipped = 0;
            try
            {
                using var db = new XodContext(Path.Combine(dir, "app.xod"), Repo.PasswordOf(fixture),
                    new DatabaseOptions { StorageFormat = Repo.FormatOf(fixture) });
                var source = new XodSource(db);
                foreach (var c in Cases.All)
                {
                    if (!c.AppliesTo(fixture) || !c.RunOnXod || c.Error != null) { skipped++; continue; }

                    string problem;
                    try
                    {
                        var want = Values.Canonical(c, expected[c.Id]);
                        var got = Values.Canonical(c, Values.EncodeRows(c, c.Rows(source)));
                        problem = Diff(want, got);
                    }
                    catch (Exception e)
                    {
                        problem = $"threw {e.GetType().Name}: {e.Message}";
                    }

                    if (c.Known != null)
                    {
                        if (problem == null) failures.Add($"{c.Id}: passes now - {c.Known} looks fixed; remove its Known marker and close the issue");
                        else { known++; output.WriteLine($"known {c.Known} {c.Id}: {problem}"); }
                    }
                    else if (problem != null) failures.Add($"{c.Id}: {problem}\n    {c.Sql}");
                    else passed++;
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* a temp copy; the OS cleans it */ }
            }

            output.WriteLine($"{fixture}: {passed} passed, {known} known failures, {skipped} not for the library or this fixture");
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        private static string Diff(List<string> want, List<string> got)
        {
            if (want.SequenceEqual(got)) return null;
            int i = 0;
            while (i < want.Count && i < got.Count && want[i] == got[i]) i++;
            return $"{got.Count} rows, expected {want.Count}; first difference at row {i}: expected {(i < want.Count ? want[i] : "(none)")}, got {(i < got.Count ? got[i] : "(none)")}";
        }

        /// <summary>The library writes as it opens (indexes, recovery), so it never opens the committed files.</summary>
        internal static string CopyFixture(string fixture)
        {
            string source = Path.Combine(Repo.Conformance, "data", fixture);
            Assert.True(Directory.Exists(source), $"missing fixture {source}");
            string dir = Path.Combine(Path.GetTempPath(), "xod-conformance", fixture + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            return dir;
        }
    }
}
