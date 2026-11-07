using System.Diagnostics;

namespace XodBench;

public sealed class RunResult
{
    public string Engine { get; set; }
    public string EngineLabel { get; set; }
    public string Suite { get; set; }
    public string Scale { get; set; }
    public int Iteration { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = new();
    public RunStamp Stamp { get; set; }
    public string Error { get; set; }
}

public static class Scales
{
    public static int FlatRows(string scale) => scale switch
    {
        "smoke" => 4_000,
        "20mb" => 8_000,
        "250mb" => 100_000,
        "1gb" => 400_000,
        _ => throw new ArgumentException($"Unknown flat scale '{scale}'")
    };

    public static int Customers(string scale) => scale switch
    {
        "smoke" => 500,
        "10k" => 10_000,
        "40k" => 40_000,
        _ => throw new ArgumentException($"Unknown relational scale '{scale}'")
    };
}

public static class Runner
{
    public static double ConcurrencySeconds = 5;
    public static int[] ReaderCounts = { 1, 4, 8 };

    private static double Time(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static void Settle()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    private static double DirSizeMb(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / (1024.0 * 1024.0);

    private static double PeakWorkingSetMb()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PeakWorkingSet64 / (1024.0 * 1024.0);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Validation failed: " + message);
    }

    private static int[] DistinctRandom(Random rng, int count, int minInclusive, int maxExclusive)
    {
        var set = new HashSet<int>();
        var list = new List<int>(count);
        while (list.Count < count)
        {
            int v = rng.Next(minInclusive, maxExclusive);
            if (set.Add(v)) list.Add(v);
        }
        return list.ToArray();
    }

    // ---------------------------------------------------------------- Flat

    public static void RunFlat(string engine, string scale, string dir, RunResult result)
    {
        int n = Scales.FlatRows(scale);
        var m = result.Metrics;
        var rows = DataGen.FlatRows(n);
        Settle();

        var store = Engines.Flat(engine);
        store.Open(dir, create: true, concurrentMode: false);
        m["bulkInsertMs"] = Time(() => store.BulkInsert(rows));
        rows = null;
        m["closeMs"] = Time(store.Dispose);
        m["sizeMB"] = DirSizeMb(dir);
        Settle();

        // Cold open (fresh handle on an existing DB) + 1 insert + commit.
        store = Engines.Flat(engine);
        m["coldOpenInsertMs"] = Time(() =>
        {
            store.Open(dir, create: false, concurrentMode: false);
            store.InsertOneAndCommit(DataGen.ExtraRow(n));
        });

        try
        {
            // The same open handle is used for every warm phase below, for every engine.
            var rng = new Random(42);
            // Touch the whole key range before timing. XOD builds a page's row index lazily, so a cold
            // page costs a full-page scan: 100 random probes leave most of a 200-page database cold,
            // which made page-size comparisons meaningless. Every engine gets the same probes, so the
            // comparison stays fair; the timed lookups are still random.
            int warmCount = Math.Min(n, 4096);
            for (int i = 0; i < warmCount; i++)
            {
                int warmId = 1 + (int)((long)i * n / warmCount);
                store.GetById(warmId);
            }
            int[] ids = Enumerable.Range(0, 1000).Select(_ => rng.Next(1, n + 1)).ToArray();
            Settle();
            m["pkLookup1000Ms"] = Time(() =>
            {
                foreach (int id in ids)
                {
                    var row = store.GetById(id);
                    if (row == null || row.Id != id) throw new InvalidOperationException($"PK lookup {id} failed");
                }
            });

            Check(store.Count() == n + 1, "count after cold insert");
            var lastRow = store.Last();
            Check(lastRow != null && lastRow.Id == n + 1, $"last id {lastRow?.Id} != {n + 1}");
            m["countLastMs"] = Time(() =>
            {
                for (int i = 0; i < 10; i++)
                {
                    if (store.Count() != n + 1) throw new InvalidOperationException("count");
                    if (store.Last()?.Id != n + 1) throw new InvalidOperationException("last");
                }
            }) / 10.0;

            store.QueryByCategory(3);
            Settle();
            int expected = 20 * (n / DataGen.Categories);
            int matched = 0;
            m["indexedQuery20Ms"] = Time(() =>
            {
                for (int i = 1; i <= 20; i++)
                {
                    int cat = i * 5;
                    var list = store.QueryByCategory(cat);
                    foreach (var r in list) if (r.CategoryId != cat) throw new InvalidOperationException("wrong category");
                    matched += list.Count;
                }
            });
            Check(matched == expected, $"indexed query matched {matched} != {expected}");

            int skip = n * 9 / 10;
            store.Page(n / 2, 25);
            m["deepPageMs"] = Time(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    var page = store.Page(skip, 25);
                    if (page.Count != 25 || page[0].Id != skip + 1 || page[24].Id != skip + 25)
                        throw new InvalidOperationException($"deep page returned {page.Count} rows starting at {page.FirstOrDefault()?.Id}");
                }
            }) / 5.0;

            int[] updateIds = DistinctRandom(rng, 100, 1, n - 200);
            int[] deleteIds = Enumerable.Range(n - 150, 100).ToArray();
            double before = store.GetById(updateIds[0]).Price;
            Settle();
            m["updateDeleteMs"] = Time(() => store.UpdateAndDelete(updateIds, deleteIds));
            Check(store.Count() == n + 1 - 100, "count after deletes");
            Check(Math.Abs(store.GetById(updateIds[0]).Price - (before + 10.0)) < 1e-9, "update persisted");
            Check(store.GetById(deleteIds[0]) == null, "delete persisted");
        }
        finally
        {
            store.Dispose();
        }
        m["peakWorkingSetMB"] = PeakWorkingSetMb();
    }

