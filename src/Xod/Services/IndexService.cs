using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Xod.Helpers;

namespace Xod.Services
{
    internal partial class IndexService
    {
        private static readonly byte[] Xidx2HeaderBytes = Encoding.UTF8.GetBytes("XIDX2:<TableIndex>\n");

        // The int after the XIDX2 header. -2: framed property blocks, then a log of delta records.
        // -3: the same, and every flush appended to the log ends with DeltaLog.OpFlushEnd (PLAN-041).
        private const int FramedSnapshot = -2;
        private const int FramedSnapshotClosedFlushes = -3;

        /// <summary>
        /// The layout of one type's .xidx file - a snapshot followed by a log of delta records - and the
        /// one rule for when that log is folded into a new snapshot (PLAN-041).
        ///
        /// Appending costs the size of the deltas; a snapshot costs the size of the whole index. The log
        /// may grow until it would outgrow the snapshot it extends, so snapshots are written at
        /// geometrically spaced sizes and the work of writing the index stays proportional to the rows
        /// written, however large the table is. The size of a single flush does not matter.
        /// </summary>
        private sealed class DeltaLog
        {
            /// <summary>A log under this size is never worth a snapshot: replaying it at open is cheap.</summary>
            internal const long FloorBytes = 256 * 1024;

            internal const byte OpAdd = 1;
            internal const byte OpRemove = 2;

            /// <summary>Closes every flush appended to a v3 file. A log that does not end with one was cut
            /// short (a crash during the append) and is not trusted.</summary>
            internal const byte OpFlushEnd = 3;

            // Pending lists up to this many records keep their memory between flushes.
            private const int RetainedCapacity = 65536;

            internal readonly List<(byte Op, string PropName, string NormVal, string ItemCode)> Pending = new();
            private long pendingBytes;
            private bool snapshotDue;

            /// <summary>End of the snapshot in the file, or -1 while this context does not know the file's
            /// layout (no file yet, an index rebuilt from pages, an encrypted file).</summary>
            internal long SnapshotBytes { get; private set; } = -1;

            /// <summary>Bytes of delta records the file holds after its snapshot.</summary>
            internal long LogBytes { get; private set; }

            /// <summary>This context appended to the log since its last snapshot.</summary>
            internal bool AppendedHere { get; private set; }

            /// <summary>This context holds no index for the type and has deleted its file, so nothing is
            /// recorded until a load or a rebuild from the pages.</summary>
            internal bool Abandoned { get; private set; }

            /// <summary>True when the next save must write a snapshot rather than append.</summary>
            internal bool SnapshotDue => snapshotDue || SnapshotBytes < 0 || Outgrows(pendingBytes);

            internal bool HasPending => Pending.Count > 0;

            // The pending records plus the record that closes their flush.
            private bool Outgrows(long extraBytes) =>
                LogBytes + extraBytes + FlushEndBytes > Math.Max(FloorBytes, SnapshotBytes);

            private static readonly long FlushEndBytes = RecordBytes(null, null, null);

            internal void Record(byte op, string propName, string normVal, string itemCode)
            {
                if (snapshotDue)
                    return;

                pendingBytes += RecordBytes(propName, normVal, itemCode);
                if (Outgrows(pendingBytes))
                {
                    // A snapshot will replace these deltas; stop holding them in memory.
                    MarkSnapshotDue();
                    return;
                }
                Pending.Add((op, propName, normVal, itemCode));
            }

            /// <summary>The in-memory index changed in a way the pending deltas do not describe.</summary>
            internal void MarkSnapshotDue()
            {
                snapshotDue = true;
                ClearPending();
            }

            internal void Abandon()
            {
                Abandoned = true;
                ClearPending();
            }

            /// <param name="closedFlushes">The file is v3: each flush in its log ends with
            /// <see cref="OpFlushEnd"/>. An older file cannot tell a cut log from a whole one, so it is
            /// never appended to: its first save writes a v3 snapshot.</param>
            internal void Loaded(long snapshotBytes, long logBytes, bool closedFlushes)
            {
                SnapshotBytes = snapshotBytes;
                LogBytes = logBytes;
                if (!closedFlushes)
                    snapshotDue = true;
            }

