using System;

namespace Xod.Helpers
{
    /// <summary>
    /// What a row stores for a scalar property, and what a read gives back. A read gives a property the row
    /// leaves out the value it has without the row: its <c>DefaultValue</c> (for an element), or what a new
    /// instance holds. So the writers leave a value out only when that is the value (ISS-060: an empty string
    /// read back as null, 0 as a different DefaultValue, Guid.Empty as a new instance's Guid). An empty string
    /// is then an empty element or attribute, and a null a new instance wouldn't hold is an element marked
    /// <c>nil="true"</c>. The index keys what a read gives back, from an object and from a row alike, so it
    /// agrees with the rows whichever way it was built (IndexService).
    /// </summary>
    internal static class StoredValue
    {
        /// <summary>The attribute that marks a property element as null.</summary>
        internal const string NilAttribute = "nil";

        /// <summary>True when the element says its property is null.</summary>
        internal static bool IsNil(System.Xml.Linq.XElement element) =>
            element.HasAttributes && element.Attribute(NilAttribute)?.Value == "true";

        /// <summary>True when an empty text is the property's value (a plain string): no other type has an empty
        /// form, and an encrypted or hashed string is never stored empty.</summary>
        internal static bool HasEmptyForm(PropertyInfoItem prop) =>
            prop.PropertyType == typeof(string) && prop.Encryption == CryptoMethod.None && !prop.IsGenericType;

        /// <summary>True when the writers leave this value out of the row. The one rule, for every write of a
        /// row: the document writer asks it (an insert with or without its relations, an update, a relationship
        /// key written back), and the fast row writer (IOService.RawPages) asks it for each value that may be left
        /// out (an empty or default one), deciding the rest inline for speed.</summary>
        internal static bool IsLeftOut(object value, Type effectivePropType, PropertyInfoItem prop)
        {
            // A null is left out when a read gives null back. An attribute can't say null, so there it is left
            // out whatever a read gives (the property then reads as a new instance holds it).
            if (value == null)
                return prop.IsGenericType || prop.ValuePosition == ValuePosition.Attribute || ReadBackOfMissing(prop) == null;

            bool isTypeDefault = value.Equals(ValueHelper.DefaultOf(effectivePropType));
            // A key at its type's default isn't set.
            if (prop.IsPrimaryKey || prop.IsRelationshipKey)
                return isTypeDefault;

            // The values the writers may leave out, as 2.0 wrote them; each only when a read gives it back,
            // so rows that read back as written are stored as before.
            bool mayLeaveOut =
                (isTypeDefault && prop.DefaultValue != null && !prop.DefaultValue.Equals(ValueHelper.DefaultOf(effectivePropType))) ||
                value switch
                {
                    string s => s.Length == 0,
                    Guid g => g == Guid.Empty,
                    DateTime dt => dt == default,
                    DateOnly d => d == default,
                    TimeSpan ts => ts == default,
                    TimeOnly to => to == default,
                    _ => false
                };
            return mayLeaveOut && (prop.IsGenericType || value.Equals(ReadBackOfMissing(prop)));
        }

        /// <summary>The value a read gives a property whose row leaves it out (XmlEngine.Read and the fast
        /// row reader): an element's non-default <c>DefaultValue</c>, otherwise what a new instance holds, made as
        /// the reader makes it (<see cref="ValueHelper.CreateInstance"/>: without a constructor when the type has
        /// no parameterless one, so its strings hold null).</summary>
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
                Type owner = prop.Property?.ReflectedType ?? prop.Property?.DeclaringType;
                value = prop.GetValue(ValueHelper.CreateInstance(owner));
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
            if (IsLeftOut(value, prop.PropertyType, prop))
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
