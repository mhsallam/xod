using System;

namespace Xod.Helpers
{
    /// <summary>
    /// What a row stores for a scalar property, and what a read gives back. The writers leave some values
    /// out of the row (a default key, an empty string, <c>Guid.Empty</c>, a default date or interval); a
    /// read then gives the property the value it has without the row: its <c>DefaultValue</c> (for an
    /// element), or what a new instance holds. The index keys what a read gives back, from an object and
    /// from a row alike, so it agrees with the rows whichever way it was built (IndexService).
    /// </summary>
    internal static class StoredValue
    {
        /// <summary>True when the writers leave this value out of the row. The one rule: the document
        /// writer asks it, and the fast row writer (IOService.RawPages) inlines the same tests for speed.</summary>
        internal static bool IsLeftOut(object value, Type effectivePropType, PropertyInfoItem prop, bool shallowWrite)
        {
            if (value == null)
                return true;

            bool isTypeDefault = value.Equals(ValueHelper.DefaultOf(effectivePropType));
            if (shallowWrite || prop.IsPrimaryKey || prop.IsRelationshipKey)
            {
                return isTypeDefault;
            }

            if (isTypeDefault && prop.DefaultValue != null && !prop.DefaultValue.Equals(ValueHelper.DefaultOf(effectivePropType)))
            {
                return true;
            }

            return value switch
            {
                string s => string.IsNullOrEmpty(s),
                Guid g => g == Guid.Empty,
                DateTime dt => dt == default,
                DateOnly d => d == default,
                TimeSpan ts => ts == default,
                TimeOnly to => to == default,
                _ => false
            };
        }

        /// <summary>The value a read gives a property whose row leaves it out (XmlEngine.Read and the fast
        /// row reader): an element's non-default <c>DefaultValue</c>, otherwise what a new instance holds.</summary>
        internal static object ReadBackOfMissing(PropertyInfoItem prop)
        {
            if (prop.MissingValueKnown)
                return prop.MissingValue;

            object value;
            if (prop.ValuePosition != ValuePosition.Attribute &&
                prop.DefaultValue != null &&
                !((object)prop.DefaultValue).Equals(ValueHelper.DefaultOf(prop.PropertyType)))
            {
                value = prop.DefaultValue;
            }
            else
            {
                try
                {
                    Type owner = prop.Property?.ReflectedType ?? prop.Property?.DeclaringType;
                    object instance = Activator.CreateInstance(owner, nonPublic: true);
                    value = prop.GetValue(instance);
                }
                catch
                {
                    // No parameterless constructor: the reader can't make one either, so the default.
                    value = ValueHelper.DefaultOf(prop.PropertyType);
                }
            }

            prop.MissingValue = value;
            prop.MissingValueKnown = true;
            return value;
        }

        /// <summary>The value a read of the stored row gives back for an object's value. A local
        /// <c>DateTime</c> is stored with its offset and read back in the reading machine's zone, which
        /// moves a time in a daylight-saving gap; it is put through its stored text.</summary>
        internal static object AsReadBack(PropertyInfoItem prop, object value)
        {
            if (IsLeftOut(value, prop.PropertyType, prop, shallowWrite: false))
                return ReadBackOfMissing(prop);
            if (value is DateTime { Kind: DateTimeKind.Local } local)
                return ValueHelper.ParseScalar(typeof(DateTime), ValueHelper.FormatScalar(local));
            return value;
        }

        /// <summary>A scalar's stored text, refusing text that can't be stored unchanged. Every value the
        /// document writer stores passes through here; the fast row writer calls <see cref="CheckText"/>.</summary>
        internal static string Text(object value, string propertyName)
        {
            string text = ValueHelper.FormatScalar(value);
            CheckText(text, propertyName);
            return text;
        }

        /// <summary>Throws <see cref="InvalidTextException"/> for a lone surrogate, which UTF-8 (the text of
        /// every format) can't carry: an encoder would store U+FFFD in its place.</summary>
        internal static void CheckText(string text, string propertyName)
        {
            if (text == null) return;
            ReadOnlySpan<char> span = text.AsSpan();
            int at = span.IndexOfAnyInRange('\uD800', '\uDFFF');
            if (at < 0) return;
            for (int i = at; i < span.Length; i++)
            {
                if (char.IsHighSurrogate(span[i]) && i + 1 < span.Length && char.IsLowSurrogate(span[i + 1]))
                {
                    i++;
                    continue;
                }
                if (char.IsSurrogate(span[i]))
                    throw new InvalidTextException(propertyName);
            }
        }
    }
}