    // ---------------------------------------------------------------- Relational

    public static void RunRelational(string engine, string scale, string dir, RunResult result)
    {
        const int ordersPer = 5, linesPer = 4, productCount = 2000;
        int c = Scales.Customers(scale);
        var m = result.Metrics;
        var products = DataGen.Products(productCount);
        var graphs = DataGen.CustomerGraphs(c, ordersPer, linesPer, products);
        Settle();

        var store = Engines.Rel(engine);
        store.Open(dir, create: true);
        m["productInsertMs"] = Time(() => store.InsertProducts(products));
        m["graphInsertMs"] = Time(() => store.InsertGraphs(graphs));
        graphs = null;
        var counts = store.Counts();
        Check(counts == (c, c * ordersPer, c * ordersPer * linesPer), $"counts after insert {counts}");
        m["closeMs"] = Time(store.Dispose);
        m["sizeMB"] = DirSizeMb(dir);
        Settle();

        store = Engines.Rel(engine);
        Customer first = null;
        m["coldOpenLoadGraphMs"] = Time(() =>
        {
            store.Open(dir, create: false);
            first = store.LoadGraph(1);
        });

        try
        {
            ValidateGraph(first, 1, ordersPer, linesPer);
            var rng = new Random(7);
            for (int i = 2; i <= 11; i++) store.LoadGraph(i);
            int[] ids = DistinctRandom(rng, 200, 1, c + 1);
            Settle();
            m["loadGraph200Ms"] = Time(() =>
            {
                foreach (int id in ids)
                    ValidateGraph(store.LoadGraph(id), id, ordersPer, linesPer);
            });

            store.OrdersByStatus("S49");
            int perStatus = c * ordersPer / DataGen.Statuses;
            Settle();
            m["ordersByStatus10Ms"] = Time(() =>
            {
                for (int s = 0; s < 10; s++)
                {
                    string status = $"S{s:D2}";
                    var list = store.OrdersByStatus(status);
                    if (list.Count != perStatus || list.Any(o => o.Status != status))
                        throw new InvalidOperationException($"status {status}: {list.Count} != {perStatus}");
                }
            });

            int[] orderIds = DistinctRandom(rng, 200, 1, c * ordersPer + 1);
            Settle();
            m["updateStatus200Ms"] = Time(() => store.UpdateOrderStatuses(orderIds, "SHIPPED"));
            Check(store.OrdersByStatus("SHIPPED").Count == 200, "updated statuses");
            Check(store.Counts() == (c, c * ordersPer, c * ordersPer * linesPer), "update must not touch children");

            int[] deleteIds = DistinctRandom(rng, 100, 1, c + 1);
            Settle();
            m["cascadeDelete100Ms"] = Time(() => store.DeleteCustomersCascade(deleteIds));
            int left = c - 100;
            var after = store.Counts();
            Check(after == (left, left * ordersPer, left * ordersPer * linesPer), $"cascade counts {after}");
            Check(store.LoadGraph(deleteIds[0]) == null, "deleted customer gone");
        }
        finally
        {
            store.Dispose();
        }
        m["peakWorkingSetMB"] = PeakWorkingSetMb();
    }

