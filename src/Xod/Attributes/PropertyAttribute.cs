using System;

namespace Xod
{
    [AttributeUsage(AttributeTargets.Property)]
    public class PropertyAttribute : Attribute
    {
        public ValuePosition Position { get; set; }
        public CascadeOptions Cascade { get; set; }
        public bool AutoNumber { get; set; }
        public bool Indexed { get; set; }
        /// <summary>The first number an <see cref="AutoNumber"/> property is given: a whole number (an
        /// <c>int</c> or <c>long</c> constant) within the property's type. 1 by default.</summary>
        public object IdentitySeed { get; set; }

        /// <summary>What each next number adds: a whole number of 1 or more. 1 by default.</summary>
        public object IdentityIncrement { get; set; }

        public PropertyAttribute()
        {
            Position = ValuePosition.Body;
            Cascade = CascadeOptions.None;
            IdentitySeed = 1;
            IdentityIncrement = 1;
        }
    }
}
