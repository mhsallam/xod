using System;

namespace Xod
{
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
}
