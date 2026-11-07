using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Xod;

namespace XodBench;

/// <summary>
/// Capacity and soak runs (PLAN-040, PLAN-042). One engine, measured over time, every sample written as
/// one JSON line so the curves can be charted later without re-running anything. The file is what the
/// site publishes, so it carries what ran (the header), what the machine was doing (every sample) and
/// how the run ended (the end row) - the page states nothing the file does not.
/// </summary>
public static class StressRunner
{
    public sealed class Options
    {
        public string Profile = "smoke";
        public int Minutes = 10;
        public int? Seconds;
        public long MaxRows = 20_000_000;
        public long SeedRows = 2_000_000;
        public long HotRows = 200_000;
        public int Readers = 8;
        public int Writers = 1;
        public int FlushMs = 1_000;
        public int PayloadChars = 256;
        public double MinFreeGb = 10;
        public string Out;
        public string Dir;
        public bool DryRun;
        public int ProbeCount = 1_000;
        public bool NoIndex;
        public int? SampleSeconds;
        public long? SampleRows;
        public long[] Checkpoints = { 1_000_000, 5_000_000, 10_000_000, 20_000_000, 50_000_000 };
        public bool PinPerformanceCores = true;

        /// <summary>Refuse to start a run the site would not publish: uncommitted code, or battery power.</summary>
        public bool Publishable;

        // Test seam: every Nth update is counted but not written, to prove the end-of-run check fails.
        internal int TestDropEveryNthUpdate;

        // What the run reads about its own code and power. The header records these same readings, so
        // the refusal and the file cannot disagree; tests replace them to judge any tree.
        internal Func<string> ReadCommit = Machine.GitCommit;
        internal Func<bool?> ReadAcPower = Machine.OnAcPower;

        internal int DurationSeconds => Seconds ?? Minutes * 60;
        internal long EffectiveHotRows => HotRows <= 0 ? SeedRows : Math.Min(HotRows, SeedRows);
        internal int SoakSampleSeconds => SampleSeconds ?? 30;
        internal long TargetRows => Profile == "smoke" ? 1_000_000 : MaxRows;
        internal long CapacitySampleRows => SampleRows ?? (TargetRows <= 1_000_000 ? 50_000 : 250_000);
    }

    private const int Batch = 10_000;

    // Written into the soak header, so the page judges a run by the rules it was started under rather
    // than by rules chosen after the data was seen (PLAN-042).
    private static readonly object SoakCriteria = new
    {
        version = "PLAN-042/2",
        minMinutes = 10,
        warmupMinutes = 5,
        windowMinutes = 10,
        readsAtLeast = 0.8,
        writesAtLeast = 0.8,
        commitsAtLeast = 0.8,
        readP99AtMost = 1.5,
        privateMemoryAtMost = 1.15,
        maxOtherCpuPct = 10,
        minFreeMbBesidesRun = 4096,
        minQuietShare = 0.8,
        maxGapSampleIntervals = 1.5,
    };

    private static readonly double NsPerTick = 1e9 / Stopwatch.Frequency;

    public static int Run(Options o) => Run(o, CancellationToken.None);

    public static int Run(Options o, CancellationToken stop)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        // A folder the harness makes is the harness's to remove (a 10M-row database is ~5 GB); one it is
        // given belongs to the caller and is kept.
        bool ownsDir = o.Dir == null;
        o.Dir ??= Path.Combine(Path.GetTempPath(), $"XodStress_{o.Profile}_{stamp}");
        o.Out ??= Path.Combine("benchmarks", "results", "stress", $"stress-{o.Profile}-{stamp}.jsonl");

        if (o.Profile == "torture")
        {
            Console.Error.WriteLine("[stress] torture runs through tests/Xod.CrashWriter; use PLAN-039's harness for now (not wired here yet)");
            return 2;
        }

        if (o.Writers is < 0 or > 1)
        {
            // The end-of-run check adds up every committed update; two writers on one row could lose one.
            Console.Error.WriteLine("[stress] --writers must be 0 or 1");
            return 2;
        }

        double freeGb = FreeGb(o.Dir);
        object workload = Workload(o);

