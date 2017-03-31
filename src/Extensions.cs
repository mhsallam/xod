using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Reflection;

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
            Type type = typeof(TSource);
            foreach (var item in items)
            {
                source.Add((TSource)System.ComponentModel.TypeDescriptor.GetConverter(typeof(TSource)).ConvertFromString(item.ToString()));
                //source.Add((TSource)Convert.ChangeType(item.ToString(), type));
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
                value += (T)arg;
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
        public static bool EqualsExcludingWhitespace(this String a, String b)
        {
            return a.Where(c => !Char.IsWhiteSpace(c))
               .SequenceEqual(b.Where(c => !Char.IsWhiteSpace(c)));
        }
    }

    public static class XmlExtensions
    {
        public static bool ElementAttributesEquals(this System.Xml.Linq.XElement a, System.Xml.Linq.XElement b)
        {
            if (a.HasAttributes != b.HasAttributes)
                return false;

            foreach (System.Xml.Linq.XAttribute at in a.Attributes())
            {
                if (b.Attribute(at.Name) == null || !at.Value.Equals(b.Attribute(at.Name).Value))
                    return false;
            }

            return true;
        }

        //identical match, order is ignored
        public static bool ElementEquals(this System.Xml.Linq.XElement a, System.Xml.Linq.XElement b)
        {
            if (!a.Name.LocalName.Equals(b.Name.LocalName))
                return false;

            if (!a.ElementAttributesEquals(b))
                return false;

            if ((!a.HasElements && !b.HasElements) && !a.Value.Equals(b.Value))
                return false;

            if (a.HasElements != b.HasElements)
                return false;

            if (a.HasElements == b.HasElements == true && a.Elements().Count() != b.Elements().Count())
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

        //true if all elements of a exists in b, even if b has more
        public static bool ElementMatch(this System.Xml.Linq.XElement a, System.Xml.Linq.XElement b)
        {
            if (!a.Name.LocalName.Equals(b.Name.LocalName))
                return false;

            if (a.HasElements != b.HasElements)
                return false;

            if (!a.HasElements && !b.HasElements && !a.Value.Equals(b.Value, StringComparison.CurrentCultureIgnoreCase))
                return false;

            foreach (System.Xml.Linq.XAttribute at in a.Attributes())
            {
                if (b.Attribute(at.Name) == null || !at.Value.Equals(b.Attribute(at.Name).Value, StringComparison.CurrentCultureIgnoreCase))
                    return false;
            }

            if (a.HasElements == b.HasElements == true && a.Elements().Count() > b.Elements().Count())
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
        public static Type ActualType<T>(T param)
        {
            return typeof(T);
        }
        public static Type GetActualType(this object param)
        {
            if (null != param)
                return (Type)typeof(TypeExtentions)
                .GetTypeInfo()
                .GetMethod("ActualType")
                .MakeGenericMethod(new[] { param.GetType() })
                .Invoke(null, new[] { param });
            else
                return null;
        }
        public static object ToType(this object obj, Type type)
        {
            //create instance of T type object:
            object tmp = Activator.CreateInstance(type);

            try
            {
                //loop through the properties of the object you want to covert:          
                foreach (var pi in obj.GetType().GetProperties())
                {
                    //get the value of property and try to assign it to the property of T type object:
                    tmp.GetType().GetProperty(pi.Name).SetValue(tmp, pi.GetValue(obj, null), null);
                }
            }
            catch (Exception ex)
            {
            }

            //return the T type object:         
            return tmp;
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
