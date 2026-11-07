using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xod;
using Xunit;

namespace Xod.Tests
{
    public class DiffRow
    {
        [PrimaryKey, Property(AutoNumber = true)] public int Id { get; set; }
        [UniqueKey] public string Sku { get; set; }
        [Property(Indexed = true)] public int Bucket { get; set; }
        public double Amount { get; set; }
    }

    /// <summary>
    /// Gate layer 5 of TEST-PLAN-2.0: the same operation script, run against XOD and against SQLite,
    /// must produce the same answers. This is not a speed comparison and not a format check - it is the
    /// "is this actually a database" question: rows inserted, updated by key, deleted by key, filtered by
    /// an index, counted and summed the same way as a reference implementation everyone trusts.
    /// </summary>
    public class XodDifferentialTests : IDisposable
    {
        private const int Rows = 500;
        private const int Updates = 50;
        private const int Deletes = 25;

        private readonly string tempRoot;

        public XodDifferentialTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "XodDiff_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        private static List<DiffRow> Seed()
        {
            var rows = new List<DiffRow>(Rows);
            for (int i = 1; i <= Rows; i++)
            {
                rows.Add(new DiffRow
                {
                    Id = i,
                    Sku = $"SKU-{i:D5}",
                    Bucket = i % 20,
                    Amount = Math.Round(i * 1.25, 2),
                });
            }

            return rows;
        }

        private static string Canonical(IEnumerable<DiffRow> rows)
        {
            var list = rows.OrderBy(r => r.Id)
                .Select(r => new { r.Id, r.Sku, r.Bucket, Amount = Math.Round(r.Amount, 2) })
                .ToList();
            return JsonSerializer.Serialize(list);
        }

        private static string Fingerprint(IEnumerable<DiffRow> all, IEnumerable<DiffRow> bucket, int count, double sum)
        {
            return JsonSerializer.Serialize(new
            {
                Count = count,
                Sum = Math.Round(sum, 2),
                All = Canonical(all),
                Bucket7 = Canonical(bucket),
            });
        }

        private string RunXod(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "diff.xod");

            var options = new DatabaseOptions
            {
                InitialCreate = true,
                EnableIndexing = true,
                StorageFormat = StorageFormat.Xml,
                Durability = DurabilityMode.Buffered,
            };

            string fingerprint;
            using (var db = new XodContext(path, options: options))
            {
                db.InsertRange(Seed());
                db.Flush();

                for (int id = 1; id <= Updates; id++)
                {
                    var row = db.Find<DiffRow>(r => r.Id == id);
                    row.Amount = Math.Round(row.Amount + 10, 2);
                    db.Update(row);
                }

                for (int id = Rows - Deletes + 1; id <= Rows; id++)
                    db.Delete(new DiffRow { Id = id });

                db.Flush();

                var all = db.Select<DiffRow>(0, Rows + 10).ToList();
                var bucket = db.Query<DiffRow>(r => r.Bucket == 7).ToList();
                fingerprint = Fingerprint(all, bucket, db.Count<DiffRow>(), all.Sum(r => r.Amount));
            }

            using (var reopened = new XodContext(path, options: options))
            {
                var all = reopened.Select<DiffRow>(0, Rows + 10).ToList();
                var bucket = reopened.Query<DiffRow>(r => r.Bucket == 7).ToList();
                fingerprint += "\n---\n" + Fingerprint(all, bucket, reopened.Count<DiffRow>(), all.Sum(r => r.Amount));
            }

            return fingerprint;
        }

        private string RunSqlite(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "diff.db");

            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();

