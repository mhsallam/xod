using System;
using System.IO;
using System.Threading;

namespace Xod.Services
{
    /// <summary>Fault injection for the crash-safety tests (gate layer 4), inert unless one of these is set:
    /// <list type="bullet">
    /// <item>XOD_CRASH_AFTER_ROWS: the process kills itself after that many rows have been written;</item>
    /// <item>XOD_CRASH_AT_STEP: it kills itself at that step of a password change;</item>
    /// <item>XOD_FAIL_AT_STEP: that step throws an IOException, once per process (a file briefly in use);</item>
    /// <item>XOD_PAUSE_AT_STEP: it prints "PAUSED &lt;step&gt;" and waits for a line on standard input.</item>
    /// </list>
    /// A step is <c>name</c>, <c>name:count</c> (after that many files) or <c>name:file</c> (<c>staged:3</c>,
    /// <c>committed</c>, <c>switching:pw.xod</c>, <c>switched:1</c>), so a fault lands at an exact, repeatable
    /// point instead of a random one.</summary>
    internal static class FaultInjection
    {
        private static readonly int CrashAfterRows = Parse();
        private static readonly string CrashAtStep = Environment.GetEnvironmentVariable("XOD_CRASH_AT_STEP");
        private static readonly string FailAtStep = Environment.GetEnvironmentVariable("XOD_FAIL_AT_STEP");
        private static readonly string PauseAtStep = Environment.GetEnvironmentVariable("XOD_PAUSE_AT_STEP");
        private static int rowsWritten;
        private static int failed;

        /// <summary>A step of a password change: <paramref name="step"/> after <paramref name="count"/> files
        /// (or the step itself when the count is 0), at <paramref name="file"/> when there is one.</summary>
        internal static void PasswordStep(string step, int count = 0, string file = null)
        {
            if (CrashAtStep == null && FailAtStep == null && PauseAtStep == null)
                return;

            string at = count == 0 ? step : $"{step}:{count}";
            string atFile = file == null ? null : $"{step}:{file}";
            bool Is(string wanted) => wanted != null && (wanted == at || wanted == atFile);

            if (Is(CrashAtStep))
                Environment.FailFast($"XOD_CRASH_AT_STEP={CrashAtStep} reached");
            if (Is(PauseAtStep))
            {
                Console.Out.WriteLine("PAUSED " + PauseAtStep);
                Console.Out.Flush();
                Console.In.ReadLine();
            }
            if (Is(FailAtStep) && Interlocked.Exchange(ref failed, 1) == 0)
                throw new IOException($"XOD_FAIL_AT_STEP={FailAtStep}: injected failure");
        }

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