        // Read before this run's own output file exists, so the stamp describes the tree that was built.
        string commit = o.ReadCommit();
        bool? onAcPower = o.ReadAcPower();

        if (o.Publishable)
        {
            // Mirrors what the site will not publish (xod-site scripts/stress-summary.mjs, provenanceReasons),
            // which stays the judge of the file; this only refuses before an hour is spent on such a run.
            string why = commit == null ? "no commit could be read"
                : commit.EndsWith("-dirty", StringComparison.Ordinal) ? $"the tree has changes that are not committed ({commit})"
                : onAcPower == false ? "the machine is on battery power"
                : null;
            if (why != null)
            {
                Console.Error.WriteLine($"[stress] not publishable: {why}");
                return 2;
            }
        }

        if (o.DryRun)
        {
            Console.WriteLine($"[stress] profile    {o.Profile}");
            Console.WriteLine($"[stress] commit     {commit ?? "(none)"}, AC power {onAcPower?.ToString() ?? "unknown"}");
            Console.WriteLine($"[stress] workload   {JsonSerializer.Serialize(workload)}");
            Console.WriteLine($"[stress] free disk  {freeGb:N1} GB (minimum {o.MinFreeGb:N1})");
            Console.WriteLine($"[stress] work dir   {o.Dir}");
            Console.WriteLine($"[stress] output     {o.Out}");
            return 0;
        }

        if (freeGb < o.MinFreeGb)
        {
            Console.Error.WriteLine($"[stress] abort: {freeGb:N1} GB free, need {o.MinFreeGb:N1}");
            return 2;
        }

