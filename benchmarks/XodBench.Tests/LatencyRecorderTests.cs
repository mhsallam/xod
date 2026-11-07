using XodBench;
using Xunit;

namespace XodBench.Tests;

/// <summary>
/// The soak reported each minute's p99 from the last 20,000 reads in a shared queue: at 200k reads a
/// second, the last tenth of a second. These pin a recorder that keeps every value in fixed buckets, so a
/// percentile covers the whole interval.
/// </summary>
public class LatencyRecorderTests
{
    // 32 sub-buckets per power of two: a reported value is at most 1/32 above the true one.
    private static long Ceiling(long v) => v * 33 / 32;

    [Fact]
    public void Percentiles_are_within_one_bucket_of_the_true_value()
    {
        var recorder = new LatencyRecorder();
        for (long v = 1; v <= 10_000; v++) recorder.Record(v);

        var s = LatencyRecorder.Summarize(recorder.Snapshot());

        Assert.Equal(10_000, s.Count);
        Assert.InRange(s.P50, 5_000, Ceiling(5_000));
        Assert.InRange(s.P99, 9_900, Ceiling(9_900));
        Assert.InRange(s.P999, 9_990, Ceiling(9_990));
        Assert.InRange(s.Max, 10_000, Ceiling(10_000));
    }

    [Fact]
    public void Small_values_are_exact()
    {
        var recorder = new LatencyRecorder();
        recorder.Record(7);
        recorder.Record(7);
        recorder.Record(0);

        var s = LatencyRecorder.Summarize(recorder.Snapshot());

        Assert.Equal(3, s.Count);
        Assert.Equal(7, s.P50);
        Assert.Equal(7, s.Max);
    }

    [Fact]
    public void An_interval_holds_only_what_was_recorded_after_the_previous_snapshot()
    {
        var recorder = new LatencyRecorder();
        for (int i = 0; i < 1_000; i++) recorder.Record(100);
        long[] before = recorder.Snapshot();
        for (int i = 0; i < 10; i++) recorder.Record(1_000_000);

        var s = LatencyRecorder.Summarize(LatencyRecorder.Subtract(recorder.Snapshot(), before));

        Assert.Equal(10, s.Count);
        Assert.InRange(s.P50, 1_000_000, Ceiling(1_000_000));
    }

    [Fact]
    public void Recorders_from_several_threads_merge_into_one_interval()
    {
        var a = new LatencyRecorder();
        var b = new LatencyRecorder();
        for (int i = 0; i < 99; i++) a.Record(10);
        b.Record(50_000);

        var s = LatencyRecorder.Summarize(LatencyRecorder.Merge(new[] { a.Snapshot(), b.Snapshot() }));

        Assert.Equal(100, s.Count);
        Assert.Equal(10, s.P50);
        Assert.InRange(s.Max, 50_000, Ceiling(50_000));
    }
}
