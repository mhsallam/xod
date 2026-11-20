using System;
using System.IO;
using System.Linq;

namespace Xod.Helpers
{
    /// <summary>Which files in a database's folder are the database's (PLAN-046 section 2). The folder is the
    /// database: tables are named by type, so a second database there would share them.</summary>
    internal static class DatabaseFiles
    {
        /// <summary>The database file itself: <c>.xod</c>, <c>.xodj</c> or <c>.xodb</c>.</summary>
        internal static bool IsDatabaseFile(string file) =>
            file.EndsWith(".xod", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".xodj", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".xodb", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The database a path names, decided here only (a context, the password service and the migrator ask): a
        /// path ending in .xod, .xodj or .xodb names that file; any other path names a folder, whose database file
        /// is the one in it, or <c>data.xod</c> for a new database. A folder holds one database (PLAN-046 section
        /// 2): every database file in it shares its tables, while each has its own lock file, so contexts opened
        /// through two of them wouldn't see each other, and one would write the tables under a password change or a
        /// conversion holding the other (ISS-085). So a folder holding two is refused, and so is a path naming a
        /// new database file beside one (a mistyped name, or a case that differs on a case-sensitive file system).
        /// This runs before the hold, whose lock files it names, and again under it (<see cref="Recheck"/>).
        /// </summary>
        internal static (string File, string Folder) Resolve(string path)
        {
            bool namesFile = IsDatabaseFile(path);
            string file = namesFile ? Path.GetFullPath(path) : null;
            string folder = namesFile ? Path.GetDirectoryName(file) : Path.GetFullPath(path);

            // By the database file's own extensions, not "*.xod*", which also matches what a write or a password
            // change leaves beside it ("pw.xod.tmp", "pw.xod.xpwnew", "pw.xod.lock").
            string[] present = Directory.Exists(folder)
                ? Directory.GetFiles(folder).Where(IsDatabaseFile).OrderBy(f => f, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

            if (present.Length > 1)
                throw new XodException($"The folder '{folder}' holds more than one database file ({string.Join(", ", present.Select(Path.GetFileName))}). " +
                    "A folder holds one database: every database file in it shares its tables, and contexts opened through different ones " +
                    "wouldn't see each other. Move all but one to folders of their own.");

            if (file == null)
                return (present.Length == 1 ? present[0] : Path.Combine(folder, "data.xod"), folder);

            // File.Exists matches as the file system does: a name differing only in case is the same file where
            // names ignore case, and another file where they don't.
            if (present.Length == 1 && !File.Exists(file))
                throw new XodException($"The folder '{folder}' already holds the database '{Path.GetFileName(present[0])}'; " +
                    $"'{Path.GetFileName(file)}' would be a second database file sharing its tables. Open '{Path.GetFileName(present[0])}' " +
                    "(or the folder), or create the new database in a folder of its own.");

            return (file, folder);
        }

        /// <summary>
        /// The check of <see cref="Resolve"/> again, once a hold on <paramref name="resolvedFile"/> is taken and before
        /// anything is recovered, created or converted: refused unless <paramref name="path"/> still names that file.
        /// <see cref="Resolve"/> ran before the hold, so another context could have created a database file in the
        /// folder since (ISS-096): two opens naming different new files in an empty folder both passed it. Under a
        /// hold the answer stands, since a database file is created only with the folder held alone
        /// (IOService.OpenAndRecover), which any hold keeps out, by every context of this release; XOD 2.0.0 and 2.0.1
        /// take no folder hold.
        /// </summary>
        internal static void Recheck(string path, string resolvedFile)
        {
            string now = Resolve(path).File;
            if (!string.Equals(now, resolvedFile, StringComparison.Ordinal))
                throw new XodException($"The folder '{Path.GetDirectoryName(now)}' now holds the database '{Path.GetFileName(now)}', " +
                    $"created by another context while this one was opening '{path}'. Open it again.");
        }

        /// <summary>The transaction journal's name, in the database's folder (the same since 2.0.0).</summary>
        internal const string JournalName = "transaction.xwal";

        // ISS-182. What a crash leaves is the database's only if it is of the database's own files: the open recovers it
        // (deletes, undoes, puts back or finishes), and a password change is refused over it. Each was matched by its
        // extension alone, so a stray beside the database that names another database file was taken for this one's: the
        // open deleted other.xod.tmp and other.xod.xpwnew, put other.xod.xdel back as other.xod (a second database file
        // every later open refused), replayed a stray committed other.xod.xpwlog into other.xod, and read or deleted any
        // *.xwal. These predicates are the one test, for the recovery, for the check of what is left (IOService.WriteLeft),
        // and for the files a password change or a conversion takes.

        /// <summary>
        /// Every file of the database whose file is <paramref name="databaseFile"/>: that file, table files, pages and
        /// indexes. Not what a write or a password change leaves beside them nor the lock file.
        /// <para>
        /// A database file is this database's when it is there, or when it is the path itself. The file system decides,
        /// not a comparison of names: a folder holds one database file (<see cref="Resolve"/>, checked again under the
        /// hold, <see cref="Recheck"/>), so the one there is this one, however the path spells it (a case the volume
        /// ignores, a Unicode form it folds, a short name). A name compared by a rule guessed from the platform missed
        /// it wherever the volume's rule differed, and a password change or a conversion then left the database file
        /// out (the review of TASK-106). One that isn't there is another database's, unless it is the path itself: the
        /// first write of a new database's file, cut short before the file was there.
        /// </para>
        /// </summary>
        internal static bool IsOfDatabase(string file, string databaseFile) =>
            IsDatabaseFile(file)
                ? File.Exists(file) || string.Equals(Path.GetFullPath(file), Path.GetFullPath(databaseFile), StringComparison.Ordinal)
                : file.EndsWith(".xtab", StringComparison.OrdinalIgnoreCase) ||
                  file.EndsWith(".xpag", StringComparison.OrdinalIgnoreCase) ||
                  file.EndsWith(".xidx", StringComparison.OrdinalIgnoreCase);

        /// <summary>The transaction journal (<see cref="JournalName"/>, as a commit names it).</summary>
        internal static bool IsJournal(string file) => string.Equals(Path.GetFileName(file), JournalName, StringComparison.Ordinal);

        /// <summary>An atomic write cut short: "&lt;file&gt;.tmp" of a file of the database or of the journal.</summary>
        internal static bool IsAtomicWrite(string file, string databaseFile)
        {
            if (!file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                return false;
            string target = file.Substring(0, file.Length - 4);
            return IsJournal(target) || IsOfDatabase(target, databaseFile);
        }

        /// <summary>A file of the database an older version's transaction set aside ("&lt;file&gt;.xdel").</summary>
        internal static bool IsSetAside(string file, string databaseFile) =>
            file.EndsWith(Services.IOService.SetAsideSuffix, StringComparison.OrdinalIgnoreCase) &&
            IsOfDatabase(file.Substring(0, file.Length - Services.IOService.SetAsideSuffix.Length), databaseFile);

        /// <summary>A password change's commit record: "&lt;database file&gt;.xpwlog", the one a switch writes.</summary>
        internal static bool IsPasswordRecord(string file, string databaseFile)
        {
            if (!file.EndsWith(Services.PasswordSwitch.JournalSuffix, StringComparison.OrdinalIgnoreCase))
                return false;
            string target = file.Substring(0, file.Length - Services.PasswordSwitch.JournalSuffix.Length);
            return IsDatabaseFile(target) && IsOfDatabase(target, databaseFile);
        }

        /// <summary>A file of the database a password change staged ("&lt;file&gt;.xpwnew").</summary>
        internal static bool IsStaged(string file, string databaseFile) =>
            file.EndsWith(Services.PasswordSwitch.StagedSuffix, StringComparison.OrdinalIgnoreCase) &&
            IsOfDatabase(file.Substring(0, file.Length - Services.PasswordSwitch.StagedSuffix.Length), databaseFile);

        /// <summary>
        /// Whether, among <paramref name="files"/> (a folder's), a crash left something the recovery gives the database
        /// file back from: the transaction journal (a commit's copy of what it replaced); the database file an older
        /// version's transaction set aside; the database file a password change staged, beside its commit record. Not an
        /// atomic write cut short (deleted: as if its write hadn't started), what is left of a table, a page or an index
        /// (the database file isn't among it), nor a staged file without a record (a change that never committed is
        /// deleted). Asked by <see cref="Services.IOService.HoldsDatabase"/>, before anything holds the database.
        /// </summary>
        internal static bool GivesBackDatabaseFile(IReadOnlyCollection<string> files, string databaseFile)
        {
            static string Without(string file, string suffix) => file.Substring(0, file.Length - suffix.Length);
            bool record = files.Any(f => IsPasswordRecord(f, databaseFile));
            return files.Any(f =>
                IsJournal(f) ||
                (IsSetAside(f, databaseFile) && IsDatabaseFile(Without(f, Services.IOService.SetAsideSuffix))) ||
                (record && IsStaged(f, databaseFile) && IsDatabaseFile(Without(f, Services.PasswordSwitch.StagedSuffix))));
        }

        /// <summary>
        /// Refuses a path that doesn't name a database file, where one is the parameter: the recovery and the check of
        /// what is left take the database's file, not its folder, whose parent they would read.
        /// </summary>
        internal static void RequireDatabaseFile(string databaseFile)
        {
            if (string.IsNullOrEmpty(databaseFile) || !IsDatabaseFile(databaseFile))
                throw new ArgumentException("A database file (.xod, .xodj or .xodb) is needed here, not '" + databaseFile + "'.", nameof(databaseFile));
        }
    }
}
