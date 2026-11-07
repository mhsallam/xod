using System;

namespace Xod
{
    /// <summary>
    /// Relationship cascade options.
    /// </summary>
    [Flags]
    public enum CascadeOptions
    {
        None = 0,
        Delete = 1,
        Update = 2,
        All = Delete | Update
    }
}
