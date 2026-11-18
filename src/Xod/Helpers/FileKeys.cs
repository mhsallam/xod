using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Xod.Helpers
{
    /// <summary>
    /// The keys a context encrypts and decrypts its files with, under its password: the one owner of the file keys
    /// (ISS-152). A file carries the salt its key was derived with. The context writes its files under one salt, chosen
    /// and derived at its first write, with a new nonce per write; it reads a file with the key of that file's salt,
    /// derived the first time it meets the salt. The keys belong to the context, and are collected with it. Each write
    /// drew a salt of its own and derived its key afresh (100,000 rounds of PBKDF2), into a process-wide cache that was
    /// never emptied: memory grew at every write, and the keys stayed in the process after the database was closed. The
    /// file format is the same.
    /// </summary>
    internal sealed class FileKeys
    {
        /// <summary>
        /// The writes one key encrypts before the next write draws a new salt and key. AES-GCM with random 96-bit nonces
        /// allows at most 2^32 encryptions under one key (NIST SP 800-38D, section 8.3); a context writes for as long as
        /// it is open, so the bound is kept well below that.
        /// </summary>
        internal const long WritesPerKey = 1L << 30;

        private readonly object sync = new object();
        private readonly ConcurrentDictionary<string, Lazy<byte[]>> keysBySalt = new ConcurrentDictionary<string, Lazy<byte[]>>(StringComparer.Ordinal);
        private readonly long writesPerKey;
        private byte[] writeSalt;
        private byte[] writeKey;
        private long writesUnderKey;

        internal FileKeys(string password) : this(password, WritesPerKey)
        {
        }

        /// <summary>With a bound of its own, for the tests.</summary>
        internal FileKeys(string password, long writesPerKey)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("A file key needs a password.", nameof(password));
            if (writesPerKey < 1)
                throw new ArgumentOutOfRangeException(nameof(writesPerKey));
            Password = password;
            this.writesPerKey = writesPerKey;
        }

        internal string Password { get; }

        /// <summary>The salt and key of this write: derived at the context's first write, and again each
        /// <see cref="WritesPerKey"/> writes.</summary>
        internal (byte[] Salt, byte[] Key) ForWrite()
        {
            lock (sync)
            {
                if (writeKey == null || writesUnderKey >= writesPerKey)
                {
                    byte[] salt = RandomNumberGenerator.GetBytes(FileCryptoHelper.SaltSize);
                    byte[] key = CryptoHelper.DeriveKey(Password, salt);
                    keysBySalt[Convert.ToBase64String(salt)] = new Lazy<byte[]>(() => key);
                    writeSalt = salt;
                    writeKey = key;
                    writesUnderKey = 0;
                }
                writesUnderKey++;
                return (writeSalt, writeKey);
            }
        }

        /// <summary>The key of a file written under <paramref name="salt"/>. Derived once per salt, however many reads
        /// ask for it at once: <c>GetOrAdd</c> may run its factory on each racing thread, the <c>Lazy</c> it adds only
        /// once.</summary>
        internal byte[] ForSalt(byte[] salt) =>
            keysBySalt.GetOrAdd(Convert.ToBase64String(salt), _ => new Lazy<byte[]>(() => CryptoHelper.DeriveKey(Password, salt))).Value;

        internal byte[] Encrypt(byte[] plain) => FileCryptoHelper.EncryptBytes(plain, this);

        internal byte[] Decrypt(byte[] data) => FileCryptoHelper.DecryptBytes(data, this);
    }
}