        Directory.CreateDirectory(o.Dir);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out))!);

        var (affinity, affinityCpus) = o.PinPerformanceCores ? Machine.PinToPerformanceCores() : ("all", Machine.LogicalCpus);

        using var writer = new StreamWriter(o.Out, append: false);
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            type = "run",
            profile = o.Profile,
            startedAt = DateTime.UtcNow.ToString("O"),
            commit,
            build = Machine.BuildId(typeof(StressRunner), typeof(XodContext)),
            // Mains power decides whether the run counts; the rest of the machine is private (Machine.RecordPath).
            onAcPower,
            workload,
            criteria = o.Profile == "soak" ? SoakCriteria : null,
        }));
        writer.Flush();
        Machine.WriteRecord(o.Out, Machine.Describe(affinity, affinityCpus, freeGb, onAcPower));

        SleepGuard.Prevent();
        int code = 0;
        Dictionary<string, object> end;
        try
        {
            end = o.Profile switch
            {
                "smoke" or "capacity" => Capacity(o, writer, stop),
                "soak" => Soak(o, writer, stop),
                _ => null,
            };
            if (end == null)
            {
                code = -1;
                end = new() { ["stoppedBy"] = "error", ["error"] = $"unknown profile '{o.Profile}'" };
            }
        }
        catch (Exception ex)
        {
            code = 1;
            end = new() { ["stoppedBy"] = "error", ["error"] = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally
        {
            SleepGuard.Restore();
        }

        // A run whose own check failed must not look like a clean exit to a script that runs it.
        if (code == 0 && end.TryGetValue("verified", out var verified) && verified is false) code = 3;

        var row = new Dictionary<string, object> { ["type"] = "end", ["code"] = code, ["at"] = Now() };
        foreach (var kv in end) row[kv.Key] = kv.Value;
        writer.WriteLine(JsonSerializer.Serialize(row));
        writer.Flush();

        if (ownsDir)
        {
            try
            {
                Directory.Delete(o.Dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The result is already written; a folder left behind is reported, not fatal to the run.
                Console.Error.WriteLine($"[stress] could not remove the work folder {o.Dir}: {ex.Message}");
            }
        }

        return code;
    }

    private static object Workload(Options o) => o.Profile == "soak"
        ? new
        {
            seedRows = o.SeedRows,
            hotRows = o.EffectiveHotRows,
            readers = o.Readers,
            writers = o.Writers,
            flushMs = o.FlushMs,
            reads = "Find by primary key, uniform over the hot rows",
            writes = "Find, then Update of Price, uniform over the hot rows; one Flush (commit) every flushMs",
            durability = nameof(DurabilityMode.Buffered),
            storage = nameof(StorageFormat.Xml),
            indexing = !o.NoIndex,
            payloadChars = o.PayloadChars + 11, // "{id:D10}:" before the payload
            seconds = o.DurationSeconds,
            sampleSeconds = o.SoakSampleSeconds,
        }
        : new
        {
            targetRows = o.TargetRows,
            batch = Batch,
            sampleRows = o.CapacitySampleRows,
            probes = o.ProbeCount,
            checkpoints = o.Checkpoints.Where(c => c <= o.TargetRows).OrderBy(c => c).ToArray(),
            durability = nameof(DurabilityMode.Buffered),
            storage = nameof(StorageFormat.Xml),
            indexing = !o.NoIndex,
            payloadChars = o.PayloadChars,
        };

    private static DatabaseOptions DbOptions(Options o) => new()
    {
        InitialCreate = true,
        EnableIndexing = !o.NoIndex,
        StorageFormat = StorageFormat.Xml,
        Durability = DurabilityMode.Buffered,
    };

    private static Dictionary<string, object> Capacity(Options o, StreamWriter writer, CancellationToken stop)
    {
        string path = Path.Combine(o.Dir, "capacity.xod");
        var dbOptions = DbOptions(o);
        long targetRows = o.TargetRows;
        long sampleEvery = o.CapacitySampleRows;
        string payload = new string('x', o.PayloadChars);
        long rows = 0, sampleRows = 0;
        var rng = new Random(5);
        var checkpoints = new Queue<long>(o.Checkpoints.Where(c => c <= targetRows).OrderBy(c => c));
        var meter = new Meter();
        var wall = Stopwatch.StartNew();
        var inserting = Stopwatch.StartNew(); // insert time only: checkpoints and probes are kept out
        var sample = Stopwatch.StartNew();
        string stoppedBy = "planned";
        long probeMisses = 0;

        XodContext db = new(path, options: dbOptions);
        double finalCloseMs;
        try
        {
            while (rows < targetRows)
            {
                if (stop.IsCancellationRequested) { stoppedBy = "interrupted"; break; }
                if (FreeGb(o.Dir) < o.MinFreeGb) { stoppedBy = "disk-floor"; break; }

                var chunk = new List<BenchRow>(Batch);
                for (int i = 0; i < Batch && rows < targetRows; i++, rows++)
                {
                    long id = rows + 1;
                    chunk.Add(new BenchRow
                    {
                        Id = (int)id,
                        Sku = MakeSku(id),
                        CategoryId = (int)(id % 200) + 1,
                        Price = (id % 500) * 1.25,
                        Payload = payload,
                    });
                }

                db.InsertRange(chunk);

                bool checkpointDue = checkpoints.Count > 0 && checkpoints.Peek() <= rows;
                if (rows > sampleRows && (rows - sampleRows >= sampleEvery || rows == targetRows || checkpointDue))
                {
                    inserting.Stop();
                    double seconds = sample.Elapsed.TotalSeconds;
                    var (probes, misses) = Probe(db, rng, rows, o.ProbeCount);
                    probeMisses += misses;
                    var line = new Dictionary<string, object>
                    {
                        ["type"] = "sample",
                        ["rows"] = rows,
                        ["rowsPerSec"] = Math.Round((rows - sampleRows) / seconds, 1),
                        ["insertSeconds"] = Math.Round(inserting.Elapsed.TotalSeconds, 2),
                        ["p50us"] = Pct(probes, 50),
                        ["p99us"] = Pct(probes, 99),
                        ["probeMisses"] = misses,
                        ["dbMb"] = Math.Round(DirBytes(o.Dir) / 1048576.0, 1),
                    };
                    meter.AddTo(line);
                    line["at"] = Now();
                    Write(writer, line);

                    sampleRows = rows;
                    sample.Restart();
                    inserting.Start();
                }

                while (checkpoints.Count > 0 && checkpoints.Peek() <= rows)
                {
                    checkpoints.Dequeue();
                    inserting.Stop();

                    var close = Stopwatch.StartNew();
                    db.Flush();
                    db.Dispose();
                    close.Stop();

                    // Collect the closed context before reopening, so the memory measured below belongs
                    // to the reopened database alone and not also to the one just closed.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    var reopen = Stopwatch.StartNew();
                    db = new XodContext(path, options: dbOptions);
                    reopen.Stop();

                    // The index loads on the first query, so "open" alone is not the cost of a cold start.
                    int firstId = rng.Next(1, (int)Math.Min(rows, int.MaxValue) + 1);
                    var first = Stopwatch.StartNew();
                    var answer = db.Find<BenchRow>(r => r.Id == firstId);
                    first.Stop();

                    var (probes, misses) = Probe(db, rng, rows, o.ProbeCount);
                    misses += answer == null ? 1 : 0;
                    probeMisses += misses;
                    var line = new Dictionary<string, object>
                    {
                        ["type"] = "checkpoint",
                        ["rows"] = rows,
                        ["closeMs"] = Ms(close),
                        ["reopenMs"] = Ms(reopen),
                        ["firstAnswerMs"] = Math.Round(reopen.Elapsed.TotalMilliseconds + first.Elapsed.TotalMilliseconds, 1),
                        ["probes"] = probes.Count,
                        ["p50us"] = Pct(probes, 50),
                        ["p99us"] = Pct(probes, 99),
                        ["probeMisses"] = misses,
                        ["dbMb"] = Math.Round(DirBytes(o.Dir) / 1048576.0, 1),
                    };
                    meter.AddTo(line);
                    line["at"] = Now();
                    Write(writer, line);

                    sample.Restart();
                    inserting.Start();
                }
            }

            inserting.Stop();
        }
        finally
        {
            var close = Stopwatch.StartNew();
            db.Flush();
            db.Dispose();
            finalCloseMs = Ms(close);
        }

        Console.WriteLine($"[stress] capacity {stoppedBy}: {rows:N0} rows in {wall.Elapsed.TotalSeconds:N1} s");
        return new Dictionary<string, object>
        {
            ["stoppedBy"] = stoppedBy,
            ["rows"] = rows,
            ["seconds"] = Math.Round(wall.Elapsed.TotalSeconds, 2),
            ["insertSeconds"] = Math.Round(inserting.Elapsed.TotalSeconds, 2),
            ["finalCloseMs"] = finalCloseMs,
            ["dbMb"] = Math.Round(DirBytes(o.Dir) / 1048576.0, 1),
            ["probeMisses"] = probeMisses,
            ["verified"] = probeMisses == 0,
        };
    }

    private static Dictionary<string, object> Soak(Options o, StreamWriter writer, CancellationToken stop)
    {
        string path = Path.Combine(o.Dir, "soak.xod");
        var dbOptions = DbOptions(o);
        int hot = (int)o.EffectiveHotRows;

        Console.WriteLine($"[stress] seeding {o.SeedRows:N0} rows");
        var seeding = Stopwatch.StartNew();
        long seeded = 0;
        using (var seed = new XodContext(path, options: dbOptions))
        {
            string payload = new string('x', o.PayloadChars);
            for (long start = 0; start < o.SeedRows && !stop.IsCancellationRequested; start += Batch)
            {
                var chunk = new List<BenchRow>(Batch);
                for (int i = 0; i < Batch && start + i < o.SeedRows; i++)
                {
                    long id = start + i + 1;
                    chunk.Add(new BenchRow
                    {
                        Id = (int)id,
                        Sku = $"SKU-{id:D10}",
                        CategoryId = (int)(id % 200) + 1,
                        Price = (id % 500) * 1.25,
                        Payload = $"{id:D10}:{payload}",
                    });
                }

                seed.InsertRange(chunk);
                seeded += chunk.Count;
            }

            seed.Flush();
        }

        seeding.Stop();
        hot = (int)Math.Min(hot, seeded);

        // Every price is a multiple of 0.25 far below 2^51, so these sums are exact in any order. The
        // id-weighted sum also catches an update that landed on the wrong row.
        double seedHotSum = 0, seedHotWeighted = 0;
        for (long id = 1; id <= hot; id++)
        {
            seedHotSum += (id % 500) * 1.25;
            seedHotWeighted += id * ((id % 500) * 1.25);
        }

        var readers = Enumerable.Range(0, o.Readers).Select(_ => new LatencyRecorder()).ToArray();
        var readMisses = new long[o.Readers];
        var updates = new LatencyRecorder();
        var flushes = new LatencyRecorder();
        long writerMisses = 0, updatedIdSum = 0, updateFailures = 0;
        string failure = null;

        using var halt = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var db = new XodContext(path, options: dbOptions);

        void Guarded(Action body)
        {
            try { body(); }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, $"{Thread.CurrentThread.Name}: {ex.GetType().Name}: {ex.Message}", null);
                halt.Cancel();
            }
        }

        var threads = new List<Thread>();
        for (int r = 0; r < o.Readers; r++)
        {
            var recorder = readers[r];
            int slot = r;
            threads.Add(new Thread(() => Guarded(() =>
            {
                var rng = new Random(slot + 11);
                while (!halt.IsCancellationRequested)
                {
                    int id = rng.Next(1, hot + 1);
                    long t0 = Stopwatch.GetTimestamp();
                    var found = db.Find<BenchRow>(x => x.Id == id);
                    recorder.Record((long)((Stopwatch.GetTimestamp() - t0) * NsPerTick));
                    if (found == null) readMisses[slot]++; // every hot id exists: a miss is a wrong answer
                }
            })) { IsBackground = true, Name = $"reader-{r}" });
        }

        if (o.Writers == 1)
        {
            threads.Add(new Thread(() => Guarded(() =>
            {
                var rng = new Random(7);
                long lastCommit = Stopwatch.GetTimestamp();
                long commitEvery = o.FlushMs * Stopwatch.Frequency / 1000;
                long n = 0;
                while (!halt.IsCancellationRequested)
                {
                    int id = rng.Next(1, hot + 1);
                    var row = db.Find<BenchRow>(x => x.Id == id);
                    if (row == null) { writerMisses++; continue; }

                    row.Price += 0.5;
                    long t0 = Stopwatch.GetTimestamp();
                    bool written;
                    if (o.TestDropEveryNthUpdate > 0 && ++n % o.TestDropEveryNthUpdate == 0)
                    {
                        row.Price -= 0.5; // a lost update: counted below as written, never written
                        written = true;
                    }
                    else
                    {
                        written = db.Update(row);
                    }

                    updates.Record((long)((Stopwatch.GetTimestamp() - t0) * NsPerTick));
                    if (written) updatedIdSum += id;
                    else updateFailures++;

                    if (Stopwatch.GetTimestamp() - lastCommit >= commitEvery)
                    {
                        long c0 = Stopwatch.GetTimestamp();
                        db.Flush();
                        lastCommit = Stopwatch.GetTimestamp();
                        flushes.Record((long)((lastCommit - c0) * NsPerTick));
                    }
                }
            })) { IsBackground = true, Name = "writer" });
        }

        Console.WriteLine($"[stress] soak: {o.Readers} readers, {o.Writers} writer, {hot:N0} hot rows of {seeded:N0}, {o.DurationSeconds} s");
        var meter = new Meter();
        var run = Stopwatch.StartNew();
        var interval = Stopwatch.StartNew();
        foreach (var t in threads) t.Start();

        long[] lastReads = new long[LatencyRecorder.BucketCount];
        long[] lastUpdates = new long[LatencyRecorder.BucketCount];
        long[] lastFlushes = new long[LatencyRecorder.BucketCount];
        string stoppedBy = stop.IsCancellationRequested ? "interrupted" : "planned";
        var duration = TimeSpan.FromSeconds(o.DurationSeconds);

        while (stoppedBy == "planned")
        {
            var remaining = duration - run.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var wait = TimeSpan.FromSeconds(Math.Min(o.SoakSampleSeconds, remaining.TotalSeconds));
            if (halt.Token.WaitHandle.WaitOne(wait))
            {
                stoppedBy = stop.IsCancellationRequested ? "interrupted" : "error";
                break;
            }

            double seconds = interval.Elapsed.TotalSeconds;
            interval.Restart();
            long[] readsNow = LatencyRecorder.Merge(readers.Select(r => r.Snapshot()));
            long[] updatesNow = updates.Snapshot();
            long[] flushesNow = flushes.Snapshot();
            var readInterval = LatencyRecorder.Summarize(LatencyRecorder.Subtract(readsNow, lastReads));
            var updateInterval = LatencyRecorder.Summarize(LatencyRecorder.Subtract(updatesNow, lastUpdates));
            var flushInterval = LatencyRecorder.Summarize(LatencyRecorder.Subtract(flushesNow, lastFlushes));
            lastReads = readsNow;
            lastUpdates = updatesNow;
            lastFlushes = flushesNow;

            var line = new Dictionary<string, object>
            {
                ["type"] = "sample",
                ["elapsedSec"] = Math.Round(run.Elapsed.TotalSeconds, 1),
                ["reads"] = LatencyRecorder.Total(readsNow),
                ["readsPerSec"] = Math.Round(readInterval.Count / seconds, 1),
                ["readLatency"] = Latency(readInterval),
                ["writes"] = LatencyRecorder.Total(updatesNow),
                ["writesPerSec"] = Math.Round(updateInterval.Count / seconds, 1),
                ["writeLatency"] = Latency(updateInterval),
                ["commits"] = LatencyRecorder.Total(flushesNow),
                ["commitLatency"] = Latency(flushInterval),
                ["readMisses"] = Enumerable.Range(0, readMisses.Length).Sum(i => Volatile.Read(ref readMisses[i])),
                ["dbMb"] = Math.Round(DirBytes(o.Dir) / 1048576.0, 1),
            };
            meter.AddTo(line);
            line["at"] = Now();
            Write(writer, line);
        }

        halt.Cancel();
        bool joined = threads.All(t => t.Join(TimeSpan.FromSeconds(60)));
        double measured = run.Elapsed.TotalSeconds;
        if (failure != null) stoppedBy = "error";

        // After the join every recorder is final: these are the counts the check below adds up.
        long writes = LatencyRecorder.Total(updates.Snapshot()) - updateFailures;
        long commits = LatencyRecorder.Total(flushes.Snapshot());
        long misses = readMisses.Sum() + writerMisses;

        var flush = Stopwatch.StartNew();
        db.Flush();
        flush.Stop();
        var close = Stopwatch.StartNew();
        db.Dispose();
        close.Stop();

        // Reopen and check that every committed update is on disk: the hot rows' prices must add up to the
        // seed values plus half a unit per update, every row must still be there, and the unique key must hold.
        var reopen = Stopwatch.StartNew();
        using var check = new XodContext(path, options: dbOptions);
        reopen.Stop();
        var first = Stopwatch.StartNew();
        check.Find<BenchRow>(x => x.Id == 1);
        first.Stop();

        long count = check.Count<BenchRow>();
        var hotRows = check.Query<BenchRow>(x => x.Id <= hot).ToList();
        double hotSum = hotRows.Sum(x => x.Price);
        double hotWeighted = hotRows.Sum(x => x.Id * x.Price);
        double expectedHotSum = seedHotSum + 0.5 * writes;
        double expectedHotWeighted = seedHotWeighted + 0.5 * updatedIdSum;
        bool unique;
        try
        {
            check.Insert(new BenchRow { Sku = $"SKU-{1:D10}", CategoryId = 1, Price = 0, Payload = "duplicate" });
            unique = false;
        }
        catch (ReservedUniqueKeyException)
        {
            unique = true;
        }

        bool ok = joined && failure == null && misses == 0 && updateFailures == 0 && count == seeded
            && hotRows.Count == hot && hotSum == expectedHotSum && hotWeighted == expectedHotWeighted && unique;
        Console.WriteLine($"[stress] soak {stoppedBy}: {measured:N0} s, {writes:N0} writes in {commits:N0} commits, verified {ok}");

        return new Dictionary<string, object>
        {
            ["stoppedBy"] = stoppedBy,
            ["seconds"] = Math.Round(measured, 1),
            ["seedSeconds"] = Math.Round(seeding.Elapsed.TotalSeconds, 1),
            ["reads"] = LatencyRecorder.Total(LatencyRecorder.Merge(readers.Select(r => r.Snapshot()))),
            ["writes"] = writes,
            ["commits"] = commits,
            ["misses"] = misses,
            ["readMisses"] = readMisses.Sum(),
            ["writerMisses"] = writerMisses,
            ["updateFailures"] = updateFailures,
            ["finalFlushMs"] = Ms(flush),
            ["closeMs"] = Ms(close),
            ["reopenMs"] = Ms(reopen),
            ["firstAnswerMs"] = Math.Round(reopen.Elapsed.TotalMilliseconds + first.Elapsed.TotalMilliseconds, 1),
            ["verify"] = new
            {
                rows = count,
                expectedRows = seeded,
                hotPriceSum = hotSum,
                expectedHotPriceSum = expectedHotSum,
                hotIdWeightedSum = hotWeighted,
                expectedHotIdWeightedSum = expectedHotWeighted,
                uniqueKeyEnforced = unique,
                workersStopped = joined,
                ok,
            },
            ["verified"] = ok,
            ["error"] = failure,
        };
    }

    private static object Latency(LatencySummary s) => new
    {
        count = s.Count,
        p50Us = Us(s.P50),
        p99Us = Us(s.P99),
        p999Us = Us(s.P999),
        maxUs = Us(s.Max),
    };

    private static double Us(long ns) => Math.Round(ns / 1000.0, 1);

    private static double Ms(Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 1);

    private static string Now() => DateTime.UtcNow.ToString("O");

    private static void Write(StreamWriter writer, Dictionary<string, object> line)
    {
        writer.WriteLine(JsonSerializer.Serialize(line));
        writer.Flush();
    }

    /// <summary>Asks Windows not to sleep while a run is in progress; a sleeping host punches a hole in
    /// the timeline (stress-soak-20261106_043811 has a 185-minute gap). It cannot stop a user from putting
    /// the machine to sleep (SetThreadExecutionState docs), so the site also voids a run with a gap.</summary>
    private static class SleepGuard
    {
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint flags);

        public static void Prevent()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
            }
            catch { }
        }

        public static void Restore()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    SetThreadExecutionState(ES_CONTINUOUS);
            }
            catch { }
        }
    }

    /// <summary>SKU in exactly one allocation, so the measuring client does not drown the engine in
    /// garbage: interpolated strings per row were a large share of the "insert slowdown".</summary>
    private static string MakeSku(long id) => string.Create(12, id, static (span, value) =>
    {
        span[0] = 'S';
        span[1] = 'K';
        span[2] = 'U';
        span[3] = '-';
        for (int i = 11; i >= 4; i--)
        {
            span[i] = (char)('0' + (int)(value % 10));
            value /= 10;
        }
    });

    /// <summary>Random primary-key lookups. Every id up to <paramref name="rows"/> exists, so a null is a
    /// wrong answer: it is counted, not passed off as a fast read.</summary>
    private static (List<long> Latencies, int Misses) Probe(XodContext db, Random rng, long rows, int count)
    {
        var latencies = new List<long>(count);
        int misses = 0;
        int max = (int)Math.Min(rows, int.MaxValue);
        for (int i = 0; i < count; i++)
        {
            int id = rng.Next(1, max + 1);
            var sw = Stopwatch.StartNew();
            var found = db.Find<BenchRow>(r => r.Id == id);
            sw.Stop();
            latencies.Add(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
            if (found == null) misses++;
        }

        return (latencies, misses);
    }

    private static long Pct(IEnumerable<long> values, int percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static double DirBytes(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    private static double FreeGb(string dir)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(dir))!;
            return new DriveInfo(root).AvailableFreeSpace / 1073741824.0;
        }
        catch { return double.MaxValue; }
    }
}
