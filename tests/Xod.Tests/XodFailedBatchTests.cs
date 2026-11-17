// ISS-177: an InsertRange or a Delete ended its batch, writing it, in a finally: a statement that failed part way wrote
// what it had done before its undo put the files back (in buffered mode, the range's rows stayed written until the next
// flush), and a write that failed there replaced the statement's own exception with the write's. A statement that fails
// writes nothing of its batch now. The data-file writes are counted, and failed, through FaultInjection.InProcess at the
// step file-write.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xod.Services;
using Xunit;

namespace Xod.Tests
{
    [Collection(InProcessHookCollection.Name)]
    public class XodFailedBatchTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-failed-batch-" + Guid.NewGuid().ToString("N")[..8]);
        private static readonly AsyncLocal<bool> watching = new AsyncLocal<bool>();

        public void Dispose()
        {
            FaultInjection.InProcess = null;
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private string NewPath(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "batch.xod");
        }

        private static DatabaseOptions Options(bool buffered) => new DatabaseOptions
        {
            InitialCreate = true,
            Durability = buffered ? DurabilityMode.Buffered : DurabilityMode.Immediate
        };

        private static BatchNote Note(int id) => new BatchNote { Id = id, Text = "note " + id };

        private static int[] Ids(XodContext db) => db.Select<BatchNote>().Select(n => n.Id).OrderBy(i => i).ToArray();

        // Closed with nothing left to write, a context leaves no marker of indexes behind its pages (ISS-043).
        private static void NothingLeftBehind(string path) =>
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*" + IOService.IndexesBehindSuffix));

        // Runs the statement, counting the data files it writes; with failWrites, each of those writes fails, as a file
        // another program holds fails it.
        private static (Exception Failure, int Writes) Watching(bool failWrites, Action statement)
        {
            int writes = 0;
            FaultInjection.InProcess = step =>
            {
                if (!watching.Value || !step.StartsWith("file-write:", StringComparison.Ordinal))
                    return;
                Interlocked.Increment(ref writes);
                if (failWrites)
                    throw new IOException("Injected failure: " + step);
            };
            watching.Value = true;
            try
            {
                return (Record.Exception(statement), writes);
            }
            finally
            {
                watching.Value = false;
                FaultInjection.InProcess = null;
            }
        }

        // A range its trigger refuses at its third item writes nothing, and the caller gets the trigger's exception, even
        // when every write would fail; the range leaves no row, after a flush and a reopen. Its batch is over: the next
        // write is made, and nothing is left to write at the close.
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void A_refused_range_writes_nothing_and_throws_its_own_failure(bool buffered, bool failWrites)
        {
            string path = NewPath("range-" + buffered + "-" + failWrites);
            using (var db = new XodContext(path, options: Options(buffered)))
            {
                db.Insert(Note(1));
                db.Flush();
                db.BeforeAction += (_, e) =>
                {
                    if (e.Item is BatchNote { Id: 4 })
                        throw new InvalidOperationException("refused by the trigger");
                };

                var (failure, writes) = Watching(failWrites, () => db.InsertRange(new List<BatchNote> { Note(2), Note(3), Note(4) }));

                Assert.IsType<InvalidOperationException>(failure);
                Assert.Equal(0, writes);
                Assert.Equal(new[] { 1 }, Ids(db));
                db.Insert(Note(5));
            }
            NothingLeftBehind(path);
            using (var reopened = new XodContext(path, options: Options(buffered)))
                Assert.Equal(new[] { 1, 5 }, Ids(reopened));
        }

        // A delete its AfterAction refuses writes nothing, and the caller gets the trigger's exception; the row stays,
        // the next write is made, and nothing is left to write at the close.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_refused_delete_writes_nothing_and_throws_its_own_failure(bool failWrites)
        {
            string path = NewPath("delete-" + failWrites);
            using (var db = new XodContext(path, options: Options(buffered: false)))
            {
                db.InsertRange(new List<BatchNote> { Note(1), Note(2) });
                db.AfterAction += (_, e) =>
                {
                    if (e.Action == DatabaseActions.Delete)
                        throw new InvalidOperationException("refused by the trigger");
                };

                var (failure, writes) = Watching(failWrites, () => db.Delete(new BatchNote { Id = 1 }));

                Assert.IsType<InvalidOperationException>(failure);
                Assert.Equal(0, writes);
                Assert.Equal(new[] { 1, 2 }, Ids(db));
                db.Insert(Note(5));
            }
            NothingLeftBehind(path);
            using (var reopened = new XodContext(path, options: Options(buffered: false)))
                Assert.Equal(new[] { 1, 2, 5 }, Ids(reopened));
        }

        // ISS-176: a DeleteWhere its AfterAction refuses at its second match writes nothing, the first match's delete
        // included, and the caller gets the trigger's exception; every row stays.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_refused_delete_where_writes_nothing_and_throws_its_own_failure(bool failWrites)
        {
            string path = NewPath("delete-where-" + failWrites);
            using (var db = new XodContext(path, options: Options(buffered: false)))
            {
                db.InsertRange(new List<BatchNote> { Note(1), Note(2), Note(3) });
                int deletes = 0;
                db.AfterAction += (_, e) =>
                {
                    if (e.Action == DatabaseActions.Delete && ++deletes == 2)
                        throw new InvalidOperationException("refused by the trigger");
                };

                var (failure, writes) = Watching(failWrites, () => db.DeleteWhere<BatchNote>(n => n.Id < 3));

                Assert.IsType<InvalidOperationException>(failure);
                Assert.Equal(0, writes);
                Assert.Equal(new[] { 1, 2, 3 }, Ids(db));
            }
            NothingLeftBehind(path);
            using (var reopened = new XodContext(path, options: Options(buffered: false)))
                Assert.Equal(new[] { 1, 2, 3 }, Ids(reopened));
        }

