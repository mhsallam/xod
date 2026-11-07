using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Xod.Helpers;

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
            Type targetType = typeof(TSource);
            foreach (var item in items)
            {
                if (item == null)
                {
                    source.Add(default);
                }
                else if (targetType.IsInstanceOfType(item))
                {
                    source.Add((TSource)item);
                }
                else
                {
                    source.Add((TSource)ValueHelper.ParseScalar(targetType, item.ToString()));
                }
            }
        }

        public static void AddRangeFromString<TSource>(this IList<TSource> source, object[] items)
        {
            Type type = typeof(TSource);
            foreach (var item in items)
            {
                if (item == null)
                {
                    source.Add(default);
                }
                else
                {
                    source.Add((TSource)ValueHelper.ParseScalar(type, item.ToString()));
                }
            }
        }

        public static void AddRangeFomString<TSource>(this IList<TSource> source, object[] items)
            => AddRangeFromString(source, items);

        public static bool DictionaryEqual<TKey, TValue>(
            this IDictionary<TKey, TValue> first, IDictionary<TKey, TValue> second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first == null || second == null) return false;
            if (first.Count != second.Count) return false;

            var comparer = EqualityComparer<TValue>.Default;
            foreach (KeyValuePair<TKey, TValue> kvp in first)
            {
                if (!second.TryGetValue(kvp.Key, out TValue secondValue)) return false;
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
                value += (T)Convert.ChangeType(arg, typeof(T), CultureInfo.InvariantCulture);
            return value;
        }
    }

    public static class StringExtensions
    {
        public static string Compress(this string data)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(data);
            using var ms = new MemoryStream();
            using (var zip = new GZipStream(ms, CompressionMode.Compress, true))
            {
                zip.Write(buffer, 0, buffer.Length);
            }

            byte[] compressed = ms.ToArray();
            byte[] gzBuffer = new byte[compressed.Length + 4];
            Buffer.BlockCopy(compressed, 0, gzBuffer, 4, compressed.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(buffer.Length), 0, gzBuffer, 0, 4);
            return Convert.ToBase64String(gzBuffer);
        }

        public static string Decompress(this string data)
        {
            byte[] gzBuffer = Convert.FromBase64String(data);
            using var ms = new MemoryStream();
            int dataLength = BitConverter.ToInt32(gzBuffer, 0);
            ms.Write(gzBuffer, 4, gzBuffer.Length - 4);
            byte[] buffer = new byte[dataLength];
            ms.Position = 0;

            using (var zip = new GZipStream(ms, CompressionMode.Decompress))
            {
                int totalRead = 0;
                while (totalRead < dataLength)
                {
                    int read = zip.Read(buffer, totalRead, dataLength - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }
            }

            return Encoding.UTF8.GetString(buffer);
        }

        public static bool EqualsExcludingWhitespace(this string a, string b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return a.Where(c => !char.IsWhiteSpace(c))
               .SequenceEqual(b.Where(c => !char.IsWhiteSpace(c)));
        }
    }

    public static class XmlExtensions
    {
        public static bool ElementAttributesEquals(this XElement a, XElement b)
        {
            if (a.HasAttributes != b.HasAttributes)
                return false;

            foreach (XAttribute at in a.Attributes())
            {
                var other = b.Attribute(at.Name);
                if (other == null || !at.Value.Equals(other.Value, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        public static bool ElementEquals(this XElement a, XElement b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (!a.Name.LocalName.Equals(b.Name.LocalName, StringComparison.Ordinal))
                return false;

            if (!a.ElementAttributesEquals(b))
                return false;

            if (!a.HasElements && !b.HasElements && !a.Value.Equals(b.Value, StringComparison.Ordinal))
                return false;

            if (a.HasElements != b.HasElements)
                return false;

            if (a.HasElements && b.HasElements && a.Elements().Count() != b.Elements().Count())
                return false;

            if (a.Attribute("refType") == null || a.Attribute("refType").Value != "children")
            {
                foreach (var ae in a.Elements())
                {
                    var be = b.Elements().Where(s => ae.ElementEquals(s));
                    if (!be.Any())
                        return false;
                }
            }
            else
            {
                return false;
            }

            return true;
        }

        public static bool ElementMatch(this XElement a, XElement b)
        {
            if (a == null || b == null) return false;

            if (!a.Name.LocalName.Equals(b.Name.LocalName, StringComparison.Ordinal))
                return false;

            if (a.HasElements != b.HasElements)
                return false;

            if (!a.HasElements && !b.HasElements && !a.Value.Equals(b.Value, StringComparison.CurrentCultureIgnoreCase))
                return false;

            foreach (XAttribute at in a.Attributes())
            {
                var other = b.Attribute(at.Name);
                if (other == null || !at.Value.Equals(other.Value, StringComparison.CurrentCultureIgnoreCase))
                    return false;
            }

            if (a.HasElements && b.HasElements && a.Elements().Count() > b.Elements().Count())
                return false;

            foreach (var ae in a.Elements())
            {
                var be = b.Elements().Where(s => ae.ElementMatch(s));
                if (!be.Any())
                    return false;
            }

            return true;
        }
    }

    public static class TypeExtentions
    {
        public static Type ActualType<T>(T param) => param?.GetType() ?? typeof(T);

        public static Type GetActualType(this object param) => param?.GetType();

        public static object ToType(this object obj, Type type)
        {
            if (obj == null) return null;
            object tmp = ValueHelper.CreateInstance(type);

            foreach (var pi in obj.GetType().GetProperties())
            {
                var targetProp = tmp.GetType().GetProperty(pi.Name);
                if (targetProp != null && targetProp.CanWrite)
                {
                    targetProp.SetValue(tmp, pi.GetValue(obj, null), null);
                }
            }

            return tmp;
        }

        public static bool ArraysEqual<T>(T[] a1, T[] a2)
        {
            if (ReferenceEquals(a1, a2)) return true;
            if (a1 == null || a2 == null) return false;
            if (a1.Length != a2.Length) return false;

            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < a1.Length; i++)
            {
                if (!comparer.Equals(a1[i], a2[i])) return false;
            }
            return true;
        }

        public static bool ListsEqual<T>(List<T> l1, List<T> l2)
        {
            if (ReferenceEquals(l1, l2)) return true;
            if (l1 == null || l2 == null) return false;
            if (l1.Count != l2.Count) return false;

            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < l1.Count; i++)
            {
                if (!comparer.Equals(l1[i], l2[i])) return false;
            }
            return true;
        }
    }
}
