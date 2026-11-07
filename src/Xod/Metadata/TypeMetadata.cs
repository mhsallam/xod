using System;
using System.Collections.Generic;

namespace Xod
{
    internal sealed class TypeMetadata
    {
        public Type Type { get; init; }
        public Func<object> InstanceFactory { get; init; }
        public PropertyInfoItem[] AllProperties { get; init; }
        public PropertyInfoItem[] WriteProperties { get; init; }
        public PropertyInfoItem[] ReadProperties { get; init; }
        public Dictionary<string, PropertyInfoItem> ReadPropertyMap { get; init; }
        public PropertyInfoItem[] PrimaryKeyProperties { get; init; }
        public PropertyInfoItem[] UniqueProperties { get; init; }
        public PropertyInfoItem[] IndexedProperties { get; init; }
        public PropertyInfoItem[] RequiredNonAutoProperties { get; init; }
        public PropertyInfoItem[] AutonumberProperties { get; init; }
        public bool IsStandalone { get; init; }
        public bool IsPureScalarStandalone { get; init; }
        public bool HasAttributeProperties { get; init; }
        public bool HasDefaultValueProperties { get; init; }
        public bool HasCascadeDeleteOrParentProperties { get; init; }
        public bool HasValidPrimaryKeyTypes { get; init; }
        public byte[] Utf8EntityOpenTag { get; init; }
        public byte[] Utf8EntityCloseTag { get; init; }
    }
}
