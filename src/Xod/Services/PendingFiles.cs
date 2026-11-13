using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Xod.Services
{
    /// <summary>
    /// Whole files waiting to be written (a page or table as a document, an encrypted index): one write per file,
    /// known by the file's name, of the document last saved for it, the one the cache then holds
    /// (IOService.CacheSavedUnlocked). Found by TASK-068's suite: the sets held the documents themselves, so a second
    /// document of one file (an encrypted index is saved as a new document each time) made a second write of that
    /// file, side by side with the first when enough files waited (IOService.FlushFilesList): the two collided on the
    /// file's .tmp, or the older document landed last. Used under IOService's syncLock.
    /// </summary>
    internal sealed class PendingFiles
    {
        private readonly Dictionary<string, XFile> byName = new Dictionary<string, XFile>(StringComparer.OrdinalIgnoreCase);

        private static string NameOf(XFile file) => Path.GetFileName(file.Path);

        internal int Count => byName.Count;

        /// <summary>
        /// The file is to be written with this document, in place of any other document of it waiting. True when no
        /// write of the file waited.
        /// </summary>
        internal bool Add(XFile file)
        {
            string name = NameOf(file);
            bool first = !byName.ContainsKey(name);
            byName[name] = file;
            return first;
        }

        /// <summary>A write of this file waits, with whichever document.</summary>
        internal bool Contains(XFile file) => byName.ContainsKey(NameOf(file));

        /// <summary>
        /// Takes out the write of this document. False when the file waits with another document, or not at all.
        /// </summary>
        internal bool Remove(XFile file)
        {
            string name = NameOf(file);
            if (!byName.TryGetValue(name, out XFile waiting) || !ReferenceEquals(waiting, file))
                return false;
            byName.Remove(name);
            return true;
        }

        /// <summary>Takes out the write of the file of this name, with whichever document (it is deleted).</summary>
        internal void Remove(string name) => byName.Remove(name);

        /// <summary>These documents were written: their writes are out, but not a file's saved again since.</summary>
        internal void Written(IEnumerable<XFile> files)
        {
            foreach (XFile file in files)
                Remove(file);
        }

        /// <summary>These documents weren't written: they wait again, but not over a file's saved again since.</summary>
        internal void PutBack(IEnumerable<XFile> files)
        {
            foreach (XFile file in files)
                byName.TryAdd(NameOf(file), file);
        }

        internal List<XFile> ToList() => byName.Values.ToList();

        internal void Clear() => byName.Clear();
    }
}
