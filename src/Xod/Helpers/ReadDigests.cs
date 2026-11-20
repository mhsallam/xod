using System.Runtime.CompilerServices;

namespace Xod.Helpers
{
    /// <summary>
    /// ISS-171: the digests reads gave back, known by the string object itself. A read gives a one-way
    /// <c>[Crypto]</c> value (MD5, SHA1, SHA256, SHA512) back as the text the row stores, so a write of it can't tell
    /// it from a value the caller typed: a 2.x digest is told by its hex form (<see cref="CryptoHelper.HashProperty"/>),
    /// but a XOD 1.x digest is decimal digits, which a value can be too (<see cref="CryptoHelper.IsLegacy1xDigest"/>),
    /// and only an update whose replaced row stores it could tell (ISS-151). Every other write hashed it again: a row
    /// read from a 1.x database and inserted into another, a digest read and given to another row, a keyless child its
    /// parent's update writes again. A read from a database that says it holds 1.x digests
    /// (<see cref="DatabaseOptions.Legacy1xDigests"/>) now notes each such string it gives back, with the method it was
    /// read under, and a write keeps a value of a digest's form that is that string under that method. A string the
    /// caller built, with the same digits or not, is another object, and is hashed as a value is. Only on that word: the
    /// stored text can't tell a 1.x digest from a plaintext of digits, and nothing records which version wrote a
    /// database.
    /// <para>
    /// Held weakly: an entry goes with its string. Process-wide, so a digest read through one context and written
    /// through another (another database) is known too. Read through <c>XmlEngine.ReadValue</c> (every stored scalar and
    /// attribute of a class) and SQL's columns read by a recorded schema; not SQL's columns of a table that records no
    /// schema, which carry no method.
    /// </para>
    /// <para>
    /// What it can't know. A copy that makes a new string (serialized and back, a mapper, <c>new string(...)</c>) is
    /// another object: known only by the row an update replaces (ISS-151), hashed by any other write. And the stored text
    /// is a digest only if it was written as one: in a database said to hold 1.x digests, a plaintext of digits a row
    /// stores under <c>[Crypto]</c> (2.0.1 or earlier, ISS-064, or a property given <c>[Crypto]</c> later) reads as a
    /// 1.x digest and is kept.
    /// </para>
    /// </summary>
    internal static class ReadDigests
    {
        private static readonly ConditionalWeakTable<string, StrongBox<CryptoMethod>> Read = new();

        /// <summary>
        /// Notes a one-way value a read gives back as it is stored, under the method it was read under: only one of a
        /// 1.x digest's form, the one thing a write keeps for it (a 2.x digest is told by its hex form; SHA256 and SHA512
        /// had no 1.x form). So a database with no 1.x digests notes nothing.
        /// </summary>
        internal static void Note(object value, CryptoMethod method)
        {
            if (value is string text && CryptoHelper.IsLegacy1xDigest(text, method))
                Read.AddOrUpdate(text, new StrongBox<CryptoMethod>(method));
        }

        /// <summary>Whether <paramref name="value"/> is a string a read gave back for a property of <paramref name="method"/>.</summary>
        internal static bool WasRead(object value, CryptoMethod method) =>
            value is string s && Read.TryGetValue(s, out var noted) && noted.Value == method;
    }
}
