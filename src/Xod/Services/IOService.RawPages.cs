using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xod.Engines.Serializers;
using Xod.Helpers;

namespace Xod.Services
{
    internal partial class IOService
    {
        [ThreadStatic]
        private static byte[] scratchRowBuffer;

        internal void PreloadPagesForCodes(IReadOnlyCollection<string> candidateCodes)
        {
            if (candidateCodes == null || candidateCodes.Count < 8)
                return;

            bool useRawByteCache = string.IsNullOrEmpty(password) && storageSerializer.Format == StorageFormat.Xml;
            HashSet<string> missingSet = null;
            List<RawPageBuffer> unindexedCachedPages = null;
            lock (syncLock)
            {
                foreach (var code in candidateCodes)
                {
                    if (string.IsNullOrEmpty(code))
                        continue;

                    int dot = code.IndexOf('.');
                    if (dot <= 0)
                        continue;

                    string pageFileName = code.Substring(0, dot) + ".xpag";
                    if (cachedFiles.ContainsKey(pageFileName))
                        continue;

                    if (useRawByteCache && cachedRawPages.TryGetValue(pageFileName, out var existingRaw) && existingRaw != null)
                    {
                        if (existingRaw.RowIndex == null)
                        {
                            unindexedCachedPages ??= new List<RawPageBuffer>();
                            unindexedCachedPages.Add(existingRaw);
                        }
                        continue;
                    }

                    missingSet ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    missingSet.Add(pageFileName);
                }
            }

            if (useRawByteCache)
            {
                if (unindexedCachedPages != null && unindexedCachedPages.Count > 0)
                {
                    var distinctPages = unindexedCachedPages.Distinct().ToArray();
                    if (distinctPages.Length >= 4)
                    {
                        System.Threading.Tasks.Parallel.For(0, distinctPages.Length, i =>
                        {
                            lock (distinctPages[i])
                            {
                                distinctPages[i].EnsureRowIndex();
                            }
                        });
                    }
                    else
                    {
                        for (int i = 0; i < distinctPages.Length; i++)
                        {
                            lock (distinctPages[i])
                            {
                                distinctPages[i].EnsureRowIndex();
                            }
                        }
                    }
                }

                if (missingSet == null || missingSet.Count < 4)
                    return;

                string[] missingPages = missingSet.ToArray();
                var loadedBuffers = new RawPageBuffer[missingPages.Length];
                System.Threading.Tasks.Parallel.For(0, missingPages.Length, i =>
                {
                    string filePath = Path.Combine(root, missingPages[i]);
                    var buf = ReadRawPageBufferFromDisk(filePath);
                    if (buf != null)
                    {
                        buf.EnsureRowIndex();
                        loadedBuffers[i] = buf;
                    }
                });

                lock (syncLock)
                {
                    for (int i = 0; i < missingPages.Length; i++)
                    {
                        var buf = loadedBuffers[i];
                        if (buf == null)
                            continue;

                        string normalizedName = missingPages[i];
                        if (!cachedRawPages.ContainsKey(normalizedName))
                        {
                            if (cachedRawPages.Count >= CACHE_SIZE)
                            {
                                EvictNonDirtyRawPages();
                            }
                            cachedRawPages[normalizedName] = buf;
                        }
                        else
                        {
                            buf.ReleasePooledBuffer();
                        }
                    }
                }
                return;
            }

            if (missingSet == null || missingSet.Count < 4)
                return;

            string[] missingXFiles = missingSet.ToArray();
            var loadedFiles = new XFile[missingXFiles.Length];
            string currentPwd = password;

            System.Threading.Tasks.Parallel.For(0, missingXFiles.Length, i =>
            {
                string filePath = Path.Combine(root, missingXFiles[i]);
                if (File.Exists(filePath))
                {
                    XFile xf = LoadUnlocked(typeof(TablePage), filePath, currentPwd);
                    xf?.EnsureRowMapBuilt();
                    loadedFiles[i] = xf;
                }
            });

            lock (syncLock)
            {
                DateTime now = DateTime.UtcNow;
                for (int i = 0; i < missingXFiles.Length; i++)
                {
                    var xf = loadedFiles[i];
                    if (xf == null)
                        continue;

                    string normalizedName = missingXFiles[i];
                    if (!cachedFiles.ContainsKey(normalizedName))
                    {
                        CacheFile(normalizedName, xf, xf.Path, FileCacheStatus.Locked);
                    }
                }
            }
        }

        private static bool IsEncryptedPayload(byte[] data)
        {
            if (data == null || data.Length < 8) return false;
            return data[0] == (byte)'X' && data[1] == (byte)'O' && data[2] == (byte)'D' &&
                   data[3] == (byte)'E' && data[4] == (byte)'N' && data[5] == (byte)'C';
        }

        private static RawPageBuffer ReadRawPageBufferFromDisk(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            byte[] rented = null;
            try
            {
                using var handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
                long fileLenLong = RandomAccess.GetLength(handle);
                if (fileLenLong <= 0 || fileLenLong > int.MaxValue)
                    return null;

                int fileLen = (int)fileLenLong;
                rented = PageBufferPool.Rent(fileLen);
                int totalRead = 0;
                while (totalRead < fileLen)
                {
                    int read = RandomAccess.Read(handle, rented.AsSpan(totalRead, fileLen - totalRead), totalRead);
                    if (read <= 0)
                        break;
                    totalRead += read;
                }

                if (totalRead <= 0 || IsEncryptedPayload(rented) || StorageSerializerFactory.DetectFormat(rented) != StorageFormat.Xml)
                {
                    PageBufferPool.Return(rented);
                    return null;
                }

                var buf = new RawPageBuffer
                {
                    Bytes = rented,
                    Length = totalRead,
                    DiskSyncedLength = totalRead,
                    IsPooled = true
                };
                buf.EnsureRowIndex();
                return buf;
            }
            catch
            {
                if (rented != null)
                    PageBufferPool.Return(rented);
                return null;
            }
        }

        private RawPageBuffer GetOrLoadRawPageUnlocked(string pageFileName, out string normalizedName)
        {
            normalizedName = Path.GetFileName(pageFileName);
            if (cachedFiles.ContainsKey(normalizedName))
                return null;

            if (cachedRawPages.TryGetValue(normalizedName, out var rawPage) && rawPage?.Bytes != null)
                return rawPage;

            string filePath = Path.IsPathRooted(pageFileName) ? pageFileName : Path.Combine(root, normalizedName);
            rawPage = ReadRawPageBufferFromDisk(filePath);
            if (rawPage == null)
                return null;

            if (cachedRawPages.Count >= CACHE_SIZE)
            {
                EvictNonDirtyRawPages();
            }

            cachedRawPages[normalizedName] = rawPage;
            return rawPage;
        }

