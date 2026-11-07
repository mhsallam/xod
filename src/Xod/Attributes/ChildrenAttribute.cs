using System;

namespace Xod
{
    [AttributeUsage(AttributeTargets.Property)]
    public class ChildrenAttribute : Attribute
    {
        public string RemoteParentProperty { get; set; }
    }
}
