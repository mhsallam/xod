using System;
using System.Security.Cryptography;
using System.Text;

namespace Xod.Helpers
{
    public static class CryptoHelper
    {
        private const string PropertyCipherPrefix = "enc:aes256:";
        // 2.0's property key: PBKDF2 over the database's hashed password, or over this constant without one
        // (ISS-050). Kept to read values 2.0 wrote (PropertyKeyService).
        internal static readonly byte[] LegacyPropertySalt = Encoding.UTF8.GetBytes("XOD++::PropertyEncryptionSalt::v2");
        internal const string LegacyPropertyPassphrase = "XOD++DefaultPropertySecretKey";

        public static string GenerateKey()
        {
            using Aes aes = Aes.Create();
            return Convert.ToBase64String(aes.Key);
        }

        public static string GetSHA1HashData(string data)
        {
            if (data == null) return null;
            byte[] hashData = SHA1.HashData(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hashData).ToLowerInvariant();
        }

        public static string GetSHA256HashData(string data)
        {
            if (data == null) return null;
            byte[] hashData = SHA256.HashData(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hashData).ToLowerInvariant();
        }

        public static string GetSHA512HashData(string data)
        {
            if (data == null) return null;
            byte[] hashData = SHA512.HashData(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hashData).ToLowerInvariant();
        }

        public static string GetMD5HashData(string data)
        {
            if (data == null) return null;
            byte[] hashData = MD5.HashData(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hashData).ToLowerInvariant();
        }

        /// <summary>
        /// Idempotently hashes a property value using the specified one-way <see cref="CryptoMethod"/>.
        /// If the value is already a valid hex digest for that algorithm, it is returned unchanged so updates do not re-hash existing digests.
        /// </summary>
        public static string HashProperty(string data, CryptoMethod method)
        {
            if (string.IsNullOrEmpty(data)) return data;

            int expectedHexLen = method switch
            {
                CryptoMethod.MD5 => 32,
                CryptoMethod.SHA1 => 40,
                CryptoMethod.SHA256 => 64,
                CryptoMethod.SHA512 => 128,
                _ => 0
            };

            if (expectedHexLen > 0 && IsLowerHexDigest(data, expectedHexLen))
                return data;

            return method switch
            {
                CryptoMethod.MD5 => GetMD5HashData(data),
                CryptoMethod.SHA1 => GetSHA1HashData(data),
                CryptoMethod.SHA256 => GetSHA256HashData(data),
                CryptoMethod.SHA512 => GetSHA512HashData(data),
                _ => data
            };
        }

        /// <summary>
        /// Verifies a plaintext string against a stored one-way hash digest using constant-time comparison.
        /// </summary>
        public static bool VerifyHash(string plainText, string storedHash, CryptoMethod method = CryptoMethod.SHA256)
        {
            if (plainText == null || string.IsNullOrEmpty(storedHash))
                return false;

            string computed = method switch
            {
                CryptoMethod.MD5 => GetMD5HashData(plainText),
                CryptoMethod.SHA1 => GetSHA1HashData(plainText),
                CryptoMethod.SHA256 => GetSHA256HashData(plainText),
                CryptoMethod.SHA512 => GetSHA512HashData(plainText),
                _ => plainText
            };

            if (CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computed),
                Encoding.UTF8.GetBytes(storedHash.ToLowerInvariant())))
                return true;

