using System;
using System.IO;
using System.Threading;

namespace Xod.Services
{
    /// <summary>Fault injection for the crash-safety tests (gate layer 4), inert unless one of these is set:
    /// <list type="bullet">
    /// <item>XOD_CRASH_AFTER_ROWS: the process kills itself after that many rows have been written;</item>
    /// <item>XOD_CRASH_AT_STEP: it kills itself at that step;</item>
    /// <item>XOD_FAIL_AT_STEP: those steps (comma-separated) each throw an IOException once per process, as
    /// from a file briefly in use;</item>
    /// <item>XOD_PAUSE_AT_STEP: it prints "PAUSED &lt;step&gt;" and waits for a line on standard input.</item>
    /// </list>
    /// A step is <c>name</c>, <c>name:count</c> (after that many files) or <c>name:file</c>. Password changes:
    /// <c>staged</c>, <c>committed</c>, <c>switching</c>, <c>switched</c>. Commits: <c>commit-written</c>
    /// (the batch's files written, not the indexes), <c>undo</c>. A failed statement's undo: <c>undo-file:n</c> (before
    /// it puts back the nth file it changed, outside a transaction), <c>undo-statement:n</c> (once it has taken back n of
    /// the rows it created, in a transaction or buffered mode). Opening: <c>open-checked</c> (the folder checked
    /// to hold one database, before the hold; a conversion's too), <c>creating</c> (a new database's file about
    /// to be written, the database held alone), <c>recover-restoring</c> (before a transaction journal's snapshot
    /// is written back). So a fault lands at an exact, repeatable point instead of a random one.</summary>
    internal static class FaultInjection
    {
        private static readonly int CrashAfterRows = Parse();
        private static readonly string CrashAtStep = Environment.GetEnvironmentVariable("XOD_CRASH_AT_STEP");
        private static readonly string[] FailAtSteps = (Environment.GetEnvironmentVariable("XOD_FAIL_AT_STEP") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        private static readonly int[] Failed = new int[FailAtSteps.Length];
        private static readonly string PauseAtStep = Environment.GetEnvironmentVariable("XOD_PAUSE_AT_STEP");
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

        /// <summary>
        /// In process, for the tests (null otherwise): called with each step's name (and <c>:file</c> when it names one),
        /// on the thread at the step. It may throw, as a failed write would (<c>file-write</c>). For an
        /// interleaving no public call can hold, such as a write between its turn and its locks (<c>write-turn</c>,
        /// ISS-132). A test that sets it acts only for the flow it marks: tests of other contexts run beside it.
        /// </summary>
        internal static volatile Action<string> InProcess;

        /// <summary>A step: <paramref name="step"/> after <paramref name="count"/> files (or the step itself
        /// when the count is 0), at <paramref name="file"/> when there is one.</summary>
        internal static void Step(string step, int count = 0, string file = null)
        {
            InProcess?.Invoke(file == null ? step : step + ":" + file);
            if (CrashAtStep == null && FailAtSteps.Length == 0 && PauseAtStep == null)
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
            for (int i = 0; i < FailAtSteps.Length; i++)
            {
                if (Is(FailAtSteps[i]) && Interlocked.Exchange(ref Failed[i], 1) == 0)
                    throw new IOException($"XOD_FAIL_AT_STEP={FailAtSteps[i]}: injected failure");
            }
        }
    }
}
