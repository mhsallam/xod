using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.ComponentModel;

namespace Xod.Helpers
{
    public class ValueHelper
    {
        public static T Default<T>()
        {
            return (T)Default(typeof(T));
        }
        public static object Default(Type type)
        {
            object item = Activator.CreateInstance(type);
            var props = from row in type.GetProperties()
                        where
                            (row.PropertyType.GetTypeInfo().IsPrimitive || row.PropertyType == typeof(string)) &&
                            null != row.GetCustomAttribute<DefaultValueAttribute>(false)
                        select new
                        {
                            Property = row,
                            DefaultValue = (row.GetCustomAttribute<DefaultValueAttribute>(false)).Value
                        };

            foreach (var prop in props)
            {
                prop.Property.SetValue(item, prop.DefaultValue, null);
            }
            //return Write<T>(item);
            return item;
        }
        public static object DefaultOf(Type type)
        {
            if (type == typeof(string))
                return "";
            if (type.IsArray)
                return null;
            return Activator.CreateInstance(type);
        }

        public static bool IsNumeric(object value)
        {
            if (value is sbyte) return true;
            if (value is byte) return true;
            if (value is short) return true;
            if (value is ushort) return true;
            if (value is int) return true;
            if (value is uint) return true;
            if (value is long) return true;
            if (value is ulong) return true;
            if (value is float) return true;
            if (value is double) return true;
            if (value is decimal) return true;
            if (value is bool) return true;
            try
            {
                if (value is string)
                    Double.Parse(value as string);
                else
                    Double.Parse(value.ToString());
                return true;
            }
            catch { } // just dismiss errors but return false
            return false;
        }
        public static bool IsNumeric(Type type)
        {
            if (type == typeof(sbyte)) return true;
            if (type == typeof(byte)) return true;
            if (type == typeof(short)) return true;
            if (type == typeof(ushort)) return true;
            if (type == typeof(int)) return true;
            if (type == typeof(uint)) return true;
            if (type == typeof(long)) return true;
            if (type == typeof(ulong)) return true;
            if (type == typeof(float)) return true;
            if (type == typeof(double)) return true;
            if (type == typeof(decimal)) return true;
            if (type == typeof(bool)) return true;
            return false;
        }

        internal static string PickCode()
        {
            string code = Guid.NewGuid().ToString().Replace("-", "");
            return code.Substring(0, 9);
        }

        internal static Dictionary<string, object> GetPropertyValues(Type type, object item, IEnumerable<string> propNames)
        {
            Dictionary<string, object> values = new Dictionary<string, object>();

            if (propNames == null)
                return values;

            foreach (var propName in propNames)
            {
                PropertyInfo prop = type.GetProperty(propName);
                if (null != prop)
                {
                    object value = prop.GetValue(item);
                    if (value != null && !value.Equals(ValueHelper.DefaultOf(prop.PropertyType)))
                        values.Add(prop.Name, prop.GetValue(item));
                }
                else
                    throw new PropertyKeyNameException();
            }
            return values;
        }
        internal static Dictionary<string, object> GetPropertyValues(Type type, object item, Dictionary<string, string> propNames)
        {
            Dictionary<string, object> values = new Dictionary<string, object>();
            foreach (var propName in propNames)
            {
                PropertyInfo prop = type.GetProperty(propName.Key);
                if (null != prop)
                {
                    object value = prop.GetValue(item);
                    if (value != null && !value.Equals(ValueHelper.DefaultOf(prop.PropertyType)))
                        values.Add(propName.Value, value);
                }
                else
                    throw new PropertyKeyNameException();
            }
            return values;
        }
    }
}
