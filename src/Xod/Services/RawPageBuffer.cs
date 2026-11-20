using System;
using System.Collections.Generic;
using System.Threading;

namespace Xod.Services
{
    internal sealed class RawPageBuffer
    {
        public byte[] Bytes;
        public int Length;
        /// <summary>The page's file name, for the error a damaged page raises.</summary>
        public string Name;
        public int DiskSyncedLength;
        public int DirtyStartOffset = int.MaxValue;
        public bool IsNewFile;
        public int RowCount = -1;
        public int AccessCount;
        /// <summary>
        /// Row code to (offset, length), for every row of the page, or null. A reader builds it whole from the bytes
        /// (EnsureRowIndex); a writer keeps it current only through the Row* methods below, which never leave it
        /// holding part of the page's rows: a reader that doesn't find a row in the map looks no further.
        /// </summary>
        public Dictionary<string, (int Offset, int Length)> RowIndex;

        public ReadOnlySpan<byte> Span => Bytes.AsSpan(0, Length);

        /// <summary>The page's bytes, its own array when the page fills it (a page read from disk, ISS-212). Its callers
        /// take the page out of the cache first, under the lock, and a writer reaches a page only through the cache, so
        /// nothing changes that array after (XFile.DiskBytes is never changed in place).</summary>
        public byte[] ToExactArray()
        {
            if (Bytes == null || Length <= 0) return Array.Empty<byte>();
            if (Length == Bytes.Length) return Bytes;
            byte[] copy = new byte[Length];
            Buffer.BlockCopy(Bytes, 0, copy, 0, Length);
            return copy;
        }

        // The writers of the row map. The page's writer calls them under the engine-wide write lock, which no reader
        // holds beside it, so no reader meets the map or the bytes while they change.

        /// <summary>The row was deleted: its bytes blanked in place.</summary>
        public void RowRemoved(string rowCode)
        {
            var map = RowIndex;
            if (map == null)
                return;
            if (string.IsNullOrEmpty(rowCode))
            {
                RowIndex = null; // the row isn't named: its entry can't be taken out, so a reader rebuilds the map
                return;
            }
            map.Remove(rowCode);
        }

        /// <summary>The row was written again in its own bytes, no longer than before (the rest padded).</summary>
        public void RowRewritten(string rowCode, int startOffset, int newLength)
        {
            var map = RowIndex;
            if (map == null)
                return;
            if (string.IsNullOrEmpty(rowCode))
            {
                RowIndex = null;
                return;
            }
            map[rowCode] = (startOffset, newLength);
        }

        /// <summary>
        /// A row was appended. A page whose map no reader has built keeps none: a map of the new row alone would
        /// hide the page's other rows from every reader (it used to make one).
        /// </summary>
        public void RowAppended(string rowCode, int startOffset, int length)
        {
            var map = RowIndex;
            if (map == null)
                return;
            if (string.IsNullOrEmpty(rowCode))
            {
                RowIndex = null;
                return;
            }
            map[rowCode] = (startOffset, length);
        }

        /// <summary>
        /// The row at <paramref name="startOffset"/> grew by <paramref name="growth"/> bytes to
        /// <paramref name="newLength"/>, and every row after it moved by as much. Dropping the map instead made the
        /// page's next reader rebuild it whole: a dictionary and a string for each row of the page (ISS-044,
        /// PLAN-056).
        /// </summary>
        public void RowGrew(string rowCode, int startOffset, int newLength, int growth)
        {
            var map = RowIndex;
            if (map == null)
                return;
            if (string.IsNullOrEmpty(rowCode))
            {
                RowIndex = null; // the row isn't named: its own entry can't be put right, so a reader rebuilds the map
                return;
            }

            int count = map.Count;
            string[] codes = System.Buffers.ArrayPool<string>.Shared.Rent(count);
            try
            {
                map.Keys.CopyTo(codes, 0);
                for (int i = 0; i < count; i++)
                {
                    ref var location = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(map, codes[i]);
                    if (location.Offset > startOffset)
                        location.Offset += growth;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<string>.Shared.Return(codes, clearArray: true);
            }

            map[rowCode] = (startOffset, newLength);
        }

        public void InvalidateRowIndex()
        {
            RowIndex = null;
            AccessCount = 0;
        }

        /// <summary>
        /// Builds the row map from the bytes, by <see cref="PageRows"/>: the one place a page's rows are found. A page
        /// it can't follow throws <see cref="DatabaseFileException"/>.
        /// </summary>
        public void EnsureRowIndex()
        {
            if (RowIndex != null || Bytes == null || Length <= 0)
                return;

            var map = new Dictionary<string, (int Offset, int Length)>(256, StringComparer.Ordinal);
            ReadOnlySpan<byte> span = Bytes.AsSpan(0, Length);
            int cursor = PageRows.RowsStart(span, Name);
            while (PageRows.TryNext(span, ref cursor, Name, out int start, out int length, out ReadOnlySpan<byte> code))
            {
                // Two rows with one code (XOD 1.x drew a row's code at random, so a page of its can hold two, ISS-197):
                // both are rows of the page, as the document read has them, and the code names the later one, as the
                // document read's lookup does. The earlier one keeps its place under a key no code takes (a code has
                // no '#'). The map kept the later one alone, and a read of the page's rows dropped the earlier.
                string rowCode = System.Text.Encoding.UTF8.GetString(code);
                if (map.TryGetValue(rowCode, out var earlier))
                    map[rowCode + "#" + earlier.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)] = earlier;
                map[rowCode] = (start, length);
            }

            RowIndex = map;
            if (RowCount < 0)
                RowCount = map.Count;
        }

        // The row map, built here when no reader has yet.
        private Dictionary<string, (int Offset, int Length)> Map()
        {
            var idxMap = RowIndex;
            if (idxMap == null)
            {
                lock (this)
                {
                    EnsureRowIndex();
                    idxMap = RowIndex;
                }
            }
            return idxMap;
        }

        /// <summary>How many rows the page holds, by its row map.</summary>
        public int RowsMapped() => Map()?.Count ?? 0;

        /// <summary>The row's place in the page, from the row map (built here when no reader has yet).</summary>
        public bool TryFindRow(string rowCode, out int startOffset, out int byteLength)
        {
            startOffset = -1;
            byteLength = 0;
            if (Bytes == null || Length <= 0 || string.IsNullOrEmpty(rowCode))
                return false;

            var idxMap = Map();

            if (idxMap != null && idxMap.TryGetValue(rowCode, out var loc))
            {
                startOffset = loc.Offset;
                byteLength = loc.Length;
                return true;
            }
            return false;
        }

        /// <summary>The page's last row, its code, offset and length: false when it has none.</summary>
        public bool TryLastRow(out string rowCode, out int startOffset, out int byteLength)
        {
            rowCode = null;
            startOffset = -1;
            byteLength = 0;
            var idxMap = Map();
            if (idxMap == null)
                return false;

            foreach (var entry in idxMap)
            {
                if (entry.Value.Offset > startOffset)
                {
                    rowCode = entry.Key;
                    startOffset = entry.Value.Offset;
                    byteLength = entry.Value.Length;
                }
            }
            return rowCode != null;
        }

        /// <summary>The rows in page order, each its offset and length.</summary>
        public List<(int Offset, int Length)> RowsInOrder()
        {
            var idxMap = Map();

            var rows = new List<(int Offset, int Length)>(idxMap?.Count ?? 0);
            if (idxMap != null)
            {
                foreach (var loc in idxMap.Values)
                    rows.Add(loc);
                rows.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            }
            return rows;
        }
    }
}