            using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TABLE DiffRow (Id INTEGER PRIMARY KEY, Sku TEXT UNIQUE, Bucket INTEGER, Amount REAL);";
                create.ExecuteNonQuery();
                create.CommandText = "CREATE INDEX ix_bucket ON DiffRow (Bucket);";
                create.ExecuteNonQuery();
            }

            using var tx = connection.BeginTransaction();

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO DiffRow (Id, Sku, Bucket, Amount) VALUES ($id, $sku, $bucket, $amount);";
                var pId = insert.Parameters.Add("$id", SqliteType.Integer);
                var pSku = insert.Parameters.Add("$sku", SqliteType.Text);
                var pBucket = insert.Parameters.Add("$bucket", SqliteType.Integer);
                var pAmount = insert.Parameters.Add("$amount", SqliteType.Real);

                foreach (var row in Seed())
                {
                    pId.Value = row.Id;
                    pSku.Value = row.Sku;
                    pBucket.Value = row.Bucket;
                    pAmount.Value = row.Amount;
                    insert.ExecuteNonQuery();
                }
            }

            using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE DiffRow SET Amount = ROUND(Amount + 10, 2) WHERE Id = $id;";
                var pId = update.Parameters.Add("$id", SqliteType.Integer);
                for (int id = 1; id <= Updates; id++)
                {
                    pId.Value = id;
                    update.ExecuteNonQuery();
                }
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM DiffRow WHERE Id = $id;";
                var pId = delete.Parameters.Add("$id", SqliteType.Integer);
                for (int id = Rows - Deletes + 1; id <= Rows; id++)
                {
                    pId.Value = id;
                    delete.ExecuteNonQuery();
                }
            }

            tx.Commit();

            string Read()
            {
                var all = new List<DiffRow>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT Id, Sku, Bucket, Amount FROM DiffRow ORDER BY Id;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        all.Add(new DiffRow
                        {
                            Id = reader.GetInt32(0),
                            Sku = reader.GetString(1),
                            Bucket = reader.GetInt32(2),
                            Amount = reader.GetDouble(3),
                        });
                    }
                }

                var bucket = all.Where(r => r.Bucket == 7).ToList();
                return Fingerprint(all, bucket, all.Count, all.Sum(r => r.Amount));
            }

            string fingerprint = Read();
            connection.Close();

            using (var reopened = new SqliteConnection($"Data Source={path}"))
            {
                reopened.Open();
                var all = new List<DiffRow>();
                using (var cmd = reopened.CreateCommand())
                {
                    cmd.CommandText = "SELECT Id, Sku, Bucket, Amount FROM DiffRow ORDER BY Id;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        all.Add(new DiffRow
                        {
                            Id = reader.GetInt32(0),
                            Sku = reader.GetString(1),
                            Bucket = reader.GetInt32(2),
                            Amount = reader.GetDouble(3),
                        });
                    }
                }

                var bucket = all.Where(r => r.Bucket == 7).ToList();
                fingerprint += "\n---\n" + Fingerprint(all, bucket, all.Count, all.Sum(r => r.Amount));
            }

            return fingerprint;
        }

        [Fact]
        public void The_same_script_gives_the_same_answers_as_sqlite()
        {
            string xod = RunXod("xod");
            string sqlite = RunSqlite("sqlite");
            Assert.Equal(sqlite, xod);
        }

        [Fact]
        public void A_unique_key_violation_behaves_like_sqlite()
        {
            string dir = Path.Combine(tempRoot, "unique");
            Directory.CreateDirectory(dir);
            var options = new DatabaseOptions
            {
                InitialCreate = true,
                EnableIndexing = true,
                StorageFormat = StorageFormat.Xml,
                Durability = DurabilityMode.Buffered,
            };

            using var db = new XodContext(Path.Combine(dir, "unique.xod"), options: options);
            db.InsertRange(Seed());
            db.Flush();

            Assert.ThrowsAny<Exception>(() => db.Insert(new DiffRow { Id = 9001, Sku = "SKU-00001", Bucket = 1, Amount = 1 }));

            using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "unique.db")}");
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE DiffRow (Id INTEGER PRIMARY KEY, Sku TEXT UNIQUE, Bucket INTEGER, Amount REAL);";
            create.ExecuteNonQuery();
            create.CommandText = "INSERT INTO DiffRow (Id, Sku, Bucket, Amount) VALUES (1, 'SKU-00001', 1, 1);";
            create.ExecuteNonQuery();
            create.CommandText = "INSERT INTO DiffRow (Id, Sku, Bucket, Amount) VALUES (9001, 'SKU-00001', 1, 1);";
            Assert.ThrowsAny<Exception>(() => create.ExecuteNonQuery());
        }
    }
}
