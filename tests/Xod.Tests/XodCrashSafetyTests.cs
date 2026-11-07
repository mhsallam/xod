using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

// Same global type names as tests/Xod.CrashWriter - the child process and this test read the same files.
public class CrashCustomer
{
    public int Id { get; set; }
    public string Name { get; set; }
    [Children] public List<CrashOrder> Orders { get; set; }
}

public class CrashOrder
{
    public int Id { get; set; }
    public int CrashCustomerId { get; set; }
    public string Status { get; set; }
    [ParentKey(nameof(CrashCustomerId))] public CrashCustomer Customer { get; set; }
    public double Total { get; set; }
}

namespace Xod.Tests
{
    /// <summary>
    /// Gate layer 4 of TEST-PLAN-2.0: a process killed mid-write must leave an all-or-nothing database.
    ///
    /// The child writes 25 customers with 3 orders each in one transaction, flushes, prints a marker and
    /// repeats until this process kills it. After the kill the database is reopened and must satisfy:
    /// the counts are whole batches, every order resolves to its customer, and every row is still
    /// findable by its key - a half-written page or a half-applied batch fails one of those.
    /// </summary>
    public class XodCrashSafetyTests : IDisposable
    {
        private const int BatchCustomers = 25;
        private const int OrdersPerCustomer = 3;

        private readonly string tempRoot;

        public XodCrashSafetyTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodCrash_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "Xod")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "tests")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("repository root not found above " + AppContext.BaseDirectory);
        }

        private static string WriterDll()
        {
            string root = RepoRoot();
            var candidates = Directory.Exists(Path.Combine(root, "tests", "Xod.CrashWriter", "bin"))
                ? Directory.GetFiles(Path.Combine(root, "tests", "Xod.CrashWriter", "bin"), "Xod.CrashWriter.dll", SearchOption.AllDirectories)
                : Array.Empty<string>();

            if (candidates.Length == 0)
                throw new FileNotFoundException("Xod.CrashWriter.dll not found; build tests/Xod.CrashWriter first");

            // prefer the build for the framework the tests are running on
            string own = $"net{Environment.Version.Major}.0";
            var matching = candidates.Where(c => c.Contains(own, StringComparison.OrdinalIgnoreCase)).ToList();
            return (matching.Count > 0 ? (IEnumerable<string>)matching : candidates)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .First();
        }

        private Process StartWriter(string mode, string dir)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(WriterDll());
            psi.ArgumentList.Add(mode);
            psi.ArgumentList.Add(dir);

            var process = Process.Start(psi);
            Assert.NotNull(process);
            return process!;
        }

        private (int ExitCode, string Output) RunWriter(string mode, string dir, int? crashAfterRows = null)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(WriterDll());
            psi.ArgumentList.Add(mode);
            psi.ArgumentList.Add(dir);

            if (crashAfterRows.HasValue)
                psi.Environment["XOD_CRASH_AFTER_ROWS"] = crashAfterRows.Value.ToString();

            using var process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);
            return (process.ExitCode, output);
        }

        private string Seed(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            (int code, string output) = RunWriter("setup", dir);
            Assert.True(code == 0, $"setup failed ({code}): {output}");
            return dir;
        }

        private static XodContext Open(string dir) => new(Path.Combine(dir, "crash.xod"));

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(40)]
        [InlineData(99)]
        public void A_crash_inside_a_batch_rolls_the_whole_batch_back(int crashAfterRows)
        {
            string dir = Seed($"crash-{crashAfterRows}");

            (int code, string output) = RunWriter("onebatch", dir, crashAfterRows);
            Assert.NotEqual(0, code);

            using var db = Open(dir);
            Assert.Equal(BatchCustomers, db.Count<CrashCustomer>());
            Assert.Equal(BatchCustomers * OrdersPerCustomer, db.Count<CrashOrder>());

            for (int id = 1; id <= BatchCustomers; id++)
                Assert.NotNull(db.Find<CrashCustomer>(c => c.Id == id));

            var first = db.Find<CrashCustomer>(c => c.Id == 1, include: "*");
            Assert.NotNull(first);
            Assert.Equal(OrdersPerCustomer, first.Orders?.Count ?? 0);
        }

        [Fact]
        public void A_database_after_a_crash_accepts_new_writes_and_reopens()
        {
            string dir = Seed("recover");

            (int code, string output) = RunWriter("onebatch", dir, 33);
            Assert.NotEqual(0, code);

            int before;
            using (var db = Open(dir))
            {
                before = db.Count<CrashCustomer>();
                Assert.Equal(BatchCustomers, before);

                db.InsertRange(new List<CrashCustomer>
                {
                    new()
                    {
                        Id = 10_000,
                        Name = "after recovery",
                        Orders = new List<CrashOrder> { new() { Id = 10_000, Status = "S00", Total = 1 } },
                    },
                });
                db.Flush();
            }

            using (var reopened = Open(dir))
            {
                Assert.Equal(before + 1, reopened.Count<CrashCustomer>());
                Assert.Equal(before * OrdersPerCustomer + 1, reopened.Count<CrashOrder>());
                Assert.NotNull(reopened.Find<CrashCustomer>(c => c.Id == 10_000));
            }
        }

        [Fact]
        public void A_completed_batch_is_still_there_afterwards()
        {
            string dir = Seed("commit");

            (int code, string output) = RunWriter("onebatch", dir);
            Assert.True(code == 0, $"commit run failed ({code}): {output}");

            using var db = Open(dir);
            Assert.Equal(BatchCustomers * 2, db.Count<CrashCustomer>());
            Assert.Equal(BatchCustomers * 2 * OrdersPerCustomer, db.Count<CrashOrder>());
        }
    }
}
