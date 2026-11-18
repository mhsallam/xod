using System;
using System.Runtime.ExceptionServices;

namespace Xod.Services
{
    /// <summary>
    /// Reads side by side (Parallel.For) that fail as the same reads one after another do: every read runs, and the
    /// failure of the lowest index, the one a loop meets first, is thrown as itself. Parallel.For threw an
    /// AggregateException, so a damaged page read with three others came out as a DatabaseFileException, and read with
    /// four or more as an AggregateException around it (ISS-144). <see cref="IOService"/>'s ParallelWrites holds the
    /// rule for writes.
    /// </summary>
    internal static class ParallelReads
    {
        internal static void For(int count, Action<int> read)
        {
            if (count <= 0)
                return;
            var failures = new Exception[count];
            bool failed = false;
            System.Threading.Tasks.Parallel.For(0, count, i =>
            {
                try
                {
                    read(i);
                }
                catch (Exception e)
                {
                    failures[i] = e;
                    System.Threading.Volatile.Write(ref failed, true);
                }
            });
            if (!failed)
                return;
            foreach (var failure in failures)
                if (failure != null)
                    ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
