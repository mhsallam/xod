using System;
using System.IO;
using System.Threading;
using Xod;
using Xod.Services;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-198: a full index snapshot serializes its indexed properties' blocks side by side (two or more), and a
    /// failure there came out of <c>Parallel.For</c> as an <c>AggregateException</c> around it, where one property's
    /// came out as itself: the caller's handling depended on how many properties a type indexes (as ISS-137 found for
    /// writes, and ISS-144 for reads). It is thrown as itself, the lowest property's first, as a loop meets it.
    /// </summary>
    [Collection(InProcessHookCollection.Name)]
    public class XodIndexSnapshotFailureTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-idxsnap-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class IsRow
        {
            public int Id { get; set; }
            [Property(Indexed = true)] public string City { get; set; }
            [Property(Indexed = true)] public string Code { get; set; }
        }

        private static readonly AsyncLocal<bool> Armed = new AsyncLocal<bool>();

        public sealed class BlockFailure : Exception
        {
            public BlockFailure(string message) : base(message) { }
        }

        [Fact]
        public void A_failure_serializing_one_of_several_index_blocks_is_thrown_as_itself()
        {
            Directory.CreateDirectory(tempRoot);
            string path = Path.Combine(tempRoot, "is.xod");
            Action<string> hook = step =>
            {
                if (Armed.Value && step == IndexService.IndexBlockStep + ":City")
                    throw new BlockFailure("City's block");
            };
            FaultInjection.InProcess += hook;
            try
            {
                using var db = new XodContext(path);
                Armed.Value = true;
                // The first row of a table writes its index file whole (a snapshot).
                Exception thrown = Record.Exception(() => db.Insert(new IsRow { City = "Cairo", Code = "C1" }));
                Armed.Value = false;

                Assert.NotNull(thrown);
                for (var e = thrown; e != null; e = e.InnerException)
                    Assert.IsNotType<AggregateException>(e);
                Assert.Contains(Chain(thrown), e => e is BlockFailure);
            }
            finally
            {
                FaultInjection.InProcess -= hook;
            }
        }

        private static System.Collections.Generic.IEnumerable<Exception> Chain(Exception e)
        {
            for (; e != null; e = e.InnerException)
                yield return e;
        }
    }
}