    private static void ValidateGraph(Customer customer, int id, int ordersPer, int linesPer)
    {
        if (customer == null || customer.Id != id) throw new InvalidOperationException($"customer {id} missing");
        if (customer.Orders == null || customer.Orders.Count != ordersPer)
            throw new InvalidOperationException($"customer {id}: {customer.Orders?.Count} orders");
        foreach (var o in customer.Orders)
        {
            if (o.CustomerId != id || o.Lines == null || o.Lines.Count != linesPer)
                throw new InvalidOperationException($"order {o.Id}: {o.Lines?.Count} lines");
            foreach (var l in o.Lines)
            {
                if (l.OrderId != o.Id || l.Product == null || l.Product.Id != l.ProductId || l.Product.Sku != $"P-{l.ProductId:D6}")
                    throw new InvalidOperationException($"line {l.Id}: product {l.Product?.Id} vs {l.ProductId}");
            }
        }
    }

    // ---------------------------------------------------------------- Concurrency

    public static void RunConcurrency(string engine, string scale, string dir, RunResult result)
    {
        int n = Scales.FlatRows(scale);
        var m = result.Metrics;
        var setup = Engines.Flat(engine);
        setup.Open(dir, create: true, concurrentMode: false);
        setup.BulkInsert(DataGen.FlatRows(n));
        setup.Dispose();
        Settle();

        using var store = Engines.Flat(engine);
        store.Open(dir, create: false, concurrentMode: true);
        var warm = new Random(5);
        for (int i = 0; i < 1000; i++) store.GetById(warm.Next(1, n + 1));

        foreach (int readers in ReaderCounts)
        {
            Settle();
            var window = RunWindow(store, n, readers, TimeSpan.FromSeconds(ConcurrencySeconds));
            if (window.errors > 0)
                throw new InvalidOperationException($"{window.errors} errors with {readers} readers: {window.firstError}");
            string p = $"r{readers}_";
            m[p + "readsPerSec"] = window.reads / window.seconds;
            m[p + "writesPerSec"] = window.writes / window.seconds;
            m[p + "readP50Us"] = window.readP50;
            m[p + "readP99Us"] = window.readP99;
            m[p + "writeP50Us"] = window.writeP50;
            m[p + "writeP99Us"] = window.writeP99;
        }
        m["peakWorkingSetMB"] = PeakWorkingSetMb();
    }

    private sealed class Lane
    {
        public long Ops;
        public float[] Samples = new float[1 << 20];
        public int SampleCount;
        public void Record(double us)
        {
            Ops++;
            if (SampleCount < Samples.Length) Samples[SampleCount++] = (float)us;
        }
    }

