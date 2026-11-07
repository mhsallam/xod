using System;

namespace Xod
{
    /// <summary>
    /// Enforces uniqueness across all records of this type in the database and automatically indexes the property.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class UniqueKeyAttribute : Attribute
    {
    }
}