        // ISS-176: a DeleteWhere's deletes wait in its batch and are written at its end, each data file once, however
        // many of its matches the file holds.
        [Fact]
        public void A_delete_where_writes_each_file_once()
        {
            string path = NewPath("delete-where-once");
            using var db = new XodContext(path, options: Options(buffered: false));
            db.InsertRange(new List<BatchNote> { Note(1), Note(2), Note(3), Note(4) });
            var files = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            FaultInjection.InProcess = step =>
            {
                if (watching.Value && step.StartsWith("file-write:", StringComparison.Ordinal))
                    files.AddOrUpdate(step, 1, (_, n) => n + 1);
            };
            watching.Value = true;
            try
            {
                Assert.Equal(3, db.DeleteWhere<BatchNote>(n => n.Id < 4));
            }
            finally
            {
                watching.Value = false;
                FaultInjection.InProcess = null;
            }

            Assert.NotEmpty(files);
            Assert.All(files, f => Assert.Equal(1, f.Value));
            Assert.Equal(new[] { 4 }, Ids(db));
        }

        // A trigger that writes another table inside a range refused later: its write is the range's, written with
        // nothing else of it, and gone with it after a reopen.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_trigger_s_write_inside_a_refused_range_is_not_written(bool buffered)
        {
            string path = NewPath("trigger-write-" + buffered);
            using (var db = new XodContext(path, options: Options(buffered)))
            {
                db.Insert(Note(1));
                db.Flush();
                db.BeforeAction += (_, e) =>
                {
                    if (e.Action == DatabaseActions.Insert && e.Item is BatchNote { Id: 3 })
                        db.Insert(new BatchOther { Id = 30 });
                    if (e.Item is BatchNote { Id: 4 })
                        throw new InvalidOperationException("refused by the trigger");
                };

                var (failure, writes) = Watching(false, () => db.InsertRange(new List<BatchNote> { Note(2), Note(3), Note(4) }));

                Assert.IsType<InvalidOperationException>(failure);
                Assert.Equal(0, writes);
                db.Flush();
            }
            NothingLeftBehind(path);
            using (var reopened = new XodContext(path, options: Options(buffered)))
            {
                Assert.Equal(new[] { 1 }, Ids(reopened));
                Assert.Empty(reopened.Select<BatchOther>());
            }
        }

        // A delete that cascades to its children inside its batch, refused by its AfterAction: nothing written, the
        // trigger's exception even when every write would fail, the parent and its children kept.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_refused_cascade_delete_writes_nothing_and_keeps_the_children(bool failWrites)
        {
            string path = NewPath("cascade-" + failWrites);
            using (var db = new XodContext(path, options: Options(buffered: false)))
            {
                db.Insert(new BatchParent { Id = 1, Tags = new List<BatchTag> { new BatchTag { Label = "a" }, new BatchTag { Label = "b" } } });
                db.AfterAction += (_, e) =>
                {
                    if (e.Action == DatabaseActions.Delete && e.Item is BatchParent)
                        throw new InvalidOperationException("refused by the trigger");
                };

                var (failure, writes) = Watching(failWrites, () => db.Delete(new BatchParent { Id = 1 }));

                Assert.IsType<InvalidOperationException>(failure);
                Assert.Equal(0, writes);
            }
            NothingLeftBehind(path);
            using (var reopened = new XodContext(path, options: Options(buffered: false)))
            {
                Assert.Single(reopened.Select<BatchParent>());
                Assert.Equal(new[] { "a", "b" }, reopened.Select<BatchTag>().Select(t => t.Label).OrderBy(l => l).ToArray());
            }
        }

        // The range that succeeds still writes its batch at its end, once.
        [Fact]
        public void A_range_that_succeeds_writes_its_batch()
        {
            string path = NewPath("range-ok");
            using (var db = new XodContext(path, options: Options(buffered: false)))
            {
                db.Insert(Note(1));
                var (failure, writes) = Watching(false, () => db.InsertRange(new List<BatchNote> { Note(2), Note(3) }));
                Assert.Null(failure);
                Assert.True(writes > 0);
            }
            using (var reopened = new XodContext(path, options: Options(buffered: false)))
                Assert.Equal(new[] { 1, 2, 3 }, Ids(reopened));
        }
    }

    public class BatchNote
    {
        [PrimaryKey]
        public int Id { get; set; }
        public string Text { get; set; }
    }

    public class BatchOther
    {
        [PrimaryKey]
        public int Id { get; set; }
    }

    public class BatchTag
    {
        public int Id { get; set; }
        public string Label { get; set; }
    }

    public class BatchParent
    {
        [PrimaryKey]
        public int Id { get; set; }
        [Property(Cascade = CascadeOptions.Delete)]
        public List<BatchTag> Tags { get; set; }
    }
}
