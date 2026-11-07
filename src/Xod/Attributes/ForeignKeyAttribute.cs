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
}
