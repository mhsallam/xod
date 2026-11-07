using System.Diagnostics;
using System.Text.Json;

namespace XodBench;

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--worker")
        {
            return RunWorker(args);
        }

        if (args.Length > 0 && args[0] == "--stress")
        {
            return RunStress(args);
        }
                return RunOrchestrator(args);
    }

    private static int RunStress(string[] args)
    {
        var options = new StressRunner.Options();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Length: options.Profile = args[++i]; break;
                case "--minutes" when i + 1 < args.Length: options.Minutes = int.Parse(args[++i]); break;
                case "--max-rows" when i + 1 < args.Length: options.MaxRows = long.Parse(args[++i]); break;
                case "--seed-rows" when i + 1 < args.Length: options.SeedRows = long.Parse(args[++i]); break;
                case "--payload" when i + 1 < args.Length: options.PayloadChars = int.Parse(args[++i]); break;
                case "--min-free-gb" when i + 1 < args.Length: options.MinFreeGb = double.Parse(args[++i]); break;
                case "--out" when i + 1 < args.Length: options.Out = args[++i]; break;
                case "--dir" when i + 1 < args.Length: options.Dir = args[++i]; break;
                case "--dry-run": options.DryRun = true; break;
                case "--probe" when i + 1 < args.Length: options.ProbeCount = int.Parse(args[++i]); break;
                case "--no-index": options.NoIndex = true; break;
                case "--sample-seconds" when i + 1 < args.Length: options.SampleSeconds = int.Parse(args[++i]); break;
                case "--seconds" when i + 1 < args.Length: options.Seconds = int.Parse(args[++i]); break;
                case "--hot-rows" when i + 1 < args.Length: options.HotRows = long.Parse(args[++i]); break;
                case "--readers" when i + 1 < args.Length: options.Readers = int.Parse(args[++i]); break;
                case "--writers" when i + 1 < args.Length: options.Writers = int.Parse(args[++i]); break;
                case "--flush-ms" when i + 1 < args.Length: options.FlushMs = int.Parse(args[++i]); break;
                case "--sample-rows" when i + 1 < args.Length: options.SampleRows = long.Parse(args[++i]); break;
                case "--checkpoints" when i + 1 < args.Length:
                    options.Checkpoints = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();
                    break;
                case "--cpus" when i + 1 < args.Length: options.PinPerformanceCores = args[++i] != "all"; break;
                case "--publishable": options.Publishable = true; break;
                default:
                    // A mistyped option would otherwise run the default workload under the wrong name.
                    Console.Error.WriteLine($"[stress] unknown or incomplete option: {args[i]}");
                    return 2;
            }
        }

        // Ctrl+C ends the run through its own end row ("interrupted") instead of killing it mid-sample.
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        return StressRunner.Run(options, stop.Token);
    }

    private static int RunWorker(string[] args)
    {
        string engine = "xod";
        string suite = "flat";
        string scale = "smoke";
        string dir = "";
        int iteration = 1;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--engine" when i + 1 < args.Length: engine = args[++i]; break;
                case "--suite" when i + 1 < args.Length: suite = args[++i]; break;
                case "--scale" when i + 1 < args.Length: scale = args[++i]; break;
                case "--dir" when i + 1 < args.Length: dir = args[++i]; break;
                case "--iteration" when i + 1 < args.Length: int.TryParse(args[++i], out iteration); break;
            }
        }

        var result = new RunResult
        {
            Engine = engine,
            EngineLabel = Engines.Label(engine),
            Suite = suite,
            Scale = scale,
            Iteration = iteration
        };

        try
        {
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(Path.GetTempPath(), $"XodBench_{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);
            }

            switch (suite.ToLowerInvariant())
            {
                case "flat":
                    Runner.RunFlat(engine, scale, dir, result);
                    break;
                case "relational":
                case "rel":
                    Runner.RunRelational(engine, scale, dir, result);
                    break;
                case "concurrency":
                case "conc":
                    Runner.RunConcurrency(engine, scale, dir, result);
                    break;
                default:
                    throw new ArgumentException($"Unknown suite '{suite}'");
            }

            Console.WriteLine("RESULT_JSON:" + JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            result.Error = ex.ToString();
            Console.WriteLine("RESULT_JSON:" + JsonSerializer.Serialize(result));
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch { }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: XodBench [options]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --engine, --engines <names>  Comma-separated list of engines to run (xod-xml, xod-json, xod-bin, sqlite, litedb, all). Default: all");
        Console.WriteLine("  --suite, --suites <names>    Suites to run (flat, relational, concurrency, all). Default: all");
        Console.WriteLine("  --scale, --scales <names>    Scales to run (smoke, 250mb, 1gb, 10k, 40k, all). Default: all");
        Console.WriteLine("  --smoke                      Shortcut for running smoke scale across suites (iterations=1).");
        Console.WriteLine("  --no-1gb, --exclude-1gb      Exclude the 1GB scale from flat suite.");
        Console.WriteLine("  --iterations, -n <count>     Number of iterations per suite/scale. Default: 3 (1 for smoke).");
              Console.WriteLine("  --help, -h                   Show this help text.");
    }

    private static int RunOrchestrator(string[] args)
    {
        Console.WriteLine("======================================================================");
        Console.WriteLine(" XOD++ v1.2.5 Enterprise Benchmark Suite v2 (Fair & Multi-Faceted)   ");
        Console.WriteLine(" Comparing: XOD++ v1.2.5 vs SQLite 3 (WAL) vs LiteDB 5.0             ");
        Console.WriteLine("======================================================================");

        string suitesArg = "all";
        string scalesArg = "all";
        string enginesArg = "all";
        int iterations = 3;
        bool quickSmoke = false;
        bool exclude1Gb = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--suites" or "--suite" when i + 1 < args.Length:
                    suitesArg = args[++i];
                    break;
                case "--scales" or "--scale" when i + 1 < args.Length:
                    scalesArg = args[++i];
                    break;
                case "--engines" or "--engine" when i + 1 < args.Length:
                    enginesArg = args[++i];
                    break;
                case "--iterations" or "-n" when i + 1 < args.Length:
                    int.TryParse(args[++i], out iterations);
                    break;
                case "--smoke":
                    quickSmoke = true;
                    break;
                case "--no-1gb" or "--exclude-1gb":
                    exclude1Gb = true;
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return 0;
            }
        }

        if (quickSmoke || scalesArg.Equals("smoke", StringComparison.OrdinalIgnoreCase))
        {
            iterations = 1;
        }

        var runsToExecute = new List<(string Suite, string Scale)>();
        if (quickSmoke || scalesArg.Equals("smoke", StringComparison.OrdinalIgnoreCase))
        {
            if (suitesArg == "all" || suitesArg.Contains("flat")) runsToExecute.Add(("flat", "smoke"));
            if (suitesArg == "all" || suitesArg.Contains("rel")) runsToExecute.Add(("relational", "smoke"));
            if (suitesArg == "all" || suitesArg.Contains("conc")) runsToExecute.Add(("concurrency", "smoke"));
        }
        else
        {
            if (suitesArg == "all" || suitesArg.Contains("flat"))
            {
                if (scalesArg == "all" || scalesArg.Contains("250mb")) runsToExecute.Add(("flat", "250mb"));
                if (!exclude1Gb && (scalesArg == "all" || scalesArg.Contains("1gb"))) runsToExecute.Add(("flat", "1gb"));
            }
            if (suitesArg == "all" || suitesArg.Contains("rel"))
            {
                if (scalesArg == "all" || scalesArg.Contains("10k")) runsToExecute.Add(("relational", "10k"));
                if (scalesArg == "all" || scalesArg.Contains("40k")) runsToExecute.Add(("relational", "40k"));
            }
            if (suitesArg == "all" || suitesArg.Contains("conc"))
            {
                if (scalesArg == "all" || scalesArg.Contains("250mb")) runsToExecute.Add(("concurrency", "250mb"));
            }
        }

        if (runsToExecute.Count == 0)
        {
            Console.WriteLine("[!] No matching benchmark suites/scales found to execute.");
            return 1;
        }

        string[] selectedEngines;
        if (string.Equals(enginesArg, "all", StringComparison.OrdinalIgnoreCase))
        {
            selectedEngines = Engines.All;
        }
        else
        {
            var requested = enginesArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = new List<string>();
            foreach (var r in requested)
            {
                string norm = r.ToLowerInvariant();
                if (norm == "xod") norm = "xod-xml";
                list.Add(norm);
            }
            selectedEngines = list.ToArray();
        }

        Console.WriteLine($"[+] Engines to benchmark ({selectedEngines.Length}): {string.Join(", ", selectedEngines)}");
        Console.WriteLine($"[+] Suites/Scales planned: {string.Join(", ", runsToExecute.Select(r => $"{r.Suite}({r.Scale})"))}");

        string repoRoot = Machine.RepoRoot();
        var stamp = BenchStamp.Capture(repoRoot);
        Console.WriteLine($"[+] Code: v{stamp.LibraryVersion} @ {stamp.GitCommit} ({stamp.GitWorkingTree}) | .NET {stamp.DotnetRuntime}");
        if (stamp.GitCommit == "unknown")
            Console.WriteLine("[!] git could not be read: results will be stamped 'unknown' and cannot be cited as a specific revision.");

        var allResults = new List<RunResult>();
        // Launch children the way this process was launched: the apphost runs itself, while
        // "dotnet XodBench.dll" must hand the dll back to dotnet or the child has no entry point.
        string currentExe = Environment.ProcessPath;
        string selfDll = string.Empty;
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(entryAssembly)
            && !string.Equals(Path.GetFileNameWithoutExtension(currentExe), Path.GetFileNameWithoutExtension(entryAssembly), StringComparison.OrdinalIgnoreCase))
        {
            selfDll = $"\"{entryAssembly}\" ";
        }

        foreach (var (suite, scale) in runsToExecute)
        {
            Console.WriteLine($"\n>>> Executing Suite: {suite.ToUpperInvariant()} (Scale: {scale.ToUpperInvariant()}, Iterations: {iterations}) <<<");

            for (int iter = 1; iter <= iterations; iter++)
            {
                // Rotate engine order each iteration to neutralize thermal or OS page cache sequence effects
                var allEng = selectedEngines;
                var engines = new string[allEng.Length];
                for (int e = 0; e < allEng.Length; e++)
                {
                    engines[e] = allEng[(e + iter - 1) % allEng.Length];
                }

                foreach (var eng in engines)
                {
                    Console.Write($"[{DateTime.Now:HH:mm:ss}] Iter {iter}/{iterations} | {Engines.Label(eng),-18} | {suite,11} ({scale})... ");

                    string tempDir = Path.Combine(Path.GetTempPath(), $"XodBench_{suite}_{scale}_{eng}_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempDir);

                    var psi = new ProcessStartInfo
                    {
                        FileName = currentExe,
                        Arguments = $"{selfDll}--worker --engine {eng} --suite {suite} --scale {scale} --dir \"{tempDir}\" --iteration {iter}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    string stdOut = proc.StandardOutput.ReadToEnd();
                    string stdErr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    RunResult r = null;
                    foreach (var line in stdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("RESULT_JSON:"))
                        {
                            try
                            {
                                r = JsonSerializer.Deserialize<RunResult>(line.Substring("RESULT_JSON:".Length));
                            }
                            catch { }
                        }
                    }

                    if (proc.ExitCode == 0 && r != null && string.IsNullOrEmpty(r.Error))
                    {
                        r.Stamp = stamp;
                        allResults.Add(r);
                        Console.WriteLine("DONE.");
                    }
                    else
                    {
                        Console.WriteLine($"FAILED! (ExitCode {proc.ExitCode})");
                        if (!string.IsNullOrEmpty(r?.Error)) Console.Error.WriteLine(r.Error);
                        if (!string.IsNullOrEmpty(stdErr)) Console.Error.WriteLine(stdErr);
                    }
                }
            }
        }

        string resultsDir = repoRoot != null
            ? Path.Combine(repoRoot, "benchmarks", "results")
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "results");
        Directory.CreateDirectory(resultsDir);
        string jsonPath = Path.Combine(resultsDir, $"bench-v{stamp.LibraryVersion}-{stamp.GitCommit}-{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(allResults, new JsonSerializerOptions { WriteIndented = true }));
        // The machine these ran on is private: a git-ignored record beside the result (Machine.RecordPath).
        double freeGb = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(resultsDir))!).AvailableFreeSpace / 1073741824.0;
        Machine.WriteRecord(jsonPath, Machine.Describe("all", Machine.LogicalCpus, freeGb, Machine.OnAcPower()));
        Console.WriteLine($"\n[+] Raw results saved to: {jsonPath}");

        return 0;
    }
}