        internal XElement ReadRowColdOrCached(string pageFileName, string rowCode)
        {
            if (string.IsNullOrEmpty(pageFileName) || string.IsNullOrEmpty(rowCode))
                return null;

            string normalizedName = Path.GetFileName(pageFileName);
            RawPageBuffer rawPage = null;
            lock (syncLock)
            {
                if (cachedFiles.TryGetValue(normalizedName, out var cached))
                {
                    cached.LastCheckout = DateTime.UtcNow;
                    return cached.File.GetRowByCode(rowCode);
                }

                if (string.IsNullOrEmpty(password) && storageSerializer.Format == StorageFormat.Xml)
                {
                    long tLoad = WriteDiagnostics.Begin();
                    rawPage = GetOrLoadRawPageUnlocked(pageFileName, out _);
                    WriteDiagnostics.End(ref WriteDiagnostics.PageLoadTicks, tLoad);
                }
            }

            if (rawPage != null)
            {
                try
                {
                    if (rawPage.TryFindRow(rowCode, out int startIdx, out int rowLen))
                    {
                        string rowXml = System.Text.Encoding.UTF8.GetString(rawPage.Span.Slice(startIdx, rowLen));
                        return XElement.Parse(rowXml, LoadOptions.None);
                    }
                }
                catch
                {
                }
            }

            XFile fullFile = OpenFileOrCreate(normalizedName, typeof(TablePage), openOnly: true);
            return fullFile?.GetRowByCode(rowCode);
        }

        internal bool TryReadEntityFromRawPage(string pageFileName, string rowCode, TypeMetadata meta, out object entity)
        {
            entity = null;
            if (string.IsNullOrEmpty(pageFileName) ||
                string.IsNullOrEmpty(rowCode) ||
                meta == null ||
                !meta.IsPureScalarStandalone ||
                !string.IsNullOrEmpty(password) ||
                storageSerializer.Format != StorageFormat.Xml)
            {
                return false;
            }

            RawPageBuffer rawPage;
            lock (syncLock)
            {
                rawPage = GetOrLoadRawPageUnlocked(pageFileName, out _);
            }

            if (rawPage == null)
                return false;

            if (rawPage.TryFindRow(rowCode, out int startIdx, out int rowLen))
            {
                return TryParseEntityFromUtf8Row(rawPage.Span.Slice(startIdx, rowLen), meta, out entity);
            }

            return false;
        }

