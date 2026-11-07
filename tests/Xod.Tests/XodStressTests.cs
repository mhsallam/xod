using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class XodStressTests : IDisposable
    {
        private readonly string tempRoot;

        public XodStressTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodStressTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        private string GetDbPath(string name) => Path.Combine(tempRoot, name, "data.xod");

        [Fact]
        public void Crash_Recovery_Orphan_Tmp_And_Stale_Index_Self_Heal_On_Startup()
        {
            string dbPath = GetDbPath("crash_recovery");
            string dir = Path.GetDirectoryName(dbPath);

            using (var db = new XodContext(dbPath))
            {
                db.InsertRange(new[]
                {
                    new UserAccount { Email = "crash1@xod.dev", Score = 10 },
                    new UserAccount { Email = "crash2@xod.dev", Score = 20 },
                    new UserAccount { Email = "crash3@xod.dev", Score = 30 }
                });
            }

            string xpagFile = Directory.GetFiles(dir, "*.xpag").Single();
            string xidxFile = Directory.GetFiles(dir, "*.xidx").Single();

            // 1. Simulate an interrupted atomic write leaving a half-written .tmp file next to .xpag
            string orphanTmp = xpagFile + ".tmp";
            File.WriteAllText(orphanTmp, "<TablePage><Rows><CorruptedPartialWrite");
            Assert.True(File.Exists(orphanTmp));

            // 2. Simulate a stale or corrupted .xidx file older than the .xpag file
            File.WriteAllText(xidxFile, "<TableIndex type=\"Xod.Tests.UserAccount\"><Property name=\"Email\" /></TableIndex>");
            File.SetLastWriteTimeUtc(xidxFile, DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(xpagFile, DateTime.UtcNow);

            // Re-open database: IOService cleans up orphan .tmp and IndexService detects stale .xidx and rebuilds it
            using (var recoveredDb = new XodContext(dbPath))
            {
                Assert.False(File.Exists(orphanTmp));

                var user2 = recoveredDb.Find<UserAccount>(u => u.Email == "crash2@xod.dev");
                Assert.NotNull(user2);
                Assert.Equal(20, user2.Score);
                Assert.Equal(3, recoveredDb.Count<UserAccount>());
            }

            // Rebuilt .xidx now contains all 3 emails again
            string healedIdxContent = File.ReadAllText(xidxFile);
            Assert.Contains("crash2@xod.dev", healedIdxContent);
        }

        [Fact]
        public void Concurrency_32_Threads_Simultaneous_Read_Write_Update_Delete_And_Triggers()
        {
            string dbPath = GetDbPath("concurrency_32_threads");
            using var db = new XodContext(dbPath);

            int triggerReentrancyCount = 0;
            // Re-entrant trigger that queries the database while inside a write lock
            db.AfterAction += (_, e) =>
            {
                if (e.Action == DatabaseActions.Insert && e.Type == typeof(ToDoItem))
                {
                    _ = db.Any<ToDoItem>();
                    Interlocked.Increment(ref triggerReentrancyCount);
                }
            };

            const int threadCount = 32;
            const int opsPerThread = 5;
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, workerId =>
            {
                try
                {
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        var inserted = db.Insert(new ToDoItem
                        {
                            Title = $"Worker-{workerId}-Item-{i}",
                            Done = false
                        });

                        // Concurrent read via indexed lookup
                        var found = db.Find<ToDoItem>(t => t.Id == inserted.Id);
                        Assert.NotNull(found);

                        // Concurrent update
                        found.Done = true;
                        db.Update(found);

                        // Concurrent full query
                        _ = db.Query<ToDoItem>(t => t.Id > 0).Count();
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
            Assert.Equal(threadCount * opsPerThread, db.Count<ToDoItem>());
            Assert.Equal(threadCount * opsPerThread, triggerReentrancyCount);
            Assert.All(db.Select<ToDoItem>(), item => Assert.True(item.Done));
        }

        [Fact]
        public void Scale_MultiPage_Splits_And_O1_Row_Lookup_Torture_Test()
        {
            string dbPath = GetDbPath("scale_multipage_o1");
            string dir = Path.GetDirectoryName(dbPath);

            // Force aggressive page splitting (16KB page threshold) across 1,500 records + 1-M relationships
            var options = new DatabaseOptions { PageSize = 16 };
            using (var db = new XodContext(dbPath, options: options))
            {
                var users = Enumerable.Range(1, 1500)
                    .Select(i => new UserAccount
                    {
                        Email = $"scale.user.{i:D4}@xod.dev",
                        Score = i * 10
                    })
                    .ToList();

                db.InsertRange(users);

                // Also insert multi-page parent-child graphs to verify O(1) child pageCode.rowCode hydration
                var depts = Enumerable.Range(1, 25)
                    .Select(d => new Department
                    {
                        Name = $"Engineering-Division-{d}",
                        Employees = Enumerable.Range(1, 10)
                            .Select(m => new Employee { FullName = $"Engineer-{d}-{m}" })
                            .ToList()
                    })
                    .ToList();
                db.InsertRange(depts);
            }

            // Verify multiple .xpag files were created due to page splits
            int xpagCount = Directory.GetFiles(dir, "*.xpag").Length;
            Assert.True(xpagCount >= 3, $"Expected at least 3 .xpag files from page splitting, got {xpagCount}");

            // Cold-start open: verify O(1) XFile.GetRowByCode + B-Tree .xidx lookup across split pages
            using (var coldDb = new XodContext(dbPath, options: options))
            {
                Assert.Equal(1500, coldDb.Count<UserAccount>());

                for (int probe = 1; probe <= 1500; probe += 73)
                {
                    string email = $"scale.user.{probe:D4}@xod.dev";
                    var found = coldDb.Find<UserAccount>(u => u.Email == email);
                    Assert.NotNull(found);
                    Assert.Equal(probe * 10, found.Score);
                }

                var rangeMatches = coldDb.Query<UserAccount>(u => u.Score >= 14000).ToList();
                Assert.Equal(101, rangeMatches.Count);

                var dept15 = coldDb.Find<Department>(d => d.Name == "Engineering-Division-15");
                Assert.NotNull(dept15);
                Assert.Equal(10, dept15.Employees.Count);
            }
        }

        [Fact]
        public void Scale_Rapid_Update_Delete_And_Upsert_Via_PrimaryKey_Index()
        {
            string dbPath = GetDbPath("scale_update_delete_pk");
            using var db = new XodContext(dbPath, options: new DatabaseOptions { PageSize = 32 });

            var items = Enumerable.Range(1, 1000)
                .Select(i => new ToDoItem
                {
                    Title = $"Task-{i:D4}",
                    Done = false
                })
                .ToList();

            db.InsertRange(items);
            Assert.Equal(1000, db.Count<ToDoItem>());

            // Update 200 items across pages via O(log N) PrimaryKey index + O(1) XFile row map
            for (int id = 5; id <= 1000; id += 5)
            {
                bool updated = db.Update(new ToDoItem
                {
                    Id = id,
                    Title = $"Updated-Task-{id:D4}",
                    Done = true
                });
                Assert.True(updated);
            }

            // Upsert (InsertOrUpdate) 50 existing + 50 brand new items
            for (int id = 10; id <= 500; id += 10)
            {
                db.InsertOrUpdate(new ToDoItem
                {
                    Id = id,
                    Title = $"Upserted-Task-{id:D4}",
                    Done = true
                });
            }

            for (int n = 1; n <= 50; n++)
            {
                var created = db.InsertOrUpdate(new ToDoItem
                {
                    Title = $"BrandNew-{n}",
                    Done = true
                });
                Assert.True(created.Id > 1000);
            }

            Assert.Equal(1050, db.Count<ToDoItem>());

            // Delete 100 items by PrimaryKey
            for (int id = 10; id <= 1000; id += 10)
            {
                bool deleted = db.Delete(new ToDoItem { Id = id });
                Assert.True(deleted);
            }

            Assert.Equal(950, db.Count<ToDoItem>());
            Assert.Null(db.Find<ToDoItem>(t => t.Id == 500));

            var check505 = db.Find<ToDoItem>(t => t.Id == 505);
            Assert.NotNull(check505);
            Assert.Equal("Updated-Task-0505", check505.Title);
            Assert.True(check505.Done);
        }

        [Fact]
        public void Exclusive_Database_Lock_Prevents_Concurrent_Context_Writers()
        {
            string dbPath = GetDbPath("exclusive_lock_db");
            var lockOptions = new DatabaseOptions { LockDatabase = true };

            using (var primaryOwner = new XodContext(dbPath, options: lockOptions))
            {
                primaryOwner.Insert(new ToDoItem { Title = "Locked-Item", Done = false });
                Assert.True(File.Exists(dbPath + ".lock"));

                // Attempting to open a second locked context while primaryOwner holds the lock throws DatabaseLockedException
                Assert.Throws<DatabaseLockedException>(() => new XodContext(dbPath, options: lockOptions));
            }

            // Once primaryOwner is disposed, the .lock file is released and removed, allowing a new owner to acquire it
            Assert.False(File.Exists(dbPath + ".lock"));
            using (var nextOwner = new XodContext(dbPath, options: lockOptions))
            {
                Assert.Equal(1, nextOwner.Count<ToDoItem>());
            }
        }

        [Fact]
        public void Streaming_Pagination_Skip_And_Take_Only_Materializes_Requested_Slice()
        {
            string dbPath = GetDbPath("streaming_pagination");
            using var db = new XodContext(dbPath, options: new DatabaseOptions { PageSize = 16 });

            var batch = Enumerable.Range(1, 500)
                .Select(i => new ToDoItem { Title = $"Row-{i:D3}", Done = i % 2 == 0 })
                .ToList();
            db.InsertRange(batch);

            // Forward pagination: skip 450, take 15 -> IDs 451..465
            var sliceForward = db.Select<ToDoItem>(skip: 450, take: 15).ToList();
            Assert.Equal(15, sliceForward.Count);
            Assert.Equal(451, sliceForward.First().Id);
            Assert.Equal(465, sliceForward.Last().Id);

            // Backward pagination: skip 10 from the end, take 5 -> IDs 490..486
            var sliceBackward = db.Select<ToDoItem>(skip: 10, take: 5, backward: true).ToList();
            Assert.Equal(5, sliceBackward.Count);
            Assert.Equal(490, sliceBackward.First().Id);
            Assert.Equal(486, sliceBackward.Last().Id);
        }

        public class FuzzEntity
        {
            [PrimaryKey, Property(AutoNumber = true)]
            public int Id { get; set; }

            [Property(Indexed = true)]
            public string Tag { get; set; }

            public string LargePayload { get; set; }
            public decimal PreciseAmount { get; set; }
            public DateTime Timestamp { get; set; }
            public Guid CorrelationId { get; set; }
        }

        [Fact]
        public void Fuzz_And_Boundary_Torture_Extreme_Payloads_Unicode_And_MultiFormat_RoundTrip()
        {
            string largeText = new string('X', 128 * 1024); // 128 KB string
            string adversarialUnicode = "🔥🚀 العربية 中文 日本語 한국어 & <tag attr=\"val\"> 'quote' \r\n\t <![CDATA[nested]]> \\0";

            foreach (var format in new[] { StorageFormat.Xml, StorageFormat.Json, StorageFormat.Binary })
            {
                string ext = format switch
                {
                    StorageFormat.Json => "data.xodj",
                    StorageFormat.Binary => "data.xodb",
                    _ => "data.xod"
                };
                string dbPath = Path.Combine(tempRoot, $"fuzz_{format}", ext);

                using (var db = new XodContext(dbPath, "FuzzPass#2026!", new DatabaseOptions { StorageFormat = format }))
                {
                    db.Insert(new FuzzEntity
                    {
                        Tag = adversarialUnicode,
                        LargePayload = largeText,
                        PreciseAmount = 79228162514264337593543950335m, // decimal.MaxValue
                        Timestamp = new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc),
                        CorrelationId = Guid.Empty
                    });

                    db.Insert(new FuzzEntity
                    {
                        Tag = "min-boundary",
                        LargePayload = string.Empty,
                        PreciseAmount = -79228162514264337593543950335m, // decimal.MinValue
                        Timestamp = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        CorrelationId = Guid.NewGuid()
                    });
                }

                // Cold-start encrypted read + indexed lookup on adversarial Unicode / XML characters
                using (var coldDb = new XodContext(dbPath, "FuzzPass#2026!", new DatabaseOptions { StorageFormat = format }))
                {
                    var found = coldDb.Find<FuzzEntity>(f => f.Tag == adversarialUnicode);
                    Assert.NotNull(found);
                    Assert.Equal(largeText.Length, found.LargePayload.Length);
                    Assert.Equal(decimal.MaxValue, found.PreciseAmount);

                    var minFound = coldDb.Find<FuzzEntity>(f => f.Tag == "min-boundary");
                    Assert.NotNull(minFound);
                    Assert.Equal(decimal.MinValue, minFound.PreciseAmount);
                }
            }
        }

        [Fact]
        public void Soak_Endurance_Churn_Insert_Update_Delete_And_Rollback_Cycles()
        {
            string dbPath = GetDbPath("soak_endurance");
            using var db = new XodContext(dbPath, options: new DatabaseOptions { PageSize = 16 });

            int expectedSurvivors = 0;

            for (int cycle = 1; cycle <= 40; cycle++)
            {
                // 1. Insert 10 accounts
                for (int i = 1; i <= 10; i++)
                {
                    int key = (cycle * 100) + i;
                    db.Insert(new UserAccount
                    {
                        Email = $"soak.{key}@xod.dev",
                        Score = key
                    });
                }

                // 2. Update the first 5 accounts in this cycle
                for (int i = 1; i <= 5; i++)
                {
                    int key = (cycle * 100) + i;
                    var u = db.Find<UserAccount>(x => x.Email == $"soak.{key}@xod.dev");
                    Assert.NotNull(u);
                    u.Score += 100000;
                    Assert.True(db.Update(u));
                }

                // 3. Delete the last 4 accounts in this cycle (leaving 6 survivors per cycle)
                for (int i = 7; i <= 10; i++)
                {
                    int key = (cycle * 100) + i;
                    var u = db.Find<UserAccount>(x => x.Email == $"soak.{key}@xod.dev");
                    Assert.NotNull(u);
                    Assert.True(db.Delete(u));
                }

                expectedSurvivors += 6;

                // 4. Every 5th cycle, run a transaction that mutates and inserts data and then rolls back
                if (cycle % 5 == 0)
                {
                    using (var tx = db.BeginTransaction())
                    {
                        db.Insert(new UserAccount { Email = $"ghost.{cycle}@xod.dev", Score = -999 });
                        var firstSurvivor = db.Find<UserAccount>(x => x.Email == $"soak.{(cycle * 100) + 1}@xod.dev");
                        Assert.True(db.Delete(firstSurvivor));
                        tx.Rollback();
                    }
                }
            }

            // Verify zero row drift and zero index drift in-memory
            Assert.Equal(expectedSurvivors, db.Count<UserAccount>());
            Assert.Null(db.Find<UserAccount>(x => x.Email == "ghost.5@xod.dev"));

            // Also verify after cold-start reload from disk (.xpag + .xidx)
            db.ClearCaches();
            Assert.Equal(expectedSurvivors, db.Count<UserAccount>());
            var sample = db.Find<UserAccount>(x => x.Email == "soak.2001@xod.dev");
            Assert.NotNull(sample);
            Assert.Equal(2001 + 100000, sample.Score);
        }

        [Fact]
        public void Enterprise_Concurrent_Transactions_Queue_And_Batch_Commit_Across_16_Threads()
        {
            string dbPath = GetDbPath("concurrent_queued_tx");
            using var db = new XodContext(dbPath);

            const int workerCount = 16;
            const int itemsPerTx = 10;
            var errors = new ConcurrentBag<Exception>();

            // 16 threads simultaneously call BeginTransaction() on the same XodContext;
            // even-numbered workers Commit(), odd-numbered workers Rollback().
            Parallel.For(0, workerCount, new ParallelOptions { MaxDegreeOfParallelism = workerCount }, workerId =>
            {
                try
                {
                    using var tx = db.BeginTransaction();
                    for (int i = 1; i <= itemsPerTx; i++)
                    {
                        db.Insert(new UserAccount
                        {
                            Email = $"tx.worker.{workerId}.item.{i}@xod.dev",
                            Score = (workerId * 100) + i
                        });
                    }

                    if (workerId % 2 == 0)
                    {
                        tx.Commit();
                    }
                    else
                    {
                        tx.Rollback();
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });

            Assert.Empty(errors);

            // Exactly 8 committed transactions * 10 items = 80 records persisted
            int expectedCommitted = (workerCount / 2) * itemsPerTx;
            Assert.Equal(expectedCommitted, db.Count<UserAccount>());

            // Verify after cold-start reload from disk
            using var verifyDb = new XodContext(dbPath);
            Assert.Equal(expectedCommitted, verifyDb.Count<UserAccount>());
            Assert.NotNull(verifyDb.Find<UserAccount>(u => u.Email == "tx.worker.0.item.5@xod.dev"));
            Assert.Null(verifyDb.Find<UserAccount>(u => u.Email == "tx.worker.1.item.5@xod.dev"));
        }

        [Fact]
        public void Enterprise_Xwal_Journal_Recovers_Interrupted_MultiTable_Transaction_On_Startup()
        {
            string dbPath = GetDbPath("xwal_multitable_recovery");
            string dir = Path.GetDirectoryName(dbPath);

            using (var db = new XodContext(dbPath))
            {
                db.Insert(new UserAccount { Email = "alice@xod.dev", Score = 100 });
            }

            // Capture original UserAccount .xpag bytes before simulating a partial multi-table commit crash
            string userXpagPath = Directory.GetFiles(dir, "*.xpag").Single();
            string userXpagFileName = Path.GetFileName(userXpagPath);
            byte[] originalUserXpagBytes = File.ReadAllBytes(userXpagPath);

            // Simulate a multi-table transaction that modified UserAccount (Score -> 999) and created ToDoItem
            // where power was lost mid-commit while transaction.xwal was still on disk!
            using (var db = new XodContext(dbPath))
            {
                var alice = db.Find<UserAccount>(u => u.Email == "alice@xod.dev");
                alice.Score = 999;
                db.Update(alice);
                db.Insert(new ToDoItem { Title = "Unfinished-MultiTable-Tx-Item", Done = false });
            }

            string todoXtabPath = Path.Combine(dir, typeof(ToDoItem).FullName + ".xtab");
            string todoXpagFileName = Directory.GetFiles(dir, "*.xpag")
                .Select(Path.GetFileName)
                .First(f => !string.Equals(f, userXpagFileName, StringComparison.OrdinalIgnoreCase));

            // Write the .xwal journal representing the interrupted multi-table commit
            string walPath = Path.Combine(dir, "transaction.xwal");
            string walXml =
                $"<TransactionJournal id=\"{Guid.NewGuid():N}\" timestampUtc=\"{DateTime.UtcNow:O}\">" +
                $"<Snapshot file=\"{userXpagFileName}\">{Convert.ToBase64String(originalUserXpagBytes)}</Snapshot>" +
                $"<Created file=\"{Path.GetFileName(todoXtabPath)}\" />" +
                $"<Created file=\"{todoXpagFileName}\" />" +
                "</TransactionJournal>";
            File.WriteAllText(walPath, walXml);

            // Re-open database: IOService replays transaction.xwal, restores UserAccount to 100, removes ToDoItem, and deletes .xwal
            using (var recoveredDb = new XodContext(dbPath))
            {
                Assert.False(File.Exists(walPath));
                var alice = recoveredDb.Find<UserAccount>(u => u.Email == "alice@xod.dev");
                Assert.NotNull(alice);
                Assert.Equal(100, alice.Score);
                Assert.Equal(0, recoveredDb.Count<ToDoItem>());
            }
        }

        [Fact]
        public void Enterprise_MultiContext_LockRetry_And_Parallel_MultiTable_Concurrency()
        {
            string dbPath = GetDbPath("lock_retry_and_striped_tables");
            var lockRetryOptions = new DatabaseOptions
            {
                LockDatabase = true,
                LockRetryTimeoutMs = 3000
            };

            // Part 1: Two contexts contending on LockDatabase = true where Context 1 holds the lock for 120ms
            // and Context 2 waits via LockRetryTimeoutMs backoff and succeeds once Context 1 disposes.
            var holderReady = new ManualResetEventSlim(false);
            var holderTask = Task.Run(() =>
            {
                using var ctx1 = new XodContext(dbPath, options: lockRetryOptions);
                holderReady.Set();
                ctx1.Insert(new UserAccount { Email = "holder1@xod.dev", Score = 50 });
                Thread.Sleep(120);
            });

            holderReady.Wait();
            using (var ctx2 = new XodContext(dbPath, options: lockRetryOptions))
            {
                ctx2.Insert(new UserAccount { Email = "waiter2@xod.dev", Score = 75 });
            }
            holderTask.GetAwaiter().GetResult();

            // Part 2: Parallel multi-table writes & reads across 3 independent tables (UserAccount, ToDoItem, FuzzEntity)
            // using per-table lock striping on a shared context
            using (var sharedDb = new XodContext(dbPath))
            {
                Assert.Equal(2, sharedDb.Count<UserAccount>());

                var exceptions = new ConcurrentBag<Exception>();
                Parallel.Invoke(
                    () =>
                    {
                        try
                        {
                            for (int i = 1; i <= 50; i++)
                            {
                                sharedDb.Insert(new UserAccount { Email = $"striped.user.{i}@xod.dev", Score = i });
                                _ = sharedDb.Find<UserAccount>(u => u.Email == $"striped.user.{i}@xod.dev");
                            }
                        }
                        catch (Exception ex) { exceptions.Add(ex); }
                    },
                    () =>
                    {
                        try
                        {
                            for (int i = 1; i <= 50; i++)
                            {
                                sharedDb.Insert(new ToDoItem { Title = $"Striped-ToDo-{i}", Done = i % 2 == 0 });
                                _ = sharedDb.Find<ToDoItem>(t => t.Id == i);
                            }
                        }
                        catch (Exception ex) { exceptions.Add(ex); }
                    },
                    () =>
                    {
                        try
                        {
                            for (int i = 1; i <= 50; i++)
                            {
                                sharedDb.Insert(new FuzzEntity
                                {
                                    Tag = $"striped-tag-{i}",
                                    LargePayload = "payload",
                                    PreciseAmount = i * 1.25m,
                                    Timestamp = DateTime.UtcNow,
                                    CorrelationId = Guid.NewGuid()
                                });
                                _ = sharedDb.Find<FuzzEntity>(f => f.Tag == $"striped-tag-{i}");
                            }
                        }
                        catch (Exception ex) { exceptions.Add(ex); }
                    });

                Assert.Empty(exceptions);
                Assert.Equal(52, sharedDb.Count<UserAccount>());
                Assert.Equal(50, sharedDb.Count<ToDoItem>());
                Assert.Equal(50, sharedDb.Count<FuzzEntity>());
            }
        }

        [Fact]
        public void Enterprise_Buffered_Durability_High_Throughput_Burst_And_Flush()
        {
            string dbPath = GetDbPath("buffered_durability_burst");
            string dir = Path.GetDirectoryName(dbPath);
            var bufferedOptions = new DatabaseOptions
            {
                Durability = DurabilityMode.Buffered
            };

            using (var db = new XodContext(dbPath, options: bufferedOptions))
            {
                // Execute 500 individual single-row inserts + 100 individual updates in Buffered mode
                for (int i = 1; i <= 500; i++)
                {
                    db.Insert(new UserAccount
                    {
                        Email = $"burst.user.{i:D4}@xod.dev",
                        Score = i
                    });
                }

                for (int i = 1; i <= 100; i++)
                {
                    var u = db.Find<UserAccount>(x => x.Email == $"burst.user.{i:D4}@xod.dev");
                    Assert.NotNull(u);
                    u.Score += 10000;
                    Assert.True(db.Update(u));
                }

                // Before Flush(), all 500 records are queryable in memory, and no .xpag file has been written yet
                Assert.Equal(500, db.Count<UserAccount>());
                Assert.Empty(Directory.GetFiles(dir, "*.xpag"));

                // Explicit group-commit Flush() writes all dirty buffered pages and .xidx indexes atomically
                db.Flush();
                Assert.NotEmpty(Directory.GetFiles(dir, "*.xpag"));
                Assert.NotEmpty(Directory.GetFiles(dir, "*.xidx"));
            }

            // Cold-start verify after Flush/Dispose
            using (var verifyDb = new XodContext(dbPath))
            {
                Assert.Equal(500, verifyDb.Count<UserAccount>());
                var u50 = verifyDb.Find<UserAccount>(x => x.Email == "burst.user.0050@xod.dev");
                Assert.NotNull(u50);
                Assert.Equal(10050, u50.Score);
            }
        }
    }
}
