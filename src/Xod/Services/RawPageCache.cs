using System;
using System.Collections.Generic;

namespace Xod.Services
{
    /// <summary>
    /// A context's raw pages by file name, under <c>IOService.syncLock</c>. <see cref="Put"/> is the only way a page
    /// enters it (ISS-105): from then on a reader can take the page and read its bytes outside the lock, so its array is
    /// never reused: not when it leaves the cache (an eviction, a document read of it, a delete), and not when a writer
    /// moves the page to a larger array. The GC takes it once the last reader drops it. No page's array is pooled
    /// (ISS-212: a pool refilled only by buffers that never entered the cache saved nothing, and only convention kept a
    /// cached page from being marked pooled). Writing into a cached page's array in place still rests on no reader
    /// running beside a write (the engine's write lock).
    /// </summary>
    internal sealed class RawPageCache
    {
        private readonly Dictionary<string, RawPageBuffer> pages = new Dictionary<string, RawPageBuffer>(StringComparer.OrdinalIgnoreCase);

        public int Count => pages.Count;

        public IEnumerable<string> Keys => pages.Keys;

        public bool TryGetValue(string name, out RawPageBuffer page) => pages.TryGetValue(name, out page);

        public bool ContainsKey(string name) => pages.ContainsKey(name);

        /// <summary>Takes the page out. Its bytes stay with it, for any reader holding it.</summary>
        public bool Remove(string name) => pages.Remove(name);

        public void Put(string name, RawPageBuffer page) => pages[name] = page;
    }
}
