using System;
using System.Reflection;

namespace Xod
{
    internal class AutonumberCache
    {
        public Type Type { get; set; }
        public string PropertyName { get; set; }
        public dynamic Value { get; set; }
        public long Int64Value { get; set; }
        public MethodInfo Method { get; set; }
    }

    internal class ReadWriteTrack
    {
        public object Item { get; set; }
        public Type Type { get; set; }
        public string Code { get; set; }
        public ReadWriteTrack Parent { get; set; }

        /// <summary>
        /// The <c>[Children]</c> list of <see cref="Parent"/> this row is written through, or null: a row written through
        /// any other relation (a reference, a plain list, a cascade), or on its own, has no parent by its track. Set
        /// on the row's own track when it is made (Create, UpdateItem), so a later write through the same parent
        /// can't inherit it. It was a field of the parent's track, set by a child's write and never reset: a reference
        /// the parent wrote afterwards took the parent as its own when the types matched (review of TASK-100).
        /// </summary>
        public PropertyInfoItem Via { get; set; }
        public Guid ReadId { get; set; }
    }
}
