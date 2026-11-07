using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Xod.Services
{
    /// <summary>
    /// Thread-safe reusable buffer pool for 512 KB database page serialization and deserialization.
    /// </summary>
    internal static class PageBufferPool
    {
        private const int CacheSize = 1024;
        public const int DefaultPageBufferSize = 540672;
        private static readonly ConcurrentQueue<byte[]> SharedPageBufferPool = new();
        private static int sharedPagePoolCount;

        public static byte[] Rent(int minSize)
        {
            int targetSize = Math.Max(minSize, DefaultPageBufferSize);
            while (SharedPageBufferPool.TryDequeue(out byte[] buf))
            {
                Interlocked.Decrement(ref sharedPagePoolCount);
                if (buf.Length >= minSize)
                    return buf;
            }
            return GC.AllocateUninitializedArray<byte>(targetSize);
        }

        public static void Return(byte[] buf)
        {
            if (buf != null && buf.Length >= DefaultPageBufferSize)
            {
                if (Interlocked.Increment(ref sharedPagePoolCount) <= CacheSize)
                {
                    SharedPageBufferPool.Enqueue(buf);
                }
                else
                {
                    Interlocked.Decrement(ref sharedPagePoolCount);
                }
            }
        }
    }
}
