using System;
using System.Threading;

namespace Xod.Services
{
    /// <summary>Fault injection for the crash-safety tests (gate layer 4). Inert unless
    /// XOD_CRASH_AFTER_ROWS is set; when it is, the process kills itself after that many rows have been
    /// written, which makes a crash land at an exact, repeatable point instead of a random one.</summary>
    internal static class FaultInjection
    {
        private static readonly int CrashAfterRows = Parse();
        private static int rowsWritten;

        private static int Parse()
        {
            string raw = Environment.GetEnvironmentVariable("XOD_CRASH_AFTER_ROWS");
            return int.TryParse(raw, out int n) && n > 0 ? n : 0;
        }

        internal static void RowWritten()
        {
            if (CrashAfterRows == 0)
                return;

            if (Interlocked.Increment(ref rowsWritten) >= CrashAfterRows)
                Environment.FailFast($"XOD_CRASH_AFTER_ROWS={CrashAfterRows} reached");
        }
    }
}
