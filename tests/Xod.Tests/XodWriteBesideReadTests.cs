// ISS-169 and ISS-108: a write to a standalone table (one with no reference properties of its own) took the engine's
// read lock and its table's write lock, and a read of a type with relations took the engine's read lock only, so the
// write ran in the middle of a read that reached its rows: the read could show rows from before the write and after it
// (ISS-169), and a child's write could move the bytes of a page its parent's read was parsing (ISS-108). Every write now
// takes the engine-wide write lock (ISS-125), so a write on another thread waits for a read under way. The test stops the
// read inside its predicate, starts the write, and waits until the write is either made or waiting for the lock: only
// waiting is right. The only clock is the bound that turns a deadlock into a failure.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Xod.Tests
{
    public class BesideCustomer
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class BesideOrder
    {
        [PrimaryKey]
        public int Id { get; set; }
        public BesideCustomer Customer { get; set; }
    }

    public class BesideTag
    {
        [PrimaryKey]
        public int Id { get; set; }
        public string Label { get; set; }
    }

    public class BesideParent
    {
        [PrimaryKey]
        public int Id { get; set; }
        public List<BesideTag> Tags { get; set; }
    }

    public class XodWriteBesideReadTests : IDisposable
    {
        private readonly List<string> dirs = new();

        public void Dispose()
        {
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-write-beside-read", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            dirs.Add(dir);
            return Path.Combine(dir, "app.xod");
        }

        // Runs the read on a thread of its own, stopped inside its predicate at the row stopAt picks; starts the write on
        // another thread there; and says whether the write was made while the read was still under way. Then lets the
        // read go on, and waits for both.
        private static (bool WrittenInsideRead, List<T> Read) WriteDuringRead<T>(XodContext db, Func<T, bool> stopAt, Action write)
            where T : class
        {
            var inside = new ManualResetEventSlim();
            var go = new ManualResetEventSlim();
            Task<List<T>> read = null;
            Task writer = null;
            bool writtenInsideRead;
            try
            {
                read = Task.Run(() => db.QueryDelegate<T>(row =>
                {
                    if (stopAt(row))
                    {
                        inside.Set();
                        go.Wait();
                    }
                    return true;
                }).ToList());
                Assert.True(inside.Wait(EngineLocks.Bound), "the read never ran its predicate");

                writer = Task.Run(write);
                EngineLocks.Until(() => writer.IsCompleted || EngineLocks.WaitingWriters(db) > 0, "the write made or waiting");
                writtenInsideRead = writer.IsCompleted;
            }
            finally
            {
                go.Set();
            }
            EngineLocks.Until(() => read.IsCompleted && writer.IsCompleted, "the read and the write finished");
            Assert.True(read.IsCompletedSuccessfully, read.Exception?.ToString());
            Assert.True(writer.IsCompletedSuccessfully, writer.Exception?.ToString());
            return (writtenInsideRead, read.Result);
        }

        // ISS-169: a write to a standalone table, on another thread, waits for a read whose rows reference it; the read
        // shows its rows as they were before the write, all of them.
        [Fact]
        public void A_write_waits_for_a_read_that_references_its_rows()
        {
            using var db = new XodContext(NewDatabase("references"));
            var customer = db.Insert(new BesideCustomer { Name = "Old" });
            db.Insert(new BesideOrder { Id = 1, Customer = customer });
            db.Insert(new BesideOrder { Id = 2, Customer = customer });

            var (writtenInsideRead, read) = WriteDuringRead<BesideOrder>(db, o => o.Id == 1,
                () => db.Update(new BesideCustomer { Id = customer.Id, Name = "New" }));

            Assert.False(writtenInsideRead, "the write was made in the middle of the read");
            Assert.Equal(2, read.Count);
            Assert.All(read, o => Assert.Equal("Old", o.Customer.Name));
            Assert.Equal("New", db.Find<BesideCustomer>(c => c.Id == customer.Id).Name);
        }

        // ISS-108: a child's write (a standalone table read through its parent), on another thread, lengthening its row,
        // waits for its parent's read; the read shows the children as they were.
        [Fact]
        public void A_child_s_write_waits_for_its_parent_s_read()
        {
            using var db = new XodContext(NewDatabase("children"));
            db.Insert(new BesideParent
            {
                Id = 1,
                Tags = new List<BesideTag> { new BesideTag { Id = 1, Label = "a" }, new BesideTag { Id = 2, Label = "b" } }
            });
            string longer = new string('x', 300);

            var (writtenInsideRead, read) = WriteDuringRead<BesideParent>(db, p => p.Id == 1,
                () => db.Update(new BesideTag { Id = 1, Label = longer }));

            Assert.False(writtenInsideRead, "the child's write was made in the middle of its parent's read");
            Assert.Equal(new[] { "a", "b" }, Assert.Single(read).Tags.Select(t => t.Label).OrderBy(l => l).ToArray());
            Assert.Equal(longer, db.Find<BesideTag>(t => t.Id == 1).Label);
        }
    }
}
