using System;
using System.Diagnostics;
using System.Threading;

namespace Xod.Services
{
    /// <summary>Opt-in phase timers for the relational insert path (PLAN-038). XOD_DIAG=1 only.</summary>
    internal static class WriteDiagnostics
    {
        internal static readonly bool Enabled =
            string.Equals(Environment.GetEnvironmentVariable("XOD_DIAG"), "1", StringComparison.Ordinal);

        internal static long WriteTicks;
        internal static long PageSaveTicks;
        internal static long TableSaveTicks;
        internal static long IndexTicks;
        internal static long AddRowTicks;
        internal static long RawTicks;
        internal static long UniqueTicks;
        internal static long SaveTicks;
        internal static long PageLoadTicks;
        internal static long AppendTicks;
        internal static long FullPages;
        internal static long OpenTableTicks;
        internal static long OpenPageTicks;
        internal static long PickCodeTicks;
        internal static long Entities;
        internal static long PageSaves;
        internal static long TableSaves;

        static WriteDiagnostics()
        {
            if (!Enabled) return;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Console.Error.WriteLine(
                    $"WRITE_DIAG entities={Entities} " +
                    $"writeMs={Ms(WriteTicks):F0} writes={Writes} " +
                    $"pageSaveMs={Ms(PageSaveTicks):F0} pageSaves={PageSaves} " +
                    $"tableSaveMs={Ms(TableSaveTicks):F0} tableSaves={TableSaves} " +
                    $"indexMs={Ms(IndexTicks):F0} addRowMs={Ms(AddRowTicks):F0} rawMs={Ms(RawTicks):F0} uniqueMs={Ms(UniqueTicks):F0} saveMs={Ms(SaveTicks):F0} loadMs={Ms(PageLoadTicks):F0} appendMs={Ms(AppendTicks):F0} fullPages={FullPages} " +
                    $"openTableMs={Ms(OpenTableTicks):F0} openPageMs={Ms(OpenPageTicks):F0} pickCodeMs={Ms(PickCodeTicks):F0}");
            };
        }

        internal static long Writes;

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        internal static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;
        internal static void End(ref long target, long start) { if (Enabled) Interlocked.Add(ref target, Stopwatch.GetTimestamp() - start); }
        internal static void EndWrite() { if (!Enabled) return; Interlocked.Increment(ref Writes); }
        internal static void Entity() { if (Enabled) Interlocked.Increment(ref Entities); }
        internal static void PageSaved() { if (Enabled) Interlocked.Increment(ref PageSaves); }
        internal static void TableSaved() { if (Enabled) Interlocked.Increment(ref TableSaves); }
    }
}
