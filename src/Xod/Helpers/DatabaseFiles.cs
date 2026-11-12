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

        /// <summary>Every file of the database: the database file, table files, pages and indexes. Not what a
        /// write or a password change leaves beside them (<c>.tmp</c>, <c>.xwal</c>, <c>.xpwnew</c>,
        /// <c>.xpwlog</c>) nor the lock file.</summary>
        internal static bool IsOfDatabase(string file) =>
            IsDatabaseFile(file) ||
            file.EndsWith(".xtab", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".xpag", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".xidx", StringComparison.OrdinalIgnoreCase);
    }
}
