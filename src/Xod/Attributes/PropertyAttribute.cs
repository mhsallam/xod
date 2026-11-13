using System;

namespace Xod
{
    [AttributeUsage(AttributeTargets.Property)]
    public class PropertyAttribute : Attribute
    {
        /// <summary>The seed and the increment of an autonumber the model gives neither: the one statement of it
        /// (the getters below, PropertyService.ReadIdentitySettings, PropertyInfoItem).</summary>
        internal const int DefaultIdentity = 1;

        // What the model gave, null when it gave nothing (or null): the default can't be told from a value the model
        // set once the getter has filled it in, and a Guid autonumber refuses only a setting given (ISS-117). A
        // subclass that sets them in its constructor gives them, as 2.0.x's only way to set them was.
        private object identitySeed;
        private object identityIncrement;

        public ValuePosition Position { get; set; }
        public CascadeOptions Cascade { get; set; }
        public bool AutoNumber { get; set; }
        public bool Indexed { get; set; }
        /// <summary>The first number an <see cref="AutoNumber"/> property is given: a whole number (an
        /// <c>int</c> or <c>long</c> constant) within the property's type. Reads 1 when not set, or set to
        /// <c>null</c>. A <see cref="Guid"/> autonumber refuses it when it is set.</summary>
        public object IdentitySeed
        {
            get => identitySeed ?? DefaultIdentity;
            set => identitySeed = value;
        }

        /// <summary>What each next number adds: a whole number of 1 or more. Reads 1 when not set, or set to
        /// <c>null</c>. A <see cref="Guid"/> autonumber refuses it when it is set.</summary>
        public object IdentityIncrement
        {
            get => identityIncrement ?? DefaultIdentity;
            set => identityIncrement = value;
        }

        /// <summary>The seed the model gave, or null when it gave none (PropertyService.ReadIdentitySettings).</summary>
        internal object IdentitySeedGiven => identitySeed;

        /// <summary>The increment the model gave, or null when it gave none.</summary>
        internal object IdentityIncrementGiven => identityIncrement;

        public PropertyAttribute()
        {
            Position = ValuePosition.Body;
            Cascade = CascadeOptions.None;
        }
    }
}
