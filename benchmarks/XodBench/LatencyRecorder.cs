using System.Numerics;

namespace XodBench;

/// <summary>
/// Counts latencies in fixed log-linear buckets: exact below 64, then 32 buckets per power of two, so a
/// reported value is at most 1/32 above the true one. Every value is kept, so a percentile covers the
/// whole interval rather than whatever fitted in a queue. One recorder per thread and no locks: only the
/// owning thread writes; the sampler copies the counts, and a bucket copied mid-increment is one short
/// until the next sample.
/// </summary>
public sealed class LatencyRecorder
{
    private const int SubBits = 5;
    private const int Sub = 1 << SubBits;

    public const int BucketCount = Sub + (64 - SubBits) * Sub;

    private readonly long[] counts = new long[BucketCount];

    public void Record(long value) => counts[Index(value)]++;

    /// <summary>The counts so far (cumulative); subtract an earlier snapshot to get an interval.</summary>
    public long[] Snapshot()
    {
        var copy = new long[BucketCount];
        for (int i = 0; i < copy.Length; i++) copy[i] = Volatile.Read(ref counts[i]);
        return copy;
    }

    public static long[] Subtract(long[] now, long[] before)
    {
        var result = new long[BucketCount];
        for (int i = 0; i < result.Length; i++) result[i] = Math.Max(0, now[i] - before[i]);
        return result;
    }

    public static long[] Merge(IEnumerable<long[]> parts)
    {
        var result = new long[BucketCount];
        foreach (var part in parts)
            for (int i = 0; i < result.Length; i++) result[i] += part[i];
        return result;
    }

    public static long Total(long[] counts)
    {
        long total = 0;
        foreach (long c in counts) total += c;
        return total;
    }

    /// <summary>Each percentile is the upper bound of the bucket that holds it.</summary>
    public static LatencySummary Summarize(long[] counts)
    {
        long total = Total(counts);
        if (total == 0) return default;

        long max = 0;
        for (int i = counts.Length - 1; i >= 0; i--)
        {
            if (counts[i] > 0) { max = UpperBound(i); break; }
        }

        return new LatencySummary(total, At(counts, total, 0.50), At(counts, total, 0.99), At(counts, total, 0.999), max);
    }

    private static long At(long[] counts, long total, double quantile)
    {
        long rank = Math.Max(1, (long)Math.Ceiling(quantile * total));
        long seen = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            seen += counts[i];
            if (seen >= rank) return UpperBound(i);
        }

        return UpperBound(counts.Length - 1);
    }

    private static int Index(long value)
    {
        if (value < 2 * Sub) return value < 0 ? 0 : (int)value;
        int msb = 63 - BitOperations.LeadingZeroCount((ulong)value);
        int shift = msb - SubBits;
        int top = (int)(value >> shift); // 32..63
        return Sub + shift * Sub + (top - Sub);
    }

    private static long UpperBound(int index)
    {
        if (index < 2 * Sub) return index;
        int k = index - Sub;
        int shift = k / Sub;
        long top = Sub + k % Sub;
        return unchecked(((top + 1) << shift) - 1);
    }
}

public readonly record struct LatencySummary(long Count, long P50, long P99, long P999, long Max);