    private static (long reads, long writes, double seconds, double readP50, double readP99, double writeP50, double writeP99, int errors, string firstError)
        RunWindow(IFlatStore store, int n, int readers, TimeSpan duration)
    {
        var readLanes = Enumerable.Range(0, readers).Select(_ => new Lane()).ToArray();
        var writeLane = new Lane();
        var workers = new List<IFlatWorker>();
        int errors = 0;
        string firstError = null;
        bool stop = false;
        var start = new Barrier(readers + 2);
        var threads = new List<Thread>();

        void Fail(Exception ex)
        {
            if (Interlocked.Increment(ref errors) == 1) firstError = ex.GetType().Name + ": " + ex.Message;
        }

        for (int t = 0; t < readers; t++)
        {
            var lane = readLanes[t];
            var worker = store.CreateWorker();
            workers.Add(worker);
            int seed = 1000 + t;
            threads.Add(new Thread(() =>
            {
                var rng = new Random(seed);
                start.SignalAndWait();
                long ticks0 = Stopwatch.GetTimestamp();
                while (!Volatile.Read(ref stop))
                {
                    int id = rng.Next(1, n + 1);
                    try
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        var row = worker.Get(id);
                        long t1 = Stopwatch.GetTimestamp();
                        if (row == null || row.Id != id) throw new InvalidOperationException($"read {id} returned {row?.Id}");
                        lane.Record((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
                    }
                    catch (Exception ex) { Fail(ex); }
                }
            }) { IsBackground = true });
        }

        var writer = store.CreateWorker();
        workers.Add(writer);
        threads.Add(new Thread(() =>
        {
            var rng = new Random(999);
            start.SignalAndWait();
            while (!Volatile.Read(ref stop))
            {
                int id = rng.Next(1, n + 1);
                try
                {
                    long t0 = Stopwatch.GetTimestamp();
                    writer.Update(id);
                    long t1 = Stopwatch.GetTimestamp();
                    writeLane.Record((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
                }
                catch (Exception ex) { Fail(ex); }
            }
        }) { IsBackground = true });

        foreach (var th in threads) th.Start();
        start.SignalAndWait();
        var sw = Stopwatch.StartNew();
        Thread.Sleep(duration);
        Volatile.Write(ref stop, true);
        foreach (var th in threads) th.Join();
        sw.Stop();
        foreach (var w in workers) w.Dispose();

        var readSamples = readLanes.SelectMany(l => l.Samples.Take(l.SampleCount)).ToArray();
        var writeSamples = writeLane.Samples.Take(writeLane.SampleCount).ToArray();
        return (readLanes.Sum(l => l.Ops), writeLane.Ops, sw.Elapsed.TotalSeconds,
            Percentile(readSamples, 0.50), Percentile(readSamples, 0.99),
            Percentile(writeSamples, 0.50), Percentile(writeSamples, 0.99), errors, firstError);
    }

    private static double Percentile(float[] samples, double p)
    {
        if (samples.Length == 0) return double.NaN;
        Array.Sort(samples);
        int idx = (int)Math.Min(samples.Length - 1, Math.Round(p * (samples.Length - 1)));
        return samples[idx];
    }
}

/// <summary>
/// Provenance for a set of runs: which library build and which runtime. Carried inside every result, so a
/// number cannot be read without knowing what produced it. The machine is not in it: result files are
/// committed and published, and the owner does not publish the machine (CPU, threads, OS build, runtime
/// patch level); Program writes it to a git-ignored record beside the result (Machine.RecordPath).
/// </summary>
public sealed class RunStamp
{
    public string Tool { get; set; } = "XodBench v2";
    public string LibraryVersion { get; set; }
    public string GitCommit { get; set; }
    public string GitWorkingTree { get; set; }
    /// <summary>The .NET major version ("9"); the patch level is in the private machine record.</summary>
    public string DotnetRuntime { get; set; }
    public string CapturedAtUtc { get; set; }
}

public static class BenchStamp
{
    public static RunStamp Capture(string repoRoot)
    {
        if (repoRoot == null) return Unknown();

        string commit = Git(repoRoot, "rev-parse", "--short", "HEAD");
        if (commit == "unknown") return Unknown();

        // Generated output (result JSON, dashboard HTML) must not make the tree look dirty: the
        // harness writes it while running, and a run that marks itself dirty cannot be cited as a
        // release result. Only source changes count.
        string status = Git(repoRoot, "status", "--porcelain", "--", ".",
            ":(exclude)benchmarks/results", ":(exclude)benchmarks/dashboard.html");
        var stamp = Unknown();
        stamp.GitCommit = commit;
        stamp.GitWorkingTree = status == "unknown" ? "unknown" : (status.Length == 0 ? "clean" : "dirty");
        return stamp;
    }

    private static RunStamp Unknown() => new()
    {
        LibraryVersion = typeof(Xod.XodContext).Assembly.GetName().Version?.ToString(3) ?? "unknown",
        GitCommit = "unknown",
        GitWorkingTree = "unknown",
        DotnetRuntime = Environment.Version.Major.ToString(),
        CapturedAtUtc = DateTime.UtcNow.ToString("O")
    };

    // "unknown" is a real answer: a run whose revision cannot be determined must say so. An empty
    // string would read as a valid commit, which is how the v1 results lost their provenance.
    private static string Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
