using System;
using System.Collections.Generic;
using System.IO;
using Xod;

// Model names are global so the parent test process, which declares the same types, maps the files
// the dying child wrote.
// A unique key, for the index crash tests (ISS-043).
public class CrashMember
{
    [PrimaryKey] public int Id { get; set; }
    [UniqueKey] public string Email { get; set; }
}

// A second standalone table (no relations), beside CrashMember (ISS-124).
public class CrashNote
{
    [PrimaryKey] public int Id { get; set; }
    public string Text { get; set; }
}

// Signals when fault injection pauses (it writes PAUSED), and lets it go on when the test says so: the process
// drives its own pause, so a thread of it can act while another is paused.
public sealed class PauseSignal : TextWriter
{
    private readonly TextWriter inner;
    private readonly System.Threading.ManualResetEventSlim paused;
    public PauseSignal(TextWriter inner, System.Threading.ManualResetEventSlim paused) { this.inner = inner; this.paused = paused; }
    public override System.Text.Encoding Encoding => inner.Encoding;
    public override void Write(char value) => inner.Write(value);
    public override void Write(string value) { inner.Write(value); Seen(value); }
    public override void WriteLine(string value) { inner.WriteLine(value); Seen(value); }
    public override void Flush() => inner.Flush();
    private void Seen(string value)
    {
        if (value != null && value.StartsWith("PAUSED", StringComparison.Ordinal))
            paused.Set();
    }
}

public sealed class ReleaseOnRead : TextReader
{
    private readonly System.Threading.ManualResetEventSlim release;
    public ReleaseOnRead(System.Threading.ManualResetEventSlim release) { this.release = release; }
    public override string ReadLine() { release.Wait(); return ""; }
}

public class CrashCustomer
{
    public int Id { get; set; }
    public string Name { get; set; }
    [Children] public List<CrashOrder> Orders { get; set; }
}

public class CrashOrder
{
    public int Id { get; set; }
    public int CrashCustomerId { get; set; }
    public string Status { get; set; }
    [ParentKey(nameof(CrashCustomerId))] public CrashCustomer Customer { get; set; }
    public double Total { get; set; }
}

