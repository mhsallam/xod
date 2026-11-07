using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xod.Engines.Serializers;
using Xod.Helpers;

namespace Xod
{
    /// <summary>
    /// Converts a XOD / XOD++ database between XML, JSON, and Binary storage formats without requiring model assemblies.
    /// </summary>
    public static class XodMigrator
    {
        public static int ConvertDatabase(
            string sourceDirectoryOrFile,
            string targetDirectory,
            StorageFormat targetFormat,
            string password = null)
        {
            if (string.IsNullOrEmpty(sourceDirectoryOrFile))
                throw new ArgumentNullException(nameof(sourceDirectoryOrFile));
            if (string.IsNullOrEmpty(targetDirectory))
                throw new ArgumentNullException(nameof(targetDirectory));

            string sourceDir = File.Exists(sourceDirectoryOrFile)
                ? Path.GetDirectoryName(Path.GetFullPath(sourceDirectoryOrFile))
                : Path.GetFullPath(sourceDirectoryOrFile);

            string targetDir = Path.GetFullPath(targetDirectory);
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source database directory not found: {sourceDir}");

            Directory.CreateDirectory(targetDir);

            string hashedPassword = string.IsNullOrEmpty(password)
                ? null
                : CryptoHelper.GetSHA256HashData(password);

            var targetSerializer = StorageSerializerFactory.Get(targetFormat);
            var files = Directory.GetFiles(sourceDir)
                .Where(f => f.EndsWith(".xod", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".xodj", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".xodb", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".xtab", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".xpag", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".xidx", StringComparison.OrdinalIgnoreCase))
                .ToList();

            int convertedCount = 0;
            foreach (var file in files)
            {
                byte[] rawBytes = File.ReadAllBytes(file);
                if (!string.IsNullOrEmpty(hashedPassword))
                {
                    rawBytes = FileCryptoHelper.DecryptBytes(rawBytes, hashedPassword);
                }

                if (file.EndsWith(".xidx", StringComparison.OrdinalIgnoreCase) &&
                    rawBytes.Length >= 6 &&
                    rawBytes.AsSpan(0, 6).SequenceEqual("XIDX2:"u8))
                {
                    byte[] outIdxBytes = !string.IsNullOrEmpty(hashedPassword)
                        ? FileCryptoHelper.EncryptBytes(rawBytes, hashedPassword)
                        : rawBytes;
                    File.WriteAllBytes(Path.Combine(targetDir, Path.GetFileName(file)), outIdxBytes);
                    convertedCount++;
                    continue;
                }

                StorageFormat detected = StorageSerializerFactory.DetectFormat(rawBytes);
                XDocument doc = StorageSerializerFactory.Get(detected).Deserialize(rawBytes);

                if (doc.Root?.Name.LocalName == nameof(Database))
                {
                    doc.Root.SetAttributeValue("Format", targetFormat.ToString());
                }

                byte[] convertedBytes = targetSerializer.Serialize(doc);
                if (!string.IsNullOrEmpty(hashedPassword))
                {
                    convertedBytes = FileCryptoHelper.EncryptBytes(convertedBytes, hashedPassword);
                }

                string targetPath = Path.Combine(targetDir, Path.GetFileName(file));
                File.WriteAllBytes(targetPath, convertedBytes);
                convertedCount++;
            }

            return convertedCount;
        }
    }
}
