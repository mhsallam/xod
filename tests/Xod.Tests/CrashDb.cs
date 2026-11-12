using System;
using System.IO;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>The crash writer's password database: 25 customers with 3 orders each per batch, under the
    /// password "first" (tests/Xod.CrashWriter, mode "pwsetup").</summary>
    internal static class CrashDb
    {
        internal static string New(string root, string name)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            var setup = XodCrashSafetyTests.RunWriterProcess("pwsetup", dir);
            Assert.True(setup.ExitCode == 0, setup.Output);
            return dir;
        }

        // Every customer and order is there and found by its key, through the index.
        internal static void HoldsBatches(string dir, string password, int batches)
        {
            using var db = new XodContext(Path.Combine(dir, "pw.xod"), password);
            Assert.Equal(25 * batches, db.Count<CrashCustomer>());
            Assert.Equal(75 * batches, db.Count<CrashOrder>());
            for (int id = 1; id <= 25 * batches; id++)
                Assert.True(db.Find<CrashCustomer>(c => c.Id == id) != null, "customer " + id + " not found");
            for (int id = 1; id <= 75 * batches; id++)
                Assert.True(db.Find<CrashOrder>(o => o.Id == id) != null, "order " + id + " not found");
        }

        internal static void DoesNotOpen(string dir, string password)
        {
            Assert.ThrowsAny<Exception>(() =>
            {
                using var db = new XodContext(Path.Combine(dir, "pw.xod"), password);
                db.Count<CrashCustomer>();
                db.Count<CrashOrder>();
            });
        }
    }
}
