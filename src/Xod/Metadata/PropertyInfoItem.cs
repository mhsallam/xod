using System;
using System.Collections.Generic;
using System.Reflection;

namespace Xod
{
    internal enum ScalarPropertyKind : byte
    {
        None = 0,
        String = 1,
        Int32 = 2,
        Int64 = 3,
        Double = 4,
        Decimal = 5,
        Boolean = 6,
        Guid = 7,
        DateTime = 8,
        OtherScalar = 9
    }

    internal enum PropertyTypeCategory : byte
    {
        None = 0,
        ValueTypeArray = 1,
        ValueTypeCollection = 2,
        Class = 3,
        Array = 4,
        GenericCollection = 5,
        Dictionary = 6
    }

    internal enum PropertyReferenceType
    {
        None,
        Foreign,
        SelfForeign,
        Reference,
        Complex,
        Children,
        Parent
    }

    internal class PropertyInfoItem
    {
        public Type Type { get; set; }
        public string PropertyName { get; set; }
        public Type PropertyType { get; set; }
        public PropertyInfo Property { get; set; }
        public Func<object, object> Getter { get; set; }
        public Action<object, object> Setter { get; set; }

        public ScalarPropertyKind ScalarKind { get; set; }
        public string CachedIndexKey { get; set; }
        public byte[] Utf8NameBytes { get; set; }
        public byte[] Utf8OpenTag { get; set; }
        public byte[] Utf8CloseTag { get; set; }

        public Func<object, int> IntGetter { get; set; }
        public Action<object, int> IntSetter { get; set; }
        public Func<object, long> LongGetter { get; set; }
        public Action<object, long> LongSetter { get; set; }
        public Func<object, double> DoubleGetter { get; set; }
        public Action<object, double> DoubleSetter { get; set; }
        public Func<object, decimal> DecimalGetter { get; set; }
        public Action<object, decimal> DecimalSetter { get; set; }
        public Func<object, bool> BoolGetter { get; set; }
        public Action<object, bool> BoolSetter { get; set; }
        public Func<object, string> StringGetter { get; set; }
        public Action<object, string> StringSetter { get; set; }
        public Func<object, Guid> GuidGetter { get; set; }
        public Action<object, Guid> GuidSetter { get; set; }

        public object GetValue(object instance)
        {
            if (instance == null) return null;
            return Getter != null ? Getter(instance) : Property?.GetValue(instance);
        }

        public void SetValue(object instance, object value)
        {
            if (instance == null) return;
            if (Setter != null)
                Setter(instance, value);
            else if (Property != null && Property.CanWrite)
                Property.SetValue(instance, value);
        }

        public PropertyTypeCategory TypeCategory { get; set; }
        public PropertyReferenceType ReferenceType { get; set; }
        public CascadeOptions Cascade { get; set; }
        public string ChildParentProperty { get; set; }
        public Type CollectionItemType { get; set; }

        public bool IsGenericType { get; set; }
        public string GenericTypeProperty { get; set; }

        public List<ForeignKeyAttribute> ForeignKeys { get; set; }
        public List<ParentKeyAttribute> ParentKeys { get; set; }

        public string RelationshipName { get; set; }

        public bool IsPrimaryKey { get; set; }
        public bool IsRelationshipKey { get; set; }
        public bool IsRequired { get; set; }
        public bool IsUnique { get; set; }
        public bool IsNotMapped { get; set; }
        public bool IsMarkup { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsInherited { get; set; }
        public bool IsIndexed { get; set; }

        public bool IsAutonumber { get; set; }
        public dynamic IdentityIncrement { get; set; } = 1;
        public dynamic IdentitySeed { get; set; } = 1;
        public int IntSeed { get; set; } = 1;
        public int IntIncrement { get; set; } = 1;

        public ValuePosition ValuePosition { get; set; }
        public dynamic DefaultValue { get; set; }

        public CryptoMethod Encryption { get; set; }
    }
}
