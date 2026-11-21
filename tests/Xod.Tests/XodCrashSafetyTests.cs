using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xod;
using Xunit;

// Same global type names as tests/Xod.CrashWriter - the child process and this test read the same files.
// A unique key, for the index crash tests (ISS-043).
public class CrashMember
{
    [PrimaryKey] public int Id { get; set; }
    [UniqueKey] public string Email { get; set; }
}

// A second standalone table (no relations), beside CrashMember (ISS-124; ISS-209's long-lived context).
public class CrashNote
{
    [PrimaryKey] public int Id { get; set; }
    public string Text { get; set; }
}

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
    /// Gate layer 4 of TEST-PLAN-2.0: a process killed inside an <c>InsertRange</c> leaves none of that batch.
    ///
    /// The child writes 25 customers with 3 orders each in one <c>InsertRange</c> (its rows reach the disk only at its
    /// end), flushes, prints a marker and
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

        internal static string RepoRoot()
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

        // The writer built with these tests: the same configuration and framework, beside them (the project
        // reference builds it). The newest writer of any build was taken before, and a Debug one built from other
        // code ran in a Release run.
        internal static string WriterDll()
        {
            var framework = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string writer = Path.Combine(RepoRoot(), "tests", "Xod.CrashWriter", "bin", framework.Parent!.Name, framework.Name, "Xod.CrashWriter.dll");
            if (!File.Exists(writer))
                throw new FileNotFoundException("The crash writer built with these tests isn't there: " + writer);
            return writer;
        }

        private (int ExitCode, string Output) RunWriter(string mode, string dir, int? crashAfterRows = null) =>
            crashAfterRows.HasValue
                ? RunWriterProcess(mode, dir, ("XOD_CRASH_AFTER_ROWS", crashAfterRows.Value.ToString()))
                : RunWriterProcess(mode, dir);

        /// <summary>Starts the crash writer with its standard input open (for XOD_PAUSE_AT_STEP).</summary>
        internal static Process StartWriterProcess(string mode, string dir, params (string Name, string Value)[] env)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(WriterDll());
            psi.ArgumentList.Add(mode);
            psi.ArgumentList.Add(dir);
            foreach (var (name, value) in env)
                psi.Environment[name] = value;
            return Launch(psi);
        }

        // Each writer's standard error, read from its start. A writer that fails can write more there than a pipe
        // holds (an aggregate of a parallel flush's exceptions passed 11 KB), and one read only once its standard
        // output had ended waited forever: the writer blocked on the full pipe and never closed its output.
        private static readonly ConditionalWeakTable<Process, Task<string>> Errors = new();

        private static Process Launch(ProcessStartInfo psi)
        {
            var process = Process.Start(psi)!;
            Errors.Add(process, process.StandardError.ReadToEndAsync());
            return process;
        }

        /// <summary>The rest of a writer's output, its standard error after it, once it has exited. One that hasn't
        /// within <paramref name="timeoutMs"/> is killed, and the test fails.</summary>
        internal static string FinishWriter(Process process, int timeoutMs = 120_000)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.WaitForExit();
                Assert.Fail("The writer didn't exit within " + timeoutMs + " ms: " + output.Result + Errors.GetValue(process, p => Task.FromResult("")).Result);
            }
            process.WaitForExit();
            return output.Result + Errors.GetValue(process, p => Task.FromResult("")).Result;
        }

        /// <summary>Runs the crash writer to completion (or its crash); <paramref name="env"/> sets the fault
        /// injection variables and the writer's options (XOD_TEST_DURABILITY).</summary>
        internal static (int ExitCode, string Output) RunWriterProcess(string mode, string dir, params (string Name, string Value)[] env)
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

            foreach (var (name, value) in env)
                psi.Environment[name] = value;

            using var process = Launch(psi);
            string output = FinishWriter(process);
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
