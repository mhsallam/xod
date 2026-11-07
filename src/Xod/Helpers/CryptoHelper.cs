using System;
using System.Security.Cryptography;
using System.Text;

namespace Xod.Helpers
{
    public static class CryptoHelper
    {
        private const string PropertyCipherPrefix = "enc:aes256:";
        private static readonly byte[] DefaultPropertyKeySalt = Encoding.UTF8.GetBytes("XOD++::PropertyEncryptionSalt::v2");

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

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computed),
                Encoding.UTF8.GetBytes(storedHash.ToLowerInvariant()));
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
        /// Encrypts a string property value using authenticated AES-256-GCM.
        /// </summary>
        public static string EncryptProperty(string plainText, string passphrase = null)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            if (plainText.StartsWith(PropertyCipherPrefix, StringComparison.Ordinal))
                return plainText;

            byte[] key = DeriveKey(passphrase ?? "XOD++DefaultPropertySecretKey", DefaultPropertyKeySalt);
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
        /// Decrypts a string property value encrypted by <see cref="EncryptProperty"/>.
        /// </summary>
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

            byte[] key = DeriveKey(passphrase ?? "XOD++DefaultPropertySecretKey", DefaultPropertyKeySalt);
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

        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> DerivedKeyCache = new();

        internal static byte[] DeriveKey(string password, byte[] salt, int iterations = 100_000)
        {
            string cacheKey = $"{password ?? string.Empty}::{Convert.ToBase64String(salt)}::{iterations}";
            return DerivedKeyCache.GetOrAdd(cacheKey, _ =>
                Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(password ?? string.Empty),
                    salt,
                    iterations,
                    HashAlgorithmName.SHA256,
                    32));
        }
    }
}
