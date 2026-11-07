using System;

namespace Xod
{
    [AttributeUsage(AttributeTargets.Property)]
    public class CryptoAttribute : Attribute
    {
        public CryptoMethod Method { get; set; }

        public CryptoAttribute()
        {
            Method = CryptoMethod.SHA256;
        }

        public CryptoAttribute(CryptoMethod method)
        {
            Method = method;
        }
    }
}
