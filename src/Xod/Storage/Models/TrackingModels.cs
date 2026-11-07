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
        public PropertyInfoItem RootProperty { get; set; }
        public Guid ReadId { get; set; }
    }
}
