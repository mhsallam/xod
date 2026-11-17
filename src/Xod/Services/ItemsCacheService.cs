using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Xod.Services
{
    /// <summary>
    /// The objects one read builds for the rows it references: the rows of that read that reference one row share
    /// one object, built once. Nothing outlives the read (ISS-138). It used to live as long as the context, cleared
    /// row by row by the writes that remembered to (not a standalone table's), never by another context's write, and
    /// it handed one instance to every reader, so what a caller changed without saving the next read returned.
    /// Keyed by the reference as stored ("page.row"): a row's code alone is unique only within its page, and pages
    /// written by different runs can share one.
    /// <para>
    /// A read is the outermost read on its thread, from the engine's read entry (XmlEngine.EnterReadOperation, the
    /// SQL query) to its exit: <see cref="BeginRead"/> and <see cref="EndRead"/>, paired by those helpers. Nested
    /// reads (a query inside a predicate) share their outer read's objects. A write inside a read is refused on the
    /// read's thread, and another thread's waits for the read to end (ISS-125), except inside a write: a trigger's read
    /// may write (its thread holds the write lock already). A write ends the sharing for the rest of the read it is
    /// made in (<see cref="WriteStarted"/>, from the engine's write entry): the rows the read takes after it are built
    /// afresh. Outside a read nothing is kept, so a read made inside a write (a trigger's) shares only within itself,
    /// and the threads a read lends rows to (a parallel read of a standalone table) keep nothing.
    /// </para>
    /// </summary>
    internal sealed class ItemsCacheService
    {
        private const int CACHE_SIZE = 1024;

        // Per thread: how deep in reads it is, and what its outermost read shares (null: nothing kept).
        private sealed class ReadScope
        {
            public int Depth;
            public Dictionary<(Type Type, string Code), ItemCache> Items;
            public Queue<(Type Type, string Code)> Order;
        }

        // Not disposed with the context: a close can come from inside a read (on its thread, or beside reads on
        // others), which still ends after it (EndRead). Each read empties its own scope as it ends, so nothing is held
        // past it but a depth per thread, and the ThreadLocal's finalizer frees its slots.
        private readonly ThreadLocal<ReadScope> scopes = new ThreadLocal<ReadScope>(() => new ReadScope());

        /// <summary>A read starts on this thread: the outermost one starts sharing afresh.</summary>
        internal void BeginRead()
        {
            var scope = scopes.Value;
            if (scope.Depth++ == 0)
            {
                scope.Items = new Dictionary<(Type Type, string Code), ItemCache>();
                scope.Order = new Queue<(Type Type, string Code)>();
            }
        }

        /// <summary>A read ends on this thread: the outermost one's objects go with it.</summary>
        internal void EndRead()
        {
            var scope = scopes.Value;
            if (--scope.Depth == 0)
            {
                scope.Items = null;
                scope.Order = null;
            }
        }

        /// <summary>A write starts on this thread: whatever its read has shared is no longer the disk's, and the
        /// rest of that read shares nothing.</summary>
        internal void WriteStarted()
        {
            var scope = scopes.Value;
            scope.Items = null;
            scope.Order = null;
        }

        internal object Get(Type type, string code, bool lazyLoad, string[] include)
        {
            if (type == null || string.IsNullOrEmpty(code))
                return null;

            var items = scopes.Value.Items;
            if (items == null || !items.TryGetValue((type, code), out var item))
                return null;

            if (include == null)
                return item.Item;
            if (include.Length == 0)
                return !item.LazyLoaded ? item.Item : null;
            if (item.IncludedReferenceProperties != null &&
                include.All(t => item.IncludedReferenceProperties.Contains(t)))
                return item.Item;

            return null;
        }

        internal void Add(ItemCache cacheItem)
        {
            if (cacheItem?.Type == null || string.IsNullOrEmpty(cacheItem.Code))
                return;

            var scope = scopes.Value;
            var items = scope.Items;
            if (items == null)
                return;

            var key = (cacheItem.Type, cacheItem.Code);
            if (items.ContainsKey(key))
                return;

            while (items.Count >= CACHE_SIZE && scope.Order.Count > 0)
                items.Remove(scope.Order.Dequeue());

            items[key] = cacheItem;
            scope.Order.Enqueue(key);
        }
    }
}