/// <summary>
/// Child process for the crash-safety test (gate layer 4). It writes a fixed-size graph in one
/// transaction, flushes, prints BATCH and repeats until the parent kills it. Every batch is 25
/// customers with 3 orders each, so an all-or-nothing database can only contain a multiple of 25
/// customers and 75 orders - a partial batch is a durability failure.
/// </summary>
public static class Program
{
    private const int BatchCustomers = 25;
    private const int OrdersPerCustomer = 3;

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: Xod.CrashWriter <setup|onebatch|burst|count|pwsetup|pwchange|pwchange-twice|pwchange-then-write|bufferedflush|pwchange-buffered|txcommit|txfail|pwopen|pwopen-locked|convert> <directory>");
            return 2;
        }

        string mode = args[0];
        string dir = args[1];
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "crash.xod");

        var options = new DatabaseOptions
        {
            InitialCreate = true,
            EnableIndexing = true,
            StorageFormat = StorageFormat.Xml,
        };
        // Options a test sets for any mode.
        if (Environment.GetEnvironmentVariable("XOD_TEST_DURABILITY") == "Buffered")
            options.Durability = DurabilityMode.Buffered;
        if (Environment.GetEnvironmentVariable("XOD_TEST_LOCK") == "1")
            options.LockDatabase = true;

        // XodMigrator (ISS-072): the password database converted to Binary, into XOD_TEST_TARGET (the folder
        // itself when unset).
        if (mode == "convert")
        {
            string target = Environment.GetEnvironmentVariable("XOD_TEST_TARGET") ?? dir;
            int converted = XodMigrator.ConvertDatabase(dir, target, StorageFormat.Binary, "first");
            Console.WriteLine("CONVERTED " + converted);
            return 0;
        }

        if (mode == "setup")
        {
            using var seed = new XodContext(path, options: options);
            seed.InsertRange(BuildBatch(0));
            seed.Flush();
            Console.WriteLine("SETUP");
            return 0;
        }

        if (mode == "onebatch")
        {
            // one transaction, committed once; XOD_CRASH_AFTER_ROWS makes the process die part-way
            using var db = new XodContext(path, options: options);
            int batch = (int)(db.Count<CrashCustomer>() / BatchCustomers);
            db.InsertRange(BuildBatch(batch));
            db.Flush();
            Console.WriteLine("COMMITTED");
            return 0;
        }

        // Password changes (ISS-065): a database under the password "first", then a change to "second"
        // that XOD_CRASH_AT_STEP stops at an exact step.
        if (mode == "pwsetup")
        {
            using var seed = new XodContext(Path.Combine(dir, "pw.xod"), "first", options);
            seed.InsertRange(BuildBatch(0));
            seed.Flush();
            Console.WriteLine("SETUP");
            return 0;
        }

        if (mode == "pwchange")
        {
            using var db = new XodContext(Path.Combine(dir, "pw.xod"), "first", options);
            if (!Change(db, "first", "second"))
                return 3;
            Console.WriteLine("CHANGED");
            return 0;
        }

        // A change that couldn't finish (XOD_FAIL_AT_STEP), then a second change in the same context.
        if (mode == "pwchange-twice")
        {
            using var db = new XodContext(Path.Combine(dir, "pw.xod"), "first", options);
            Change(db, "first", "second");
            db.ChangePassword("second", "third");
            Console.WriteLine("CHANGED");
            return 0;
        }

        // A change that couldn't finish, then writes in the same context.
        if (mode == "pwchange-then-write")
        {
            using var db = new XodContext(Path.Combine(dir, "pw.xod"), "first", options);
            Change(db, "first", "second");
            try
            {
                db.InsertRange(BuildBatch(1));
                db.Flush();
                Console.WriteLine("WROTE");
            }
            catch (Exception e)
            {
                Console.WriteLine("REFUSED " + e.GetType().Name + ": " + e.Message);
            }
            return 0;
        }

        // A change whose hold alone fails for another reason than another's lock (XOD_FAIL_AT_STEP=hold-alone), then
        // the context waits for a line, meanwhile the test checks what it still holds, and writes.
        if (mode == "pwchange-hold")
        {
            using var db = new XodContext(Path.Combine(dir, "pw.xod"), "first", options);
            if (Change(db, "first", "second"))
                Console.WriteLine("CHANGED");
            Console.WriteLine("WAITING");
            Console.Out.Flush();
            Console.In.ReadLine();
            db.InsertRange(BuildBatch(1));
            db.Flush();
            Console.WriteLine("WROTE");
            return 0;
        }

        // Buffered durability under a password: a batch, Flush, and the process dies at once.
        if (mode == "bufferedflush")
        {
            options.Durability = DurabilityMode.Buffered;
            using var db = new XodContext(Path.Combine(dir, "pw.xod"), "first", options);
            db.InsertRange(BuildBatch(1));
            db.Flush();
            Console.WriteLine("FLUSHED");
            Console.Out.Flush();
            Environment.FailFast("killed after Flush");
        }

        // Buffered durability: a batch not yet flushed, then a password change (XOD_CRASH_AT_STEP stops it).
        if (mode == "pwchange-buffered")
        {
            options.Durability = DurabilityMode.Buffered;
            using var db = new XodContext(Path.Combine(dir, "pw.xod"), "first", options);
            db.InsertRange(BuildBatch(1));
            db.ChangePassword("first", "second");
            Console.WriteLine("CHANGED");
            return 0;
        }

        // A transaction of one batch, committed; XOD_CRASH_AT_STEP / XOD_FAIL_AT_STEP stop it part-way.
        if (mode == "txcommit" || mode == "txfail")
        {
            using var db = new XodContext(Path.Combine(dir, "pw.xod"), "first", options);
            try
            {
                using (var tx = db.BeginTransaction())
                {
                    // XOD_TEST_BATCH: which batch the transaction inserts (1 by default).
                    int batch = int.TryParse(Environment.GetEnvironmentVariable("XOD_TEST_BATCH"), out int b) ? b : 1;
                    // XOD_TEST_SINGLE: one insert per customer, which leaves the indexes to the commit to write.
                    if (Environment.GetEnvironmentVariable("XOD_TEST_SINGLE") == "1")
                        foreach (var customer in BuildBatch(batch))
                            db.Insert(customer);
                    else
                        db.InsertRange(BuildBatch(batch));
                    tx.Commit();
                }
                Console.WriteLine("COMMITTED");
            }
            catch (Exception e)
            {
                Console.WriteLine("COMMIT FAILED " + e.GetType().Name + ": " + e.Message);
            }
            if (mode == "txcommit")
                return 0;

            // What this context sees after the failed commit, and whether it can go on writing (the next batch
            // after what it sees).
            int seen = db.Count<CrashCustomer>();
            Console.WriteLine("COUNT " + seen);
            try
            {
                db.InsertRange(BuildBatch(seen / BatchCustomers));
                db.Flush();
                Console.WriteLine("WROTE");
            }
            catch (Exception e)
            {
                Console.WriteLine("REFUSED " + e.GetType().Name + ": " + e.Message);
            }
            return 0;
        }

        // A batch written outside a transaction (buffered, with XOD_TEST_DURABILITY=Buffered), then a transaction
        // of the next batch that commits (XOD_FAIL_AT_STEP may fail it) or, in "txwrite-rollback", rolls back.
        // Then what the context sees, and whether it can go on writing; then whether this process can open the
        // database again once the context is disposed (it must have let go of it).
        if (mode == "txwrite-commit" || mode == "txwrite-rollback")
        {
            string dbPath = Path.Combine(dir, "pw.xod");
            var db = new XodContext(dbPath, "first", options);
            try
            {
                db.InsertRange(BuildBatch(1));
                try
                {
                    using (var tx = db.BeginTransaction())
                    {
                        db.InsertRange(BuildBatch(2));
                        if (mode == "txwrite-commit")
                            tx.Commit();
                        else
                            tx.Rollback();
                    }
                    Console.WriteLine(mode == "txwrite-commit" ? "COMMITTED" : "ROLLED BACK");
                }
                catch (Exception e)
                {
                    Console.WriteLine("COMMIT FAILED " + e.GetType().Name + ": " + e.Message);
                }

                int seen = db.Count<CrashCustomer>();
                Console.WriteLine("COUNT " + seen);
                try
                {
                    db.InsertRange(BuildBatch(seen / BatchCustomers));
                    db.Flush();
                    Console.WriteLine("WROTE");
                }
                catch (Exception e)
                {
                    Console.WriteLine("REFUSED " + e.GetType().Name + ": " + e.Message);
                }
            }
            finally
            {
                try { db.Dispose(); Console.WriteLine("DISPOSED"); }
                catch (Exception e) { Console.WriteLine("DISPOSE FAILED " + e.GetType().Name + ": " + e.Message); }
            }

            try
            {
                using var again = new XodContext(dbPath, "first", options);
                Console.WriteLine("REOPENED");
            }
            catch (Exception e)
            {
                Console.WriteLine("REOPEN FAILED " + e.GetType().Name + ": " + e.Message);
            }
            return 0;
        }

        // Opens the password database and counts (a recovery at open may fail: XOD_FAIL_AT_STEP).
        if (mode == "pwopen")
        {
            try
            {
                using var db = new XodContext(Path.Combine(dir, "pw.xod"), "first", options);
                Console.WriteLine("COUNT " + db.Count<CrashCustomer>());
            }
            catch (Exception e)
            {
                Console.WriteLine("OPEN FAILED " + e.GetType().Name + ": " + e.Message);
            }
            return 0;
        }

        // With LockDatabase: an open whose recovery fails (XOD_FAIL_AT_STEP, once), then another in this process.
        if (mode == "pwopen-locked")
        {
            options.LockDatabase = true;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var db = new XodContext(Path.Combine(dir, "pw.xod"), "first", options);
                    Console.WriteLine("OPEN " + attempt + " COUNT " + db.Count<CrashCustomer>());
                }
                catch (Exception e)
                {
                    Console.WriteLine("OPEN " + attempt + " FAILED " + e.GetType().Name + ": " + e.Message);
                }
            }
            return 0;
        }

        // ISS-043 (review of TASK-064): fifty members with a unique key.
        if (mode == "members")
        {
            using var seed = new XodContext(path, options: options);
            var members = new List<CrashMember>();
            for (int i = 1; i <= 50; i++)
                members.Add(new CrashMember { Id = i, Email = "e" + i });
            seed.InsertRange(members);
            seed.Flush();
            Console.WriteLine("MEMBERS");
            return 0;
        }

        // ISS-107: a transaction deletes every member (their pages emptied), then the process dies before the commit.
        if (mode == "txdelete")
        {
            var db = new XodContext(path, options: options);
            var tx = db.BeginTransaction();
            foreach (var member in db.Select<CrashMember>())
                db.Delete(member);
            Console.WriteLine("DELETED");
            Console.Out.Flush();
            Environment.FailFast("killed before a commit");
        }

        // ISS-091: a statement fails (its trigger throws), and taking its rows back fails too (a test sets
        // XOD_FAIL_AT_STEP=undo-statement): the context refuses every later write until it is reopened.
        if (mode == "undo-fails")
        {
            using var db = new XodContext(path, options: options);
            db.Insert(new CrashCustomer { Name = "seed", Orders = new List<CrashOrder> { new CrashOrder { Status = "seed" } } });
            db.AfterAction += (_, e) =>
            {
                if (e.Item is CrashOrder order && order.Status == "x")
                    throw new InvalidOperationException("refused by the trigger");
            };
            Console.WriteLine("INDEX FILES BEFORE " + string.Join(",", Array.ConvertAll(Directory.GetFiles(dir, "*.xidx"), Path.GetFileName)));
            try
            {
                db.Insert(new CrashCustomer { Name = "refused", Orders = new List<CrashOrder> { new CrashOrder { Status = "x" } } });
            }
            catch (XodException e) when (e.InnerException is AggregateException)
            {
                Console.WriteLine("BOTH FAILED");
            }
            // The indexes of the tables the statement wrote are dropped, files too: this context's marker is held, so
            // the other contexts trust the index files, and the undo stopped part way.
            Console.WriteLine("INDEX FILES AFTER " + string.Join(",", Array.ConvertAll(Directory.GetFiles(dir, "*.xidx"), Path.GetFileName)));
            try
            {
                db.Insert(new CrashCustomer { Name = "after", Orders = new List<CrashOrder>() });
                Console.WriteLine("WROTE");
            }
            catch (XodException e) when (e.Message.Contains("Reopen"))
            {
                Console.WriteLine("REFUSED");
            }
            return 0;
        }

        // ISS-110: a commit undone once its index files are written (XOD_PAUSE_AT_STEP=commit-indexed, then
        // XOD_FAIL_AT_STEP=commit-indexed fails it), while the test holds the member index, which has the commit's key.
        // Then it waits for the test (which lets go of the file) and inserts the undone commit's key again.
        if (mode == "txmember")
        {
            using var db = new XodContext(path, options: options);
            db.Insert(new CrashMember { Id = 1, Email = "seed" });
            db.Flush();
            try
            {
                using var tx = db.BeginTransaction();
                db.Insert(new CrashMember { Id = 2, Email = "tx" });
                tx.Commit();
                Console.WriteLine("COMMITTED");
            }
            catch (Exception e)
            {
                Console.WriteLine("COMMIT FAILED " + e.GetType().Name);
            }
            Console.WriteLine("UNDONE");
            Console.Out.Flush();
            Console.In.ReadLine();
            try
            {
                db.Insert(new CrashMember { Id = 3, Email = "tx" });
                Console.WriteLine("WROTE");
            }
            catch (Exception e)
            {
                Console.WriteLine("REFUSED " + e.GetType().Name + ": " + e.Message);
            }
            return 0;
        }

        // ISS-096: opens "second.xod" in the folder, paused once it has checked that the folder holds one database
        // (XOD_PAUSE_AT_STEP=open-checked) while the test creates another database there.
        if (mode == "open-second")
        {
            try
            {
                using var db = new XodContext(Path.Combine(dir, "second.xod"), options: options);
                Console.WriteLine("OPENED");
            }
            catch (XodException e)
            {
                Console.WriteLine("REFUSED " + e.Message);
            }
            return 0;
        }

        if (mode == "undo-beside-seed")
        {
            using var db = new XodContext(path, options: options);
            db.Insert(new CrashMember { Id = 1, Email = "seed" });
            db.Insert(new CrashNote { Id = 1, Text = "seed" });
            return 0;
        }

        // ISS-124: a standalone table's statement fails at its write (XOD_FAIL_AT_STEP=statement-saving) and its undo
        // is paused (XOD_PAUSE_AT_STEP=undo-statement) while a statement on another standalone table runs beside it.
        // That statement's row must be on disk when its insert returns.
        if (mode == "undo-beside")
        {
            var paused = new System.Threading.ManualResetEventSlim();
            var release = new System.Threading.ManualResetEventSlim();
            Console.SetOut(new PauseSignal(Console.Out, paused));
            Console.SetIn(new ReleaseOnRead(release));
            try
            {
            using (var db = new XodContext(path, options: options))
            {
                var first = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        db.Insert(new CrashMember { Id = 2, Email = "refused" });
                        return "WROTE";
                    }
                    catch (Exception e)
                    {
                        return e.GetType().Name;
                    }
                });
                if (!paused.Wait(TimeSpan.FromSeconds(60)))
                {
                    release.Set();
                    Console.WriteLine("NOT PAUSED: " + first.Result);
                    return 1;
                }
                db.Insert(new CrashNote { Id = 2, Text = "beside-marker" });
                bool onDisk = false;
                foreach (string file in Directory.GetFiles(dir))
                {
                    // The pages and table files only: an index or a marker isn't a row's write, and the context holds its
                    // marker.
                    if (file.EndsWith(".xidx", StringComparison.Ordinal) || file.EndsWith(".xidxdirty", StringComparison.Ordinal))
                        continue;
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (new StreamReader(stream).ReadToEnd().Contains("beside-marker"))
                        onDisk = true;
                }
                Console.WriteLine(onDisk ? "BESIDE WRITTEN" : "BESIDE NOT WRITTEN");
                release.Set();
                Console.WriteLine("FIRST " + first.Result);
            }
            }
            finally
            {
                // A paused thread holds the process open: let it go whatever happened here.
                release.Set();
            }
            using (var again = new XodContext(path, options: options))
                Console.WriteLine("MEMBERS " + again.Count<CrashMember>() + " NOTES " + again.Count<CrashNote>());
            return 0;
        }

        // A table's first row, written with no batch and no buffer (a test sets where the process dies).
        if (mode == "firstrow")
        {
            using var db = new XodContext(path, options: options);
            db.Insert(new CrashMember { Id = 1, Email = "e1" });
            Console.WriteLine("INSERTED");
            return 0;
        }

        // A flush inside a transaction, then the process dies before the commit: the transaction's pages wait in
        // its batch, while its index changes are written by the flush.
        if (mode == "txflush")
        {
            var db = new XodContext(path, options: options);
            db.Find<CrashMember>(m => m.Email == "e5");
            var tx = db.BeginTransaction();
            var five = db.Find<CrashMember>(m => m.Id == 5);
            five.Email = "e5x";
            db.Update(five);
            db.Insert(new CrashMember { Id = 400, Email = "e400" });
            db.Flush();
            Console.WriteLine("FLUSHED");
            Console.Out.Flush();
            Environment.FailFast("killed after a flush inside a transaction");
        }

        if (mode == "count")
        {
            using var check = new XodContext(path);
            int customers = check.Count<CrashCustomer>();
            int orders = check.Count<CrashOrder>();
            int orphans = 0;
            for (int id = 1; id <= orders; id++)
            {
                var order = check.Find<CrashOrder>(o => o.Id == id);
                if (order == null) { orphans++; continue; }
                if (order.CrashCustomerId > customers) orphans++;
            }
            Console.WriteLine($"COUNT customers={customers} orders={orders} orphans={orphans}");
            return 0;
        }

        if (mode == "burst")
        {
            using var db = new XodContext(path, options: options);
            // continue after whatever setup (or a previous burst) already committed
            int batch = (int)(db.Count<CrashCustomer>() / BatchCustomers);
            while (true)
            {
                db.InsertRange(BuildBatch(batch));
                db.Flush();
                batch++;
                Console.WriteLine($"BATCH {batch}");
                Console.Out.Flush();
            }
        }

        Console.Error.WriteLine($"unknown mode {mode}");
        return 2;
    }

    // A password change; one the library reports as made but not finished prints PENDING.
    private static bool Change(XodContext db, string from, string to)
    {
        try
        {
            db.ChangePassword(from, to);
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine("PENDING " + e.GetType().Name + ": " + e.Message);
            return false;
        }
    }

    private static List<CrashCustomer> BuildBatch(int batch)
    {
        var list = new List<CrashCustomer>(BatchCustomers);
        for (int i = 0; i < BatchCustomers; i++)
        {
            int customerId = batch * BatchCustomers + i + 1;
            var orders = new List<CrashOrder>(OrdersPerCustomer);
            for (int o = 0; o < OrdersPerCustomer; o++)
            {
                orders.Add(new CrashOrder
                {
                    Id = (customerId - 1) * OrdersPerCustomer + o + 1,
                    Status = $"S{o:D2}",
                    Total = (customerId + o) * 1.5,
                });
            }

            list.Add(new CrashCustomer { Id = customerId, Name = $"Customer {customerId:D6}", Orders = orders });
        }

        return list;
    }
}
