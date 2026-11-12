using System;
using System.Collections.Generic;
using System.IO;

namespace Xod.Services
{
    internal partial class IOService
    {
        // ISS-043. An index (.xidx) is behind its pages from the moment a page or table file is written until this
        // context's indexes catch up (XmlEngine.FlushToDisk, when nothing of the context is pending). For that whole
        // time a marker beside the database says so: "<database file>.<id>.xidxdirty", one per context (so none
        // touches another's), held open and locked while the context is, one byte long while its indexes are behind
        // and empty once they have caught up. Changing its length is two cheap calls per flush; creating and deleting
        // it each time cost a writer that flushes after every insert about 0.2 ms a flush on Windows (measured,
        // PLAN-054).
        //
        // A marker that isn't empty and that no context holds is a context that stopped with its indexes behind.
        // While one is there no context trusts an index file (IndexesLeftBehind, asked at each load), and the next
        // open that has the database to itself drops every index (RecoverInterruptedWrites): a derived cache,
        // rebuilt from the pages when next needed (PLAN-046 section 8). Event-based: the time check in
        // IndexService.TryLoadFromDiskCore judged an index by how much newer the pages were, and trusted one written
        // just before a batch a crash kept from catching up.
        internal const string IndexesBehindSuffix = ".xidxdirty";

        private readonly object indexesBehindLock = new object();
        private FileStream indexesBehindMarker;
        private volatile bool indexesBehind;

        /// <summary>
        /// Called before every file mutation (XmlEngine's BeforeFileMutate): the first write of a page or table file
        /// since this context's indexes last caught up marks them behind first, so a crash after the write finds
        /// the mark. A marker that can't be created or marked fails the write: nothing would say the indexes may be
        /// behind. The marker shares nothing while held: that is how others tell a live context's marker from one a
        /// crash left (<see cref="ProbeMarker"/>; on Unix .NET takes flock LOCK_EX for FileShare.None, which needs
        /// the write access the marker is opened with, NFS included: SafeFileHandle.Unix.cs, dotnet/runtime
        /// release/8.0 and 9.0).
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
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                indexesBehindMarker.SetLength(1);
                indexesBehind = true;
            }
        }

        /// <summary>
        /// This context's indexes hold every row its pages do, on disk: the marker is emptied. Called only where
        /// nothing of this context is pending and no write of it can be under way (XmlEngine.FlushToDisk).
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
        /// At the close, before the hold goes: an empty marker is deleted; one still marked stays (a close that
        /// couldn't flush), so no context trusts the indexes until an open alone drops them. A marker that can't be
        /// deleted stays too: empty, it costs nothing.
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

        private enum MarkerState
        {
            Gone,
            Live,
            CaughtUp,
            Behind,
        }

        /// <summary>
        /// What a marker says. Live: its context holds it, so it is open (a probe that opens it with a share of
        /// read and write is refused: on Windows by the sharing check, on Unix because flock LOCK_SH can't pass the
        /// holder's LOCK_EX). Otherwise its context is gone: Behind if the marker isn't empty, CaughtUp if it is. A
        /// marker that can't be opened at all (its permissions) can't be told apart, and counts as Behind unless it
        /// is empty. Where the file system takes no locks (an NFS client without lock support; .NET's file locking
        /// switched off), a live context's marker reads as gone: the indexes are then rebuilt while it is marked,
        /// which costs time, not rows.
        /// </summary>
        private static MarkerState ProbeMarker(string marker)
        {
            FileStream probe;
            try
            {
                probe = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (FileNotFoundException)
            {
                return MarkerState.Gone;
            }
            catch (DirectoryNotFoundException)
            {
                return MarkerState.Gone;
            }
            catch (IOException)
            {
                return MarkerState.Live;
            }
            catch (UnauthorizedAccessException)
            {
                try
                {
                    return new FileInfo(marker).Length == 0 ? MarkerState.CaughtUp : MarkerState.Behind;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    return MarkerState.Behind;
                }
            }
            using (probe)
                return probe.Length == 0 ? MarkerState.CaughtUp : MarkerState.Behind;
        }

        private static string[] Markers(string root) => Directory.GetFiles(root, "*" + IndexesBehindSuffix);

        /// <summary>
        /// A context stopped with its indexes behind its pages, and no open alone has dropped them since: no index
        /// file can be trusted (review of TASK-064: only an open alone and a context without a hold honoured a
        /// marker, so contexts opened while another had the database open trusted a stale index). Asked by every
        /// index load (IndexService.TryLoadFromDiskCore), which then rebuilds the index from the pages. A folder that
        /// can't be listed counts as left behind: nothing tells otherwise.
        /// </summary>
        internal bool IndexesLeftBehind()
        {
            string[] markers;
            try
            {
                markers = Markers(root);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return true;
            }
            // This context's own marker is live; skipping it by name only saves the probe.
            string own = indexesBehindMarker?.Name;
            foreach (string marker in markers)
            {
                if (own != null && string.Equals(marker, own, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ProbeMarker(marker) == MarkerState.Behind)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Recovery's step for the markers, run only with the database to itself: a marker that says its context
        /// stopped with its indexes behind drops every index, then the markers go. What can't be deleted doesn't stop
        /// the open (review of TASK-064: it threw). An index that can't be dropped (held open elsewhere) leaves every
        /// marker in place, so no context trusts an index file while it is there and the next open alone tries
        /// again; a marker that can't be deleted stays (an empty one costs nothing; a marked one keeps the index files
        /// untrusted, each rebuilt from the pages when needed, until it can be). A marker still held is a live
        /// context's (a password change gives up its shared hold before it takes the database alone) and is left.
        /// </summary>
        private static void DropIndexesLeftBehind(string root)
        {
            var left = new List<string>();
            bool behind = false;
            foreach (string marker in Markers(root))
            {
                var state = ProbeMarker(marker);
                if (state == MarkerState.Live || state == MarkerState.Gone)
                    continue;
                behind |= state == MarkerState.Behind;
                left.Add(marker);
            }

            if (behind)
            {
                bool dropped = true;
                foreach (string index in Directory.GetFiles(root, "*.xidx"))
                {
                    try
                    {
                        File.Delete(index);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        dropped = false;
                    }
                }
                if (!dropped)
                    return;
            }

            foreach (string marker in left)
            {
                try
                {
                    File.Delete(marker);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
