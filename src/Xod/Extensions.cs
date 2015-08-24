using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.IO.Compression;

namespace Xod
{
    public static class CollectionExtensions
    {
        public static void Add<TSource>(this IList<TSource> source, object item)
        {
            source.Add((TSource)item);
        }
        public static void AddRange<TSource>(this IList<TSource> source, object[] items)
        {
            foreach (var item in items)
            {
                source.Add(Convert.ChangeType(item, typeof(TSource)));
            }
        }
        public static void AddRangeFromString<TSource>(this IList<TSource> source, object[] items)
        {
            foreach (var item in items)
            {
                source.Add((TSource)System.ComponentModel.TypeDescriptor.GetConverter(typeof(TSource)).ConvertFromString(item.ToString()));
            }
        }

        public static bool DictionaryEqual<TKey, TValue>(
            this IDictionary<TKey, TValue> first, IDictionary<TKey, TValue> second)
        {
            if (first == second) return true;
            if ((first == null) || (second == null)) return false;
            if (first.Count != second.Count) return false;

            var comparer = EqualityComparer<TValue>.Default;

            foreach (KeyValuePair<TKey, TValue> kvp in first)
            {
                TValue secondValue;
                if (!second.TryGetValue(kvp.Key, out secondValue)) return false;
                if (!comparer.Equals(kvp.Value, secondValue)) return false;
            }
            return true;
        }
    }

    public static class NumericOperations
    {
        public static T Sum<T>(object[] args)
        {
            dynamic value = default(T);
            foreach (var arg in args)
            {
                value += (T)arg;
            }
            return value;
        }
    }

    public static class StringExtensions
    {
        public static string Compress(this string data)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(data);

            MemoryStream ms = new MemoryStream();
            using (GZipStream zip = new GZipStream(ms, CompressionMode.Compress, true))
            {
                zip.Write(buffer, 0, buffer.Length);
            }

            ms.Position = 0;
            MemoryStream outStream = new MemoryStream();
            byte[] compressed = new byte[ms.Length];
            ms.Read(compressed, 0, compressed.Length);

            byte[] gzBuffer = new byte[compressed.Length + 4];
            System.Buffer.BlockCopy(compressed, 0, gzBuffer, 4, compressed.Length);
            System.Buffer.BlockCopy(BitConverter.GetBytes(buffer.Length), 0, gzBuffer, 0, 4);

            return Convert.ToBase64String(gzBuffer);
        }
        public static string Decompress(this string data)
        {
            byte[] gzBuffer = Convert.FromBase64String(data);

            using (MemoryStream ms = new MemoryStream())
            {
                int dataLength = BitConverter.ToInt32(gzBuffer, 0);
                ms.Write(gzBuffer, 4, gzBuffer.Length - 4);
                byte[] buffer = new byte[dataLength];
                ms.Position = 0;

                using (GZipStream zip = new GZipStream(ms, CompressionMode.Decompress))
                    zip.Read(buffer, 0, buffer.Length);

                return Encoding.UTF8.GetString(buffer);
            }
        }
    }

    public static class TypeExtentions
    {
        public static Type ActualType<T>(T param)
        {
            return typeof(T);
        }
        public static Type GetActualType(this object param)
        {
            if (null != param)
                return (Type)typeof(TypeExtentions).GetMethod("ActualType").MakeGenericMethod(new[] { param.GetType() }).Invoke(null, new[] { param });
            else
                return null;
        }
        public static bool ArraysEqual<T>(T[] a1, T[] a2)
        {
            if (ReferenceEquals(a1, a2))
                return true;

            if (a1 == null || a2 == null)
                return false;

            if (a1.Length != a2.Length)
                return false;

            EqualityComparer<T> comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < a1.Length; i++)
            {
                if (!comparer.Equals(a1[i], a2[i])) return false;
            }
            return true;
        }

        public static bool ListsEqual<T>(List<T> l1, List<T> l2)
        {
            if (ReferenceEquals(l1, l2))
                return true;

            if (l1 == null || l2 == null)
                return false;

            if (l1.Count != l2.Count)
                return false;

            EqualityComparer<T> comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < l1.Count; i++)
            {
                if (!comparer.Equals(l1[i], l2[i])) return false;
            }
            return true;
        }
    }
}
