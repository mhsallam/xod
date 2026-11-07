using System;
using System.Collections.Generic;

namespace Xod
{
    internal interface ICachedList<T> : IDisposable
    {
        List<T> GetItems();
    }

    internal class ItemCache
    {
        public string Code { get; set; }
        public Type Type { get; set; }
        public object Item { get; set; }
        public bool LazyLoaded { get; set; }
        public string[] IncludedReferenceProperties { get; set; }
        public DateTime LoadTime { get; set; }
        public Guid ReadId { get; set; }
        public Guid ParentReadId { get; set; }
        public bool ParentIsLeaf { get; set; }
    }

    internal class FileCache
    {
        public string FileName { get; set; }
        public XFile File { get; set; }
        public DateTime LastCheckout { get; set; }
        public FileCacheStatus Status { get; set; }
        public long CostBytes { get; set; }
    }

    internal enum FileCacheStatus
    {
        Shared,
        Locked
    }
}
