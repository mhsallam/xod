using System;
using System.IO;
using System.Linq;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-156, two of its defects:
    /// <list type="bullet">
    /// <item>an undefined storage format (<c>(StorageFormat)7</c>, <c>xod convert --format 7</c>) is refused. The
    /// serializer factory took any value it didn't know for XML, so the migrator recorded <c>Format="7"</c> and wrote
    /// XML;</item>
    /// <item>an open refused because there is no database (<c>InitialCreate = false</c>) takes no hold, so it leaves no
    /// lock files in the folder. An open still takes the hold, and recovers, where a crash left something of the
    /// database.</item>
    /// </list>
    /// </summary>
    public class XodApiDefectTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-apidef-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class AdRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        private string NewDir(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private string Seeded(string name)
        {
            string dir = NewDir(name);
            using var db = new XodContext(Path.Combine(dir, "data.xod"));
            db.Insert(new AdRow { Name = "one" });
            return dir;
        }

        private static string[] FilesOf(string dir) => Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();

        [Fact]
        public void The_migrator_refuses_an_undefined_format_and_changes_nothing()
        {
            string dir = Seeded("convert");
            string[] before = FilesOf(dir);
            string target = Path.Combine(tempRoot, "convert-target");

            Assert.Throws<ArgumentOutOfRangeException>(() => XodMigrator.ConvertDatabase(dir, target, (StorageFormat)7));
            Assert.Throws<ArgumentOutOfRangeException>(() => XodMigrator.ConvertDatabase(dir, dir, (StorageFormat)7));

            Assert.False(Directory.Exists(target));
            Assert.Equal(before, FilesOf(dir));
            using var db = new XodContext(Path.Combine(dir, "data.xod"));
            Assert.Equal("one", db.Find<AdRow>(r => r.Id == 1)?.Name);
        }

        [Fact]
        public void A_context_refuses_an_undefined_format_and_takes_no_hold()
        {
            string dir = NewDir("context");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new XodContext(Path.Combine(dir, "data.xod"), null, new DatabaseOptions { StorageFormat = (StorageFormat)7 }));
            Assert.Empty(FilesOf(dir));

            // Into a folder that isn't there: none is made.
            string missing = Path.Combine(tempRoot, "context-missing");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new XodContext(Path.Combine(missing, "data.xod"), null, new DatabaseOptions { StorageFormat = (StorageFormat)7 }));
            Assert.False(Directory.Exists(missing));
        }

        [Fact]
        public void An_open_of_a_missing_database_leaves_no_lock_files()
        {
            string dir = NewDir("missing");
            Assert.Throws<DatabaseFileException>(() =>
                new XodContext(Path.Combine(dir, "data.xod"), null, new DatabaseOptions { InitialCreate = false }));
            Assert.Empty(FilesOf(dir));
        }

        // A leftover the recovery only deletes (an atomic write cut short) is no database either: the open is refused
        // before it holds, so it leaves no lock files, and the leftover stays for an open that creates to tidy. It held,
        // recovered, and was refused after, leaving both lock files.
        [Fact]
        public void An_open_where_only_a_cut_short_write_is_leaves_no_lock_files()
        {
            string dir = NewDir("tmp-only");
            File.WriteAllText(Path.Combine(dir, "data.xod.tmp"), "cut short");
            Assert.Throws<DatabaseFileException>(() =>
                new XodContext(Path.Combine(dir, "data.xod"), null, new DatabaseOptions { InitialCreate = false }));
            Assert.Equal(new[] { "data.xod.tmp" }, FilesOf(dir));
        }

        // What is left of a table, or a password change's record alone, gives no database file back either.
        [Theory]
        [InlineData("T.xtab.xdel")]
        [InlineData("data.xod.xpwlog")]
        [InlineData("data.xod.xpwnew")]
        public void An_open_where_nothing_gives_the_database_file_back_leaves_no_lock_files(string leftover)
        {
            string dir = NewDir("left-" + leftover);
            File.WriteAllText(Path.Combine(dir, leftover), "left");
            Assert.Throws<DatabaseFileException>(() =>
                new XodContext(Path.Combine(dir, "data.xod"), null, new DatabaseOptions { InitialCreate = false }));
            Assert.Equal(new[] { leftover }, FilesOf(dir));
        }

        // A staged database file beside a record that names no commit is counted, and the recovery deletes both: the
        // migrator converts nothing.
        [Fact]
        public void The_migrator_refuses_what_the_recovery_gives_no_file_back_from()
        {
            string dir = NewDir("convert-uncommitted");
            File.WriteAllText(Path.Combine(dir, "data.xod.xpwnew"), "staged");
            File.WriteAllText(Path.Combine(dir, "data.xod.xpwlog"), "no commit");
            string target = Path.Combine(tempRoot, "convert-uncommitted-target");
            Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, target, StorageFormat.Json));
            Assert.False(Directory.Exists(target));
        }

        // The migrator decides as the open does: a database a crash set aside is one it converts (it recovers it under its
        // hold), and a folder holding only a cut-short write holds none, refused without a lock file.
        [Fact]
        public void The_migrator_converts_a_database_set_aside_as_a_context_opens_it()
        {
            string dir = Seeded("convert-set-aside");
            File.Move(Path.Combine(dir, "data.xod"), Path.Combine(dir, "data.xod.xdel"));
            string target = Path.Combine(tempRoot, "convert-set-aside-target");

            Assert.True(XodMigrator.ConvertDatabase(dir, target, StorageFormat.Json) > 0);
            using var db = new XodContext(Path.Combine(target, "data.xod"), null, new DatabaseOptions { InitialCreate = false });
            Assert.Equal("one", db.Find<AdRow>(r => r.Id == 1)?.Name);
        }

        [Fact]
        public void The_migrator_refuses_a_folder_holding_only_a_cut_short_write()
        {
            string dir = NewDir("convert-tmp-only");
            File.WriteAllText(Path.Combine(dir, "data.xod.tmp"), "cut short");
            Assert.ThrowsAny<XodException>(() => XodMigrator.ConvertDatabase(dir, Path.Combine(tempRoot, "convert-tmp-only-target"), StorageFormat.Json));
            Assert.Equal(new[] { "data.xod.tmp" }, FilesOf(dir));
        }

        // What a crash left of the database is still recovered by such an open: here the database file a transaction of
        // an older version set aside.
        [Fact]
        public void An_open_without_creating_still_recovers_a_database_set_aside()
        {
            string dir = Seeded("set-aside");
            File.Move(Path.Combine(dir, "data.xod"), Path.Combine(dir, "data.xod.xdel"));

            using var db = new XodContext(Path.Combine(dir, "data.xod"), null, new DatabaseOptions { InitialCreate = false });
            Assert.Equal("one", db.Find<AdRow>(r => r.Id == 1)?.Name);
        }
    }
}