            internal void Appended(long fileLength)
            {
                LogBytes = fileLength - SnapshotBytes;
                AppendedHere = true;
                ClearPending();
            }

            internal void SnapshotWritten(long snapshotBytes)
            {
                SnapshotBytes = snapshotBytes;
                LogBytes = 0;
                AppendedHere = false;
                snapshotDue = false;
                ClearPending();
            }

            private void ClearPending()
            {
                Pending.Clear();
                pendingBytes = 0;
                if (Pending.Capacity > RetainedCapacity)
                    Pending.TrimExcess(); // one huge batch must not pin its peak for the context's life
            }

            /// <summary>Writes one log record; <see cref="RecordBytes"/> is its exact size.</summary>
            internal static void WriteRecord(BinaryWriter writer, byte op, string propName, string normVal, string itemCode)
            {
                writer.Write(op);
                writer.Write(propName ?? string.Empty);
                writer.Write(normVal ?? string.Empty);
                writer.Write(itemCode ?? string.Empty);
            }

            internal static long RecordBytes(string propName, string normVal, string itemCode) =>
                1 + StringBytes(propName) + StringBytes(normVal) + StringBytes(itemCode);

            // BinaryWriter.Write(string): the UTF-8 byte count as a 7-bit encoded integer, then the bytes.
            private static int StringBytes(string s)
            {
                int bytes = string.IsNullOrEmpty(s) ? 0 : Encoding.UTF8.GetByteCount(s);
                int prefix = 1;
                for (uint rest = (uint)bytes >> 7; rest != 0; rest >>= 7)
                    prefix++;
                return prefix + bytes;
            }
        }

        private static void AddCodeToBucket(Dictionary<string, object> tree, string normKey, string itemCodePath)
        {
            if (!tree.TryGetValue(normKey, out var existing))
            {
                tree[normKey] = itemCodePath;
            }
            else if (existing is string singleCode)
            {
                if (!string.Equals(singleCode, itemCodePath, StringComparison.Ordinal))
                {
                    tree[normKey] = new HashSet<string>(2, StringComparer.Ordinal) { singleCode, itemCodePath };
                }
            }
            else if (existing is HashSet<string> set)
            {
                set.Add(itemCodePath);
            }
        }

        private static void RemoveCodeFromBucket(Dictionary<string, object> tree, string normKey, string itemCodePath)
        {
            if (!tree.TryGetValue(normKey, out var existing))
                return;

            if (existing is string singleCode)
            {
                if (string.Equals(singleCode, itemCodePath, StringComparison.Ordinal))
                    tree.Remove(normKey);
            }
            else if (existing is HashSet<string> set)
            {
                set.Remove(itemCodePath);
                if (set.Count == 0)
                    tree.Remove(normKey);
                else if (set.Count == 1)
                {
                    foreach (var remaining in set)
                    {
                        tree[normKey] = remaining;
                        break;
                    }
                }
            }
        }

        private readonly PropertyService propertyService;
        private readonly IOService ioService;
        private readonly object syncLock = new object();

        // Key: "TypeFullName::PropertyName" -> NormalizedValueString -> either string (single code) or HashSet<string> (multiple codes)
        private readonly Dictionary<string, Dictionary<string, object>> propertyIndexes =
            new(StringComparer.Ordinal);

        // Key: "TypeFullName::PropertyName" -> unhydrated binary block from .xidx
        private readonly Dictionary<string, UnhydratedIndexBlock> unhydratedBlocks =
            new(StringComparer.Ordinal);

        // Reverse lookup: "pageCode.rowCode" -> List of (IndexKey, NormalizedValueString) for O(1) removal on Update/Delete
        private readonly Dictionary<string, List<(string IndexKey, string NormVal)>> codeToIndexEntries =
            new(StringComparer.Ordinal);

        // Set of types whose reverse index (codeToIndexEntries) has been populated
        private readonly HashSet<string> reverseHydratedTypes = new(StringComparer.Ordinal);

