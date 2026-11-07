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

        public static bool IsScalarType(Type type)
        {
            if (type == null) return false;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsPrimitive
                || underlying.IsEnum
                || underlying == typeof(string)
                || underlying == typeof(decimal)
                || underlying == typeof(DateTime)
                || underlying == typeof(DateTimeOffset)
                || underlying == typeof(DateOnly)
                || underlying == typeof(TimeOnly)
                || underlying == typeof(TimeSpan)
                || underlying == typeof(Guid)
                || underlying.IsValueType;
        }

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
