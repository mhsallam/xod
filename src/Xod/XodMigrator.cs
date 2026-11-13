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
        /// commit or a password change that didn't finish) is recovered first, as an open would. Refused too: a
        /// database whose lock file can't be opened or created (nothing would then tell whether others have it open),
        /// and a folder that holds more than one database file (one folder holds one database).</para>
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

            // The database as a context finds it: a folder with two database files is refused (ISS-085), since a
            // context opened through the other would write the tables this conversion holds.
            var (databaseFile, resolvedFolder) = DatabaseFiles.Resolve(sourceDirectoryOrFile);
            FaultInjection.Step("open-checked");
            // Full paths without a trailing separator, so that one folder is one string (SameFolder).
            string sourceDir = Path.TrimEndingDirectorySeparator(resolvedFolder);
            string targetDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source database directory not found: {sourceDir}");
            if (!File.Exists(databaseFile))
                throw new XodException($"'{sourceDir}' holds no XOD database: no .xod, .xodj or .xodb file.");

            bool inPlace = SameFolder(sourceDir, targetDir);
            if (!inPlace && Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
                throw new XodException($"The target folder '{targetDir}' isn't empty. Convert into a new or empty folder: a database's folder holds only that database.");

            string hashedPassword = string.IsNullOrEmpty(password)
                ? null
                : CryptoHelper.GetSHA256HashData(password);

            // Held alone throughout. Without a hold nothing tells whether a context has the database open, or
            // whether what a crash seems to have left is another process's commit under way, so the conversion is
            // refused (ISS-084: one that went on, taking a lock file it couldn't open for a folder nothing could
            // write, undid a live commit). A lock file is opened with read access only, so a read-only one holds.
            DatabaseHold hold;
            try
            {
                hold = IOService.OpenLockFile(databaseFile, exclusive: true, retryTimeoutMs: 0);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new XodException($"The database's lock file can't be opened or created ({e.Message}), so this conversion can't hold " +
                    "the database alone, nor tell whether others have it open. Convert it as a user who can, or convert a copy.", e);
            }

            using (hold)
            {
                // The folder checked again under the hold, before anything is recovered or converted: a database file
                // added since the check above would be converted with the database (review of TASK-071).
                DatabaseFiles.Recheck(sourceDirectoryOrFile, databaseFile);
                IOService.RecoverInterruptedWrites(sourceDir);

                var files = Directory.GetFiles(sourceDir).Where(DatabaseFiles.IsOfDatabase).ToList();
                return inPlace
                    ? ConvertInPlace(files, targetFormat, hashedPassword)
                    : ConvertInto(files, targetDir, targetFormat, hashedPassword);
            }
        }

        // Each file replaced whole: a crash leaves every file in one format or the other, and a .tmp beside one,
        // which the next open deletes.
        // Neither this nor ConvertInto passes the mutation hook that marks the indexes behind (ISS-043), and neither
        // needs it: the migrator holds the source alone, after its recovery (which drops indexes a crash left behind),
        // and converts every file, the indexes among them, with its content unchanged.
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
