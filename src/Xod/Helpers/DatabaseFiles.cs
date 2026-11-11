using System;

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
