namespace Xod
{
    /// <summary>
    /// Property-level cryptographic transformation methods.
    /// </summary>
    public enum CryptoMethod
    {
        None = 0,
        MD5 = 1,
        SHA1 = 2,
        SHA256 = 3,
        SHA512 = 4,
        /// <summary>
        /// Reversible authenticated AES-256-GCM encryption at the property level.
        /// </summary>
        AES256 = 5
    }
}
