using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xod;
using Xunit;

namespace Xod.Tests
{
    // The count of key derivations is process-wide: this collection runs beside no other (xunit 2.9.2,
    // CollectionDefinitionAttribute.DisableParallelization).
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class KeyDerivationCollection
    {
        public const string Name = "Key derivations";
    }

    /// <summary>
    /// ISS-152: with a password, every file write drew a new salt and derived its key afresh (100,000 rounds of PBKDF2),
    /// and kept it in a process-wide cache never emptied: memory grew at every write, and the password's derived keys
    /// stayed in the process after the database was closed. Now a context derives one key for its writes, and one per
    /// salt it reads, in a cache it owns. The file format is unchanged: each file still carries its salt.
    /// </summary>
    [Collection(KeyDerivationCollection.Name)]
    public class XodFileKeyTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-filekey-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class FkRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        private static long Derivations() =>
            (long)typeof(XodContext).Assembly.GetType("Xod.Helpers.CryptoHelper")!
                .GetField("Derivations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetValue(null)!;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_context_derives_its_write_key_once(bool indexing)
        {
            string dir = Path.Combine(tempRoot, indexing ? "indexed" : "unindexed");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "fk.xod");
            using (var db = new XodContext(path, "pw", new DatabaseOptions { EnableIndexing = indexing }))
                db.Insert(new FkRow { Name = "seed" });

            using (var db = new XodContext(path, "pw", new DatabaseOptions { EnableIndexing = indexing }))
            {
                long before = Derivations();
                for (int i = 0; i < 40; i++)
                    db.Insert(new FkRow { Name = "n" + i });
                Assert.Equal(41, db.Count<FkRow>());
                long derived = Derivations() - before;
                // The write key, and a key for each salt the files read carry: the seed's context wrote them with its own.
                Assert.InRange(derived, 1, 4);
            }

            using (var db = new XodContext(path, "pw", new DatabaseOptions { EnableIndexing = indexing }))
                Assert.Equal(41, db.Select<FkRow>().Count());
        }

        // The keys are the context's: a new context derives the key of a file it reads, where a cache shared across
        // contexts would hand it over and keep it past their close.
        [Fact]
        public void A_new_context_derives_the_keys_of_the_files_it_reads()
        {
            string dir = Path.Combine(tempRoot, "second");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "fk.xod");
            using (var db = new XodContext(path, "pw", new DatabaseOptions { EnableIndexing = false }))
                db.Insert(new FkRow { Name = "seed" });

            long before = Derivations();
            using (var db = new XodContext(path, "pw", new DatabaseOptions { EnableIndexing = false }))
            {
                Assert.Equal("seed", db.Select<FkRow>().Single().Name);
                Assert.True(Derivations() > before, "the second context read the file with a key it didn't derive");
            }
        }

        // AES-GCM with random nonces allows at most 2^32 encryptions under one key: a write key is replaced after its
        // bound, and what each key wrote still reads.
        [Fact]
        public void A_write_key_is_replaced_after_its_bound_and_every_file_still_reads()
        {
            var keys = new Xod.Helpers.FileKeys("pw", writesPerKey: 3);
            var salts = Enumerable.Range(0, 7).Select(_ => Convert.ToBase64String(keys.ForWrite().Salt)).ToArray();
            Assert.Equal(3, salts.Distinct().Count());
            Assert.Single(salts.Take(3).Distinct());

            var written = Enumerable.Range(0, 7).Select(i => keys.Encrypt(new[] { (byte)i })).ToArray();
            var reader = new Xod.Helpers.FileKeys("pw");
            for (int i = 0; i < written.Length; i++)
                Assert.Equal(new[] { (byte)i }, reader.Decrypt(written[i]));
        }

        // A conversion writes every file under one key: it derived two per file, a salt per file.
        [Fact]
        public void A_conversion_derives_one_write_key()
        {
            string dir = Path.Combine(tempRoot, "convert");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "fk.xod");
            using (var db = new XodContext(path, "pw", new DatabaseOptions { EnableIndexing = true }))
                for (int i = 0; i < 5; i++)
                    db.Insert(new FkRow { Id = i + 1, Name = "n" + i });
            int files = Directory.GetFiles(dir).Count(f => !f.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
            Assert.True(files >= 3, "files: " + files);

            long before = Derivations();
            Assert.Equal(files, XodMigrator.ConvertDatabase(dir, dir, StorageFormat.Binary, "pw"));
            // The key of the one salt the files were written under, and the conversion's write key.
            Assert.Equal(2, Derivations() - before);

            using (var db = new XodContext(path, "pw"))
                Assert.Equal(5, db.Count<FkRow>());
        }
    }
}
