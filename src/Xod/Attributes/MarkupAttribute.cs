using System;

namespace Xod
{
    /// <summary>
    /// For properties that contain markup values like HTML or XML content (stored in CDATA in XML engine).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class MarkupAttribute : Attribute
    {
    }
}
