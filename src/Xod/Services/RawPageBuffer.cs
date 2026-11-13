using System;
using System.Collections.Generic;
using System.Threading;

namespace Xod.Services
{
    internal sealed class RawPageBuffer
    {
        public byte[] Bytes;
        public int Length;
        public int DiskSyncedLength;
        public int DirtyStartOffset = int.MaxValue;
        public bool IsNewFile;
        public bool IsPooled;
        public int RowCount = -1;
        public int AccessCount;
        /// <summary>
        /// Row code to (offset, length), for every row of the page, or null. A reader builds it whole from the bytes
        /// (EnsureRowIndex); a writer keeps it current only through the Row* methods below, which never leave it
        /// holding part of the page's rows: a reader that doesn't find a row in the map looks no further.
        /// </summary>
        public Dictionary<string, (int Offset, int Length)> RowIndex;

        public ReadOnlySpan<byte> Span => Bytes.AsSpan(0, Length);

        public void ReleasePooledBuffer()
        {
            if (IsPooled && Bytes != null)
            {
                var b = Bytes;
                Bytes = null;
                IsPooled = false;
                PageBufferPool.Return(b);
            }
        }

        public byte[] ToExactArray()
        {
            if (Bytes == null || Length <= 0) return Array.Empty<byte>();
            if (Length == Bytes.Length) return Bytes;
            byte[] copy = new byte[Length];
            Buffer.BlockCopy(Bytes, 0, copy, 0, Length);
            return copy;
        }

        // The writers of the row map. The page's writer calls them under the table's write lock, which keeps out the
        // readers of that table, not a reader of another type that loads this type's rows (ISS-108): that reader
        // can meet the map, as it can the bytes, while the writer changes them.

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

        public void EnsureRowIndex()
        {
            if (RowIndex != null || Bytes == null || Length <= 0)
                return;

            var map = new Dictionary<string, (int Offset, int Length)>(256, StringComparer.Ordinal);
            ReadOnlySpan<byte> span = Bytes.AsSpan(0, Length);
            int cursor = 0;
            while (cursor < span.Length)
            {
                int relStart = span.Slice(cursor).IndexOf("<Row code=\""u8);
                if (relStart < 0)
                    break;

                int absStart = cursor + relStart;
                int codeStart = absStart + 11;
                int quoteEnd = span.Slice(codeStart).IndexOf((byte)'"');
                if (quoteEnd <= 0)
                    break;

                string code = System.Text.Encoding.UTF8.GetString(span.Slice(codeStart, quoteEnd));
                int relEnd = span.Slice(absStart).IndexOf("</Row>"u8);
                if (relEnd < 0)
                    break;

                int rowLen = relEnd + 6;
                map[code] = (absStart, rowLen);
                cursor = absStart + rowLen;
            }

            RowIndex = map;
            if (RowCount < 0)
                RowCount = map.Count;
        }

        public bool TryFindRow(string rowCode, out int startOffset, out int byteLength)
        {
            startOffset = -1;
            byteLength = 0;
            if (Bytes == null || Length <= 0 || string.IsNullOrEmpty(rowCode))
                return false;

            var idxMap = RowIndex;
            if (idxMap == null)
            {
                lock (this)
                {
                    EnsureRowIndex();
                    idxMap = RowIndex;
                }
            }

            if (idxMap != null)
            {
                if (idxMap.TryGetValue(rowCode, out var loc))
                {
                    startOffset = loc.Offset;
                    byteLength = loc.Length;
                    return true;
                }
                return false;
            }

            ReadOnlySpan<byte> span = Bytes.AsSpan(0, Length);
            if (rowCode.Length <= 48)
            {
                Span<byte> patternBuf = stackalloc byte[64];
                "<Row code=\""u8.CopyTo(patternBuf);
                int codeLen = System.Text.Encoding.UTF8.GetBytes(rowCode.AsSpan(), patternBuf.Slice(11));
                patternBuf[11 + codeLen] = (byte)'"';
                patternBuf[12 + codeLen] = (byte)'>';
                ReadOnlySpan<byte> pattern = patternBuf.Slice(0, 13 + codeLen);
                int idx = span.IndexOf(pattern);
                if (idx >= 0)
                {
                    int endIdx = span.Slice(idx).IndexOf("</Row>"u8);
                    if (endIdx >= 0)
                    {
                        startOffset = idx;
                        byteLength = endIdx + 6;
                        return true;
                    }
                }
                return false;
            }
            else
            {
                byte[] startPattern = System.Text.Encoding.UTF8.GetBytes($"<Row code=\"{rowCode}\">");
                int idx = span.IndexOf(startPattern);
                if (idx >= 0)
                {
                    int endIdx = span.Slice(idx).IndexOf("</Row>"u8);
                    if (endIdx >= 0)
                    {
                        startOffset = idx;
                        byteLength = endIdx + 6;
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