        // Set of types whose index has been hydrated from disk
        private readonly HashSet<string> hydratedTypes = new(StringComparer.Ordinal);

        // Set of types whose in-memory index has unpersisted changes
        private readonly HashSet<Type> dirtyTypes = new();

        // Per type: the .xidx layout and its unsaved deltas (Op: 1=Add, 2=Remove, PropName, NormVal, ItemCode)
        private readonly Dictionary<Type, DeltaLog> deltaLogs = new();

        private DeltaLog LogFor(Type type)
        {
            if (!deltaLogs.TryGetValue(type, out var log))
            {
                log = new DeltaLog();
                deltaLogs[type] = log;
            }
            return log;
        }

        internal IndexService(PropertyService propertyService, IOService ioService = null)
        {
            this.propertyService = propertyService;
            this.ioService = ioService;
        }

        internal static string GetIndexFileName(Type type) => $"{type.FullName}.xidx";

        private static string GetIndexKey(Type type, string propertyName) => $"{type.FullName}::{propertyName}";

        internal void FlushDirtyIndexes()
        {
            List<Type> toSave;
            lock (syncLock)
            {
                if (dirtyTypes.Count == 0)
                    return;
                toSave = dirtyTypes.ToList();
                dirtyTypes.Clear();
            }

            foreach (var type in toSave)
            {
                SaveToDisk(type);
            }
        }

        /// <summary>
        /// On a clean close, folds every log this context appended to that is larger than
        /// <see cref="DeltaLog.FloorBytes"/> into a snapshot, so the next open replays at most that much.
        /// While open, a log may grow to its snapshot's size. A context that only read writes nothing.
        /// </summary>
        internal void FoldLargeLogs()
        {
            List<Type> toFold;
            lock (syncLock)
            {
                toFold = deltaLogs
                    .Where(kv => kv.Value.AppendedHere && kv.Value.LogBytes > DeltaLog.FloorBytes)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var type in toFold)
                    deltaLogs[type].MarkSnapshotDue();
            }

            foreach (var type in toFold)
            {
                SaveToDisk(type);
            }
        }

        /// <summary>
        /// True when this context holds the type's whole index, so a change can be recorded against it and
        /// a snapshot written from memory drops nothing (PLAN-041). Callers may change rows without having
        /// loaded the index (indexing off; a delete that finds its row by scanning pages): the file is
        /// loaded here first. A file that cannot be loaded (missing, stale, unreadable) cannot be kept
        /// current, so it is deleted and the next load rebuilds the index from the pages; until then
        /// nothing is recorded.
        /// </summary>
        private bool HoldsWholeIndex(Type type)
        {
            lock (syncLock)
            {
                if (hydratedTypes.Contains(type.FullName))
                    return true;
                if (LogFor(type).Abandoned)
                    return false;
            }

            if (TryLoadFromDiskCore(type))
                return true;

            AbandonIndexFile(type);
            return false;
        }

        private void AbandonIndexFile(Type type)
        {
            lock (syncLock)
            {
                var log = LogFor(type);
                if (log.Abandoned)
                    return;
                log.Abandon();
                dirtyTypes.Remove(type);
            }

            string indexFileName = GetIndexFileName(type);
            if (ioService != null && ioService.FileExists(indexFileName))
                ioService.Delete(indexFileName);
        }