            // A digest XOD 1.x stored (ISS-151).
            string legacy = Legacy1xDigest(plainText, method);
            return legacy != null && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(legacy), Encoding.UTF8.GetBytes(storedHash));
        }

        /// <summary>
        /// Whether <paramref name="value"/> has the form of a digest XOD 1.x stored (<see cref="Legacy1xDigest"/>):
        /// decimal digits that read as exactly the hash's bytes, each 0 to 255 written without a leading zero (16 for
        /// MD5, 20 for SHA1). A value of that form can't be told from a digest by itself: the caller also asks that
        /// it is what the row stores (XmlEngine.StoredDigest).
        /// </summary>
        internal static bool IsLegacy1xDigest(string value, CryptoMethod method)
        {
            int bytes = method switch { CryptoMethod.MD5 => 16, CryptoMethod.SHA1 => 20, _ => 0 };
            if (bytes == 0 || string.IsNullOrEmpty(value) || value.Length < bytes || value.Length > bytes * 3)
                return false;
            foreach (char c in value)
                if (c < '0' || c > '9')
                    return false;

            // Can the digits be cut into exactly that many byte values? reach[i]: the counts of bytes the first i
            // digits can make (a bit per count).
            var reach = new ulong[value.Length + 1];
            reach[0] = 1;
            for (int i = 0; i < value.Length; i++)
            {
                if (reach[i] == 0)
                    continue;
                for (int len = 1; len <= 3 && i + len <= value.Length; len++)
                {
                    if (len > 1 && value[i] == '0')
                        break;
                    if (len == 3 && int.Parse(value.AsSpan(i, 3), provider: System.Globalization.CultureInfo.InvariantCulture) > 255)
                        break;
                    reach[i + len] |= reach[i] << 1;
                }
            }
            return (reach[value.Length] & (1UL << bytes)) != 0;
        }

        /// <summary>
        /// The digest XOD 1.x stored for a <c>[Crypto]</c> value (MD5, its default, or SHA1): the decimal digits of
        /// each byte of the hash of the value's ASCII bytes, run together (<c>xod.core/src/Helpers/CryptoHelper.cs</c>).
        /// From September 2016 1.x hashed ASCII, each other character as '?'; before, the system's code page, so a
        /// value with other characters from those versions doesn't verify. Null for a method 1.x didn't have.
        /// </summary>
        internal static string Legacy1xDigest(string plainText, CryptoMethod method)
        {
            if (plainText == null)
                return null;
            byte[] bytes = Encoding.ASCII.GetBytes(plainText);
            byte[] hash = method switch
            {
                CryptoMethod.MD5 => MD5.HashData(bytes),
                CryptoMethod.SHA1 => SHA1.HashData(bytes),
                _ => null
            };
            if (hash == null)
                return null;
            var digits = new StringBuilder(hash.Length * 3);
            foreach (byte b in hash)
                digits.Append(b.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return digits.ToString();
        }

        private static bool IsLowerHexDigest(string value, int expectedLength)
        {
            if (value == null || value.Length != expectedLength)
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex) return false;
            }
            return true;
        }

        /// <summary>
        /// Encrypts a string in XOD 2.0's property format, under a key derived from
        /// <paramref name="passphrase"/>, or from a constant built into the library when it is null, which
        /// anyone with XOD can decrypt (ISS-050). XOD no longer uses it: <c>[Crypto(AES256)]</c> properties are
        /// encrypted with the database's own key (DatabaseOptions.PropertyEncryptionKey, PLAN-049).
        /// </summary>
        [Obsolete("XOD 2.0's property format: with no passphrase it uses a key built into the library. [Crypto(AES256)] properties are encrypted by the database itself.")]
        public static string EncryptProperty(string plainText, string passphrase = null)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            if (plainText.StartsWith(PropertyCipherPrefix, StringComparison.Ordinal))
                return plainText;

            byte[] key = LegacyPropertyKey(passphrase);
            byte[] nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize); // 12 bytes
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] cipherBytes = new byte[plainBytes.Length];
            byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes

            using (var aesGcm = new AesGcm(key, AesGcm.TagByteSizes.MaxSize))
            {
                aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);
            }

            byte[] payload = new byte[nonce.Length + tag.Length + cipherBytes.Length];
            Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
            Buffer.BlockCopy(cipherBytes, 0, payload, nonce.Length + tag.Length, cipherBytes.Length);

            return PropertyCipherPrefix + Convert.ToBase64String(payload);
        }

        /// <summary>
        /// Decrypts a string in XOD 2.0's property format (see <see cref="EncryptProperty"/>). XOD reads
        /// <c>[Crypto(AES256)]</c> properties itself, in both formats.
        /// </summary>
        [Obsolete("XOD 2.0's property format. [Crypto(AES256)] properties are decrypted by the database itself.")]
        public static string DecryptProperty(string cipherText, string passphrase = null)
        {
            if (string.IsNullOrEmpty(cipherText) || !cipherText.StartsWith(PropertyCipherPrefix, StringComparison.Ordinal))
                return cipherText;

            string base64 = cipherText.Substring(PropertyCipherPrefix.Length);
            byte[] payload = Convert.FromBase64String(base64);

            int nonceSize = AesGcm.NonceByteSizes.MaxSize;
            int tagSize = AesGcm.TagByteSizes.MaxSize;
            if (payload.Length < nonceSize + tagSize)
                throw new SecurityException("Corrupted encrypted property payload.");

            byte[] nonce = new byte[nonceSize];
            byte[] tag = new byte[tagSize];
            byte[] cipherBytes = new byte[payload.Length - nonceSize - tagSize];

            Buffer.BlockCopy(payload, 0, nonce, 0, nonceSize);
            Buffer.BlockCopy(payload, nonceSize, tag, 0, tagSize);
            Buffer.BlockCopy(payload, nonceSize + tagSize, cipherBytes, 0, cipherBytes.Length);

            byte[] key = LegacyPropertyKey(passphrase);
            byte[] plainBytes = new byte[cipherBytes.Length];

            try
            {
                using var aesGcm = new AesGcm(key, tagSize);
                aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (CryptographicException)
            {
                throw new SecurityException("Failed to decrypt AES-256 property value.");
            }
        }

        /// <summary>The keys derived in this process, for a test of how many a context derives (ISS-152).</summary>
        internal static long Derivations;

        /// <summary>
        /// A key derived from a password and a salt (PBKDF2, SHA-256). Not cached here: whoever needs a key again holds
        /// it for as long as it needs it (FileKeys, PropertyKeyService). A process-wide cache kept every key, one more
        /// at every file write, after its database was closed (ISS-152).
        /// </summary>
        internal static byte[] DeriveKey(string password, byte[] salt, int iterations = 100_000)
        {
            System.Threading.Interlocked.Increment(ref Derivations);
            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password ?? string.Empty),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                32);
        }

        // The key of XOD 2.0's property format under the passphrase built into the library: no secret, made once.
        private static readonly Lazy<byte[]> BuiltInPropertyKey =
            new Lazy<byte[]>(() => DeriveKey(LegacyPropertyPassphrase, LegacyPropertySalt));

        internal static byte[] LegacyPropertyKey(string passphrase) =>
            passphrase == null || passphrase == LegacyPropertyPassphrase
                ? BuiltInPropertyKey.Value
                : DeriveKey(passphrase, LegacyPropertySalt);
    }
}
