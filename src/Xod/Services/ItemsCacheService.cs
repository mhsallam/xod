using System;
using System.Collections.Generic;
using System.Linq;

namespace Xod.Services
{
    internal class ItemsCacheService : IDisposable
    {
        private const int CACHE_SIZE = 1024;
        private readonly object syncLock = new object();
        private readonly Dictionary<(Type Type, string Code), ItemCache> itemsByKey = new();
        private readonly Queue<(Type Type, string Code)> evictionQueue = new(CACHE_SIZE + 16);

        public ItemsCacheService(string path)
        {
        }

        internal object Get(Type type, string code, bool lazyLoad, string[] include)
        {
            if (type == null || string.IsNullOrEmpty(code))
                return null;

            lock (syncLock)
            {
                if (!itemsByKey.TryGetValue((type, code), out var item))
                    return null;

                if (include == null)
                {
                    return item.Item;
                }
                if (include.Length == 0)
                {
                    return !item.LazyLoaded ? item.Item : null;
                }
                if (item.IncludedReferenceProperties != null &&
                    include.All(t => item.IncludedReferenceProperties.Contains(t)))
                {
                    return item.Item;
                }

                return null;
            }
        }

        internal void Add(ItemCache cacheItem)
        {
            if (cacheItem?.Type == null || string.IsNullOrEmpty(cacheItem.Code))
                return;

            cacheItem.LoadTime = DateTime.UtcNow;
            var key = (cacheItem.Type, cacheItem.Code);

            lock (syncLock)
            {
                if (!itemsByKey.ContainsKey(key))
                {
                    while (itemsByKey.Count >= CACHE_SIZE && evictionQueue.Count > 0)
                    {
                        var oldest = evictionQueue.Dequeue();
                        itemsByKey.Remove(oldest);
                    }

                    itemsByKey[key] = cacheItem;
                    evictionQueue.Enqueue(key);

                    if (cacheItem.ParentIsLeaf)
                    {
                        var toRemove = itemsByKey
                            .Where(kv => kv.Value.ReadId == cacheItem.ParentReadId && kv.Value.LazyLoaded)
                            .Select(kv => kv.Key)
                            .ToList();
                        foreach (var k in toRemove)
                            itemsByKey.Remove(k);
                    }
                }
            }
        }

        internal void Clear(Type type, string code)
        {
            if (type == null || string.IsNullOrEmpty(code))
                return;

            lock (syncLock)
            {
                if (itemsByKey.Remove((type, code), out var item))
                {
                    var relations = itemsByKey.Values.Where(s => s.ParentReadId == item.ReadId).ToArray();
                    foreach (var relation in relations)
                        Clear(relation.Type, relation.Code);
                }
            }
        }

        internal void ClearCurrentCache()
        {
            lock (syncLock)
            {
                itemsByKey.Clear();
                evictionQueue.Clear();
            }
        }

        internal void ClearAllCache()
        {
            ClearCurrentCache();
        }

        public void Dispose()
        {
            ClearCurrentCache();
        }
    }
}
