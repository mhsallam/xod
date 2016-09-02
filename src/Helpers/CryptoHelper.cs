using System.Security.Cryptography;
using System.Text;

namespace Xod.Helpers
{
    public class CryptoHelper
    {
        // Function to Generate a 64 bits Key.
        public static string GenerateKey()
        {
            // Create an instance of Symetric Algorithm. Key and IV is generated automatically.
            // AesCryptoServiceProvider aesCrypto = (AesCryptoServiceProvider)AesCryptoServiceProvider.Create();
            Aes aesCrypto = System.Security.Cryptography.Aes.Create();

            // Use the Automatically generated key for Encryption. 
            return ASCIIEncoding.ASCII.GetString(aesCrypto.Key);
        }

        /// <summary>
        /// take any string and encrypt it using SHA1 then
        /// return the encrypted data
        /// </summary>
        /// <param name="data">input text you will enterd to encrypt it</param>
        /// <returns>return the encrypted text as hexadecimal string</returns>
        public static string GetSHA1HashData(string data)
        {
            //create new instance of md5
            SHA1 sha1 = SHA1.Create();

            //convert the input text to array of bytes
            byte[] hashData = sha1.ComputeHash(Encoding.ASCII.GetBytes(data));

            //create new instance of StringBuilder to save hashed data
            StringBuilder returnValue = new StringBuilder();

            //loop for each byte and add it to StringBuilder
            for (int i = 0; i < hashData.Length; i++)
            {
                returnValue.Append(hashData[i].ToString());
            }

            // return hexadecimal string
            return returnValue.ToString();
        }
        /// <summary>
        /// take any string and encrypt it using SHA1 then
        /// return the encrypted data
        /// </summary>
        /// <param name="data">input text you will enterd to encrypt it</param>
        /// <returns>return the encrypted text as hexadecimal string</returns>
        public static string GetSHA256HashData(string data)
        {
            //create new instance of SHA256
            SHA256 sha = SHA256.Create();

            //convert the input text to array of bytes
            byte[] hashData = sha.ComputeHash(Encoding.ASCII.GetBytes(data));

            //create new instance of StringBuilder to save hashed data
            StringBuilder returnValue = new StringBuilder();

            //loop for each byte and add it to StringBuilder
            for (int i = 0; i < hashData.Length; i++)
            {
                returnValue.Append(hashData[i].ToString());
            }

            // return hexadecimal string
            return returnValue.ToString();
        }

        /// <summary>
        /// take any string and encrypt it using SHA1 then
        /// return the encrypted data
        /// </summary>
        /// <param name="data">input text you will enterd to encrypt it</param>
        /// <returns>return the encrypted text as hexadecimal string</returns>
        public static string GetSHA512HashData(string data)
        {
            //create new instance of SHA512
            SHA512 sha = SHA512.Create();

            //convert the input text to array of bytes
            byte[] hashData = sha.ComputeHash(Encoding.ASCII.GetBytes(data));
            // System.Text.ASCIIEncoding  encoding = new System.Text.ASCIIEncoding();
            // byte[] hashData = encoding.GetBytes(data);

            //create new instance of StringBuilder to save hashed data
            StringBuilder returnValue = new StringBuilder();

            //loop for each byte and add it to StringBuilder
            for (int i = 0; i < hashData.Length; i++)
            {
                returnValue.Append(hashData[i].ToString());
            }

            // return hexadecimal string
            return returnValue.ToString();
        }

        /// <summary>
        /// take any string and encrypt it using MD5 then
        /// return the encrypted data 
        /// </summary>
        /// <param name="data">input text you will enterd to encrypt it</param>
        /// <returns>return the encrypted text as hexadecimal string</returns>
        public static string GetMD5HashData(string data)
        {
            //create new instance of md5
            MD5 md5 = MD5.Create();

            //convert the input text to array of bytes
            byte[] hashData = md5.ComputeHash(Encoding.ASCII.GetBytes(data));

            //create new instance of StringBuilder to save hashed data
            StringBuilder returnValue = new StringBuilder();

            //loop for each byte and add it to StringBuilder
            for (int i = 0; i < hashData.Length; i++)
            {
                returnValue.Append(hashData[i].ToString());
            }

            // return hexadecimal string
            return returnValue.ToString();
        }
    }
}