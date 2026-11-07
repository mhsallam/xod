using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Xod.Helpers;

namespace Xod.Services
{
    internal partial class IndexService
    {
        internal bool TryLoadFromDisk(Type type)
        {
            return TryLoadFromDiskCore(type);
        }

        private bool TryLoadFromDiskCore(Type type)
        {
            if (type == null || ioService == null)
                return false;

            string indexFileName = GetIndexFileName(type);
            if (!ioService.FileExists(indexFileName))
                return false;

            string indexFullPath = Path.Combine(ioService.RootDirectory, indexFileName);
            DateTime indexWriteTime = File.GetLastWriteTimeUtc(indexFullPath);

            // Verify no .xpag file of this table is newer than the .xidx file (e.g. after an unclean
            // process crash). The page names are read from the table document as text: opening it as a
            // document here cost 9-21 ms on a 100k-row table, which was most of the index load.
            string tableFileName = $"{type.FullName}.xtab";
            string tableFullPath = Path.Combine(ioService.RootDirectory, tableFileName);
            if (File.Exists(tableFullPath))
            {
                DateTime tableWriteTime = File.GetLastWriteTimeUtc(tableFullPath);
                if (tableWriteTime > indexWriteTime)
                {
                    var pageNames = new List<string>(8);
                    string tableText;
                    try
                    {
                        tableText = File.ReadAllText(tableFullPath);
                    }
                    catch
                    {
                        tableText = null;
                    }

                    if (tableText != null)
                    {
                        int scan = 0;
                        var all = new List<string>();
                        while (scan < tableText.Length)
                        {
                            int at = tableText.IndexOf("file=\"", scan, StringComparison.Ordinal);
                            if (at < 0) break;
                            at += 6;
                            int close = tableText.IndexOf('"', at);
                            if (close < 0) break;
                            all.Add(tableText.Substring(at, close - at));
                            scan = close + 1;
                        }

                        if (all.Count > 0)
                        {
                            for (int i = 0; i < all.Count; i++)
                            {
                                if (all.Count > 8 && i == 2)
                                    i = all.Count - 2;

                                pageNames.Add(all[i]);
                            }
                        }
                    }

                    foreach (string pageFileName in pageNames)
                    {
                        if (string.IsNullOrEmpty(pageFileName))
                            continue;

                        string pageFullPath = Path.Combine(ioService.RootDirectory, pageFileName);
                        if (File.Exists(pageFullPath) && File.GetLastWriteTimeUtc(pageFullPath) > indexWriteTime.AddMilliseconds(50))
                        {
                            return false;
                        }
                    }
                }
            }

            try
            {
                byte[] rawBytes = null;
                string blockFilePath = null;
                Stream source;

                if (!string.IsNullOrEmpty(ioService.Password))
                {
                    rawBytes = FileCryptoHelper.DecryptBytes(File.ReadAllBytes(indexFullPath), ioService.Password);
                    if (rawBytes.Length < Xidx2HeaderBytes.Length ||
                        !rawBytes.AsSpan(0, Xidx2HeaderBytes.Length).SequenceEqual(Xidx2HeaderBytes))
                    {
                        return TryLoadEncryptedOrXmlFromIOService(type, indexFileName);
                    }

                    var memory = new MemoryStream(rawBytes, writable: false);
                    memory.Position = Xidx2HeaderBytes.Length;
                    source = memory;
                }
                else
                {
                    var file = new FileStream(indexFullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    if (file.Length < Xidx2HeaderBytes.Length)
                    {
                        file.Dispose();
                        return false;
                    }

                    byte[] header = new byte[Xidx2HeaderBytes.Length];
                    int headerRead = 0;
                    while (headerRead < header.Length)
                    {
                        int n = file.Read(header, headerRead, header.Length - headerRead);
                        if (n <= 0) break;
                        headerRead += n;
                    }

                    if (headerRead != header.Length || !header.AsSpan().SequenceEqual(Xidx2HeaderBytes))
                    {
                        file.Dispose();
                        return TryLoadEncryptedOrXmlFromIOService(type, indexFileName);
                    }

                    source = file;
                    blockFilePath = indexFullPath;
                }

                lock (syncLock)
                {
                    bool loaded = false;
                    try
                    {
                        using (source)
                        {
                            using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);

                            int firstInt = reader.ReadInt32();
                            if (firstInt == FramedSnapshot || firstInt == FramedSnapshotClosedFlushes)
                            {
                                int propCount = reader.ReadInt32();
                                if (propCount < 0 || propCount > 1024)
                                    return false;

                                for (int p = 0; p < propCount; p++)
                                {
                                    string propName = reader.ReadString();
                                    int entryCount = reader.ReadInt32();
                                    string minKey = reader.ReadString();
                                    string maxKey = reader.ReadString();
                                    int blockByteLen = reader.ReadInt32();
                                    if (entryCount < 0 || blockByteLen < 0 || source.Position + blockByteLen > source.Length)
                                        return false;

                                    string indexKey = GetIndexKey(type, propName);
                                    propertyIndexes[indexKey] = new Dictionary<string, object>(StringComparer.Ordinal);
                                    if (entryCount > 0 && blockByteLen > 0)
                                    {
                                        unhydratedBlocks[indexKey] = new UnhydratedIndexBlock
                                        {
                                            RawBytes = rawBytes,
                                            FilePath = blockFilePath,
                                            Offset = (int)source.Position,
                                            ByteLength = blockByteLen,
                                            EntryCount = entryCount,
                                            MinKey = minKey,
                                            MaxKey = maxKey
                                        };
                                        source.Position += blockByteLen;
                                    }
                                }
                            }
                            else
                            {
                                int propCount = firstInt;
                                if (propCount < 0 || propCount > 1024)
                                    return false;

                                for (int p = 0; p < propCount; p++)
                                {
                                    string propName = reader.ReadString();
                                    int entryCount = reader.ReadInt32();
                                    if (entryCount < 0)
                                        return false;

                                    string indexKey = GetIndexKey(type, propName);
                                    var tree = new Dictionary<string, object>(Math.Max(16, entryCount), StringComparer.Ordinal);
                                    propertyIndexes[indexKey] = tree;

                                    for (int e = 0; e < entryCount; e++)
                                    {
                                        string normKey = reader.ReadString();
                                        int codeCount = reader.ReadInt32();
                                        if (codeCount == 1)
                                        {
                                            tree[normKey] = reader.ReadString();
                                        }
                                        else if (codeCount > 1)
                                        {
                                            var set = new HashSet<string>(codeCount, StringComparer.Ordinal);
                                            for (int c = 0; c < codeCount; c++)
                                            {
                                                set.Add(reader.ReadString());
                                            }
                                            tree[normKey] = set;
                                        }
                                    }
                                }
                            }

                            long snapshotEnd = source.Position;
                            long lastFlushEnd = snapshotEnd;
                            while (source.Position < source.Length)
                            {
                                byte op = reader.ReadByte();
                                string propName = reader.ReadString();
                                string normKey = reader.ReadString();
                                string itemCode = reader.ReadString();

                                if (op == DeltaLog.OpFlushEnd)
                                {
                                    lastFlushEnd = source.Position;
                                }
                                else if (op == DeltaLog.OpAdd)
                                {
                                    if (!string.IsNullOrEmpty(propName) && normKey != null && !string.IsNullOrEmpty(itemCode))
                                    {
                                        string dKey = GetIndexKey(type, propName);
                                        if (!propertyIndexes.TryGetValue(dKey, out var dTree))
                                        {
                                            dTree = new Dictionary<string, object>(StringComparer.Ordinal);
                                            propertyIndexes[dKey] = dTree;
                                        }
                                        AddCodeToBucket(dTree, normKey, itemCode);
                                        if (reverseHydratedTypes.Contains(type.FullName))
                                        {
                                            RegisterReverseIndexEntry(itemCode, dKey, normKey);
                                        }
                                    }
                                }
                                else if (op == DeltaLog.OpRemove)
                                {
                                    if (!string.IsNullOrEmpty(propName) && normKey != null && !string.IsNullOrEmpty(itemCode))
                                    {
                                        string dKey = GetIndexKey(type, propName);
                                        EnsurePropertyHydratedUnlocked(dKey);
                                        if (propertyIndexes.TryGetValue(dKey, out var dTree))
                                        {
                                            RemoveCodeFromBucket(dTree, normKey, itemCode);
                                        }
                                    }
                                    else if (!string.IsNullOrEmpty(itemCode))
                                    {
                                        EnsureReverseIndexBuilt(type);
                                        if (codeToIndexEntries.TryGetValue(itemCode, out var entries))
                                        {
                                            foreach (var (idxKey, nVal) in entries)
                                            {
                                                if (propertyIndexes.TryGetValue(idxKey, out var dTree))
                                                {
                                                    RemoveCodeFromBucket(dTree, nVal, itemCode);
                                                }
                                            }
                                            codeToIndexEntries.Remove(itemCode);
                                        }
                                    }
                                }
                                else
                                {
                                    return false;
                                }
                            }

                            // A v3 log ends with the record that closed its last flush. Anything after it is a
                            // flush cut short by a crash, even when the cut falls between two whole records.
                            if (firstInt == FramedSnapshotClosedFlushes && lastFlushEnd != source.Length)
                                return false;

                            hydratedTypes.Add(type.FullName);

                            // Only a plain file can be appended to; an encrypted index is always rewritten.
                            if (blockFilePath != null)
                                LogFor(type).Loaded(snapshotEnd, source.Length - snapshotEnd,
                                closedFlushes: firstInt == FramedSnapshotClosedFlushes);
                            loaded = true;
                        }
                    }
                    finally
                    {
                        // A load that stopped part way has filled some trees; the rebuild from the pages
                        // that follows must start from nothing.
                        if (!loaded)
                            ForgetTypeUnlocked(type);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryLoadEncryptedOrXmlFromIOService(Type type, string indexFileName)
        {
            XFile idxFile;
            try
            {
                idxFile = ioService.OpenFileOrCreate(indexFileName, typeof(Table), openOnly: true);
            }
            catch
            {
                return false;
            }

            XElement root = idxFile?.Root();
            if (root == null || root.Name.LocalName != "TableIndex")
                return false;

            lock (syncLock)
            {
                foreach (var propElm in root.Elements("Property"))
                {
                    string propName = propElm.Attribute("name")?.Value;
                    if (string.IsNullOrEmpty(propName))
                        continue;

                    string indexKey = GetIndexKey(type, propName);
                    var tree = new Dictionary<string, object>(StringComparer.Ordinal);

                    foreach (var entryElm in propElm.Elements("Entry"))
                    {
                        string normKey = entryElm.Attribute("key")?.Value;
                        string codesAttr = entryElm.Attribute("codes")?.Value;
                        if (normKey == null || string.IsNullOrEmpty(codesAttr))
                            continue;

                        if (codesAttr.IndexOf(',') < 0)
                        {
                            tree[normKey] = codesAttr;
                        }
                        else
                        {
                            string[] splitCodes = codesAttr.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            if (splitCodes.Length == 1)
                            {
                                tree[normKey] = splitCodes[0];
                            }
                            else if (splitCodes.Length > 1)
                            {
                                tree[normKey] = new HashSet<string>(splitCodes, StringComparer.Ordinal);
                            }
                        }
                    }

                    propertyIndexes[indexKey] = tree;
                }

                hydratedTypes.Add(type.FullName);
            }

            return true;
        }

        // Reads one block from the index file instead of holding the whole sidecar in memory. A short
        // read falls back to reading the whole file and slicing. Neither path can tell that the file was
        // rewritten since the block was located: within one context that cannot happen (a snapshot
        // hydrates every block first, an append moves nothing), but another context that rewrites the
        // file leaves these offsets pointing at other bytes (ISS-042).
        private static byte[] ReadIndexBlockBytes(UnhydratedIndexBlock block)
        {
            var buffer = new byte[block.ByteLength];
            try
            {
                using var fs = new FileStream(block.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                fs.Position = block.Offset;
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = fs.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }

                if (read == buffer.Length)
                    return buffer;
            }
            catch
            {
            }

            var whole = File.ReadAllBytes(block.FilePath);
            Buffer.BlockCopy(whole, block.Offset, buffer, 0, block.ByteLength);
            return buffer;
        }

        private static Dictionary<string, object> DeserializePropertyBlock(UnhydratedIndexBlock block, int extraCapacity = 0)
        {
            var tree = new Dictionary<string, object>(Math.Max(16, block.EntryCount + extraCapacity), StringComparer.Ordinal);
            byte[] bytes = block.RawBytes;
            int offset = block.Offset;
            if (block.FilePath != null)
            {
                bytes = ReadIndexBlockBytes(block);
                offset = 0;
            }
            using var ms = new MemoryStream(bytes, offset, block.ByteLength, writable: false);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

            for (int e = 0; e < block.EntryCount; e++)
            {
                string normKey = reader.ReadString();
                int codeCount = reader.ReadInt32();
                if (codeCount == 1)
                {
                    tree[normKey] = reader.ReadString();
                }
                else if (codeCount > 1)
                {
                    var set = new HashSet<string>(codeCount, StringComparer.Ordinal);
                    for (int c = 0; c < codeCount; c++)
                    {
                        set.Add(reader.ReadString());
                    }
                    tree[normKey] = set;
                }
            }

            return tree;
        }

        private void EnsurePropertyHydratedUnlocked(string indexKey)
        {
            if (!unhydratedBlocks.TryGetValue(indexKey, out var block))
                return;

            propertyIndexes.TryGetValue(indexKey, out var deltaTree);
            var hydrated = DeserializePropertyBlock(block, deltaTree?.Count ?? 0);
            unhydratedBlocks.Remove(indexKey); // only once read: a failed read must not leave an empty tree
            if (deltaTree != null && deltaTree.Count > 0)
            {
                foreach (var kvp in deltaTree)
                {
                    if (kvp.Value is string singleCode)
                        AddCodeToBucket(hydrated, kvp.Key, singleCode);
                    else if (kvp.Value is HashSet<string> set)
                    {
                        foreach (var c in set)
                            AddCodeToBucket(hydrated, kvp.Key, c);
                    }
                }
            }
            propertyIndexes[indexKey] = hydrated;
        }

        private void EnsurePropertyHydratedIfInBoundsUnlocked(string indexKey, string normVal)
        {
            if (!unhydratedBlocks.TryGetValue(indexKey, out var block))
                return;

            if (block.EntryCount > 0 &&
                string.CompareOrdinal(normVal, block.MinKey) >= 0 &&
                string.CompareOrdinal(normVal, block.MaxKey) <= 0)
            {
                EnsurePropertyHydratedUnlocked(indexKey);
            }
        }

        private void EnsureAllPropertiesHydratedUnlocked(Type type)
        {
            if (type == null || unhydratedBlocks.Count == 0)
                return;

            string prefix = type.FullName + "::";
            List<string> keysToHydrate = null;
            foreach (var k in unhydratedBlocks.Keys)
            {
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                {
                    keysToHydrate ??= new List<string>(4);
                    keysToHydrate.Add(k);
                }
            }

            if (keysToHydrate == null || keysToHydrate.Count == 0)
                return;

            if (keysToHydrate.Count == 1)
            {
                EnsurePropertyHydratedUnlocked(keysToHydrate[0]);
                return;
            }

            var blocks = new UnhydratedIndexBlock[keysToHydrate.Count];
            var extraCaps = new int[keysToHydrate.Count];
            for (int i = 0; i < keysToHydrate.Count; i++)
            {
                string k = keysToHydrate[i];
                blocks[i] = unhydratedBlocks[k];
                if (propertyIndexes.TryGetValue(k, out var dt))
                    extraCaps[i] = dt.Count;
            }

            var deserialized = new Dictionary<string, object>[keysToHydrate.Count];
            System.Threading.Tasks.Parallel.For(0, keysToHydrate.Count, i =>
            {
                deserialized[i] = DeserializePropertyBlock(blocks[i], extraCaps[i]);
            });

            // Only once every block is read: a failed read must not leave empty trees behind.
            for (int i = 0; i < keysToHydrate.Count; i++)
                unhydratedBlocks.Remove(keysToHydrate[i]);

            for (int i = 0; i < keysToHydrate.Count; i++)
            {
                string k = keysToHydrate[i];
                var hydrated = deserialized[i];
                if (propertyIndexes.TryGetValue(k, out var deltaTree) && deltaTree.Count > 0)
                {
                    foreach (var kvp in deltaTree)
                    {
                        if (kvp.Value is string singleCode)
                            AddCodeToBucket(hydrated, kvp.Key, singleCode);
                        else if (kvp.Value is HashSet<string> set)
                        {
                            foreach (var c in set)
                                AddCodeToBucket(hydrated, kvp.Key, c);
                        }
                    }
                }
                propertyIndexes[k] = hydrated;
            }
        }

        private void EnsureReverseIndexBuilt(Type type)
        {
            if (type == null || reverseHydratedTypes.Contains(type.FullName))
                return;

            EnsureAllPropertiesHydratedUnlocked(type);

            string prefix = type.FullName + "::";
            foreach (var kvp in propertyIndexes)
            {
                if (!kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                string indexKey = kvp.Key;
                foreach (var entry in kvp.Value)
                {
                    string normKey = entry.Key;
                    if (entry.Value is string singleCode)
                    {
                        RegisterReverseIndexEntry(singleCode, indexKey, normKey);
                    }
                    else if (entry.Value is HashSet<string> codeSet)
                    {
                        foreach (var code in codeSet)
                        {
                            RegisterReverseIndexEntry(code, indexKey, normKey);
                        }
                    }
                }
            }

            reverseHydratedTypes.Add(type.FullName);
        }

        internal void SaveToDisk(Type type)
        {
            if (type == null || ioService == null)
                return;

            var meta = propertyService.GetMetadata(type);
            var indexedProps = meta != null
                ? meta.IndexedProperties
                : propertyService.Properties(type.FullName)
                    .Where(s => s.IsIndexed || s.IsPrimaryKey || s.IsUnique)
                    .ToArray();

            if (indexedProps.Length == 0)
                return;

            // Every recorder holds the whole index before it records (HoldsWholeIndex), so this only
            // meets a type with nothing to save. Writing from a partial index would drop entries.
            if (!IsTypeHydrated(type))
            {
                AbandonIndexFile(type);
                return;
            }

            string indexFilePath = Path.Combine(ioService.RootDirectory, GetIndexFileName(type));

            if (!string.IsNullOrEmpty(ioService.Password))
            {
                SaveEncryptedViaIOService(type, indexedProps, indexFilePath);
                return;
            }

            lock (syncLock)
            {
                var log = LogFor(type);
                if (!log.SnapshotDue)
                {
                    // The file holds every change but the pending deltas, if there are any.
                    if (!log.HasPending || TryAppendDeltasToDisk(indexFilePath, log))
                        return;
                }

                WriteFullSnapshotToDisk(type, indexedProps, indexFilePath, log);
            }
        }

        private bool TryAppendDeltasToDisk(string indexFilePath, DeltaLog log)
        {
            try
            {
                ioService.BeforeFileMutate?.Invoke(indexFilePath);

                using var fs = new FileStream(indexFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536);

                Span<byte> hdrBuf = stackalloc byte[Xidx2HeaderBytes.Length];
                if (fs.Read(hdrBuf) != Xidx2HeaderBytes.Length || !hdrBuf.SequenceEqual(Xidx2HeaderBytes))
                    return false;

                // Appended at the end of whatever the file holds. Another context may have appended too
                // (concurrent writers need DatabaseOptions.LockDatabase); appending keeps its records and
                // ours, where a snapshot from this context's memory would drop its records.
                var deltas = log.Pending;
                fs.Seek(0, SeekOrigin.End);
                using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
                {
                    for (int i = 0; i < deltas.Count; i++)
                    {
                        var d = deltas[i];
                        DeltaLog.WriteRecord(writer, d.Op, d.PropName, d.NormVal, d.ItemCode);
                    }
                    DeltaLog.WriteRecord(writer, DeltaLog.OpFlushEnd, null, null, null);
                }

                fs.Flush(flushToDisk: false);
                log.Appended(fs.Length);
                return true;
            }
            catch
            {
                // A failed append leaves a flush without its end record, which no load trusts; the
                // caller writes a snapshot next.
                return false;
            }
        }

        private void WriteFullSnapshotToDisk(Type type, PropertyInfoItem[] indexedProps, string indexFilePath, DeltaLog log)
        {
            EnsureAllPropertiesHydratedUnlocked(type);
            ioService.BeforeFileMutate?.Invoke(indexFilePath);

            var trees = new Dictionary<string, object>[indexedProps.Length];
            for (int i = 0; i < indexedProps.Length; i++)
            {
                string indexKey = GetIndexKey(type, indexedProps[i].PropertyName);
                propertyIndexes.TryGetValue(indexKey, out trees[i]);
            }

            var serializedBlocks = new (int EntryCount, string MinKey, string MaxKey, byte[] Buffer, int Length)[indexedProps.Length];

            void SerializePropBlock(int idx)
            {
                var tree = trees[idx];
                if (tree == null || tree.Count == 0)
                {
                    serializedBlocks[idx] = (0, string.Empty, string.Empty, Array.Empty<byte>(), 0);
                    return;
                }

                var ms = new MemoryStream(Math.Max(256, tree.Count * 36));
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    string minK = null;
                    string maxK = null;
                    foreach (var kvp in tree)
                    {
                        string k = kvp.Key;
                        if (minK == null || string.CompareOrdinal(k, minK) < 0) minK = k;
                        if (maxK == null || string.CompareOrdinal(k, maxK) > 0) maxK = k;

                        if (kvp.Value is string singleCode)
                        {
                            bw.Write(k);
                            bw.Write(1);
                            bw.Write(singleCode);
                        }
                        else if (kvp.Value is HashSet<string> codeSet)
                        {
                            bw.Write(k);
                            bw.Write(codeSet.Count);
                            foreach (var code in codeSet)
                            {
                                bw.Write(code);
                            }
                        }
                        else
                        {
                            bw.Write(k);
                            bw.Write(0);
                        }
                    }

                    bw.Flush();
                    serializedBlocks[idx] = (tree.Count, minK ?? string.Empty, maxK ?? string.Empty, ms.GetBuffer(), (int)ms.Length);
                }
            }

            if (indexedProps.Length > 1)
            {
                System.Threading.Tasks.Parallel.For(0, indexedProps.Length, SerializePropBlock);
            }
            else if (indexedProps.Length == 1)
            {
                SerializePropBlock(0);
            }

            bool useTempFile = (ioService.HasActiveTransaction != null && ioService.HasActiveTransaction()) || File.Exists(indexFilePath);
            string writePath = useTempFile ? indexFilePath + ".tmp" : indexFilePath;

            long snapshotBytes;
            using (var fs = new FileStream(writePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Xidx2HeaderBytes);
                writer.Write(FramedSnapshotClosedFlushes);
                writer.Write(indexedProps.Length);

                for (int i = 0; i < indexedProps.Length; i++)
                {
                    var b = serializedBlocks[i];
                    writer.Write(indexedProps[i].PropertyName);
                    writer.Write(b.EntryCount);
                    writer.Write(b.MinKey);
                    writer.Write(b.MaxKey);
                    writer.Write(b.Length);
                    if (b.Length > 0)
                    {
                        writer.Flush();
                        fs.Write(b.Buffer, 0, b.Length);
                    }
                }

                writer.Flush();
                snapshotBytes = fs.Length;
            }

            if (useTempFile)
            {
                File.Move(writePath, indexFilePath, overwrite: true);
            }

            log.SnapshotWritten(snapshotBytes);
        }

        private void SaveEncryptedViaIOService(Type type, PropertyInfoItem[] indexedProps, string indexFilePath)
        {
            XDocument doc;
            lock (syncLock)
            {
                EnsureAllPropertiesHydratedUnlocked(type);
                var root = new XElement("TableIndex", new XAttribute("type", type.FullName));
                foreach (var prop in indexedProps)
                {
                    string indexKey = GetIndexKey(type, prop.PropertyName);
                    var propElm = new XElement("Property", new XAttribute("name", prop.PropertyName));
                    if (propertyIndexes.TryGetValue(indexKey, out var tree))
                    {
                        foreach (var kvp in tree)
                        {
                            if (kvp.Value is string singleCode)
                            {
                                propElm.Add(new XElement("Entry",
                                    new XAttribute("key", kvp.Key),
                                    new XAttribute("codes", singleCode)));
                            }
                            else if (kvp.Value is HashSet<string> codeSet && codeSet.Count > 0)
                            {
                                propElm.Add(new XElement("Entry",
                                    new XAttribute("key", kvp.Key),
                                    new XAttribute("codes", codeSet.Count == 1 ? codeSet.First() : string.Join(",", codeSet))));
                            }
                        }
                    }
                    root.Add(propElm);
                }
                doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
            }

            var xFile = new XFile(doc, typeof(Table), indexFilePath, ioService.Password);
            ioService.Save(xFile);
            lock (syncLock)
            {
                LogFor(type).SnapshotWritten(-1); // an encrypted file is never appended to
            }
        }
    }
}