        internal XElement ReadLastRowColdOrCached(string pageFileName)
        {
            if (string.IsNullOrEmpty(pageFileName))
                return null;

            string normalizedName = Path.GetFileName(pageFileName);
            RawPageBuffer rawPage = null;
            lock (syncLock)
            {
                if (cachedFiles.TryGetValue(normalizedName, out var cached))
                {
                    cached.LastCheckout = DateTime.UtcNow;
                    return cached.File.Rows().LastOrDefault();
                }

                cachedRawPages.TryGetValue(normalizedName, out rawPage);
            }

            if (rawPage?.Bytes != null)
            {
                try
                {
                    ReadOnlySpan<byte> span = rawPage.Span;
                    int startIdx = span.LastIndexOf("<Row code=\""u8);
                    if (startIdx >= 0)
                    {
                        ReadOnlySpan<byte> tail = span.Slice(startIdx);
                        int endIdx = tail.IndexOf("</Row>"u8);
                        if (endIdx >= 0)
                        {
                            string rowXml = System.Text.Encoding.UTF8.GetString(tail.Slice(0, endIdx + 6));
                            return XElement.Parse(rowXml, LoadOptions.None);
                        }
                    }
                }
                catch
                {
                }
            }

            if (string.IsNullOrEmpty(password) && storageSerializer.Format == StorageFormat.Xml)
            {
                string filePath = Path.IsPathRooted(pageFileName) ? pageFileName : Path.Combine(root, normalizedName);
                if (File.Exists(filePath))
                {
                    try
                    {
                        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                        long len = fs.Length;
                        if (len > 16)
                        {
                            int readLen = (int)Math.Min(len, 65536L);
                            fs.Seek(len - readLen, SeekOrigin.Begin);
                            byte[] buf = new byte[readLen];
                            int actual = fs.Read(buf, 0, readLen);
                            ReadOnlySpan<byte> span = buf.AsSpan(0, actual);
                            if (!IsEncryptedPayload(buf))
                            {
                                int startIdx = span.LastIndexOf("<Row code=\""u8);
                                if (startIdx >= 0)
                                {
                                    ReadOnlySpan<byte> tail = span.Slice(startIdx);
                                    int endIdx = tail.IndexOf("</Row>"u8);
                                    if (endIdx >= 0)
                                    {
                                        string rowXml = System.Text.Encoding.UTF8.GetString(tail.Slice(0, endIdx + 6));
                                        return XElement.Parse(rowXml, LoadOptions.None);
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            XFile fullFile = OpenFileOrCreate(normalizedName, typeof(TablePage), openOnly: true);
            return fullFile?.Rows().LastOrDefault();
        }

        internal bool TryReadLastEntityFromRawPage(string pageFileName, TypeMetadata meta, out object entity)
        {
            entity = null;
            if (string.IsNullOrEmpty(pageFileName) ||
                meta == null ||
                !meta.IsPureScalarStandalone ||
                !string.IsNullOrEmpty(password) ||
                storageSerializer.Format != StorageFormat.Xml)
            {
                return false;
            }

            string normalizedName = Path.GetFileName(pageFileName);
            RawPageBuffer rawPage = null;
            lock (syncLock)
            {
                if (cachedFiles.ContainsKey(normalizedName))
                    return false;

                cachedRawPages.TryGetValue(normalizedName, out rawPage);
            }

            if (rawPage?.Bytes != null)
            {
                ReadOnlySpan<byte> span = rawPage.Span;
                int startIdx = span.LastIndexOf("<Row code=\""u8);
                if (startIdx >= 0)
                {
                    ReadOnlySpan<byte> tail = span.Slice(startIdx);
                    int endIdx = tail.IndexOf("</Row>"u8);
                    if (endIdx >= 0)
                    {
                        return TryParseEntityFromUtf8Row(tail.Slice(0, endIdx + 6), meta, out entity);
                    }
                }
                return false;
            }

            string filePath = Path.IsPathRooted(pageFileName) ? pageFileName : Path.Combine(root, normalizedName);
            if (File.Exists(filePath))
            {
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                    long len = fs.Length;
                    if (len > 16)
                    {
                        int readLen = (int)Math.Min(len, 65536L);
                        fs.Seek(len - readLen, SeekOrigin.Begin);
                        byte[] buf = new byte[readLen];
                        int actual = fs.Read(buf, 0, readLen);
                        ReadOnlySpan<byte> span = buf.AsSpan(0, actual);
                        if (!IsEncryptedPayload(buf))
                        {
                            int startIdx = span.LastIndexOf("<Row code=\""u8);
                            if (startIdx >= 0)
                            {
                                ReadOnlySpan<byte> tail = span.Slice(startIdx);
                                int endIdx = tail.IndexOf("</Row>"u8);
                                if (endIdx >= 0)
                                {
                                    return TryParseEntityFromUtf8Row(tail.Slice(0, endIdx + 6), meta, out entity);
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        internal bool TryGetRowFromRawPage(string pageFileName, string rowCode, out XElement rowElement, out int startOffset, out int byteLength)
        {
            rowElement = null;
            startOffset = -1;
            byteLength = 0;

            if (!string.IsNullOrEmpty(password) || storageSerializer.Format != StorageFormat.Xml)
                return false;

            RawPageBuffer rawPage;
            lock (syncLock)
            {
                rawPage = GetOrLoadRawPageUnlocked(pageFileName, out _);
            }

            if (rawPage == null)
                return false;

            try
            {
                if (rawPage.TryFindRow(rowCode, out int startIdx, out int len))
                {
                    string rowXml = System.Text.Encoding.UTF8.GetString(rawPage.Span.Slice(startIdx, len));
                    rowElement = XElement.Parse(rowXml, LoadOptions.None);
                    startOffset = startIdx;
                    byteLength = len;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        internal bool TryDeleteRow(string pageFileName, string rowCode, out XElement deletedRow, out bool pageEmpty)
        {
            deletedRow = null;
            pageEmpty = false;

            if (string.IsNullOrEmpty(pageFileName) || string.IsNullOrEmpty(rowCode))
                return false;

            string normalizedName = Path.GetFileName(pageFileName);
            XFile cachedXFile = null;
            lock (syncLock)
            {
                if (cachedFiles.TryGetValue(normalizedName, out var cached))
                {
                    cached.LastCheckout = DateTime.UtcNow;
                    deletedRow = cached.File.GetRowByCode(rowCode);
                    if (deletedRow != null)
                    {
                        cached.File.RemoveRow(deletedRow);
                        pageEmpty = !cached.File.Rows().Any();
                        cachedXFile = cached.File;
                    }
                }
            }

            if (cachedXFile != null)
            {
                Save(cachedXFile);
                return true;
            }

            if (TryGetRowFromRawPage(pageFileName, rowCode, out deletedRow, out int startOffset, out int byteLen))
            {
                SpliceRowInRawPage(pageFileName, startOffset, byteLen, null, rowCode, out bool hasRemainingRows);
                pageEmpty = !hasRemainingRows;
                return true;
            }

            // Cold page: the row exists on disk but neither cache holds this page yet. Load it through
            // the normal file path and delete there. Without this, deleting by item code says "not
            // found" for a page that simply is not warm, and the caller cannot tell that apart from a
            // real miss -- which is how a cascade delete on JSON/Binary left orphan children (ISS-037).
            XFile coldPage = OpenFileOrCreate(pageFileName, type: null, openOnly: true);
            if (coldPage != null)
            {
                deletedRow = coldPage.GetRowByCode(rowCode);
                if (deletedRow != null)
                {
                    coldPage.RemoveRow(deletedRow);
                    pageEmpty = !coldPage.Rows().Any();
                    Save(coldPage);
                    return true;
                }
            }

            return false;
        }

        internal void SpliceRowInRawPage(string pageFileName, int startOffset, int oldByteLength, XElement newRowElement, out bool hasRemainingRows) =>
            SpliceRowInRawPage(pageFileName, startOffset, oldByteLength, newRowElement, null, out hasRemainingRows);

        internal void SpliceRowInRawPage(string pageFileName, int startOffset, int oldByteLength, XElement newRowElement, string rowCode, out bool hasRemainingRows)
        {
            hasRemainingRows = true;
            string normalizedName = Path.GetFileName(pageFileName);
            string fullPath = Path.IsPathRooted(pageFileName) ? pageFileName : Path.Combine(root, normalizedName);

            byte[] replacementBytes = newRowElement != null
                ? System.Text.Encoding.UTF8.GetBytes(newRowElement.ToString(SaveOptions.DisableFormatting))
                : Array.Empty<byte>();

            lock (syncLock)
            {
                if (!cachedRawPages.TryGetValue(normalizedName, out var rawPage) || rawPage?.Bytes == null)
                    return;

                if (newRowElement == null)
                {
                    // Fast in-place tombstone: overwrite deleted row with whitespace padding without shifting bytes
                    rawPage.Bytes.AsSpan(startOffset, oldByteLength).Fill((byte)' ');

                    if (startOffset < rawPage.DirtyStartOffset)
                        rawPage.DirtyStartOffset = startOffset;

                    if (rawPage.RowIndex != null && !string.IsNullOrEmpty(rowCode))
                    {
                        rawPage.RowIndex.Remove(rowCode);
                    }

                    if (rawPage.RowCount > 0)
                        rawPage.RowCount--;

                    hasRemainingRows = rawPage.RowCount > 0 || (rawPage.RowIndex != null ? rawPage.RowIndex.Count > 0 : rawPage.Span.IndexOf("<Row code=\""u8) >= 0);
                }
                else if (replacementBytes.Length <= oldByteLength)
                {
                    // Fast in-place update: overwrite row in-place and pad remainder with whitespace
                    Buffer.BlockCopy(replacementBytes, 0, rawPage.Bytes, startOffset, replacementBytes.Length);
                    int padLen = oldByteLength - replacementBytes.Length;
                    if (padLen > 0)
                    {
                        rawPage.Bytes.AsSpan(startOffset + replacementBytes.Length, padLen).Fill((byte)' ');
                    }

                    if (startOffset < rawPage.DirtyStartOffset)
                        rawPage.DirtyStartOffset = startOffset;

                    if (rawPage.RowIndex != null && !string.IsNullOrEmpty(rowCode))
                    {
                        rawPage.RowIndex[rowCode] = (startOffset, replacementBytes.Length);
                    }
                }
                else
                {
                    // Row expanded: shift tail bytes and invalidate index
                    int oldPageLen = rawPage.Length;
                    int newPageLen = oldPageLen - oldByteLength + replacementBytes.Length;
                    int tailSrcOffset = startOffset + oldByteLength;
                    int tailLen = oldPageLen - tailSrcOffset;

                    if (newPageLen <= rawPage.Bytes.Length)
                    {
                        if (tailLen > 0)
                        {
                            Buffer.BlockCopy(rawPage.Bytes, tailSrcOffset, rawPage.Bytes, startOffset + replacementBytes.Length, tailLen);
                        }
                        Buffer.BlockCopy(replacementBytes, 0, rawPage.Bytes, startOffset, replacementBytes.Length);
                        rawPage.Length = newPageLen;
                    }
                    else
                    {
                        byte[] newPageBytes = new byte[Math.Max(newPageLen + 4096, rawPage.Bytes.Length)];
                        if (startOffset > 0)
                            Buffer.BlockCopy(rawPage.Bytes, 0, newPageBytes, 0, startOffset);
                        Buffer.BlockCopy(replacementBytes, 0, newPageBytes, startOffset, replacementBytes.Length);
                        if (tailLen > 0)
                            Buffer.BlockCopy(rawPage.Bytes, tailSrcOffset, newPageBytes, startOffset + replacementBytes.Length, tailLen);

                        if (rawPage.IsPooled)
                        {
                            PageBufferPool.Return(rawPage.Bytes);
                        }
                        rawPage.Bytes = newPageBytes;
                        rawPage.Length = newPageLen;
                        rawPage.IsPooled = false;
                    }

                    if (startOffset < rawPage.DirtyStartOffset)
                        rawPage.DirtyStartOffset = startOffset;

                    rawPage.RowIndex = null;
                }

                if (!hasRemainingRows)
                    return;

                if (batchDepth > 0)
                {
                    BeforeFileMutate?.Invoke(fullPath);
                    dirtyBatchRawPages.Add(normalizedName);
                }
                else if (bufferedDurability)
                {
                    dirtyBufferedRawPages.Add(normalizedName);
                }
                else
                {
                    BeforeFileMutate?.Invoke(fullPath);
                    bool inTx = HasActiveTransaction != null && HasActiveTransaction();
                    FlushRawPageToDisk(fullPath, rawPage, inTx);
                }
            }
        }

        internal bool TryAppendRawRowToPage(
            XFile table,
            object item,
            TypeMetadata meta,
            ExceptionService exceptionService,
            int pageMaxSizeBytes,
            bool isInTransaction,
            out string itemPath,
            out XElement pageElement,
            out bool tableModified)
        {
            itemPath = null;
            pageElement = null;
            tableModified = false;

            if (!string.IsNullOrEmpty(password) ||
                storageSerializer.Format != StorageFormat.Xml ||
                table == null ||
                item == null ||
                meta == null ||
                !meta.IsPureScalarStandalone)
            {
                return false;
            }

            string itemCode = ValueHelper.PickCode();
            int rowByteLen = FormatStandaloneRowUtf8(item, itemCode, meta, exceptionService, out byte[] rowBuf);

            lock (syncLock)
            {
                XElement pagesRoot = table.Root()?.Element("Pages");
                if (pagesRoot == null)
                    return false;

                XElement page = FindAppendPage(pagesRoot);

                string pageFileName;
                RawPageBuffer rawPage;
                if (page == null)
                {
                    page = AddPageElement(pagesRoot, out pageFileName);
                    tableModified = true;

                    byte[] initialBuf = PageBufferPool.Rent(Math.Max(pageMaxSizeBytes + 16384, 65536));
                    Buffer.BlockCopy(EmptyXmlPageBytes, 0, initialBuf, 0, EmptyXmlPageBytes.Length);
                    rawPage = new RawPageBuffer
                    {
                        Bytes = initialBuf,
                        Length = EmptyXmlPageBytes.Length,
                        DiskSyncedLength = 0,
                        DirtyStartOffset = 0,
                        IsNewFile = true,
                        IsPooled = true,
                        RowCount = 0
                    };

                    if (cachedRawPages.Count >= CACHE_SIZE)
                    {
                        EvictNonDirtyRawPages();
                    }
                    cachedRawPages[pageFileName] = rawPage;
                }
                else
                {
                    pageFileName = page.Attribute("file").Value;
                    if (cachedFiles.ContainsKey(pageFileName))
                        return false;

                    long tLoad = WriteDiagnostics.Begin();
                    rawPage = GetOrLoadRawPageUnlocked(pageFileName, out _);
                    WriteDiagnostics.End(ref WriteDiagnostics.PageLoadTicks, tLoad);
                    if (rawPage == null)
                    {
                        string existingFullPath = Path.Combine(root, pageFileName);
                        if (!File.Exists(existingFullPath))
                        {
                            byte[] initialBuf = PageBufferPool.Rent(Math.Max(pageMaxSizeBytes + 16384, 65536));
                            Buffer.BlockCopy(EmptyXmlPageBytes, 0, initialBuf, 0, EmptyXmlPageBytes.Length);
                            rawPage = new RawPageBuffer
                            {
                                Bytes = initialBuf,
                                Length = EmptyXmlPageBytes.Length,
                                DiskSyncedLength = 0,
                                DirtyStartOffset = 0,
                                IsNewFile = true,
                                IsPooled = true,
                                RowCount = 0,
                                RowIndex = new Dictionary<string, (int, int)>(256, StringComparer.Ordinal)
                            };
                            cachedRawPages[pageFileName] = rawPage;
                        }
                        else
                        {
                            return false;
                        }
                    }
                }

                long tAppend = WriteDiagnostics.Begin();
                ReadOnlySpan<byte> pageSpan = rawPage.Span;
                int insertPos;
                if (pageSpan.Length >= XmlPageTrailerBytes.Length &&
                    pageSpan.Slice(pageSpan.Length - XmlPageTrailerBytes.Length).SequenceEqual(XmlPageTrailerBytes))
                {
                    insertPos = pageSpan.Length - XmlPageTrailerBytes.Length;
                }
                else
                {
                    insertPos = pageSpan.LastIndexOf("</Rows>"u8);
                    if (insertPos < 0)
                        return false;
                }

                int newLength = insertPos + rowByteLen + XmlPageTrailerBytes.Length;
                if (newLength > rawPage.Bytes.Length)
                {
                    int newCap = Math.Max(rawPage.Bytes.Length * 2, newLength + 65536);
                    byte[] grown = new byte[newCap];
                    Buffer.BlockCopy(rawPage.Bytes, 0, grown, 0, insertPos);
                    if (rawPage.IsPooled)
                    {
                        PageBufferPool.Return(rawPage.Bytes);
                    }
                    rawPage.Bytes = grown;
                    rawPage.IsPooled = true;
                }

                Buffer.BlockCopy(rowBuf, 0, rawPage.Bytes, insertPos, rowByteLen);
                Buffer.BlockCopy(XmlPageTrailerBytes, 0, rawPage.Bytes, insertPos + rowByteLen, XmlPageTrailerBytes.Length);

                if (insertPos < rawPage.DirtyStartOffset)
                    rawPage.DirtyStartOffset = insertPos;

                rawPage.Length = newLength;
                rawPage.RowIndex ??= new Dictionary<string, (int, int)>(256, StringComparer.Ordinal);
                rawPage.RowIndex[itemCode] = (insertPos, rowByteLen);

                XAttribute countAttr = page.Attribute("count");
                if (rawPage.RowCount >= 0)
                {
                    rawPage.RowCount++;
                    if (countAttr != null)
                        countAttr.Value = rawPage.RowCount.ToString();
                    else
                        page.SetAttributeValue("count", rawPage.RowCount);
                }
                else if (countAttr != null && int.TryParse(countAttr.Value, out int currentCount))
                {
                    rawPage.RowCount = currentCount + 1;
                    countAttr.Value = rawPage.RowCount.ToString();
                }

                if (IsBatchOrBuffered)
                    tableModified = true;

                bool becameFull = false;
                if (rawPage.Length >= pageMaxSizeBytes)
                {
                    page.SetAttributeValue("full", "true");
                    tableModified = true;
                    becameFull = true;
                    WriteDiagnostics.FullPages++;
                }

                WriteDiagnostics.End(ref WriteDiagnostics.AppendTicks, tAppend);
                string fullPath = Path.Combine(root, pageFileName);
                if (batchDepth > 0)
                {
                    BeforeFileMutate?.Invoke(fullPath);
                    if (becameFull && !isInTransaction)
                    {
                        dirtyBatchRawPages.Remove(pageFileName);
                        byte[] bytesToWrite = rawPage.Bytes;
                        int lenToWrite = rawPage.Length;
                        rawPage.IsNewFile = false;
                        rawPage.DiskSyncedLength = lenToWrite;
                        rawPage.DirtyStartOffset = int.MaxValue;
                        pendingBatchWrites.Add(System.Threading.Tasks.Task.Run(() =>
                        {
                            using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.None))
                            {
                                fs.Write(bytesToWrite, 0, lenToWrite);
                                fs.Flush(flushToDisk: false);
                            }
                        }));
                    }
                    else
                    {
                        dirtyBatchRawPages.Add(pageFileName);
                    }
                }
                else if (bufferedDurability)
                {
                    dirtyBufferedRawPages.Add(pageFileName);
                }
                else
                {
                    BeforeFileMutate?.Invoke(fullPath);
                    FlushRawPageToDisk(fullPath, rawPage, isInTransaction);
                }

                pageElement = page;
                string pageCode = Path.GetFileNameWithoutExtension(pageFileName);
                itemPath = string.Concat(pageCode, ".", itemCode);
                return true;
            }
        }

        private static void EnsureScratchCapacity(ref byte[] buf, int required)
        {
            if (buf.Length >= required)
                return;
            int newCap = Math.Max(buf.Length * 2, required + 4096);
            byte[] grown = new byte[newCap];
            Buffer.BlockCopy(buf, 0, grown, 0, buf.Length);
            buf = grown;
        }

        private static int FormatStandaloneRowUtf8(
            object item,
            string rowCode,
            TypeMetadata meta,
            ExceptionService exceptionService,
            out byte[] buffer)
        {
            byte[] buf = scratchRowBuffer ??= new byte[16384];
            int pos = 0;

            "<Row code=\""u8.CopyTo(buf.AsSpan(pos));
            pos += 11;
            pos += System.Text.Encoding.UTF8.GetBytes(rowCode.AsSpan(), buf.AsSpan(pos));
            buf[pos++] = (byte)'"';
            buf[pos++] = (byte)'>';

            byte[] entityOpen = meta.Utf8EntityOpenTag;
            Buffer.BlockCopy(entityOpen, 0, buf, pos, entityOpen.Length);
            pos += entityOpen.Length;

            var props = meta.WriteProperties;
            for (int i = 0; i < props.Length; i++)
            {
                var prop = props[i];
                switch (prop.ScalarKind)
                {
                    case ScalarPropertyKind.Int32:
                    {
                        int v = prop.IntGetter(item);
                        if (v == 0 && (prop.IsPrimaryKey || prop.IsRelationshipKey))
                            continue;
                        EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length + 32);
                        Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
                        pos += prop.Utf8OpenTag.Length;
                        System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(pos), out int written);
                        pos += written;
                        Buffer.BlockCopy(prop.Utf8CloseTag, 0, buf, pos, prop.Utf8CloseTag.Length);
                        pos += prop.Utf8CloseTag.Length;
                        break;
                    }
                    case ScalarPropertyKind.Int64:
                    {
                        long v = prop.LongGetter(item);
                        if (v == 0L && (prop.IsPrimaryKey || prop.IsRelationshipKey))
                            continue;
                        EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length + 32);
                        Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
                        pos += prop.Utf8OpenTag.Length;
                        System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(pos), out int written);
                        pos += written;
                        Buffer.BlockCopy(prop.Utf8CloseTag, 0, buf, pos, prop.Utf8CloseTag.Length);
                        pos += prop.Utf8CloseTag.Length;
                        break;
                    }
                    case ScalarPropertyKind.Decimal:
                    {
                        decimal v = prop.DecimalGetter(item);
                        if (v == 0m && (prop.IsPrimaryKey || prop.IsRelationshipKey))
                            continue;
                        EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length + 48);
                        Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
                        pos += prop.Utf8OpenTag.Length;
                        System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(pos), out int written);
                        pos += written;
                        Buffer.BlockCopy(prop.Utf8CloseTag, 0, buf, pos, prop.Utf8CloseTag.Length);
                        pos += prop.Utf8CloseTag.Length;
                        break;
                    }
                    case ScalarPropertyKind.Double:
                    {
                        double v = prop.DoubleGetter(item);
                        if (v == 0.0 && (prop.IsPrimaryKey || prop.IsRelationshipKey))
                            continue;
                        EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length + 48);
                        Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
                        pos += prop.Utf8OpenTag.Length;
                        if (System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(pos), out int written))
                        {
                            pos += written;
                        }
                        else
                        {
                            string strVal = ValueHelper.FormatScalar(v);
                            pos += System.Text.Encoding.UTF8.GetBytes(strVal.AsSpan(), buf.AsSpan(pos));
                        }
                        Buffer.BlockCopy(prop.Utf8CloseTag, 0, buf, pos, prop.Utf8CloseTag.Length);
                        pos += prop.Utf8CloseTag.Length;
                        break;
                    }
                    case ScalarPropertyKind.Boolean:
                    {
                        bool v = prop.BoolGetter(item);
                        if (!v && (prop.IsPrimaryKey || prop.IsRelationshipKey))
                            continue;
                        EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length + 8);
                        Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
                        pos += prop.Utf8OpenTag.Length;
                        if (v)
                        {
                            "true"u8.CopyTo(buf.AsSpan(pos));
                            pos += 4;
                        }
                        else
                        {
                            "false"u8.CopyTo(buf.AsSpan(pos));
                            pos += 5;
                        }
                        Buffer.BlockCopy(prop.Utf8CloseTag, 0, buf, pos, prop.Utf8CloseTag.Length);
                        pos += prop.Utf8CloseTag.Length;
                        break;
                    }
                    case ScalarPropertyKind.Guid:
                    {
                        Guid v = prop.GuidGetter(item);
                        if (prop.IsRequired && v == Guid.Empty)
                            exceptionService.Throw(new RequiredPropertyException(prop.PropertyName));
                        if (v == Guid.Empty)
                            continue;
                        EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length + 40);
                        Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
                        pos += prop.Utf8OpenTag.Length;
                        System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(pos), out int written);
                        pos += written;
                        Buffer.BlockCopy(prop.Utf8CloseTag, 0, buf, pos, prop.Utf8CloseTag.Length);
                        pos += prop.Utf8CloseTag.Length;
                        break;
                    }
                    case ScalarPropertyKind.String:
                    {
                        string v = prop.StringGetter(item);
                        if (prop.IsRequired && string.IsNullOrWhiteSpace(v))
                            exceptionService.Throw(new RequiredPropertyException(prop.PropertyName));
                        if (string.IsNullOrEmpty(v))
                            continue;

                        ReadOnlySpan<char> chars = v.AsSpan();
                        int maxUtf8 = System.Text.Encoding.UTF8.GetMaxByteCount(chars.Length);
                        EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length + maxUtf8 * 6);
                        Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
                        pos += prop.Utf8OpenTag.Length;

                        if (chars.IndexOfAny('&', '<', '>') < 0)
                        {
                            pos += System.Text.Encoding.UTF8.GetBytes(chars, buf.AsSpan(pos));
                        }
                        else
                        {
                            pos = WriteXmlEscapedUtf8(chars, ref buf, pos);
                        }

                        Buffer.BlockCopy(prop.Utf8CloseTag, 0, buf, pos, prop.Utf8CloseTag.Length);
                        pos += prop.Utf8CloseTag.Length;
                        break;
                    }
                    default:
                    {
                        object val = prop.GetValue(item);
                        if (prop.IsRequired && (val == null || (val is DateTime dtReq && dtReq == default) || (val is DateOnly doReq && doReq == default)))
                            exceptionService.Throw(new RequiredPropertyException(prop.PropertyName));
                        if (val == null)
                            continue;
                        bool isDefault = val.Equals(ValueHelper.DefaultOf(prop.PropertyType));
                        if (isDefault && (prop.IsPrimaryKey || prop.IsRelationshipKey || val is DateTime || val is DateOnly || val is TimeSpan || val is TimeOnly))
                            continue;

                        string formatted = ValueHelper.FormatScalar(val);
                        ReadOnlySpan<char> chars = formatted.AsSpan();
                        int maxUtf8 = System.Text.Encoding.UTF8.GetMaxByteCount(chars.Length);
                        EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length + maxUtf8 * 2);
                        Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
                        pos += prop.Utf8OpenTag.Length;
                        if (chars.IndexOfAny('&', '<', '>') < 0)
                            pos += System.Text.Encoding.UTF8.GetBytes(chars, buf.AsSpan(pos));
                        else
                            pos = WriteXmlEscapedUtf8(chars, ref buf, pos);
                        Buffer.BlockCopy(prop.Utf8CloseTag, 0, buf, pos, prop.Utf8CloseTag.Length);
                        pos += prop.Utf8CloseTag.Length;
                        break;
                    }
                }
            }

            byte[] entityClose = meta.Utf8EntityCloseTag;
            EnsureScratchCapacity(ref buf, pos + entityClose.Length + 6);
            Buffer.BlockCopy(entityClose, 0, buf, pos, entityClose.Length);
            pos += entityClose.Length;
            "</Row>"u8.CopyTo(buf.AsSpan(pos));
            pos += 6;

            scratchRowBuffer = buf;
            buffer = buf;
            return pos;
        }

        private static int WriteXmlEscapedUtf8(ReadOnlySpan<char> chars, ref byte[] buf, int pos)
        {
            int start = 0;
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == '&' || c == '<' || c == '>')
                {
                    if (i > start)
                    {
                        pos += System.Text.Encoding.UTF8.GetBytes(chars.Slice(start, i - start), buf.AsSpan(pos));
                    }
                    if (c == '&')
                    {
                        "&amp;"u8.CopyTo(buf.AsSpan(pos));
                        pos += 5;
                    }
                    else if (c == '<')
                    {
                        "&lt;"u8.CopyTo(buf.AsSpan(pos));
                        pos += 4;
                    }
                    else
                    {
                        "&gt;"u8.CopyTo(buf.AsSpan(pos));
                        pos += 4;
                    }
                    start = i + 1;
                }
            }
            if (start < chars.Length)
            {
                pos += System.Text.Encoding.UTF8.GetBytes(chars.Slice(start), buf.AsSpan(pos));
            }
            return pos;
        }

