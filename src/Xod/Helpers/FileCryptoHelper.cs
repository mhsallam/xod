using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Xod.Helpers
{
    internal static class FileCryptoHelper
    {
        private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("XODENC2\0");
        private const int SaltSize = 16;
        private const int NonceSize = 12;
        private const int TagSize = 16;

        internal static byte[] EncryptBytes(byte[] plainBytes, string key)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] tag = new byte[TagSize];
            byte[] cipherBytes = new byte[plainBytes.Length];
            byte[] derivedKey = CryptoHelper.DeriveKey(key, salt);

            using (var aesGcm = new AesGcm(derivedKey, TagSize))
            {
                aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag, MagicHeader);
            }

            using var ms = new MemoryStream(MagicHeader.Length + SaltSize + NonceSize + TagSize + cipherBytes.Length);
            ms.Write(MagicHeader, 0, MagicHeader.Length);
            ms.Write(salt, 0, SaltSize);
            ms.Write(nonce, 0, NonceSize);
            ms.Write(tag, 0, TagSize);
            ms.Write(cipherBytes, 0, cipherBytes.Length);
            return ms.ToArray();
        }

        internal static byte[] DecryptBytes(byte[] encryptedData, string key)
        {
            if (encryptedData == null || encryptedData.Length < MagicHeader.Length)
                throw new SecurityException();

            bool isV2 = true;
            for (int i = 0; i < MagicHeader.Length; i++)
            {
                if (encryptedData[i] != MagicHeader[i])
                {
                    isV2 = false;
                    break;
                }
            }

            if (isV2)
            {
                int headerOffset = MagicHeader.Length;
                if (encryptedData.Length < headerOffset + SaltSize + NonceSize + TagSize)
                    throw new SecurityException();

                byte[] salt = new byte[SaltSize];
                byte[] nonce = new byte[NonceSize];
                byte[] tag = new byte[TagSize];
                int cipherLen = encryptedData.Length - (headerOffset + SaltSize + NonceSize + TagSize);
                byte[] cipherBytes = new byte[cipherLen];

                Buffer.BlockCopy(encryptedData, headerOffset, salt, 0, SaltSize);
                Buffer.BlockCopy(encryptedData, headerOffset + SaltSize, nonce, 0, NonceSize);
                Buffer.BlockCopy(encryptedData, headerOffset + SaltSize + NonceSize, tag, 0, TagSize);
                Buffer.BlockCopy(encryptedData, headerOffset + SaltSize + NonceSize + TagSize, cipherBytes, 0, cipherLen);

                byte[] derivedKey = CryptoHelper.DeriveKey(key, salt);
                byte[] plainBytes = new byte[cipherLen];

                try
                {
                    using var aesGcm = new AesGcm(derivedKey, TagSize);
                    aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes, MagicHeader);
                    return plainBytes;
                }
                catch (CryptographicException)
                {
                    throw new SecurityException();
                }
            }

            // A file of XOD 1.x (2015-2017): AES-CBC, no header (ISS-151).
            string legacyKey = Legacy1xKey(key);
            if (legacyKey == null)
                throw new SecurityException();
            try
            {
                using Aes aes = Aes.Create();
                aes.Key = Encoding.ASCII.GetBytes(legacyKey).Take(32).ToArray();
                aes.IV = Encoding.ASCII.GetBytes(legacyKey).Take(16).ToArray();
                using var ms = new MemoryStream(encryptedData);
                using var cryptoStream = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
                using var outStream = new MemoryStream();
                cryptoStream.CopyTo(outStream);
                return WithoutDeclaration(outStream.ToArray());
            }
            catch (Exception)
            {
                throw new SecurityException();
            }
        }

        /// <summary>
        /// The text of a 1.x file, without its XML declaration. 1.x encrypted a document's text as ASCII
        /// (<c>EncryptContent</c>), under the declaration a <c>StringWriter</c> gives it, <c>encoding="utf-16"</c>,
        /// and read it back as text, which ignores the declaration; a parser of bytes believes it and fails. Without
        /// it the ASCII bytes read as UTF-8, of which ASCII is part.
        /// </summary>
        private static byte[] WithoutDeclaration(byte[] text)
        {
            ReadOnlySpan<byte> span = text;
            if (!span.StartsWith("<?xml"u8))
                return text;
            int end = span.IndexOf("?>"u8);
            return end < 0 ? text : span.Slice(end + 2).ToArray();
        }

        /// <summary>
        /// The key XOD 1.x keyed its files with, from the 2.x one: the decimal digits of each byte of SHA-256 of the
        /// password's ASCII bytes, run together; its first 32 characters are the AES key, its first 16 the IV
        /// (<c>xod.core/src/Helpers/CryptoHelper.cs</c>, <c>FileCryptoHelper.cs</c>). The 2.x key is SHA-256 of the
        /// password's UTF-8 bytes, in hex, which for an ASCII password are its ASCII bytes, so the 1.x hash is the
        /// same and its digits follow. A password with other characters was hashed by 1.x with each of them as '?',
        /// so its database opens with that form of it (versions before September 2016 hashed the system's code
        /// page: such a password doesn't open). Null when <paramref name="key"/> isn't a 2.x key, or gives fewer than
        /// 32 digits (it can't: 32 bytes give at least 32).
        /// </summary>
        internal static string Legacy1xKey(string key)
        {
            if (key == null || key.Length != 64)
                return null;
            byte[] hash;
            try { hash = Convert.FromHexString(key); }
            catch (FormatException) { return null; }
            string digits = string.Concat(hash.Select(b => b.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return digits.Length >= 32 ? digits : null;
        }

        internal static string DecryptContent(string inputFile, string key)
        {
            byte[] encrypted = File.ReadAllBytes(inputFile);
            byte[] plainBytes = DecryptBytes(encrypted, key);
            return Encoding.UTF8.GetString(plainBytes);
        }
    }
}
