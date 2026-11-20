using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xod.Helpers;
using Xod.Services;

namespace Xod.Engines.Xml
{
    public partial class XmlEngine
    {
        /// <summary>
        /// ISS-139: the one test of a stored row against an example, for every match by example (the query by
        /// example, Update and Delete by an item, the primary-key and unique checks made without an index). A property
        /// takes part when the example sets it (ValueHelper.SetsValue). A scalar matches when the value a read
        /// gives back from the row and the example's have one index key, by the index's own functions
        /// (IndexService.GetNormalizedPropertyValue and NormalizeIndexValue, StoredValue.ReadBack): equal exactly
        /// when C# says equal, text exact. So a match gives the rows an index lookup gives, indexed or not. Any other
        /// property the example sets matches by the form a shallow write gives it, text compared exactly (a value
        /// collection, a map, a one-way digest); one that form leaves out doesn't take part, as before: a reference
        /// (ISS-168). It compared the written text case-insensitively in the current culture: "alice" matched
        /// "Alice", "Carl" a stored "Carl" with a soft hyphen in it, and 12.5m missed a stored 12.50.
        /// </summary>
        private sealed class ExampleTest
        {
            /// <summary>Every property the example sets, which an index lookup may take first.</summary>
            public List<PropertyInfoItem> Set;
            /// <summary>The scalars, with the index key of the example's value (null for a value equal to nothing, NaN).</summary>
            public List<(PropertyInfoItem Prop, string Key)> Keys;
            /// <summary>The properties matched by their stored form.</summary>
            public List<PropertyInfoItem> ByForm;
            /// <summary>The example written with only <see cref="ByForm"/>, or null when there is none.</summary>
            public XElement Written;
        }

        // The properties of an example that take part are those it sets (ValueHelper.SetsValue: not null and not their
        // type's default, which for text is the empty string, so an empty string doesn't take part). The example filters
        // (FilterItemPrimaryProperties) clear the other properties to those defaults.

        // A one-way digest is stored as itself, not as the value: it matches by its stored form.
        private static bool MatchesByValue(PropertyInfoItem prop) =>
            ValueHelper.IsScalarType(prop.PropertyType) &&
            (prop.Encryption == CryptoMethod.None || prop.Encryption == CryptoMethod.AES256);

        private ExampleTest ExampleTestFor(Type type, object example)
        {
            var meta = propertyService.GetMetadata(type);
            var props = meta != null
                ? meta.ReadProperties
                : propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped && !s.IsReadOnly).ToArray();

            var test = new ExampleTest
            {
                Set = new List<PropertyInfoItem>(2),
                Keys = new List<(PropertyInfoItem, string)>(2),
                ByForm = new List<PropertyInfoItem>(),
            };
            foreach (var prop in props)
            {
                if (!ValueHelper.SetsValue(prop, example))
                    continue;
                test.Set.Add(prop);
                if (MatchesByValue(prop))
                    test.Keys.Add((prop, IndexService.GetNormalizedPropertyValue(prop, example)));
                else
                    test.ByForm.Add(prop);
            }

            if (test.ByForm.Count > 0)
            {
                string[] skip = props.Where(p => !test.ByForm.Contains(p)).Select(p => p.PropertyName).ToArray();
                test.Written = skip.Length > 0
                    ? Write(type, example, true, null, new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = skip })
                    : Write(type, example, true);
                // What the shallow write leaves out (a reference) has nothing to match by, and doesn't take part: an
                // example that sets only that sets nothing, and matches nothing (review of ISS-139: it matched every
                // row).
                foreach (var prop in test.ByForm.ToArray())
                {
                    bool written = prop.ValuePosition == ValuePosition.Attribute
                        ? test.Written.Attribute(prop.PropertyName) != null
                        : test.Written.Element(prop.PropertyName) != null;
                    if (!written)
                    {
                        test.ByForm.Remove(prop);
                        test.Set.Remove(prop);
                    }
                }
            }
            return test;
        }

        /// <summary>Whether the row whose entity element is <paramref name="entity"/> matches the example.</summary>
        private bool Matches(ExampleTest test, XElement entity)
        {
            // An example that sets nothing matches nothing, as it always has (its written form had no elements, which
            // no row's matched): a Delete or Update by an example whose key was left unset takes no row, not all.
            if (entity == null || test.Set.Count == 0)
                return false;

            foreach (var (prop, key) in test.Keys)
            {
                if (key == null)
                    return false;
                object stored = StoredScalar(prop, entity);
                if (stored == null || !string.Equals(IndexService.NormalizeIndexValue(prop.PropertyType, stored), key, StringComparison.Ordinal))
                    return false;
            }

            foreach (var prop in test.ByForm)
            {
                if (prop.ValuePosition == ValuePosition.Attribute)
                {
                    if (!string.Equals(test.Written.Attribute(prop.PropertyName).Value, entity.Attribute(prop.PropertyName)?.Value, StringComparison.Ordinal))
                        return false;
                    continue;
                }
                if (!test.Written.Element(prop.PropertyName).ElementMatch(entity.Element(prop.PropertyName), StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }
}
