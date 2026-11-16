using System;
using System.IO;
using Xod;

namespace Xod.Tests
{
    /// <summary>
    /// An index file replaced under an open context from outside it. No second context can have the database open
    /// (PLAN-064), but a file can still change behind a context's back: a sync, backup or restore tool, a checkout of a
    /// database kept in git, or a context of another user writing a folder this context opened with no hold (it couldn't
    /// create the lock files). Here the replacing file is made as another context made it before: in a copy of the
    /// folder, opened on its own, where <c>rebuild</c> gets the copy's context and the copy's index file (it deletes the
    /// file and reads, so the index is rebuilt from the pages and written whole, every block moved); the file is then
    /// copied over the database's, as such a tool would.
    /// </summary>
    internal static class OutsideReplace
    {
        internal static void Index(string databasePath, Type type, Action<XodContext, string> rebuild)
        {
            string dir = Path.GetDirectoryName(databasePath)!;
            string copy = dir + "-outside-" + Guid.NewGuid().ToString("N")[..6];
            Directory.CreateDirectory(copy);
            // Not the lock files and markers the open context holds: they are its own, and held without sharing.
            foreach (string file in Directory.GetFiles(dir))
                if (!file.EndsWith(".lock", StringComparison.Ordinal) && !file.EndsWith(".xidxdirty", StringComparison.Ordinal))
                    File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));

            string index = type.FullName + ".xidx";
            using (var other = new XodContext(Path.Combine(copy, Path.GetFileName(databasePath))))
                rebuild(other, Path.Combine(copy, index));
            File.Copy(Path.Combine(copy, index), Path.Combine(dir, index), overwrite: true);
        }
    }
}