        /// <summary>Drops everything this context holds for the type's index (not the file).</summary>
        private void ForgetTypeUnlocked(Type type)
        {
            string prefix = type.FullName + "::";
            var keys = propertyIndexes.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in keys)
                propertyIndexes.Remove(k);
            var unhydKeys = unhydratedBlocks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in unhydKeys)
                unhydratedBlocks.Remove(k);
            hydratedTypes.Remove(type.FullName);
            reverseHydratedTypes.Remove(type.FullName);
            dirtyTypes.Remove(type);
            deltaLogs.Remove(type);
        }

        internal bool IsTypeHydrated(Type type)
        {
            lock (syncLock)
            {
                return hydratedTypes.Contains(type.FullName);
            }
        }

        internal void MarkTypeHydrated(Type type)
        {
            lock (syncLock)
            {
                hydratedTypes.Add(type.FullName);
            }
        }

        internal bool IndexItem(Type type, object item, string itemCodePath, XElement pageElementInTable = null)
        {
            if (type == null || item == null || string.IsNullOrEmpty(itemCodePath))
                return false;

            var meta = propertyService.GetMetadata(type);
            var indexedProps = meta != null
                ? meta.IndexedProperties
                : propertyService.Properties(type.FullName)
                    .Where(s => s.IsIndexed || s.IsPrimaryKey || s.IsUnique)
                    .ToArray();

            if (indexedProps.Length == 0)
                return false;

            bool tableStructureChanged = false;
            bool holdsIndex = HoldsWholeIndex(type);
            lock (syncLock)
            {
                bool trackReverse = reverseHydratedTypes.Contains(type.FullName);
                bool updatePageElms = pageElementInTable != null && !pageElementInTable.HasElements;
                var log = LogFor(type);

                for (int i = 0; i < indexedProps.Length; i++)
                {
                    var prop = indexedProps[i];
                    string normVal = GetNormalizedPropertyValue(prop, item);
                    if (normVal == null) continue;

                    if (holdsIndex)
                    {
                        string key = GetIndexKey(type, prop.PropertyName);

                        if (!propertyIndexes.TryGetValue(key, out var tree))
                        {
                            tree = new Dictionary<string, object>(StringComparer.Ordinal);
                            propertyIndexes[key] = tree;
                        }

                        AddCodeToBucket(tree, normVal, itemCodePath);

                        if (trackReverse)
                        {
                            RegisterReverseIndexEntry(itemCodePath, key, normVal);
                        }

                        log.Record(DeltaLog.OpAdd, prop.PropertyName, normVal, itemCodePath);
                    }

                    // The page's key range lives in the table file, not the index: always kept.
                    if (updatePageElms)
                    {
                        if (UpdatePageIndexElement(pageElementInTable, prop.PropertyName, normVal))
                            tableStructureChanged = true;
                    }
                }
                if (holdsIndex)
                    dirtyTypes.Add(type);
            }
            return tableStructureChanged;
        }

        internal void IndexRowElement(Type type, XElement rowElement, string pageCode)
        {
            if (type == null || rowElement == null || string.IsNullOrEmpty(pageCode))
                return;

            string rowCode = rowElement.Attribute("code")?.Value;
            if (string.IsNullOrEmpty(rowCode)) return;

            XElement entityElement = rowElement.Element(type.Name) ?? rowElement.Elements().FirstOrDefault();
            if (entityElement == null) return;

            string itemCodePath = $"{pageCode}.{rowCode}";
            var indexedProps = propertyService.Properties(type.FullName)
                .Where(s => s.IsIndexed || s.IsPrimaryKey || s.IsUnique)
                .ToList();

            lock (syncLock)
            {
                // A rebuild from the pages records no deltas: only a snapshot can persist it.
                LogFor(type).MarkSnapshotDue();

                foreach (var prop in indexedProps)
                {
                    string rawVal = prop.ValuePosition == ValuePosition.Attribute
                        ? entityElement.Attribute(prop.PropertyName)?.Value
                        : entityElement.Element(prop.PropertyName)?.Value;

                    if (string.IsNullOrEmpty(rawVal)) continue;

                    object parsed = ValueHelper.ParseScalar(prop.PropertyType, rawVal);
                    if (parsed == null) continue;

                    string normVal = NormalizeIndexValue(prop.PropertyType, parsed);
                    string key = GetIndexKey(type, prop.PropertyName);

                    if (!propertyIndexes.TryGetValue(key, out var tree))
                    {
                        tree = new Dictionary<string, object>(StringComparer.Ordinal);
                        propertyIndexes[key] = tree;
                    }

                    AddCodeToBucket(tree, normVal, itemCodePath);
                    RegisterReverseIndexEntry(itemCodePath, key, normVal);
                }
            }
        }

        private void RegisterReverseIndexEntry(string itemCodePath, string indexKey, string normVal)
        {
            if (!codeToIndexEntries.TryGetValue(itemCodePath, out var list))
            {
                list = new List<(string IndexKey, string NormVal)>(4);
                codeToIndexEntries[itemCodePath] = list;
            }
            list.Add((indexKey, normVal));
        }

        private static bool UpdatePageIndexElement(XElement pageElement, string propertyName, string formattedValue)
        {
            if (pageElement == null || string.IsNullOrEmpty(formattedValue)) return false;

            XElement indexElm = pageElement.Elements("Index")
                .FirstOrDefault(s => s.Attribute("property")?.Value == propertyName);

            if (indexElm == null)
            {
                indexElm = new XElement("Index",
                    new XAttribute("property", propertyName),
                    new XAttribute("begins", formattedValue),
                    new XAttribute("ends", formattedValue));
                pageElement.Add(indexElm);
                return true;
            }
            else
            {
                indexElm.SetAttributeValue("ends", formattedValue);
                return false;
            }
        }

        internal void RemoveRowElement(Type type, XElement rowElement, string itemCodePath, bool persist = false)
        {
            if (type == null || string.IsNullOrEmpty(itemCodePath))
                return;

            if (rowElement == null)
            {
                RemoveItemCode(type, itemCodePath, persist);
                return;
            }

            XElement entityElement = rowElement.Element(type.Name) ?? rowElement.Elements().FirstOrDefault();
            if (entityElement == null)
            {
                RemoveItemCode(type, itemCodePath, persist);
                return;
            }

            var meta = propertyService.GetMetadata(type);
            var indexedProps = meta != null
                ? meta.IndexedProperties
                : propertyService.Properties(type.FullName)
                    .Where(s => s.IsIndexed || s.IsPrimaryKey || s.IsUnique)
                    .ToArray();

            if (indexedProps.Length == 0 || !HoldsWholeIndex(type))
                return;

            lock (syncLock)
            {
                var log = LogFor(type);

                for (int i = 0; i < indexedProps.Length; i++)
                {
                    var prop = indexedProps[i];
                    string rawVal = prop.ValuePosition == ValuePosition.Attribute
                        ? entityElement.Attribute(prop.PropertyName)?.Value
                        : entityElement.Element(prop.PropertyName)?.Value;

                    if (string.IsNullOrEmpty(rawVal))
                        continue;

                    object parsed = ValueHelper.ParseScalar(prop.PropertyType, rawVal);
                    if (parsed == null)
                        continue;

                    string normVal = NormalizeIndexValue(prop.PropertyType, parsed);
                    string key = GetIndexKey(type, prop.PropertyName);

                    EnsurePropertyHydratedUnlocked(key);
                    if (propertyIndexes.TryGetValue(key, out var tree))
                    {
                        RemoveCodeFromBucket(tree, normVal, itemCodePath);
                    }

                    log.Record(DeltaLog.OpRemove, prop.PropertyName, normVal, itemCodePath);
                }

                if (reverseHydratedTypes.Contains(type.FullName))
                {
                    codeToIndexEntries.Remove(itemCodePath);
                }

                dirtyTypes.Add(type);
            }

            if (persist)
            {
                SaveToDisk(type);
            }
        }

        internal void RemoveItemCode(Type type, string itemCodePath, bool persist = false)
        {
            if (type == null || string.IsNullOrEmpty(itemCodePath) || !HoldsWholeIndex(type)) return;

            lock (syncLock)
            {
                EnsureReverseIndexBuilt(type);
                if (codeToIndexEntries.TryGetValue(itemCodePath, out var entries))
                {
                    foreach (var (indexKey, normVal) in entries)
                    {
                        if (propertyIndexes.TryGetValue(indexKey, out var tree))
                        {
                            RemoveCodeFromBucket(tree, normVal, itemCodePath);
                        }
                    }
                    codeToIndexEntries.Remove(itemCodePath);
                }

                LogFor(type).Record(DeltaLog.OpRemove, null, null, itemCodePath);

                dirtyTypes.Add(type);
            }

            if (persist)
            {
                SaveToDisk(type);
            }
        }

        internal void Remove(Type type)
        {
            if (type == null) return;
            lock (syncLock)
            {
                ForgetTypeUnlocked(type);
            }

            string indexFileName = GetIndexFileName(type);
            if (ioService != null && ioService.FileExists(indexFileName))
            {
                ioService.Delete(indexFileName);
            }
        }

        internal void ClearAll()
        {
            lock (syncLock)
            {
                propertyIndexes.Clear();
                unhydratedBlocks.Clear();
                codeToIndexEntries.Clear();
                hydratedTypes.Clear();
                reverseHydratedTypes.Clear();
                dirtyTypes.Clear();
                deltaLogs.Clear();
            }
        }

        internal bool HasKeyConflict(Type type, PropertyInfoItem prop, object value, string existingCodeToIgnore)
        {
            if (type == null || prop == null || value == null)
                return false;

            string normVal = NormalizeIndexValue(prop.PropertyType, value);
            return HasNormalizedKeyConflict(type, prop, normVal, existingCodeToIgnore);
        }

        internal bool HasNormalizedKeyConflict(Type type, PropertyInfoItem prop, string normVal, string existingCodeToIgnore)
        {
            if (type == null || prop == null || normVal == null)
                return false;

            string key = prop.CachedIndexKey ??= GetIndexKey(type, prop.PropertyName);

            lock (syncLock)
            {
                EnsurePropertyHydratedIfInBoundsUnlocked(key, normVal);
                if (!propertyIndexes.TryGetValue(key, out var tree) || !tree.TryGetValue(normVal, out var bucket))
                    return false;

                if (bucket is string singleCode)
                {
                    return !string.Equals(singleCode, existingCodeToIgnore, StringComparison.Ordinal);
                }
                if (bucket is HashSet<string> codeSet)
                {
                    foreach (var c in codeSet)
                    {
                        if (!string.Equals(c, existingCodeToIgnore, StringComparison.Ordinal))
                            return true;
                    }
                }
                return false;
            }
        }

        internal IReadOnlyCollection<string> LookupExactProperty(Type type, PropertyInfoItem prop, object item)
        {
            if (type == null || prop == null || item == null)
                return Array.Empty<string>();

            string normVal = GetNormalizedPropertyValue(prop, item);
            if (string.IsNullOrEmpty(normVal))
                return Array.Empty<string>();

            string key = prop.CachedIndexKey ??= GetIndexKey(type, prop.PropertyName);
            lock (syncLock)
            {
                EnsurePropertyHydratedIfInBoundsUnlocked(key, normVal);
                if (propertyIndexes.TryGetValue(key, out var tree) && tree.TryGetValue(normVal, out var bucket))
                {
                    if (bucket is string singleCode)
                        return new[] { singleCode };
                    if (bucket is HashSet<string> codeSet)
                        return codeSet;
                }
            }

            return Array.Empty<string>();
        }

        internal IReadOnlyCollection<string> LookupExact(Type type, string propertyName, object value)
        {
            if (type == null || string.IsNullOrEmpty(propertyName) || value == null)
                return Array.Empty<string>();

            var meta = propertyService.GetMetadata(type);
            var prop = meta != null && meta.ReadPropertyMap.TryGetValue(propertyName, out var foundProp)
                ? foundProp
                : propertyService.Properties(type.FullName).FirstOrDefault(p => p.PropertyName == propertyName);
            if (prop == null) return Array.Empty<string>();

            string normVal = NormalizeIndexValue(prop.PropertyType, value);
            string key = GetIndexKey(type, propertyName);

            lock (syncLock)
            {
                EnsurePropertyHydratedIfInBoundsUnlocked(key, normVal);
                if (propertyIndexes.TryGetValue(key, out var tree) && tree.TryGetValue(normVal, out var bucket))
                {
                    if (bucket is string singleCode)
                        return new[] { singleCode };
                    if (bucket is HashSet<string> codeSet)
                        return codeSet;
                }
            }

            return Array.Empty<string>();
        }
    }
}
