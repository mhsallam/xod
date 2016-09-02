using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Xod.Helpers
{
    internal class FileCryptoHelper
    {
        internal static void EncryptFile(string inputFile, string outputFile, string key)
        {
            // AesCryptoServiceProvider aes = new AesCryptoServiceProvider();
            Aes aes = System.Security.Cryptography.Aes.Create();
            aes.Key = ASCIIEncoding.ASCII.GetBytes(key).Take(32).ToArray();
            aes.IV = ASCIIEncoding.ASCII.GetBytes(key).Take(16).ToArray();

            using (FileStream fsInput = new FileStream(inputFile, FileMode.Open, FileAccess.Read)) {
                FileStream fsEncrypted = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
                using (CryptoStream cryptostream = new CryptoStream(fsEncrypted, aes.CreateEncryptor(), CryptoStreamMode.Write)) {
                    byte[] bytearrayinput = new byte[fsInput.Length];
                    fsInput.Read(bytearrayinput, 0, bytearrayinput.Length);
                    cryptostream.Write(bytearrayinput, 0, bytearrayinput.Length);
                }
            }

        }

        internal static void EncryptContent(string content, string outputFile, string key)
        {
            // AesCryptoServiceProvider aes = new AesCryptoServiceProvider();
            Aes aes = System.Security.Cryptography.Aes.Create();
            aes.Key = ASCIIEncoding.ASCII.GetBytes(key).Take(32).ToArray();
            aes.IV = ASCIIEncoding.ASCII.GetBytes(key).Take(16).ToArray();

            FileStream fsEncrypted = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
            using (CryptoStream cryptostream = new CryptoStream(fsEncrypted, aes.CreateEncryptor(), CryptoStreamMode.Write)) {
                byte[] bytearrayinput = new byte[content.Length];
                cryptostream.Write(ASCIIEncoding.ASCII.GetBytes(content), 0, content.Length);
            }

        }

        internal static void DecryptFile(string inputFile, string outputFile, string key)
        {
            Aes aes = System.Security.Cryptography.Aes.Create();
            //A 64 bit key and IV is required for this provider.
            //Set secret key For aes algorithm.
            aes.Key = ASCIIEncoding.ASCII.GetBytes(key).Take(32).ToArray();
            //Set initialization vector.
            aes.IV = ASCIIEncoding.ASCII.GetBytes(key).Take(16).ToArray();

            //Create a file stream to read the encrypted file back.
            FileStream fsread = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
            //Create a aes decryptor from the aes instance.
            ICryptoTransform aesDecrypt = aes.CreateDecryptor();
            //Create crypto stream set to read and do a 
            //aes decryption transform on incoming bytes.
            using (CryptoStream cryptostreamDecr = new CryptoStream(fsread, aesDecrypt, CryptoStreamMode.Read)) {
                //Print the contents of the decrypted file.
                //Create a file stream to write the decrypted file back.
                FileStream fswrite = new FileStream(outputFile, FileMode.OpenOrCreate, FileAccess.Write);
                using(StreamWriter fsDecrypted = new StreamWriter(fswrite)) {
                    fsDecrypted.Write(new StreamReader(cryptostreamDecr).ReadToEnd());
                }
            }
        }

        internal static string DecryptContent(string inputFile, string key)
        {
            Aes aes = System.Security.Cryptography.Aes.Create();
            //A 64 bit key and IV is required for this provider.
            //Set secret key For aes algorithm.
            aes.Key = ASCIIEncoding.ASCII.GetBytes(key).Take(32).ToArray();
            //Set initialization vector.
            aes.IV = ASCIIEncoding.ASCII.GetBytes(key).Take(16).ToArray();

            //Create a file stream to read the encrypted file back.
            FileStream fsread = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
            //Create a aes decryptor from the aes instance.
            ICryptoTransform aesDecrypt = aes.CreateDecryptor();
            //Create crypto stream set to read and do a 
            //aes decryption transform on incoming bytes.
            using (CryptoStream cryptostreamDecr = new CryptoStream(fsread, aesDecrypt, CryptoStreamMode.Read)) {
                using (StreamReader sr = new StreamReader(cryptostreamDecr)) {
                    string content = sr.ReadToEnd();
                    return content;
                }
            }
        }
    }
}