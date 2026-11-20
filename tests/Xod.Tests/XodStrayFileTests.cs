using System;
using System.IO;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-182: a file beside the database that names another database file is none of this database's. What a crash
    /// leaves was matched by its extension alone: the open deleted a stray <c>other.xod.tmp</c> or
    /// <c>other.xod.xpwnew</c>, put a stray <c>other.xod.xdel</c> back as <c>other.xod</c> (a second database file that
    /// every later open refused), replayed a stray committed <c>other.xod.xpwlog</c> into <c>other.xod</c>, read or
    /// deleted any <c>*.xwal</c>, and a password change was refused over any of them. Each is now the database's by its
    /// name: its own file, its tables, pages and indexes, <c>transaction.xwal</c>, and its file's <c>.xpwlog</c>.
    /// </summary>
    public class XodStrayFileTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-stray-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class SfRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        private string Seeded(string name)
        {
            string dir = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "data.xod");
            using (var db = new XodContext(path))
                db.Insert(new SfRow { Name = "one" });
            return path;
        }

        [Fact]
        public void A_stray_tmp_naming_another_database_file_is_left_alone()
        {
            string path = Seeded("tmp");
            string stray = Path.Combine(Path.GetDirectoryName(path)!, "other.xod.tmp");
            File.WriteAllText(stray, "not this database's");

            using (var db = new XodContext(path))
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);

            Assert.True(File.Exists(stray));
            Assert.Equal("not this database's", File.ReadAllText(stray));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "other.xod")));
        }

        [Fact]
        public void A_stray_set_aside_file_naming_another_database_file_is_not_put_back()
        {
            string path = Seeded("xdel");
            string dir = Path.GetDirectoryName(path)!;
            string stray = Path.Combine(dir, "other.xod.xdel");
            File.WriteAllText(stray, "not this database's");

            using (var db = new XodContext(path))
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);
            // A second open: a database file put back beside this one would refuse it.
            using (var db = new XodContext(path))
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);

            Assert.True(File.Exists(stray));
            Assert.False(File.Exists(Path.Combine(dir, "other.xod")));
        }

        // A password change is refused over a write a crash left; a stray beside the database (here put there after
        // the open, which would otherwise leave it alone too) is none.
        [Fact]
        public void A_password_change_goes_ahead_beside_a_stray_tmp()
        {
            string dir = Path.Combine(tempRoot, "password");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "data.xod");
            using (var db = new XodContext(path, "first"))
                db.Insert(new SfRow { Name = "one" });

            using (var db = new XodContext(path, "first"))
            {
                File.WriteAllText(Path.Combine(dir, "other.xod.tmp"), "not this database's");
                db.ChangePassword("first", "second");
            }
            using (var db = new XodContext(path, "second"))
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);
        }

        [Fact]
        public void A_stray_password_change_naming_another_database_file_is_neither_finished_nor_deleted()
        {
            string path = Seeded("xpw");
            string dir = Path.GetDirectoryName(path)!;
            string staged = Path.Combine(dir, "other.xod.xpwnew");
            string record = Path.Combine(dir, "other.xod.xpwlog");
            File.WriteAllText(staged, "not this database's");
            // A commit record as a switch writes it, naming the stray's target.
            File.WriteAllText(record, "XOD password change v1\nother.xod\ncommit\n");

            using (var db = new XodContext(path))
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);
            using (var db = new XodContext(path))
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);

            Assert.True(File.Exists(staged));
            Assert.True(File.Exists(record));
            Assert.False(File.Exists(Path.Combine(dir, "other.xod")));
        }

        // Neither read, undone nor deleted by the open, and no write left for a password change.
        [Fact]
        public void A_stray_journal_of_another_name_is_left_alone()
        {
            string dir = Path.Combine(tempRoot, "xwal");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "data.xod");
            using (var db = new XodContext(path, "first"))
                db.Insert(new SfRow { Name = "one" });
            string stray = Path.Combine(dir, "notes.xwal");
            File.WriteAllText(stray, "not this database's");

            using (var db = new XodContext(path, "first"))
            {
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);
                db.ChangePassword("first", "second");
            }

            Assert.Equal("not this database's", File.ReadAllText(stray));
        }

        // Where names are case-sensitive, a file whose name differs from the database file's only in case is another
        // file: a stray set aside under it is not put back as a second database file. (Where names ignore case it is
        // the database's own file, and this checks nothing: the suite's run on Linux does.)
        [Fact]
        public void A_stray_naming_the_database_file_in_another_case_is_another_file_where_names_are_case_sensitive()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                return;
            string path = Seeded("case");
            string dir = Path.GetDirectoryName(path)!;
            string stray = Path.Combine(dir, "DATA.xod.xdel");
            File.WriteAllText(stray, "not this database's");

            using (var db = new XodContext(path))
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);
            using (var db = new XodContext(path))
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);

            Assert.True(File.Exists(stray));
            Assert.False(File.Exists(Path.Combine(dir, "DATA.xod")));
        }

        // Opened by a path in another case (where the volume ignores case), a password change goes through, and the old
        // password opens nothing. (The database file is the database's because it is there, DatabaseFiles.IsOfDatabase;
        // this passes here with names compared exactly too, so it checks the behaviour, not that rule.)
        [Fact]
        public void A_password_change_through_a_path_spelled_in_another_case_takes_the_database_file()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                return;
            string dir = Path.Combine(tempRoot, "spelling");
            Directory.CreateDirectory(dir);
            using (var db = new XodContext(Path.Combine(dir, "data.xod"), "first"))
                db.Insert(new SfRow { Name = "one" });

            using (var db = new XodContext(Path.Combine(dir, "DATA.xod"), "first"))
                db.ChangePassword("first", "second");

            using (var db = new XodContext(Path.Combine(dir, "data.xod"), "second"))
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);
            // Every file went over, the database file among them: the old password opens nothing.
            Assert.ThrowsAny<Exception>(() =>
            {
                using var db = new XodContext(Path.Combine(dir, "data.xod"), "first");
                db.Find<SfRow>(r => r.Id == 1);
            });
            Assert.Empty(Directory.GetFiles(dir, "*.xpwnew"));
        }

        // A leftover whose suffix is in another case is recovered as the check of what is left reports it: the recovery
        // listed by a glob, whose case rule is the platform's, and on Linux missed what the check reported, so every
        // password change was refused. (Where names ignore case, the glob matched it too.)
        [Fact]
        public void A_leftover_with_its_suffix_in_capitals_is_recovered_and_no_longer_blocks_a_password_change()
        {
            string dir = Path.Combine(tempRoot, "capitals");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "data.xod");
            using (var db = new XodContext(path, "first"))
                db.Insert(new SfRow { Name = "one" });
            string left = Path.Combine(dir, "cut-short.xpag.TMP");
            File.WriteAllText(left, "cut short");

            using (var db = new XodContext(path, "first"))
                db.ChangePassword("first", "second");

            Assert.False(File.Exists(left));
        }

        // The recovery and its check take the database's file: a folder would have them read its parent.
        [Fact]
        public void The_check_of_what_a_crash_left_refuses_a_folder()
        {
            Directory.CreateDirectory(tempRoot);
            Assert.Throws<ArgumentException>(() => Xod.Services.IOService.WriteLeft(tempRoot));
            Assert.Throws<ArgumentException>(() => Xod.Services.IOService.RecoverInterruptedWrites(tempRoot));
        }

        // The database's own: a write of its file cut short is still recovered (deleted).
        [Fact]
        public void A_tmp_of_the_database_s_own_file_is_still_recovered()
        {
            string path = Seeded("own");
            string own = path + ".tmp";
            File.WriteAllText(own, "cut short");

            using (var db = new XodContext(path))
                Assert.Equal("one", db.Find<SfRow>(r => r.Id == 1)?.Name);

            Assert.False(File.Exists(own));
        }
    }
}
