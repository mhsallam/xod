using System;

namespace Xod
{
    /// <summary>
    /// Trigger action associated object.
    /// </summary>
    public class TriggerEventArgs : EventArgs
    {
        public object Item { get; set; }
        public Type Type { get; set; }
        public DatabaseActions Action { get; set; }
        public bool Cancel { get; set; }
    }

    /// <summary>
    /// Trigger action type.
    /// </summary>
    public enum DatabaseActions
    {
        Insert,
        Update,
        Delete,
        Drop
    }
}
