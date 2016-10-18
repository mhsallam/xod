using System;
using System.Collections.Generic;
using System.Linq;

namespace Xod.Services
{
    //shared service: thread-safe for multiple databases and connections
    internal class ItemsCacheService: ICachedList<ItemCache>
    {
        private const int CACHE_SIZE = 512;
        private static readonly object locker = new object();
        private static Dictionary<string, object> lockers = null;
        private static Dictionary<string, List<ItemCache>> items = null;
        private static int instances = 0;

        string path = null;

        public ItemsCacheService(string path)
        {
            this.path = path.ToLower();

            //double-checking lock pattern; optimized thread-safe
            if (items == null)
            {
                lock (locker)
                {
                    if (items == null)
                    {
                        items = new Dictionary<string, List<ItemCache>>();
                        lockers = new Dictionary<string, object>();
                    }
                }
            }

            if (!items.ContainsKey(this.path))
            {
                lock (locker)
                {
                    if (!items.ContainsKey(this.path))
                    {
                        items.Add(this.path, new List<ItemCache>());
                        lockers.Add(this.path, new object());
                    }
                }
            }
            instances++;
        }

        public List<ItemCache> GetItems()
        {
            //no need for lock(): this is index-based item fitching, and the
            //item existance is garanteed through the life of this service instance
            return items[this.path];
        }

        int iii = 0;
        private bool Contains(ItemCache item, string[] include)
        {
            lock(GetLock())
            {
                System.Diagnostics.Debug.WriteLine(++iii);
                if (include == null)
                    return this.GetItems().Any(s => s.Type == item.Type && s.Code == item.Code);
                else if(include.Length == 0)
                    return this.GetItems().Any(s => s.Type == item.Type && s.Code == item.Code && !s.LazyLoaded);
                else
                    return this.GetItems().Any(s =>
                    s.Type == item.Type && s.Code == item.Code && (
                    s.IncludedReferenceProperties != null && include.All(t => s.IncludedReferenceProperties.Contains(t))));

                //(s.LazyLoaded == item.LazyLoaded || (s.IncludedReferenceProperties == null && include == null) ||
                //(item.IncludedReferenceProperties != null && include != null && item.IncludedReferenceProperties.All(t => include.Contains(t)))));
            }
        }

        private object GetLock()
        {
            //no need for lock(): this is index-based item fitching, and the
            //item existance is garanteed through the life of this service instance
            return lockers[this.path];
        }
        
        internal object Get(Type type, string code, bool lazyLoad, string[] include)
        {
            lock(GetLock())
            {
                ItemCache item = null;
                if (include == null)
                    item = this.GetItems().FirstOrDefault(s => s.Type == type && s.Code == code);
                else if(include.Length == 0)
                    item = this.GetItems().FirstOrDefault(s => s.Type == type && s.Code == code && !s.LazyLoaded);
                else
                    item = this.GetItems().FirstOrDefault(s =>
                    s.Type == type && s.Code == code && (
                    s.IncludedReferenceProperties != null && include.All(t => s.IncludedReferenceProperties.Contains(t))));

                if (item != null)
                    return item.Item;
            }
            return null;
        }

        internal void Add(ItemCache cacheItem)//Type type, string code, object item, bool lazyLoaded, ItemCacheLoadType requestType = ItemCacheLoadType.Unspecified)
        {
            if (cacheItem == null)
                return;

            cacheItem.LoadTime = DateTime.Now;

            //double-checking lock pattern; optimized thread-safe
            if (!this.Contains(cacheItem, null))
            {
                lock (GetLock())
                {
                    if (!this.Contains(cacheItem, null))
                    {
                        this.GetItems().Add(cacheItem);
                        if(cacheItem.ParentIsLeaf)
                            this.GetItems().RemoveAll(s => s.ReadId == cacheItem.ParentReadId && s.LazyLoaded == true);
                    }
                }
            }
        }

        internal void Clear(Type type, string code)
        {
            lock (GetLock())
            {
                var item = this.GetItems().FirstOrDefault(s => s.Type == type && s.Code.Equals(code));
                if(item != null)
                {
                    var relations = this.GetItems().Where(s => s.ParentReadId.Equals(item.ReadId)).ToArray();
                    foreach (var relation in relations)
                        Clear(relation.Type, relation.Code);

                    this.GetItems().Remove(item);
                }
            }
        }

        internal void ClearCurrentCache()
        {
            lock (GetLock())
            {
                items[this.path] = new List<ItemCache>();
            }
        }

        internal void ClearAllCache()
        {
            lock (GetLock())
            {
                this.GetItems().Clear();
            }
        }

        public void Dispose()
        {
            instances--;

            if(instances <= 0)
            {
                items = null;
                lockers = null;
                instances = 0;
            }
        }
    }
}
