using System;

namespace Xod
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    public class ForeignKeyAttribute : Attribute
    {
        public string LocalProperty { get; set; }
        public string RemoteProperty { get; set; }
        public ForeignKeyAttribute(string localProp)
        {
            LocalProperty = localProp;
            RemoteProperty = "Id";
        }
        public ForeignKeyAttribute(string localProp, string remoteProp)
        {
            LocalProperty = localProp;
            RemoteProperty = remoteProp;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class ChildrenAttribute : Attribute
    {
        public string RemoteParentProperty { get; set; }
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    public class ParentKeyAttribute : Attribute
    {
        public string LocalProperty { get; set; }
        public string RemoteProperty { get; set; }
        public ParentKeyAttribute()
        {
            RemoteProperty = "Id";
        }
        public ParentKeyAttribute(string localProperty)
        {
            LocalProperty = localProperty;
            RemoteProperty = "Id";
        }
        public ParentKeyAttribute(string localProperty, string remoteProperty)
        {
            LocalProperty = localProperty;
            RemoteProperty = remoteProperty;
        }
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    public class GenericTypePropertyAttribute : Attribute
    {
        public string Name { get; set; }
        public GenericTypePropertyAttribute(string name)
        {
            Name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    public class InheritedAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class PropertyAttribute : Attribute
    {
        public ValuePosition Position { get; set; }
        public CascadeOptions Cascade { get; set; }
        public bool AutoNumber { get; set; }
        //public bool OverrideAutoNumber { get; set; }
        public bool Indexed { get; set; }

        public PropertyAttribute()
        {
            Position = ValuePosition.Body;
            Cascade = CascadeOptions.None;
            IdentitySeed = 1;
            IdentityIncrement = 1;
            //OverrideAutoNumber = true;
        }

        public dynamic IdentitySeed { get; set; }
        public dynamic IdentityIncrement { get; set; }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class CryptoAttribute : Attribute
    {
        public CryptoMethod Method { get; set; }

        public CryptoAttribute()
        {
            Method = CryptoMethod.MD5;
        }

        public CryptoAttribute(CryptoMethod method)
        {
            Method = method;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class PrimaryKeyAttribute : Attribute
    {
    }
    
    /// <summary>
    /// Not implemented yet
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class UniqueKeyAttribute : Attribute
    {
    }

    /// <summary>
    /// For runtime properties that are not persisted in the database files
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class NotMappedAttribute : Attribute
    {
    }

    /// <summary>
    /// For the properties that contains markup value like HTML content
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class MarkupAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class RequiredAttribute : Attribute
    {
    }

    /// <summary>
    /// Where the value will be stored in the struction of the database file
    /// </summary>
    public enum ValuePosition
    {
        Body, Attribute 
    }

    /// <summary>
    /// Relationship cascade opetions
    /// </summary>
    public enum CascadeOptions
    {
        None, Delete, Update
    }

    public enum CryptoMethod
    {
        MD5, SHA1, None
    }
}
