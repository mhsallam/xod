using System;

namespace Xod
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    public class GenericTypePropertyAttribute : Attribute
    {
        public string Name { get; set; }

        public GenericTypePropertyAttribute(string name)
        {
            Name = name;
        }
    }
}
