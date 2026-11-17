using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using Xunit;

namespace Xod.Tests
{
    // What the tests that drive reads and writes step by step see of the engine's locks.
    internal static class EngineLocks
    {
        // The bound that turns a deadlock into a failure; no step waits on a clock otherwise.
        internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

        // The writers waiting on the engine's locks, whichever they are (the engine-wide lock, or a table's, before
        // ISS-125 removed those).
        internal static int WaitingWriters(XodContext db)
        {
            object engine = db.Engine;
            int waiting = 0;
            foreach (var field in engine.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                object value = field.GetValue(engine);
                if (value is ReaderWriterLockSlim one)
                    waiting += one.WaitingWriteCount;
                else if (value is ConcurrentDictionary<string, ReaderWriterLockSlim> many)
                    waiting += many.Values.Sum(l => l.WaitingWriteCount);
            }
            return waiting;
        }

        internal static void Until(Func<bool> condition, string what) =>
            Assert.True(SpinWait.SpinUntil(condition, Bound), "never reached: " + what);
    }
}
