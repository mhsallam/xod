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
