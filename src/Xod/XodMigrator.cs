using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xod.Engines.Serializers;
using Xod.Helpers;
using Xod.Services;

namespace Xod
{
    /// <summary>
    /// Converts a XOD / XOD++ database between XML, JSON, and Binary storage formats without requiring model assemblies.
    /// </summary>
    public static class XodMigrator
    {
        /// <summary>
        /// Converts the database in <paramref name="sourceDirectoryOrFile"/> (its folder, or its database file) to
        /// <paramref name="targetFormat"/> in <paramref name="targetDirectory"/>, and returns how many files it
        /// converted.
        /// <para>The source is held alone while it is read, as a context opened with
        /// <see cref="DatabaseOptions.LockDatabase"/> holds it: refused with <see cref="DatabaseLockedException"/>
        /// while a context or process has it open, and no other can open it meanwhile. What a crash left in it (a
        /// commit or a password change that didn't finish) is recovered first, as an open would.</para>
        /// <para>Into another folder, which must be new or empty (one that holds anything is refused): the database
        /// is written beside it and moved into place whole, so a crash leaves no target, only a folder named
        /// <c>.&lt;target&gt;.converting-&lt;id&gt;</c> beside it, which can be deleted. Into the source folder itself:
        /// each file is replaced whole (written beside it, then moved over it), so a crash leaves a database that
        /// opens, each file in the old format or the new (a file's format is read from the file).</para>
        /// <para>As no write of this library is, the files aren't flushed to the device (a power cut is another
        /// matter; PLAN-046 section 9).</para>
        /// </summary>
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

            // Full paths without a trailing separator, so that one folder is one string (SameFolder).
            string sourceDir = Path.TrimEndingDirectorySeparator(File.Exists(sourceDirectoryOrFile)
                ? Path.GetDirectoryName(Path.GetFullPath(sourceDirectoryOrFile))
                : Path.GetFullPath(sourceDirectoryOrFile));

            string targetDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source database directory not found: {sourceDir}");

            bool inPlace = SameFolder(sourceDir, targetDir);
            if (!inPlace && Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
                throw new XodException($"The target folder '{targetDir}' isn't empty. Convert into a new or empty folder: a database's folder holds only that database.");

            // The folder is the database; each database file in it is opened through its own lock file.
            var databaseFiles = Directory.GetFiles(sourceDir).Where(DatabaseFiles.IsDatabaseFile).ToList();
            if (databaseFiles.Count == 0)
                throw new XodException($"'{sourceDir}' holds no XOD database: no .xod, .xodj or .xodb file.");

            string hashedPassword = string.IsNullOrEmpty(password)
                ? null
                : CryptoHelper.GetSHA256HashData(password);

            var holds = new List<FileStream>();
            try
            {
                foreach (var databaseFile in databaseFiles)
                {
                    try
                    {
                        holds.Add(IOService.OpenLockFile(databaseFile, exclusive: true, retryTimeoutMs: 0));
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // The folder can't be written (read-only media): neither this conversion's recovery nor any
                        // context can write it, as for a context that opens it to read (IOService.OpenAndRecover).
                    }
                }

                IOService.RecoverInterruptedWrites(sourceDir);

                var files = Directory.GetFiles(sourceDir).Where(DatabaseFiles.IsOfDatabase).ToList();
                return inPlace
                    ? ConvertInPlace(files, targetFormat, hashedPassword)
                    : ConvertInto(files, targetDir, targetFormat, hashedPassword);
            }
            finally
            {
                foreach (var hold in holds)
                    hold.Dispose();
            }
        }

        // Each file replaced whole: a crash leaves every file in one format or the other, and a .tmp beside one,
        // which the next open deletes.
        private static int ConvertInPlace(List<string> files, StorageFormat targetFormat, string hashedPassword)
        {
            int converted = 0;
            foreach (var file in files)
            {
                byte[] bytes = ConvertFile(file, targetFormat, hashedPassword);
                IOService.WriteRawBytesAtomic(file, bytes, bytes.Length);
                converted++;
                FaultInjection.Step("convert-written", converted);
            }
            return converted;
        }

        // The database written in a folder beside the target, on the same volume, then moved into place whole. A
        // failure deletes that folder, which only this call has written; a crash leaves it.
        private static int ConvertInto(List<string> files, string targetDir, StorageFormat targetFormat, string hashedPassword)
        {
            string parent = Path.GetDirectoryName(targetDir);
            Directory.CreateDirectory(parent);
            string staging = Path.Combine(parent, "." + Path.GetFileName(targetDir) + ".converting-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);
            try
            {
                int converted = 0;
                foreach (var file in files)
                {
                    File.WriteAllBytes(Path.Combine(staging, Path.GetFileName(file)), ConvertFile(file, targetFormat, hashedPassword));
                    converted++;
                    FaultInjection.Step("convert-written", converted);
                }

                // An empty target folder makes way (Directory.Delete refuses one that isn't empty any more).
                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir);
                Directory.Move(staging, targetDir);
                return converted;
            }
            catch
            {
                // Best effort: the failure is what this call reports; a folder left here is only ever this call's.
                try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        private static byte[] ConvertFile(string file, StorageFormat targetFormat, string hashedPassword)
        {
            byte[] rawBytes = File.ReadAllBytes(file);
            if (!string.IsNullOrEmpty(hashedPassword))
                rawBytes = FileCryptoHelper.DecryptBytes(rawBytes, hashedPassword);

            byte[] convertedBytes;
            if (file.EndsWith(".xidx", StringComparison.OrdinalIgnoreCase) &&
                rawBytes.Length >= 6 &&
                rawBytes.AsSpan(0, 6).SequenceEqual("XIDX2:"u8))
            {
                // A binary index is the same in every format.
                convertedBytes = rawBytes;
            }
            else
            {
                StorageFormat detected = StorageSerializerFactory.DetectFormat(rawBytes);
                XDocument doc = StorageSerializerFactory.Get(detected).Deserialize(rawBytes);
                if (doc.Root?.Name.LocalName == nameof(Database))
                    doc.Root.SetAttributeValue("Format", targetFormat.ToString());
                convertedBytes = StorageSerializerFactory.Get(targetFormat).Serialize(doc);
            }

            return string.IsNullOrEmpty(hashedPassword)
                ? convertedBytes
                : FileCryptoHelper.EncryptBytes(convertedBytes, hashedPassword);
        }

        // Two full paths without trailing separators, compared as the platform's usual file system does (a folder
        // reached another way, through a link, isn't recognised: it then holds the source's files, so it is refused
        // as a target that isn't empty).
        private static bool SameFolder(string a, string b) =>
            string.Equals(a, b, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
