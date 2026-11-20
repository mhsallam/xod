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

            bool useRawByteCache = string.IsNullOrEmpty(Password) && storageSerializer.Format == StorageFormat.Xml;
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
                        ParallelReads.For(distinctPages.Length, i =>
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
                ParallelReads.For(missingPages.Length, i =>
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
                            cachedRawPages.Put(normalizedName, buf);
                        }
                        else
                        {
                            buf.ReleasePooledBuffer(); // never cached: no reader holds it
                        }
                    }
                }
                return;
            }

            if (missingSet == null || missingSet.Count < 4)
                return;

            string[] missingXFiles = missingSet.ToArray();
            var loadedFiles = new XFile[missingXFiles.Length];
            FileKeys currentKeys = FileKeys;

            ParallelReads.For(missingXFiles.Length, i =>
            {
                string filePath = Path.Combine(root, missingXFiles[i]);
                if (OnDiskHere(filePath))
                {
                    XFile xf = LoadUnlocked(typeof(TablePage), filePath, currentKeys);
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

        private RawPageBuffer ReadRawPageBufferFromDisk(string filePath)
        {
            if (!OnDiskHere(filePath))
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
                    IsPooled = true,
                    Name = Path.GetFileName(filePath)
                };
                buf.EnsureRowIndex();
                return buf;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is DatabaseFileException)
            {
                if (rented != null)
                    PageBufferPool.Return(rented);
                // A file it can't read is read as a document instead (OpenFileOrCreate), which names it when it can't
                // either. A page whose rows it can't follow (PageRows) is damaged: read whole, it hid a fault of the
                // scanner as a slower read (ISS-143).
                if (e is DatabaseFileException)
                    throw;
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

            cachedRawPages.Put(normalizedName, rawPage);
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

                if (string.IsNullOrEmpty(Password) && storageSerializer.Format == StorageFormat.Xml)
                {
                    long tLoad = WriteDiagnostics.Begin();
                    rawPage = GetOrLoadRawPageUnlocked(pageFileName, out _);
                    WriteDiagnostics.End(ref WriteDiagnostics.PageLoadTicks, tLoad);
                }
            }

            if (rawPage != null)
            {
                // A row the map doesn't hold isn't on the page (the map is the page's whole list of rows); the
                // document read below gave the same answer, read again. A row found that doesn't parse used to read
                // as absent (ISS-144).
                if (rawPage.TryFindRow(rowCode, out int startIdx, out int rowLen))
                    return ParseRow(rawPage, startIdx, rowLen, rowCode);
            }

            XFile fullFile = OpenFileOrCreate(normalizedName, typeof(TablePage), openOnly: true);
            return fullFile?.GetRowByCode(rowCode);
        }

        /// <summary>A row's XML from the page's bytes; a row that doesn't parse is a damaged page (ISS-144).</summary>
        private static XElement ParseRow(RawPageBuffer rawPage, int start, int length, string rowCode)
        {
            string rowXml = System.Text.Encoding.UTF8.GetString(rawPage.Span.Slice(start, length));
            try
            {
                return XmlText.Parse(rowXml);
            }
            catch (System.Xml.XmlException ex)
            {
                throw PageRows.Damaged(rawPage.Name, (rowCode != null ? $"its row '{rowCode}'" : $"its row at offset {start}") +
                    $" doesn't parse ({ex.Message})");
            }
        }

        // The page's raw bytes for a read of its rows, loaded when no cache holds it; null when the page is held as a
        // document, or isn't read from its bytes (a password, another format).
        private RawPageBuffer RawPageForRead(string pageFileName)
        {
            if (!string.IsNullOrEmpty(Password) || storageSerializer.Format != StorageFormat.Xml)
                return null;
            lock (syncLock)
            {
                return GetOrLoadRawPageUnlocked(pageFileName, out _);
            }
        }

        /// <summary>
        /// The step a read of one row by its code passes holding the row's bytes in the page's array, outside the cache's
        /// lock, before it parses them (ISS-105): the interleaving a test holds, to read beside another read that drops
        /// the page from the cache and loads others into whatever arrays the pool gives out
        /// (<see cref="RawPageBuffer.ReleasePooledBuffer"/>). One read is enough to hold it; a scan isn't slowed by it.
        /// </summary>
        internal const string RowBytesHeldStep = "row-bytes-held";

        internal bool TryReadEntityFromRawPage(string pageFileName, string rowCode, TypeMetadata meta, out object entity)
        {
            entity = null;
            if (string.IsNullOrEmpty(pageFileName) ||
                string.IsNullOrEmpty(rowCode) ||
                meta == null ||
                !meta.IsPureScalarStandalone ||
                !string.IsNullOrEmpty(Password) ||
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
                ReadOnlySpan<byte> row = rawPage.Span.Slice(startIdx, rowLen);
                FaultInjection.Step(RowBytesHeldStep);
                return TryParseEntityFromUtf8Row(row, meta, out entity);
            }

            return false;
        }

        internal XElement ReadLastRowColdOrCached(string pageFileName)
        {
            if (string.IsNullOrEmpty(pageFileName))
                return null;

            string normalizedName = Path.GetFileName(pageFileName);
            lock (syncLock)
            {
                if (cachedFiles.TryGetValue(normalizedName, out var cached))
                {
                    cached.LastCheckout = DateTime.UtcNow;
                    return cached.File.Rows().LastOrDefault();
                }
            }

            // The last row by the page's row map. It was the last "<Row code=" in the page's bytes, or in the last
            // 64 KB of its file, which a value could hold (ISS-143).
            RawPageBuffer rawPage = RawPageForRead(pageFileName);
            if (rawPage != null)
            {
                if (!rawPage.TryLastRow(out string lastCode, out int lastStart, out int lastLength))
                    return null;
                return ParseRow(rawPage, lastStart, lastLength, lastCode);
            }

            XFile fullFile = OpenFileOrCreate(normalizedName, typeof(TablePage), openOnly: true);
            return fullFile?.Rows().LastOrDefault();
        }

        internal bool TryReadLastEntityFromRawPage(string pageFileName, TypeMetadata meta, out object entity)
        {
            entity = null;
            if (string.IsNullOrEmpty(pageFileName) || meta == null || !meta.IsPureScalarStandalone)
                return false;

            lock (syncLock)
            {
                if (cachedFiles.ContainsKey(Path.GetFileName(pageFileName)))
                    return false;
            }

            RawPageBuffer rawPage = RawPageForRead(pageFileName);
            if (rawPage == null)
                return false;

            if (!rawPage.TryLastRow(out _, out int lastStart, out int lastLength))
                return false;
            // False when the fast parse can't take the row: its caller reads it as XML (ReadLastRowColdOrCached).
            return TryParseEntityFromUtf8Row(rawPage.Span.Slice(lastStart, lastLength), meta, out entity);
        }

        internal bool TryGetRowFromRawPage(string pageFileName, string rowCode, out XElement rowElement, out int startOffset, out int byteLength)
        {
            rowElement = null;
            startOffset = -1;
            byteLength = 0;

            if (!string.IsNullOrEmpty(Password) || storageSerializer.Format != StorageFormat.Xml)
                return false;

            RawPageBuffer rawPage;
            lock (syncLock)
            {
                rawPage = GetOrLoadRawPageUnlocked(pageFileName, out _);
            }

            if (rawPage == null)
                return false;

            if (rawPage.TryFindRow(rowCode, out int startIdx, out int len))
            {
                rowElement = ParseRow(rawPage, startIdx, len, rowCode);
                startOffset = startIdx;
                byteLength = len;
                return true;
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

        // A row replaced (or removed, newRowElement null) in its page as a document, saved as any page is. Called under
        // syncLock.
        private void SpliceRowInPageUnlocked(string fullPath, string rowCode, XElement newRowElement, out bool hasRemainingRows)
        {
            XFile page = string.IsNullOrEmpty(rowCode) ? null : OpenFileOrCreate(fullPath, typeof(TablePage), openOnly: true);
            XElement row = page?.GetRowByCode(rowCode);
            if (row == null)
                throw new InvalidOperationException("The row '" + rowCode + "' is no longer on the page '" + Path.GetFileName(fullPath) +
                    "', so it can't be changed there.");
            if (newRowElement == null)
            {
                page.RemoveRow(row);
                hasRemainingRows = page.Rows().Any();
            }
            else
            {
                row.ReplaceNodes(newRowElement.Nodes());
                hasRemainingRows = true;
            }
            Save(page);
        }

        internal void SpliceRowInRawPage(string pageFileName, int startOffset, int oldByteLength, XElement newRowElement, out bool hasRemainingRows) =>
            SpliceRowInRawPage(pageFileName, startOffset, oldByteLength, newRowElement, null, out hasRemainingRows);

        internal void SpliceRowInRawPage(string pageFileName, int startOffset, int oldByteLength, XElement newRowElement, string rowCode, out bool hasRemainingRows)
        {
            hasRemainingRows = true;
            string normalizedName = Path.GetFileName(pageFileName);
            string fullPath = Path.IsPathRooted(pageFileName) ? pageFileName : Path.Combine(root, normalizedName);

            byte[] replacementBytes = newRowElement != null
                ? XmlText.SerializeElement(newRowElement)
                : Array.Empty<byte>();

            lock (syncLock)
            {
                if (!cachedRawPages.TryGetValue(normalizedName, out var rawPage) || rawPage?.Bytes == null)
                {
                    // The page left the bytes cache after its row was found there (opened as a document, or evicted):
                    // the change is made to the page as it is held now. It used to return without writing, so an
                    // update reported success and changed nothing (ISS-113).
                    SpliceRowInPageUnlocked(fullPath, rowCode, newRowElement, out hasRemainingRows);
                    return;
                }

                RawPagePendingUnlocked(normalizedName, fullPath, rawPage, startOffset);
                if (newRowElement == null)
                {
                    // Fast in-place tombstone: overwrite deleted row with whitespace padding without shifting bytes
                    rawPage.Bytes.AsSpan(startOffset, oldByteLength).Fill((byte)' ');

                    if (startOffset < rawPage.DirtyStartOffset)
                        rawPage.DirtyStartOffset = startOffset;

                    rawPage.RowRemoved(rowCode);

                    if (rawPage.RowCount > 0)
                        rawPage.RowCount--;

                    hasRemainingRows = rawPage.RowCount > 0 || rawPage.RowsMapped() > 0;
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

                    rawPage.RowRewritten(rowCode, startOffset, replacementBytes.Length);
                }
                else
                {
                    // Row expanded: shift the tail bytes, and the row map with them
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

                        // The old array stays with any reader holding it (RawPageCache).
                        rawPage.Bytes = newPageBytes;
                        rawPage.Length = newPageLen;
                    }

                    if (startOffset < rawPage.DirtyStartOffset)
                        rawPage.DirtyStartOffset = startOffset;

                    rawPage.RowGrew(rowCode, startOffset, replacementBytes.Length, replacementBytes.Length - oldByteLength);
                }

                // An emptied page is deleted by its caller (Delete), which drops what is pending of it. Direct: written
                // when the statement ends (WriteSaved, ISS-099).
                if (hasRemainingRows && !IsBatchOrBuffered)
                    saved.AddRawPage(normalizedName);
            }
        }

        internal bool TryAppendRawRowToPage(
            XFile table,
            object item,
            TypeMetadata meta,
            ExceptionService exceptionService,
            int pageMaxSizeBytes,
            out string itemPath,
            out XElement pageElement,
            out bool tableModified)
        {
            itemPath = null;
            pageElement = null;
            tableModified = false;

            if (!string.IsNullOrEmpty(Password) ||
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
                    pageFileName = NewPageFileName();
                    page = AddPageElement(pagesRoot, pageFileName);
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
                        RowCount = 0,
                        Name = pageFileName
                    };

                    if (cachedRawPages.Count >= CACHE_SIZE)
                    {
                        EvictNonDirtyRawPages();
                    }
                    cachedRawPages.Put(pageFileName, rawPage);
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
                        if (!OnDiskHere(existingFullPath))
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
                                RowCount = 0,
                                RowIndex = new Dictionary<string, (int, int)>(256, StringComparer.Ordinal),
                                Name = pageFileName
                            };
                            cachedRawPages.Put(pageFileName, rawPage);
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

                RawPagePendingUnlocked(pageFileName, Path.Combine(root, pageFileName), rawPage, insertPos);
                int newLength = insertPos + rowByteLen + XmlPageTrailerBytes.Length;
                if (newLength > rawPage.Bytes.Length)
                {
                    int newCap = Math.Max(rawPage.Bytes.Length * 2, newLength + 65536);
                    byte[] grown = new byte[newCap];
                    Buffer.BlockCopy(rawPage.Bytes, 0, grown, 0, insertPos);
                    // The old array stays with any reader holding it (RawPageCache).
                    rawPage.Bytes = grown;
                }

                Buffer.BlockCopy(rowBuf, 0, rawPage.Bytes, insertPos, rowByteLen);
                Buffer.BlockCopy(XmlPageTrailerBytes, 0, rawPage.Bytes, insertPos + rowByteLen, XmlPageTrailerBytes.Length);

                if (insertPos < rawPage.DirtyStartOffset)
                    rawPage.DirtyStartOffset = insertPos;

                rawPage.Length = newLength;
                rawPage.RowAppended(itemCode, insertPos, rowByteLen);

                // A hint (ChangeHint): not a write to make of the table file.
                XAttribute countAttr = page.Attribute("count");
                bool outerHint = !table.HintChanging;
                table.HintChanging = true;
                try
                {
                    if (rawPage.RowCount >= 0)
                    {
                        rawPage.RowCount++;
                        if (countAttr != null)
                            countAttr.Value = rawPage.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        else
                            page.SetAttributeValue("count", rawPage.RowCount);
                    }
                    else if (countAttr != null && int.TryParse(countAttr.Value, out int currentCount))
                    {
                        rawPage.RowCount = currentCount + 1;
                        countAttr.Value = rawPage.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                finally
                {
                    if (outerHint)
                        table.HintChanging = false;
                }

                if (IsBatchOrBuffered)
                    tableModified = true;

                if (rawPage.Length >= pageMaxSizeBytes)
                {
                    page.SetAttributeValue("full", "true");
                    tableModified = true;
                    WriteDiagnostics.FullPages++;
                }

                WriteDiagnostics.End(ref WriteDiagnostics.AppendTicks, tAppend);
                if (batchDepth > 0)
                {
                    // Waits in the batch (RawPagePendingUnlocked, above), full or not, and is written with the rest of it
                    // at its end by the batch's one writer (FlushPending). ISS-129: a page that filled was written by a
                    // task of its own, from its pooled bytes, while the batch went on; opening the page as a document
                    // meanwhile gave those bytes back to the pool, the next page rented them and wrote its rows into
                    // them, and the task wrote that: other pages' rows in a full page, the table unreadable.
                }
                else if (bufferedDurability)
                {
                    dirtyBufferedRawPages.Add(pageFileName);
                }
                else
                {
                    // Direct: written when the statement ends, after the row is indexed (WriteSaved, ISS-099).
                    saved.AddRawPage(pageFileName);
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
                        // As the document writer writes it (ValueHelper.FormatScalar), ISS-055.
                        if (v)
                        {
                            "True"u8.CopyTo(buf.AsSpan(pos));
                            pos += 4;
                        }
                        else
                        {
                            "False"u8.CopyTo(buf.AsSpan(pos));
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
                        if (v == Guid.Empty && StoredValue.IsLeftOut(v, typeof(Guid), prop))
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
                        {
                            if (StoredValue.IsLeftOut(v, typeof(string), prop))
                                continue;
                            pos = WriteStoredEmpty(prop, nil: v == null, ref buf, pos);
                            break;
                        }
                        StoredValue.CheckText(v, prop.PropertyName);

                        ReadOnlySpan<char> chars = v.AsSpan();
                        int maxUtf8 = System.Text.Encoding.UTF8.GetMaxByteCount(chars.Length);
                        EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length + maxUtf8 * 6);
                        Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
                        pos += prop.Utf8OpenTag.Length;

                        if (chars.IndexOfAny(XmlText.TextSpecials) < 0)
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
                        {
                            if (StoredValue.IsLeftOut(null, prop.PropertyType, prop))
                                continue;
                            pos = WriteStoredEmpty(prop, nil: true, ref buf, pos);
                            break;
                        }
                        bool isDefault = val.Equals(ValueHelper.DefaultOf(prop.PropertyType));
                        if (isDefault && (prop.IsPrimaryKey || prop.IsRelationshipKey || val is DateTime || val is DateOnly || val is TimeSpan || val is TimeOnly) &&
                            StoredValue.IsLeftOut(val, prop.PropertyType, prop))
                            continue;

                        string formatted = StoredValue.Text(val, prop.PropertyName);
                        ReadOnlySpan<char> chars = formatted.AsSpan();
                        int maxUtf8 = System.Text.Encoding.UTF8.GetMaxByteCount(chars.Length);
                        // A reference is up to 8 bytes for one char (XmlText).
                        EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length + maxUtf8 * 6);
                        Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
                        pos += prop.Utf8OpenTag.Length;
                        if (chars.IndexOfAny(XmlText.TextSpecials) < 0)
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

        // An empty string (an open and a close tag) or a null a read wouldn't give back (nil="true"), as the
        // document writer writes them (ISS-060).
        private static int WriteStoredEmpty(PropertyInfoItem prop, bool nil, ref byte[] buf, int pos)
        {
            if (nil)
            {
                EnsureScratchCapacity(ref buf, pos + prop.Utf8NameBytes.Length + 16);
                buf[pos++] = (byte)'<';
                Buffer.BlockCopy(prop.Utf8NameBytes, 0, buf, pos, prop.Utf8NameBytes.Length);
                pos += prop.Utf8NameBytes.Length;
                " nil=\"true\" />"u8.CopyTo(buf.AsSpan(pos));
                return pos + 14;
            }
            EnsureScratchCapacity(ref buf, pos + prop.Utf8OpenTag.Length + prop.Utf8CloseTag.Length);
            Buffer.BlockCopy(prop.Utf8OpenTag, 0, buf, pos, prop.Utf8OpenTag.Length);
            pos += prop.Utf8OpenTag.Length;
            Buffer.BlockCopy(prop.Utf8CloseTag, 0, buf, pos, prop.Utf8CloseTag.Length);
            return pos + prop.Utf8CloseTag.Length;
        }

        // Element text by XmlText's policy: the markup three as entities, and the characters XML can't
        // carry raw (control characters, CR) as numeric references, as the document writer writes them.
        private static int WriteXmlEscapedUtf8(ReadOnlySpan<char> chars, ref byte[] buf, int pos)
        {
            int start = 0;
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (XmlText.NeedsReference(c))
                {
                    if (i > start)
                        pos += System.Text.Encoding.UTF8.GetBytes(chars.Slice(start, i - start), buf.AsSpan(pos));
                    "&#x"u8.CopyTo(buf.AsSpan(pos));
                    pos += 3;
                    ((int)c).TryFormat(buf.AsSpan(pos), out int hexLength, "X", System.Globalization.CultureInfo.InvariantCulture);
                    pos += hexLength;
                    buf[pos++] = (byte)';';
                    start = i + 1;
                }
                else if (c == '&' || c == '<' || c == '>')
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

        /// <summary>
        /// The object a row of a type of scalars holds, parsed from its bytes: the fast path of a read. It reads the rows
        /// XOD writes: elements, attributes and escaped text. Any other row it leaves to its caller, which reads it as XML
        /// and reads it or names the damage (ISS-144): a row holding CDATA, a comment or a processing instruction, a
        /// value it doesn't parse, a close tag that doesn't name its element, or a row that ends before its element. It
        /// left such a value at its default, read <c>&lt;![CDATA[</c> as text, ended a tag at a '>' inside a quoted
        /// attribute value, and returned the object read so far. On a row written by hand the XML read is stricter in
        /// three places, filed as ISS-195: <c>nil</c> found by its text (a single-quoted one is missed), a number in
        /// exponent form read as a decimal, and a character reference written <c>&amp;#X</c>.
        /// </summary>
        private static bool TryParseEntityFromUtf8Row(ReadOnlySpan<byte> rowSpan, TypeMetadata meta, out object entity)
        {
            entity = null;
            try
            {
                // Markup it doesn't follow: CDATA, comments, processing instructions.
                if (rowSpan.IndexOf("<!"u8) >= 0 || rowSpan.IndexOf("<?"u8) >= 0)
                    return false;

                int rowOpenEnd = PageRows.TagEnd(rowSpan, 0) - 1;
                if (rowOpenEnd < 0)
                    return false;

                int entityOpenStart = rowSpan.Slice(rowOpenEnd + 1).IndexOf((byte)'<');
                if (entityOpenStart < 0)
                    return false;

                int cursor = rowOpenEnd + 1 + entityOpenStart;
                int entityOpenEnd = PageRows.TagEnd(rowSpan, cursor) - 1 - cursor;
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
                        return false;

                    cursor += nextLt;
                    if (cursor + 1 >= rowSpan.Length)
                        return false;
                    if (rowSpan[cursor + 1] == (byte)'/')
                        break; // the entity's close tag: the row is read

                    int tagEnd = PageRows.TagEnd(rowSpan, cursor);
                    if (tagEnd < 0)
                        return false;
                    int tagEndRel = tagEnd - 1 - (cursor + 1);
                    if (tagEndRel <= 0)
                        return false;

                    bool isSelfClosing = rowSpan[cursor + tagEndRel] == (byte)'/';
                    int nameLen = isSelfClosing ? tagEndRel - 1 : tagEndRel;
                    ReadOnlySpan<byte> tagSpan = rowSpan.Slice(cursor + 1, nameLen);
                    // A property element may carry nil="true" (ISS-060): its name ends at the first space.
                    int nameEnd = tagSpan.IndexOf((byte)' ');
                    ReadOnlySpan<byte> propNameSpan = nameEnd < 0 ? tagSpan : tagSpan.Slice(0, nameEnd);
                    bool isNil = nameEnd >= 0 && tagSpan.Slice(nameEnd).IndexOf(" nil=\"true\""u8) >= 0;

                    cursor += 1 + tagEndRel + 1;

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

                    if (isSelfClosing)
                    {
                        // No text: null when marked, an empty string for a string (ISS-060).
                        if (matchedProp != null && isNil)
                            matchedProp.SetValue(item, null);
                        else if (matchedProp != null && matchedProp.ScalarKind == ScalarPropertyKind.String)
                            matchedProp.StringSetter(item, string.Empty);
                        continue;
                    }

                    int valEndRel = rowSpan.Slice(cursor).IndexOf("</"u8);
                    if (valEndRel < 0)
                        return false;

                    ReadOnlySpan<byte> valSpan = rowSpan.Slice(cursor, valEndRel);
                    cursor += valEndRel + 2;
                    // The close tag names the element it closes, as the XML reader requires.
                    if (!rowSpan.Slice(cursor).StartsWith(propNameSpan))
                        return false;
                    cursor += propNameSpan.Length;
                    while (cursor < rowSpan.Length && (rowSpan[cursor] == (byte)' ' || rowSpan[cursor] == (byte)'\t' ||
                                                       rowSpan[cursor] == (byte)'\r' || rowSpan[cursor] == (byte)'\n'))
                        cursor++;
                    if (cursor >= rowSpan.Length || rowSpan[cursor] != (byte)'>')
                        return false;
                    cursor++;

                    if (matchedProp == null)
                        continue;
                    if (isNil)
                    {
                        matchedProp.SetValue(item, null);
                        continue;
                    }
                    if (valSpan.Length == 0)
                    {
                        if (matchedProp.ScalarKind == ScalarPropertyKind.String)
                            matchedProp.StringSetter(item, string.Empty);
                        continue;
                    }

                    switch (matchedProp.ScalarKind)
                    {
                        case ScalarPropertyKind.Int32:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out int iv, out int ic) && ic == valSpan.Length)
                                matchedProp.IntSetter(item, iv);
                            else
                                return false;
                            break;
                        case ScalarPropertyKind.Int64:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out long lv, out int lc) && lc == valSpan.Length)
                                matchedProp.LongSetter(item, lv);
                            else
                                return false;
                            break;
                        case ScalarPropertyKind.Decimal:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out decimal decv, out int decc) && decc == valSpan.Length)
                                matchedProp.DecimalSetter(item, decv);
                            else
                                return false;
                            break;
                        case ScalarPropertyKind.Double:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out double dv, out int dc) && dc == valSpan.Length)
                                matchedProp.DoubleSetter(item, dv);
                            else
                            {
                                string dStr = UnescapeXmlUtf8(valSpan);
                                if (double.TryParse(dStr, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out double dv2))
                                    matchedProp.DoubleSetter(item, dv2);
                                else
                                    return false;
                            }
                            break;
                        case ScalarPropertyKind.Boolean:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out bool bv, out int bc) && bc == valSpan.Length)
                                matchedProp.BoolSetter(item, bv);
                            else
                                return false;
                            break;
                        case ScalarPropertyKind.Guid:
                            if (System.Buffers.Text.Utf8Parser.TryParse(valSpan, out Guid gv, out int gc) && gc == valSpan.Length)
                                matchedProp.GuidSetter(item, gv);
                            else
                                return false;
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

            return XmlText.Unescape(System.Text.Encoding.UTF8.GetString(utf8Span));
        }

        internal bool TryReadEntitySliceFromRawPage(string pageFileName, TypeMetadata meta, ref int remainingSkip, int maxTake, System.Collections.IList outEntities)
        {
            if (meta == null || !meta.IsPureScalarStandalone)
                return false;

            RawPageBuffer rawPage = RawPageForRead(pageFileName);
            if (rawPage == null)
                return false;

            // All or nothing: a row the fast parse can't take sends the whole page to its caller's XML read
            // (TryReadRowSliceFromRawPage), which reads every row or names the damaged one. It was left out (ISS-144).
            ReadOnlySpan<byte> span = rawPage.Span;
            int skip = remainingSkip;
            var taken = new List<object>();
            foreach (var row in rawPage.RowsInOrder())
            {
                if (taken.Count >= maxTake)
                    break;
                if (skip > 0)
                {
                    skip--;
                    continue;
                }
                if (!TryParseEntityFromUtf8Row(span.Slice(row.Offset, row.Length), meta, out object entity))
                    return false;
                taken.Add(entity);
            }

            remainingSkip = skip;
            foreach (var entity in taken)
                outEntities.Add(entity);
            return true;
        }

        internal bool TryReadRowSliceFromRawPage(string pageFileName, ref int remainingSkip, int maxTake, List<XElement> outRows)
        {
            RawPageBuffer rawPage = RawPageForRead(pageFileName);
            if (rawPage == null)
                return false;

            int added = 0;
            foreach (var row in rawPage.RowsInOrder())
            {
                if (added >= maxTake)
                    break;
                if (remainingSkip > 0)
                {
                    remainingSkip--;
                    continue;
                }
                outRows.Add(ParseRow(rawPage, row.Offset, row.Length, null));
                added++;
            }

            return true;
        }

        internal bool TryCountRowsInRawPage(string pageFileName, out int rowCount)
        {
            rowCount = 0;
            RawPageBuffer rawPage = RawPageForRead(pageFileName);
            if (rawPage == null)
                return false;

            if (rawPage.RowCount >= 0)
            {
                rowCount = rawPage.RowCount;
                return true;
            }

            rowCount = rawPage.RowsMapped();
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
            // An evicted page keeps its bytes: a reader beside this one may hold it (RawPageBuffer.ReleasePooledBuffer).
            for (int i = 0; i < keysToRemove.Count; i++)
                cachedRawPages.Remove(keysToRemove[i]);
        }

        /// <summary>
        /// The raw page counterpart of <see cref="FilePendingUnlocked"/>: called before a cached raw page changes (from
        /// <paramref name="from"/> on), so a change that fails part way, or whose write fails, is still a write to make
        /// (ISS-099), and a statement's savepoint notes the bytes it replaces (ISS-125). Called under syncLock.
        /// </summary>
        private void RawPagePendingUnlocked(string pageFileName, string fullPath, RawPageBuffer rawPage, int from)
        {
            StatementChangesUnlocked();
            KeepRawPage(fullPath, rawPage, from);
            if (batchDepth > 0)
            {
                // The hook first, then the batch (FilePendingUnlocked says why).
                if (!dirtyBatchRawPages.Contains(pageFileName))
                {
                    BeforeFileMutate?.Invoke(fullPath);
                    dirtyBatchRawPages.Add(pageFileName);
                }
                return;
            }
            dirtyBufferedRawPages.Add(pageFileName);
        }

        private void FlushRawPagesList(List<string> pageNames)
        {
            if (pageNames.Count > 0)
                RefuseWriteInsideTransaction(Path.Combine(root, pageNames[0]));
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

            // No transaction is open here (RefuseWriteInsideTransaction above): a transaction's pages are written by
            // its commit, once it is no longer active, in place like any other, its journal holding their before-images.
            Action<string> changed = ChangedOnDisk;
            if (items.Count < 4)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    FlushRawPageToDisk(items[i].FullPath, items[i].Page, changed, Capture);
                }
            }
            else
            {
                ParallelWrites(items, item => FlushRawPageToDisk(item.FullPath, item.Page, changed, Capture));
            }
        }

        // changed: IOService.ChangedOnDisk, called once the file on disk may differ (may run on pool threads).
        // capture: IOService.Capture, before each write (a statement's savepoint notes what the write replaces).
        private static void FlushRawPageToDisk(string fullPath, RawPageBuffer rawPage, Action<string> changed, Action<string, long> capture)
        {
            if (rawPage?.Bytes == null || rawPage.Length <= 0)
                return;

            bool inPlace = !rawPage.IsNewFile &&
                rawPage.DirtyStartOffset < rawPage.Length &&
                rawPage.DirtyStartOffset >= 0 &&
                File.Exists(fullPath);
            // What the write replaces: in place, from where it starts.
            capture(fullPath, inPlace ? rawPage.DirtyStartOffset : 0);
            FaultInjection.Step("file-write", file: System.IO.Path.GetFileName(fullPath));
            if (inPlace)
            {
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None, 65536, FileOptions.None))
                {
                    if (fs.Length == rawPage.DiskSyncedLength)
                    {
                        changed?.Invoke(fullPath);
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

            // Not as last written, or a new page: the file is written whole, so the rest of it is noted too. A new page
            // too goes through its .tmp (ISS-134): written straight to its name, a crash left half a page there.
            capture(fullPath, 0);
            WriteRawBytesAtomic(fullPath, rawPage.Bytes, rawPage.Length);
            changed?.Invoke(fullPath);

            rawPage.IsNewFile = false;
            rawPage.DiskSyncedLength = rawPage.Length;
            rawPage.DirtyStartOffset = int.MaxValue;
        }

        /// <summary>
        /// Writes these files side by side (4 or more: Parallel.ForEach). One that fails is thrown as itself, as a
        /// write of fewer files throws it, not wrapped in the AggregateException Parallel.ForEach throws (ISS-137: the
        /// caller's handling depended on how many files the statement saved). Several failures stay together.
        /// </summary>
        private static void ParallelWrites<T>(List<T> items, Action<T> write)
        {
            try
            {
                System.Threading.Tasks.Parallel.ForEach(items, write);
            }
            catch (AggregateException e) when (e.InnerExceptions.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
        }

        /// <summary>Writes a whole file so that a crash leaves the old one or the new one, or none for a new file:
        /// beside it as <c>.tmp</c>, then moved over it. A <c>.tmp</c> left is deleted at the next open
        /// (<see cref="RecoverInterruptedWrites"/>): nothing says it was written to its end. The one writer of a data
        /// file written whole (pages, tables); the index snapshot and a transaction's journal and undo follow the same
        /// order. The step between its halves is a crash's point for the tests (FaultInjection).</summary>
        internal static void WriteRawBytesAtomic(string fullPath, byte[] bytes, int length)
        {
            string tempPath = fullPath + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.None))
            {
                int half = length / 2;
                fs.Write(bytes, 0, half);
                fs.Flush(flushToDisk: false);
                FaultInjection.Step("file-partly-written", file: System.IO.Path.GetFileName(fullPath));
                fs.Write(bytes, half, length - half);
                fs.Flush(flushToDisk: false);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
    }
}
