using System.Text.Json;
using XodBench;
using Xunit;

namespace XodBench.Tests;

/// <summary>
/// The stress files are what the site publishes, so their contents are the contract. A published soak
/// said "a writer keeps committing" while its updates were never flushed, "four hours" for a run that
/// was stopped from outside after five, and "cold reopen" for the time to construct a context. These
/// tests pin a header that describes what ran, writes that are committed during the run and verified
/// after a reopen, an end row that says how the run stopped, and a reopen measured to its first answer.
/// </summary>
public class StressRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "XodBenchTests_" + Guid.NewGuid().ToString("N"));

    public StressRunnerTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private StressRunner.Options SoakOptions(int seconds) => new()
    {
        Profile = "soak",
        SeedRows = 20_000,
        HotRows = 5_000,
        Readers = 2,
        Writers = 1,
        Seconds = seconds,
        SampleSeconds = 1,
        FlushMs = 200,
        PayloadChars = 32,
        MinFreeGb = 0,
        PinPerformanceCores = false,
        Dir = Path.Combine(root, "db"),
        Out = Path.Combine(root, "out.jsonl"),
    };

    private static List<JsonElement> Lines(string path) =>
        File.ReadAllLines(path)
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();

    private static JsonElement Single(List<JsonElement> lines, string type) =>
        Assert.Single(lines, l => l.GetProperty("type").GetString() == type);

    private static List<JsonElement> All(List<JsonElement> lines, string type) =>
        lines.Where(l => l.GetProperty("type").GetString() == type).ToList();

    [Fact]
    public void The_machine_a_run_used_goes_to_a_private_record_beside_the_result_not_into_it()
    {
        var o = SoakOptions(seconds: 2);
        Assert.Equal(0, StressRunner.Run(o, CancellationToken.None));

        // The result file is committed and published; the owner does not publish the machine.
        var header = Single(Lines(o.Out), "run");
        Assert.False(header.TryGetProperty("machine", out _), "the published header must not describe the machine");
        string text = File.ReadAllText(o.Out);
        foreach (var key in new[] { "\"cpu\"", "\"logicalCpus\"", "\"ramGb\"", "\"os\"", "\"affinityCpus\"", "\"freeDiskGb\"", "\"availMbAtStart\"", "\"runtime\"" })
            Assert.DoesNotContain(key, text);
        // What the site needs to judge the run stays in it: whether it ran on mains power.
        Assert.True(header.TryGetProperty("onAcPower", out _), "the header must say whether the run was on mains power");

        // The owner keeps the machine to reproduce a run, in a record git ignores.
        var record = JsonDocument.Parse(File.ReadAllText(Machine.RecordPath(o.Out))).RootElement;
        Assert.True(record.GetProperty("logicalCpus").GetInt32() > 0);
        Assert.True(record.GetProperty("ramGb").GetDouble() > 0);
        Assert.False(string.IsNullOrEmpty(record.GetProperty("runtime").GetString()));
        Assert.Equal("all", record.GetProperty("affinity").GetString());
        Assert.Equal(Path.Combine(root, ".machine", "out.jsonl.json"), Machine.RecordPath(o.Out));
    }

    [Fact]
    public void A_comparison_stamp_names_the_code_and_the_runtime_major_not_the_machine()
    {
        string json = JsonSerializer.Serialize(BenchStamp.Capture(null));
        foreach (var key in new[] { "\"Os\"", "\"Cpu\"", "\"CpuCount\"", "\"Machine\"" })
            Assert.DoesNotContain(key, json);
        Assert.Equal(Environment.Version.Major.ToString(), BenchStamp.Capture(null).DotnetRuntime);
    }

    [Fact]
    public void A_soak_header_describes_the_workload_that_ran()
    {
        var o = SoakOptions(seconds: 2);
        Assert.Equal(0, StressRunner.Run(o, CancellationToken.None));

        var header = Single(Lines(o.Out), "run");
        var workload = header.GetProperty("workload");
        Assert.Equal(20_000, workload.GetProperty("seedRows").GetInt64());
        Assert.Equal(5_000, workload.GetProperty("hotRows").GetInt64());
        Assert.Equal(2, workload.GetProperty("readers").GetInt32());
        Assert.Equal(1, workload.GetProperty("writers").GetInt32());
        Assert.Equal(200, workload.GetProperty("flushMs").GetInt32());
        Assert.Equal("Buffered", workload.GetProperty("durability").GetString());
        Assert.Equal(2, workload.GetProperty("seconds").GetInt32());
        Assert.False(header.TryGetProperty("rows", out _), "the soak header must not carry the capacity row target");

        // The identity of the code that ran, and the rules the run will be judged by, fixed at its start.
        Assert.Matches("^[0-9a-f]{12}$", header.GetProperty("build").GetString());
        var criteria = header.GetProperty("criteria");
        Assert.Equal(0.8, criteria.GetProperty("writesAtLeast").GetDouble());
        Assert.Equal(4096, criteria.GetProperty("minFreeMbBesidesRun").GetInt32());
    }

    [Fact]
    public void Every_read_in_an_interval_is_in_that_interval_s_percentiles()
    {
        var o = SoakOptions(seconds: 3);
        Assert.Equal(0, StressRunner.Run(o, CancellationToken.None));

        var lines = Lines(o.Out);
        var samples = All(lines, "sample");
        Assert.True(samples.Count >= 2, $"{samples.Count} samples");

        long previous = 0;
        foreach (var s in samples)
        {
            long reads = s.GetProperty("reads").GetInt64();
            var latency = s.GetProperty("readLatency");
            Assert.Equal(reads - previous, latency.GetProperty("count").GetInt64());
            Assert.True(latency.GetProperty("p99Us").GetDouble() >= latency.GetProperty("p50Us").GetDouble());
            Assert.True(latency.GetProperty("maxUs").GetDouble() >= latency.GetProperty("p999Us").GetDouble());
            Assert.True(s.TryGetProperty("privateMb", out _));
            Assert.True(s.TryGetProperty("gcHeapMb", out _));
            Assert.True(s.TryGetProperty("hostCpuPct", out _));
            Assert.True(s.TryGetProperty("otherCpuPct", out _));
            Assert.True(s.TryGetProperty("availMb", out _));
            previous = reads;
        }

        // The end row counts every reader after they stop; the last sample is taken at the deadline, a
        // moment before. A sample that missed a reader's reads would fall far short of it.
        long total = Single(lines, "end").GetProperty("reads").GetInt64();
        Assert.InRange(previous, total * 9 / 10, total);
    }

    [Fact]
    public void Writes_are_committed_during_the_run_and_verified_after_a_reopen()
    {
        var o = SoakOptions(seconds: 3);
        Assert.Equal(0, StressRunner.Run(o, CancellationToken.None));

        var lines = Lines(o.Out);
        Assert.Contains(All(lines, "sample"), s => s.GetProperty("commits").GetInt64() > 0);

        var end = Single(lines, "end");
        Assert.Equal("planned", end.GetProperty("stoppedBy").GetString());
        Assert.Equal(0, end.GetProperty("code").GetInt32());
        Assert.Equal(0, end.GetProperty("misses").GetInt64());
        Assert.True(end.GetProperty("verified").GetBoolean());
        Assert.True(end.GetProperty("writes").GetInt64() > 0);
        Assert.True(end.GetProperty("commits").GetInt64() >= 5, $"{end.GetProperty("commits")} commits in 3 s at 200 ms");
        Assert.True(end.GetProperty("closeMs").GetDouble() >= 0);
        Assert.True(end.GetProperty("firstAnswerMs").GetDouble() >= end.GetProperty("reopenMs").GetDouble());

        var verify = end.GetProperty("verify");
        Assert.Equal(20_000, verify.GetProperty("rows").GetInt64());
        Assert.Equal(verify.GetProperty("expectedHotPriceSum").GetDouble(), verify.GetProperty("hotPriceSum").GetDouble());
        Assert.Equal(verify.GetProperty("expectedHotIdWeightedSum").GetDouble(), verify.GetProperty("hotIdWeightedSum").GetDouble());
        Assert.True(verify.GetProperty("uniqueKeyEnforced").GetBoolean());
        Assert.True(verify.GetProperty("ok").GetBoolean());
    }

    // The check must be able to fail: an update that is counted but never written has to show up.
    [Fact]
    public void A_lost_update_fails_the_check_after_a_reopen_and_the_exit_code()
    {
        var o = SoakOptions(seconds: 2);
        o.TestDropEveryNthUpdate = 50;
        Assert.Equal(3, StressRunner.Run(o, CancellationToken.None));

        var end = Single(Lines(o.Out), "end");
        Assert.Equal(3, end.GetProperty("code").GetInt32());
        Assert.False(end.GetProperty("verified").GetBoolean());
        var verify = end.GetProperty("verify");
        Assert.False(verify.GetProperty("ok").GetBoolean());
        Assert.NotEqual(verify.GetProperty("expectedHotPriceSum").GetDouble(), verify.GetProperty("hotPriceSum").GetDouble());
    }

    [Fact]
    public void A_mistyped_option_stops_the_run_instead_of_running_defaults()
    {
        Assert.Equal(2, Program.Main(new[] { "--stress", "--profiel", "soak" }));
        Assert.Equal(2, Program.Main(new[] { "--stress", "--seconds" }));
    }

    [Fact]
    public void A_soak_stopped_early_says_so_and_still_verifies()
    {
        var o = SoakOptions(seconds: 120);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.Equal(0, StressRunner.Run(o, stop.Token));

        var end = Single(Lines(o.Out), "end");
        Assert.Equal("interrupted", end.GetProperty("stoppedBy").GetString());
        Assert.True(end.GetProperty("seconds").GetDouble() < 60);
        Assert.True(end.GetProperty("verify").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Capacity_samples_by_rows_and_reports_reopen_to_a_first_answer()
    {
        var o = new StressRunner.Options
        {
            Profile = "capacity",
            MaxRows = 30_000,
            SampleRows = 10_000,
            Checkpoints = new long[] { 20_000 },
            ProbeCount = 200,
            PayloadChars = 32,
            MinFreeGb = 0,
            PinPerformanceCores = false,
            Dir = Path.Combine(root, "cap"),
            Out = Path.Combine(root, "cap.jsonl"),
        };
        Assert.Equal(0, StressRunner.Run(o, CancellationToken.None));

        var lines = Lines(o.Out);
        var workload = Single(lines, "run").GetProperty("workload");
        Assert.Equal(30_000, workload.GetProperty("targetRows").GetInt64());
        Assert.Equal(10_000, workload.GetProperty("sampleRows").GetInt64());
        Assert.Equal(200, workload.GetProperty("probes").GetInt32());

        var samples = All(lines, "sample");
        Assert.Equal(new long[] { 10_000, 20_000, 30_000 }, samples.Select(s => s.GetProperty("rows").GetInt64()).ToArray());

        var checkpoint = Single(lines, "checkpoint");
        Assert.Equal(20_000, checkpoint.GetProperty("rows").GetInt64());
        Assert.Equal(200, checkpoint.GetProperty("probes").GetInt32());
        Assert.True(checkpoint.GetProperty("firstAnswerMs").GetDouble() >= checkpoint.GetProperty("reopenMs").GetDouble());
        Assert.True(checkpoint.TryGetProperty("closeMs", out _));
        Assert.True(checkpoint.TryGetProperty("privateMb", out _));
        Assert.Equal(0, checkpoint.GetProperty("probeMisses").GetInt32());

        var end = Single(lines, "end");
        Assert.Equal("planned", end.GetProperty("stoppedBy").GetString());
        Assert.Equal(30_000, end.GetProperty("rows").GetInt64());
        Assert.True(end.GetProperty("seconds").GetDouble() > 0);
        Assert.True(end.GetProperty("verified").GetBoolean());
    }

    // A 10M-row capacity run leaves a 4.85 GB database; five in a row filled the temp folder by 24 GB.
    [Fact]
    public void A_work_folder_the_harness_made_is_removed_and_one_it_was_given_is_kept()
    {
        var made = SoakOptions(seconds: 2);
        made.Dir = null;
        Assert.Equal(0, StressRunner.Run(made, CancellationToken.None));
        Assert.NotNull(made.Dir);
        Assert.False(Directory.Exists(made.Dir), $"left behind: {made.Dir}");
        Assert.Equal("planned", Single(Lines(made.Out), "end").GetProperty("stoppedBy").GetString());

        var given = SoakOptions(seconds: 2);
        Assert.Equal(0, StressRunner.Run(given, CancellationToken.None));
        Assert.True(Directory.Exists(given.Dir));
    }

    // An hour-long run of a tree the site will not publish is an hour lost: refuse it before it starts.
    [Fact]
    public void A_publishable_run_refuses_uncommitted_code_or_battery_power_before_it_starts()
    {
        var dirty = SoakOptions(seconds: 2);
        dirty.Publishable = true;
        dirty.ReadCommit = () => "abc1234-dirty";
        dirty.ReadAcPower = () => true;
        Assert.Equal(2, StressRunner.Run(dirty, CancellationToken.None));
        Assert.False(File.Exists(dirty.Out));

        var battery = SoakOptions(seconds: 2);
        battery.Publishable = true;
        battery.ReadCommit = () => "abc1234";
        battery.ReadAcPower = () => false;
        Assert.Equal(2, StressRunner.Run(battery, CancellationToken.None));
        Assert.False(File.Exists(battery.Out));

        var clean = SoakOptions(seconds: 2);
        clean.Publishable = true;
        clean.ReadCommit = () => "abc1234";
        clean.ReadAcPower = () => null; // a desktop cannot tell, and is not held to it
        Assert.Equal(0, StressRunner.Run(clean, CancellationToken.None));
        var header = Single(Lines(clean.Out), "run");
        Assert.Equal("abc1234", header.GetProperty("commit").GetString());
        Assert.Equal(JsonValueKind.Null, header.GetProperty("onAcPower").ValueKind);
    }

    // "A number is quotable only when ... its stamp is clean" (benchmarks/results/README.md). A new source
    // file git does not track yet changes the code as much as an edit does; the run's own result files,
    // written into benchmarks/results while it runs, do not.
    [Theory]
    [InlineData("", false)]
    [InlineData(" M benchmarks/XodBench/StressRunner.cs", true)]
    [InlineData("M  src/Xod/XodContext.cs", true)]
    [InlineData("?? benchmarks/XodBench/NewFile.cs", true)]
    [InlineData("?? benchmarks/results/stress/stress-soak-20261107_030000.jsonl", false)]
    [InlineData(" M benchmarks/results/README.md", false)]
    [InlineData("?? benchmarks/results/stress/a.jsonl\n?? src/Xod/New.cs", true)]
    [InlineData("R  benchmarks/results/old.json -> src/Xod/Moved.cs", true)]
    [InlineData("?? \"benchmarks/results/stress/with space.jsonl\"", false)]
    public void Uncommitted_code_marks_the_commit_dirty_and_result_files_do_not(string porcelain, bool dirty)
    {
        Assert.Equal(dirty, Machine.IsDirty(porcelain));
    }

    // A run names the checkout the harness was built from. In a linked worktree or a submodule .git is a
    // file, not a folder; a lookup for the folder found nothing there, so the comparison suite stamped its
    // results "unknown" and wrote them under bin/ instead of benchmarks/results.
    [Fact]
    public void The_harness_finds_the_checkout_it_was_built_from()
    {
        string root = Machine.RepoRoot();
        Assert.NotNull(root);
        Assert.True(File.Exists(Path.Combine(root, "benchmarks", "XodBench", "XodBench.csproj")), root);
    }

    // The machine records sit beside committed result files; only the ignore rule keeps them out of git.
    [Fact]
    public void Git_ignores_the_machine_records_beside_the_results()
    {
        string repo = Machine.RepoRoot();
        Assert.NotNull(repo);
        string record = Machine.RecordPath(Path.Combine(repo!, "benchmarks", "results", "stress", "probe.jsonl"));

        var psi = new System.Diagnostics.ProcessStartInfo("git", $"check-ignore -q \"{record}\"")
        {
            WorkingDirectory = repo!,
            UseShellExecute = false,
        };
        using var git = System.Diagnostics.Process.Start(psi)!;
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode); // 0: ignored; 1: it would be committed
    }
}
