using System;
using System.Collections.Generic;
using System.Linq;

namespace Xod.Services
{
    /// <summary>
    /// In direct mode (no batch, no buffer), what each statement under way saved and hasn't written yet, by the thread
    /// running it: written when that statement ends (IOService.WriteSaved, ISS-099). The sets are by thread from when
    /// statements on standalone tables ran side by side (review of TASK-068); every write statement now takes the
    /// engine's write lock (ISS-124), so one set is in use at a time. Each file saved here also waits in the buffer's
    /// sets, so a flush writes it if its statement doesn't (Written). Used under IOService's syncLock.
    /// </summary>
    internal sealed class StatementSaves
    {
        private sealed class Saves
        {
            internal readonly PendingFiles Files = new PendingFiles();
            internal readonly HashSet<string> RawPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal bool Empty => Files.Count == 0 && RawPages.Count == 0;
        }

        private readonly Dictionary<int, Saves> byThread = new Dictionary<int, Saves>();

        private Saves Mine()
        {
            int thread = Environment.CurrentManagedThreadId;
            if (!byThread.TryGetValue(thread, out Saves saves))
                byThread[thread] = saves = new Saves();
            return saves;
        }

        internal void AddFile(XFile file) => Mine().Files.Add(file);

        internal void AddRawPage(string pageFileName) => Mine().RawPages.Add(pageFileName);

        /// <summary>A raw page saved by a statement became a document (opened as one): it stays that statement's.</summary>
        internal void RawPageBecameFile(string pageFileName, XFile file)
        {
            foreach (Saves saves in byThread.Values)
            {
                if (saves.RawPages.Remove(pageFileName))
                    saves.Files.Add(file);
            }
        }

        /// <summary>What the statement on this thread saved, to be written now.</summary>
        internal (List<XFile> Files, List<string> RawPages) OfThisThread()
        {
            if (!byThread.TryGetValue(Environment.CurrentManagedThreadId, out Saves saves))
                return (new List<XFile>(), new List<string>());
            return (saves.Files.ToList(), saves.RawPages.ToList());
        }

        /// <summary>These were written (by a statement's end or a flush): no statement waits to write them.</summary>
        internal void Written(IEnumerable<XFile> files, IEnumerable<string> rawPages)
        {
            var fileList = files as ICollection<XFile> ?? files.ToList();
            var rawList = rawPages as ICollection<string> ?? rawPages.ToList();
            foreach (var kv in byThread.ToList())
            {
                kv.Value.Files.Written(fileList);
                kv.Value.RawPages.ExceptWith(rawList);
                if (kv.Value.Empty)
                    byThread.Remove(kv.Key);
            }
        }

        /// <summary>The file is deleted: no statement writes it.</summary>
        internal void Remove(string name)
        {
            foreach (Saves saves in byThread.Values)
            {
                saves.Files.Remove(name);
                saves.RawPages.Remove(name);
            }
        }

        internal void Clear() => byThread.Clear();
    }
}
