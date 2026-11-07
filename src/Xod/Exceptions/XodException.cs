using System;

namespace Xod
{
    public class XodException : Exception
    {
        public XodException(string message) : base(message) { }
        public XodException(string message, Exception innerException) : base(message, innerException) { }
    }
}