        private static bool TryParseEntityFromUtf8Row(ReadOnlySpan<byte> rowSpan, TypeMetadata meta, out object entity)
        {
            entity = null;
            try
            {
                int rowOpenEnd = rowSpan.IndexOf((byte)'>');
                if (rowOpenEnd < 0)
                    return false;

                int entityOpenStart = rowSpan.Slice(rowOpenEnd + 1).IndexOf((byte)'<');
                if (entityOpenStart < 0)
                    return false;

                int cursor = rowOpenEnd + 1 + entityOpenStart;
                int entityOpenEnd = rowSpan.Slice(cursor).IndexOf((byte)'>');
                if (entityOpenEnd < 0)
                    return false;

                if (entityOpenEnd > 0 && rowSpan[cursor + entityOpenEnd - 1] == (byte)'/')
                {
                    entity = meta.InstanceFactory();
                    return true;
                }

                cursor += entityOpenEnd + 1;
                object item = meta.InstanceFactory();
                var props = meta.ReadProperties;
                int propCount = props.Length;
                int hintIdx = 0;

                while (cursor < rowSpan.Length)
                {
                    int nextLt = rowSpan.Slice(cursor).IndexOf((byte)'<');
                    if (nextLt < 0)
                        break;

                    cursor += nextLt;
                    if (cursor + 1 >= rowSpan.Length || rowSpan[cursor + 1] == (byte)'/')
                        break;

                    int tagEndRel = rowSpan.Slice(cursor + 1).IndexOf((byte)'>');
                    if (tagEndRel <= 0)
                        break;

                    bool isSelfClosing = rowSpan[cursor + tagEndRel] == (byte)'/';
                    int nameLen = isSelfClosing ? tagEndRel - 1 : tagEndRel;
                    ReadOnlySpan<byte> propNameSpan = rowSpan.Slice(cursor + 1, nameLen);

                    cursor += 1 + tagEndRel + 1;
                    if (isSelfClosing)
                        continue;

                    PropertyInfoItem matchedProp = null;
                    for (int offset = 0; offset < propCount; offset++)
                    {
                        var candidate = props[(hintIdx + offset) % propCount];
                        if (candidate.Utf8NameBytes.Length == propNameSpan.Length &&
                            propNameSpan.SequenceEqual(candidate.Utf8NameBytes))
                        {
                            matchedProp = candidate;
                            hintIdx = (hintIdx + offset + 1) % propCount;
                            break;
                        }
                    }

                    int valEndRel = rowSpan.Slice(cursor).IndexOf("</"u8);
                    if (valEndRel < 0)
                        break;

                    ReadOnlySpan<byte> valSpan = rowSpan.Slice(cursor, valEndRel);
                    cursor += valEndRel + 2;
                    int closeGt = rowSpan.Slice(cursor).IndexOf((byte)'>');
                    if (closeGt < 0)
                        break;
                    cursor += closeGt + 1;

                    if (matchedProp == null || valSpan.Length == 0)
                        continue;

                    switch (matchedProp.ScalarKind)
                    {
                        case ScalarPropertyKind.Int32:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out int iv, out int ic) && ic == valSpan.Length)
                                matchedProp.IntSetter(item, iv);
                            break;
                        case ScalarPropertyKind.Int64:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out long lv, out int lc) && lc == valSpan.Length)
                                matchedProp.LongSetter(item, lv);
                            break;
                        case ScalarPropertyKind.Decimal:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out decimal decv, out int decc) && decc == valSpan.Length)
                                matchedProp.DecimalSetter(item, decv);
                            break;
                        case ScalarPropertyKind.Double:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out double dv, out int dc) && dc == valSpan.Length)
                                matchedProp.DoubleSetter(item, dv);
                            else
                            {
                                string dStr = UnescapeXmlUtf8(valSpan);
                                if (double.TryParse(dStr, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out double dv2))
                                    matchedProp.DoubleSetter(item, dv2);
                            }
                            break;
                        case ScalarPropertyKind.Boolean:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out bool bv, out int bc) && bc == valSpan.Length)
                                matchedProp.BoolSetter(item, bv);
                            break;
                        case ScalarPropertyKind.Guid:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out Guid gv, out int gc) && gc == valSpan.Length)
                                matchedProp.GuidSetter(item, gv);
                            break;
                        case ScalarPropertyKind.String:
                            matchedProp.StringSetter(item, UnescapeXmlUtf8(valSpan));
                            break;
                        default:
                        {
                            string sVal = UnescapeXmlUtf8(valSpan);
                            object parsed = ValueHelper.ParseScalar(matchedProp.PropertyType, sVal);
                            if (parsed != null)
                                matchedProp.SetValue(item, parsed);
                            break;
                        }
                    }
                }

                entity = item;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string UnescapeXmlUtf8(ReadOnlySpan<byte> utf8Span)
        {
            if (utf8Span.IndexOf((byte)'&') < 0)
            {
                return System.Text.Encoding.UTF8.GetString(utf8Span);
            }

            string raw = System.Text.Encoding.UTF8.GetString(utf8Span);
            return raw
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&amp;", "&");
        }

        internal bool TryReadEntitySliceFromRawPage(string pageFileName, TypeMetadata meta, ref int remainingSkip, int maxTake, System.Collections.IList outEntities)
        {
            if (!string.IsNullOrEmpty(password) ||
                storageSerializer.Format != StorageFormat.Xml ||
                meta == null ||
                !meta.IsPureScalarStandalone)
            {
                return false;
            }

            RawPageBuffer rawPage;
            lock (syncLock)
            {
                rawPage = GetOrLoadRawPageUnlocked(pageFileName, out _);
            }

            if (rawPage == null)
                return false;

            ReadOnlySpan<byte> span = rawPage.Span;
            int cursor = 0;
            int added = 0;

            while (cursor < span.Length && added < maxTake)
            {
                int relStart = span.Slice(cursor).IndexOf("<Row code=\""u8);
                if (relStart < 0)
                    break;

                int absStart = cursor + relStart;
                int relEnd = span.Slice(absStart).IndexOf("</Row>"u8);
                if (relEnd < 0)
                    break;

                int rowLen = relEnd + 6;
                if (remainingSkip > 0)
                {
                    remainingSkip--;
                }
                else
                {
                    if (TryParseEntityFromUtf8Row(span.Slice(absStart, rowLen), meta, out object entity))
                    {
                        outEntities.Add(entity);
                        added++;
                    }
                }

                cursor = absStart + rowLen;
            }

            return true;
        }

        internal bool TryReadRowSliceFromRawPage(string pageFileName, ref int remainingSkip, int maxTake, List<XElement> outRows)
        {
            if (!string.IsNullOrEmpty(password) || storageSerializer.Format != StorageFormat.Xml)
                return false;

            RawPageBuffer rawPage;
            lock (syncLock)
            {
                rawPage = GetOrLoadRawPageUnlocked(pageFileName, out _);
            }

            if (rawPage == null)
                return false;

            ReadOnlySpan<byte> span = rawPage.Span;
            int cursor = 0;
            int added = 0;

            while (cursor < span.Length && added < maxTake)
            {
                int relStart = span.Slice(cursor).IndexOf("<Row code=\""u8);
                if (relStart < 0)
                    break;

                int absStart = cursor + relStart;
                int relEnd = span.Slice(absStart).IndexOf("</Row>"u8);
                if (relEnd < 0)
                    break;

                int rowLen = relEnd + 6;
                if (remainingSkip > 0)
                {
                    remainingSkip--;
                }
                else
                {
                    try
                    {
                        string rowXml = System.Text.Encoding.UTF8.GetString(span.Slice(absStart, rowLen));
                        outRows.Add(XElement.Parse(rowXml, LoadOptions.None));
                        added++;
                    }
                    catch
                    {
                    }
                }

                cursor = absStart + rowLen;
            }

            return true;
        }

        internal bool TryCountRowsInRawPage(string pageFileName, out int rowCount)
        {
            rowCount = 0;
            if (!string.IsNullOrEmpty(password) || storageSerializer.Format != StorageFormat.Xml)
                return false;

            RawPageBuffer rawPage;
            lock (syncLock)
            {
                rawPage = GetOrLoadRawPageUnlocked(pageFileName, out _);
            }

            if (rawPage == null)
                return false;

            if (rawPage.RowCount >= 0)
            {
                rowCount = rawPage.RowCount;
                return true;
            }

            if (rawPage.RowIndex != null)
            {
                rowCount = rawPage.RowIndex.Count;
                rawPage.RowCount = rowCount;
                return true;
            }

            ReadOnlySpan<byte> span = rawPage.Span;
            int cursor = 0;
            while (cursor < span.Length)
            {
                int relStart = span.Slice(cursor).IndexOf("<Row code=\""u8);
                if (relStart < 0)
                    break;
                rowCount++;
                cursor += relStart + 11;
            }
            rawPage.RowCount = rowCount;
            return true;
        }

        private void EvictNonDirtyRawPages()
        {
            var keysToRemove = new List<string>();
            foreach (var k in cachedRawPages.Keys)
            {
                if (!dirtyBatchRawPages.Contains(k) && !dirtyBufferedRawPages.Contains(k))
                {
                    keysToRemove.Add(k);
                    if (keysToRemove.Count >= 128)
                        break;
                }
            }
            for (int i = 0; i < keysToRemove.Count; i++)
            {
                string key = keysToRemove[i];
                if (cachedRawPages.TryGetValue(key, out var rawPage))
                {
                    cachedRawPages.Remove(key);
                    rawPage?.ReleasePooledBuffer();
                }
            }
        }

        private void FlushRawPagesList(List<string> pageNames)
        {
            var items = new List<(string FullPath, RawPageBuffer Page)>(pageNames.Count);
            for (int i = 0; i < pageNames.Count; i++)
            {
                string name = pageNames[i];
                if (cachedRawPages.TryGetValue(name, out var rawPage) && rawPage?.Bytes != null)
                {
                    string fullPath = Path.Combine(root, name);
                    BeforeFileMutate?.Invoke(fullPath);
                    items.Add((fullPath, rawPage));
                }
            }

            if (items.Count == 0)
                return;

            bool inTx = HasActiveTransaction != null && HasActiveTransaction();
            if (items.Count < 4)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    FlushRawPageToDisk(items[i].FullPath, items[i].Page, inTx);
                }
            }
            else
            {
                System.Threading.Tasks.Parallel.ForEach(items, item => FlushRawPageToDisk(item.FullPath, item.Page, inTx));
            }
        }

        private static void FlushRawPageToDisk(string fullPath, RawPageBuffer rawPage, bool inTransaction)
        {
            if (rawPage?.Bytes == null || rawPage.Length <= 0)
                return;

            if (!inTransaction &&
                !rawPage.IsNewFile &&
                rawPage.DirtyStartOffset < rawPage.Length &&
                rawPage.DirtyStartOffset >= 0 &&
                File.Exists(fullPath))
            {
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None, 65536, FileOptions.None))
                {
                    if (fs.Length == rawPage.DiskSyncedLength)
                    {
                        if (rawPage.DirtyStartOffset > 0)
                            fs.Seek(rawPage.DirtyStartOffset, SeekOrigin.Begin);
                        fs.Write(rawPage.Bytes, rawPage.DirtyStartOffset, rawPage.Length - rawPage.DirtyStartOffset);
                        if (rawPage.Length != rawPage.DiskSyncedLength)
                            fs.SetLength(rawPage.Length);
                        fs.Flush(flushToDisk: false);
                        rawPage.DiskSyncedLength = rawPage.Length;
                        rawPage.DirtyStartOffset = int.MaxValue;
                        return;
                    }
                }
            }

            if (inTransaction || File.Exists(fullPath))
            {
                WriteRawBytesAtomic(fullPath, rawPage.Bytes, rawPage.Length);
            }
            else
            {
                using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.None))
                {
                    fs.Write(rawPage.Bytes, 0, rawPage.Length);
                    fs.Flush(flushToDisk: false);
                }
            }

            rawPage.IsNewFile = false;
            rawPage.DiskSyncedLength = rawPage.Length;
            rawPage.DirtyStartOffset = int.MaxValue;
        }

        private static void WriteRawBytesAtomic(string fullPath, byte[] bytes, int length)
        {
            string tempPath = fullPath + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.None))
            {
                fs.Write(bytes, 0, length);
                fs.Flush(flushToDisk: false);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
    }
}
