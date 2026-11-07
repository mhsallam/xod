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

            // Legacy XOD 1.x fallback (AES-CBC with ASCII key slice)
            try
            {
                using Aes aes = Aes.Create();
                aes.Key = Encoding.ASCII.GetBytes(key.PadRight(32, '0')).Take(32).ToArray();
                aes.IV = Encoding.ASCII.GetBytes(key.PadRight(16, '0')).Take(16).ToArray();
                using var ms = new MemoryStream(encryptedData);
                using var cryptoStream = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
                using var outStream = new MemoryStream();
                cryptoStream.CopyTo(outStream);
                return outStream.ToArray();
            }
            catch (Exception)
            {
                throw new SecurityException();
            }
        }

        internal static void EncryptFile(string inputFile, string outputFile, string key)
        {
            byte[] plainBytes = File.ReadAllBytes(inputFile);
            byte[] encrypted = EncryptBytes(plainBytes, key);
            File.WriteAllBytes(outputFile, encrypted);
        }

        internal static void EncryptContent(string content, string outputFile, string key)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(content);
            byte[] encrypted = EncryptBytes(plainBytes, key);
            File.WriteAllBytes(outputFile, encrypted);
        }

        internal static void DecryptFile(string inputFile, string outputFile, string key)
        {
            byte[] encrypted = File.ReadAllBytes(inputFile);
            byte[] plainBytes = DecryptBytes(encrypted, key);
            File.WriteAllBytes(outputFile, plainBytes);
        }

        internal static string DecryptContent(string inputFile, string key)
        {
            byte[] encrypted = File.ReadAllBytes(inputFile);
            byte[] plainBytes = DecryptBytes(encrypted, key);
            return Encoding.UTF8.GetString(plainBytes);
        }
    }
}
