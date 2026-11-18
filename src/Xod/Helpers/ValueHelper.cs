using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Xod.Helpers
{
    public static class ValueHelper
    {
        private static readonly ConcurrentDictionary<Type, Func<object>> FactoryCache = new();
        private static long _codeCounter = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & 0x0000_000F_FFFF_FFFFL;

        public static T Default<T>() => (T)Default(typeof(T));

        public static Func<object> GetFactory(Type type)
        {
            if (type == null) return null;
            if (type == typeof(string)) return static () => string.Empty;

            return FactoryCache.GetOrAdd(type, static t =>
            {
                var ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (ctor != null && !t.IsValueType)
                {
                    try
                    {
                        var newExpr = Expression.New(ctor);
                        return Expression.Lambda<Func<object>>(newExpr).Compile();
                    }
                    catch
                    {
                        return () => ctor.Invoke(null);
                    }
                }
                if (t.IsValueType)
                {
                    return () => Activator.CreateInstance(t);
                }
                return () => RuntimeHelpers.GetUninitializedObject(t);
            });
        }

        public static object CreateInstance(Type type)
        {
            if (type == null) return null;
            if (type == typeof(string)) return string.Empty;
            return GetFactory(type)();
        }

        public static object Default(Type type)
        {
            object item = CreateInstance(type);
            var props = from row in type.GetProperties()
                        where (IsScalarType(row.PropertyType)) &&
                              row.GetCustomAttribute<DefaultValueAttribute>(false) != null &&
                              row.CanWrite
                        select new
                        {
                            Property = row,
                            DefaultValue = row.GetCustomAttribute<DefaultValueAttribute>(false).Value
                        };

            foreach (var prop in props)
            {
                prop.Property.SetValue(item, prop.DefaultValue, null);
            }
            return item;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> DefaultValueCache = new();

        public static object DefaultOf(Type type)
        {
            if (type == null) return null;
            if (type == typeof(string)) return "";
            if (!type.IsValueType) return null;
            return DefaultValueCache.GetOrAdd(type, static t => Activator.CreateInstance(t));
        }

        /// <summary>
        /// The scalars the format stores, and their nullable forms (PLAN-046 section 7.5): every reader, writer and index
        /// key knows these. Any other value type was taken too, written as its text and read back as its default
        /// (ISS-142); a property of one is now refused when its type loads (<see cref="UnstorableValueType"/>).
        /// </summary>
        public static bool IsScalarType(Type type)
        {
            if (type == null) return false;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return (underlying.IsPrimitive && underlying != typeof(IntPtr) && underlying != typeof(UIntPtr))
                || underlying.IsEnum
                || underlying == typeof(string)
                || underlying == typeof(decimal)
                || underlying == typeof(DateTime)
                || underlying == typeof(DateTimeOffset)
                || underlying == typeof(DateOnly)
                || underlying == typeof(TimeOnly)
                || underlying == typeof(TimeSpan)
                || underlying == typeof(Guid);
        }

        /// <summary>
        /// The value type in a property of <paramref name="type"/> that the writer would store as nothing, or as text no
        /// reader gives back (ISS-142); null when there is none. It follows the writer's branches (XmlEngine.WriteProperty):
        /// a struct outside <see cref="IsScalarType"/>, a collection struct too (an ImmutableArray); an array's elements; a
        /// map's keys and values, written as text (by the IDictionary&lt;K, V&gt; it implements, a subclass too); a generic
        /// collection's first type argument, its items. A class is no value: its own properties are checked when it loads.
        /// </summary>
        internal static Type UnstorableValueType(Type type)
        {
            if (type == null) return null;
            Type underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (IsScalarType(underlying))
                return null;
            if (underlying.IsValueType)
                return underlying;
            if (underlying.IsArray)
                return UnstorableValueType(underlying.GetElementType());
            if (IsMapType(underlying))
            {
                Type map = underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    ? underlying
                    : underlying.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
                return map?.GetGenericArguments().Select(UnstorableValueType).FirstOrDefault(t => t != null);
            }
            if (underlying.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying))
                return UnstorableValueType(underlying.GetGenericArguments().FirstOrDefault());
            return null;
        }

        /// <summary>
        /// The collection in a property of <paramref name="type"/> that a read can't give back (ISS-184); null when there
        /// is none. A read makes a <c>List&lt;T&gt;</c>, or an array for an array property, so a collection is an array,
        /// a map, or a type a <c>List&lt;T&gt;</c> can be assigned to (<c>List&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>,
        /// <c>ICollection&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>,
        /// <c>IReadOnlyCollection&lt;T&gt;</c>), and so are its items. A collection class that isn't generic itself was
        /// stored as a class and read back empty; any other was stored, and its read threw.
        /// </summary>
        internal static Type UnreadableCollection(Type type)
        {
            if (type == null || type == typeof(string) || IsScalarType(type) || type == typeof(object))
                return null;
            if (type.IsArray)
                return UnreadableCollection(type.GetElementType());
            if (IsMapType(type) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                return null;
            if (!type.IsGenericType || type.GetGenericArguments().Length != 1)
                return type;
            Type item = type.GetGenericArguments()[0];
            if (!type.IsAssignableFrom(typeof(List<>).MakeGenericType(item)))
                return type;
            return UnreadableCollection(item);
        }

        /// <summary>A map, which the writer stores as entries (PropertyTypeCategory.Dictionary): the one test.</summary>
        internal static bool IsMapType(Type type) =>
            typeof(System.Collections.IDictionary).IsAssignableFrom(type) ||
            (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>));

        public static string FormatScalar(object value)
        {
            if (value == null) return null;
            return value switch
            {
                DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
                TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
                TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
        }

        public static object ParseScalar(Type targetType, string stringValue)
        {
            if (targetType == null || stringValue == null) return null;

            Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (underlying == typeof(string))
                return stringValue;

            // A char is the one character stored, whitespace too (ISS-140): the blank-text rule below made ' ', a tab
            // or a no-break space read back '\0' (a char? null), and an index rebuilt from the rows key it 0.
            if (underlying == typeof(char) && stringValue.Length == 1)
                return stringValue[0];

            if (string.IsNullOrWhiteSpace(stringValue))
                return Nullable.GetUnderlyingType(targetType) != null ? null : DefaultOf(underlying);

            if (underlying.IsEnum)
                return Enum.Parse(underlying, stringValue, ignoreCase: true);

            if (underlying == typeof(Guid))
                return Guid.Parse(stringValue);

            if (underlying == typeof(DateTime))
                return DateTime.Parse(stringValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            if (underlying == typeof(DateTimeOffset))
                return DateTimeOffset.Parse(stringValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            if (underlying == typeof(DateOnly))
                return DateOnly.Parse(stringValue, CultureInfo.InvariantCulture);

            if (underlying == typeof(TimeOnly))
                return TimeOnly.Parse(stringValue, CultureInfo.InvariantCulture);

            if (underlying == typeof(TimeSpan))
                return TimeSpan.Parse(stringValue, CultureInfo.InvariantCulture);

            if (underlying == typeof(bool))
            {
                if (bool.TryParse(stringValue, out bool b)) return b;
                if (stringValue == "1") return true;
                if (stringValue == "0") return false;
            }

            if (underlying.IsPrimitive || underlying == typeof(decimal))
                return Convert.ChangeType(stringValue, underlying, CultureInfo.InvariantCulture);

            return null;
        }

        public static bool IsNumeric(object value)
        {
            if (value == null) return false;
            return IsNumeric(value.GetType()) || double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        }

        public static bool IsNumeric(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying == typeof(sbyte)
                || underlying == typeof(byte)
                || underlying == typeof(short)
                || underlying == typeof(ushort)
                || underlying == typeof(int)
                || underlying == typeof(uint)
                || underlying == typeof(long)
                || underlying == typeof(ulong)
                || underlying == typeof(float)
                || underlying == typeof(double)
                || underlying == typeof(decimal);
        }

        public static int CompareValues(object left, object right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;

            if (left.GetType() == right.GetType() && left is IComparable comparable)
                return comparable.CompareTo(right);

            if (IsNumeric(left.GetType()) && IsNumeric(right.GetType()))
            {
                decimal dLeft = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
                decimal dRight = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
                return dLeft.CompareTo(dRight);
            }

            return string.Compare(FormatScalar(left), FormatScalar(right), StringComparison.OrdinalIgnoreCase);
        }

        internal static string PickCode()
        {
            ulong v = (ulong)System.Threading.Interlocked.Increment(ref _codeCounter) & 0xF_FFFF_FFFFUL;
            v ^= v >> 18;
            v = (v * 0x9E3779B97UL) & 0xF_FFFF_FFFFUL;
            v ^= v >> 18;
            return string.Create(9, v, static (span, val) =>
            {
                ReadOnlySpan<char> hex = "0123456789abcdef";
                for (int i = 0; i < 9; i++)
                {
                    span[i] = hex[(int)(val & 0xFUL)];
                    val >>= 4;
                }
            });
        }

        internal static Dictionary<string, object> GetPropertyValues(Type type, object item, IEnumerable<string> propNames)
        {
            var values = new Dictionary<string, object>();
            if (propNames == null || item == null) return values;

            foreach (var propName in propNames)
            {
                PropertyInfo prop = type.GetProperty(propName);
                if (prop != null)
                {
                    object value = prop.GetValue(item);
                    if (value != null && !value.Equals(DefaultOf(prop.PropertyType)))
                        values.Add(prop.Name, value);
                }
                else
                {
                    throw new PropertyKeyNameException();
                }
            }
            return values;
        }

        internal static Dictionary<string, object> GetPropertyValues(Type type, object item, Dictionary<string, string> propNames)
        {
            var values = new Dictionary<string, object>();
            if (propNames == null || item == null) return values;

            foreach (var propName in propNames)
            {
                PropertyInfo prop = type.GetProperty(propName.Key);
                if (prop != null)
                {
                    object value = prop.GetValue(item);
                    if (value != null && !value.Equals(DefaultOf(prop.PropertyType)))
                        values.Add(propName.Value, value);
                }
                else
                {
                    throw new PropertyKeyNameException();
                }
            }
            return values;
        }
    }
}
