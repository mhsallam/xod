using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Xod.Helpers
{
    internal class FileCryptoHelper
    {
        internal static void EncryptFile(string inputFile, string outputFile, string key)
        {
            FileStream fsInput = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
            FileStream fsEncrypted = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
            AesCryptoServiceProvider aes = new AesCryptoServiceProvider();
            aes.Key = ASCIIEncoding.ASCII.GetBytes(key).Take(32).ToArray();
            aes.IV = ASCIIEncoding.ASCII.GetBytes(key).Take(16).ToArray();
            ICryptoTransform aesEncrypt = aes.CreateEncryptor();
            CryptoStream cryptostream = new CryptoStream(fsEncrypted, aesEncrypt, CryptoStreamMode.Write);

            byte[] bytearrayinput = new byte[fsInput.Length];
            fsInput.Read(bytearrayinput, 0, bytearrayinput.Length);
            cryptostream.Write(bytearrayinput, 0, bytearrayinput.Length);
            cryptostream.Close();
            fsInput.Close();
            fsEncrypted.Close();
        }

        internal static void EncryptContent(string content, string outputFile, string key)
        {
            FileStream fsEncrypted = null;
            CryptoStream cryptostream = null;
            while (FileHelper.IsReady(outputFile))
            {
                System.Threading.Thread.Sleep(100);
            }

            try
            {
                fsEncrypted = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
                AesCryptoServiceProvider aes = new AesCryptoServiceProvider();
                aes.Key = ASCIIEncoding.ASCII.GetBytes(key).Take(32).ToArray();
                aes.IV = ASCIIEncoding.ASCII.GetBytes(key).Take(16).ToArray();
                ICryptoTransform aesEncrypt = aes.CreateEncryptor();
                cryptostream = new CryptoStream(fsEncrypted, aesEncrypt, CryptoStreamMode.Write);

                byte[] bytearrayinput = new byte[content.Length];
                cryptostream.Write(ASCIIEncoding.ASCII.GetBytes(content), 0, content.Length);
            }
            finally
            {
                if (cryptostream != null)
                    cryptostream.Close();

                if (fsEncrypted != null)
                    fsEncrypted.Close();
            }
        }

        internal static void DecryptFile(string inputFile, string outputFile, string key)
        {
            CryptoStream cryptostreamDecr = null;
            StreamWriter fsDecrypted = null;
            while (FileHelper.IsReady(outputFile))
            {
                System.Threading.Thread.Sleep(100);
            }

            try
            {
                AesCryptoServiceProvider aes = new AesCryptoServiceProvider();
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
                cryptostreamDecr = new CryptoStream(fsread, aesDecrypt, CryptoStreamMode.Read);
                //Print the contents of the decrypted file.
                fsDecrypted = new StreamWriter(outputFile);
                fsDecrypted.Write(new StreamReader(cryptostreamDecr).ReadToEnd());
                fsDecrypted.Flush();
            }
            finally
            {
                if (fsDecrypted != null)
                    fsDecrypted.Close();

                if (cryptostreamDecr != null)
                    cryptostreamDecr.Close();
            }
        }

        internal static string DecryptContent(string inputFile, string key)
        {
            while (FileHelper.IsReady(inputFile))
            {
                System.Threading.Thread.Sleep(100);
            }

            string content = null;
            StreamReader sr = null;
            try
            {
                AesCryptoServiceProvider aes = new AesCryptoServiceProvider();
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
                CryptoStream cryptostreamDecr = new CryptoStream(fsread, aesDecrypt, CryptoStreamMode.Read);
                sr = new StreamReader(cryptostreamDecr);
                content = sr.ReadToEnd();
            }
            finally
            {
                if(sr != null)
                    sr.Close();
            }
            return content;
        }
    }
}
