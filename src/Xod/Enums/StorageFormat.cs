namespace Xod
{
    /// <summary>
    /// Underlying storage engine format for XOD++.
    /// </summary>
    public enum StorageFormat
    {
        /// <summary>
        /// Classic XOD human-readable XML pages (.xod / .xtab / .xpag).
        /// </summary>
        Xml = 0,
        /// <summary>
        /// Modern human-readable JSON pages (.xod / .xtab / .xpag in JSON format).
        /// </summary>
        Json = 1,
        /// <summary>
        /// High-performance compact binary pages with Brotli compression (.xod / .xtab / .xpag in XODB binary format).
        /// </summary>
        Binary = 2
    }
}
