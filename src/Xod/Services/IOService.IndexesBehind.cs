using System;
using System.IO;
using System.Linq;

namespace Xod.Services
{
    internal partial class IOService
    {
        // ISS-043. An index (.xidx) is behind its pages from the moment a page or table file is written until this
        // context's indexes catch up (XmlEngine.FlushToDisk under the engine-wide write lock). For that whole time
        // a marker beside the database says so: "<database file>.<id>.xidxdirty", one per context (so none touches
        // another's), held open while the context is, one byte long while its indexes are behind and empty once
        // they have caught up. Changing its length is two cheap calls per flush; creating and deleting it each time
        // cost a writer that flushes after every insert about 0.2 ms a flush on Windows (measured, PLAN-054).
        //
        // An open that holds the database alone (RecoverInterruptedWrites) and finds a marker that isn't empty
        // knows a context stopped with its indexes behind, and drops every index: a derived cache, rebuilt from the
        // pages when next needed (PLAN-046 section 8). Event-based: the time check in
        // IndexService.TryLoadFromDiskCore judged an index by how much newer the pages were, and trusted one written
        // just before a batch a crash kept from catching up. It stays for databases written by versions without
        // the marker.
        internal const string IndexesBehindSuffix = ".xidxdirty";

        private readonly object indexesBehindLock = new object();
        private FileStream indexesBehindMarker;
        private volatile bool indexesBehind;

        /// <summary>
        /// The index files may be behind their pages and this context can't drop them: it holds no lock, so it
        /// can't tell a marker a crash left from a live context's (OpenAndRecover). It then reads no index file,
        /// and rebuilds each index it needs from the pages, in memory only (it writes nothing).
        /// </summary>
        internal bool IndexFilesUntrusted { get; private set; }

        /// <summary>
        /// Called before every file mutation (XmlEngine's BeforeFileMutate): the first write of a page or table file
        /// since this context's indexes last caught up marks them behind first, so a crash after the write finds
        /// the mark. A marker that can't be created or marked fails the write: nothing would say the indexes may be
        /// behind.
        /// </summary>
        internal void NoteFileMutation(string filePath)
        {
            if (indexesBehind || !IsTableOrPage(filePath))
                return;
            lock (indexesBehindLock)
            {
                if (indexesBehind)
                    return;
                indexesBehindMarker ??= new FileStream(path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + IndexesBehindSuffix,
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
                indexesBehindMarker.SetLength(1);
                indexesBehind = true;
            }
        }

        /// <summary>
        /// This context's indexes hold every row its pages do, on disk: the marker is emptied. Called only where no
        /// write of this context can be under way (the engine-wide write lock, or the close).
        /// </summary>
        internal void IndexesCaughtUp()
        {
            lock (indexesBehindLock)
            {
                if (!indexesBehind)
                    return;
                indexesBehindMarker.SetLength(0);
                indexesBehind = false;
            }
        }

        /// <summary>
        /// At the close, before the hold goes: an empty marker is deleted; one still marked stays for the next open
        /// alone, which drops the indexes (a close that couldn't flush). A marker that can't be deleted stays too:
        /// empty, it costs that open only its deletion.
        /// </summary>
        private void CloseIndexesMarker()
        {
            lock (indexesBehindLock)
            {
                if (indexesBehindMarker == null)
                    return;
                string marker = indexesBehindMarker.Name;
                indexesBehindMarker.Dispose();
                indexesBehindMarker = null;
                if (indexesBehind)
                    return;
                try
                {
                    File.Delete(marker);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }

        private static bool IsTableOrPage(string filePath) =>
            filePath != null &&
            (filePath.EndsWith(".xpag", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".xtab", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Recovery's step for the markers: one that isn't empty is a context that stopped with its indexes behind
        /// its pages, so every index goes; then the markers (an index that can't be deleted stops the open: it can't
        /// be trusted, nor dropped). Run only with the database to itself, as every recovery step.
        /// </summary>
        private static void DropIndexesLeftBehind(string root)
        {
            var markers = Directory.GetFiles(root, "*" + IndexesBehindSuffix).Select(m => new FileInfo(m)).ToList();
            if (markers.Count == 0)
                return;
            if (markers.Any(m => m.Length > 0))
            {
                foreach (string index in Directory.GetFiles(root, "*.xidx"))
                    File.Delete(index);
            }
            foreach (var marker in markers)
                marker.Delete();
        }
    }
}
