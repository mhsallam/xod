using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace Xod.Services
{
    /// <summary>
    /// ISS-125: what a write statement outside a transaction (direct mode: no transaction, no buffer) changes on disk,
    /// so that one that fails puts every data file back as it found it. Each page, table or other data file is noted
    /// before the statement first changes it: the bytes from where the change starts to the file's end (a page appended
    /// to has only its closing tags there), the whole file for a file written whole or deleted, or that it wasn't there.
    /// Taken from what the context holds of the file, which is what is on disk below anything changed since its last
    /// write: a cached page's bytes as it is about to change (IOService.RawPagePendingUnlocked), a document's bytes as
    /// last read or written (XFile.DiskBytes). Read from the file only when the context holds neither: on Windows a
    /// file just written can take milliseconds to open for reading (scanned again), against tens of microseconds for
    /// the write itself. The undo writes back the files the statement changed.
    /// Index files and the database file are left out: an index is a derived cache, which the statement's failure drops
    /// (IndexService.ForgetStatement), and the database file holds the AES256 key, which outlasts every statement, as it
    /// outlasts a transaction (XodTransaction.CaptureBeforeMutate).
    /// Held in memory: a crash in the middle of a statement's writes leaves each file whole, old or new (a whole file
    /// is moved into place, a page appended to keeps its rows), not the statement all or nothing; a transaction is.
    /// The writes may run on pool threads (Parallel.ForEach), so its record has a lock of its own.
    /// A page's bytes are copied into arrays from the shared pool, given back when the statement ends (Release): an
    /// update or a delete on a large page copies up to its end, and a new array each time cost the success path
    /// collections of the large object heap.
    /// </summary>
    internal sealed class StatementSavepoint
    {
        private sealed class Before
        {
            internal bool Existed;   // false: the statement made the file
            internal bool Changed;   // a write of the statement changed it, or may have (IOService.ChangedOnDisk)
            internal long Length;    // its length before
            internal long SavedFrom; // Bytes hold its bytes from here to Length; the statement changed nothing below here
            internal byte[] Bytes;   // its first Count bytes
            internal int Count;
            internal bool Rented;    // Bytes is from ArrayPool<byte>.Shared, given back by Prepend or Release
        }

        private readonly Dictionary<string, Before> files = new Dictionary<string, Before>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();

        /// <summary>
        /// Before the statement changes the file from <paramref name="from"/> to its end, with <paramref name="held"/>
        /// what the context holds of it: equal to the file as the statement found it from <paramref name="from"/> to
        /// <paramref name="length"/>, and below the statement's earlier changes. The statement changed nothing below
        /// SavedFrom, so a change lower down notes the bytes between.
        /// </summary>
        internal void Keep(string fullPath, long from, long length, ReadOnlySpan<byte> held)
        {
            lock (gate)
            {
                if (!files.TryGetValue(fullPath, out var before))
                {
                    long start = Math.Clamp(from, 0, length);
                    int count = (int)(length - start);
                    byte[] copy = count > 0 ? ArrayPool<byte>.Shared.Rent(count) : Array.Empty<byte>();
                    held.Slice((int)start, count).CopyTo(copy);
                    files[fullPath] = new Before { Existed = true, Length = length, SavedFrom = start, Bytes = copy, Count = count, Rented = count > 0 };
                    return;
                }
                if (!before.Existed || from >= before.SavedFrom)
                    return;
                long lower = Math.Max(from, 0);
                Prepend(before, held.Slice((int)lower, (int)(before.SavedFrom - lower)), lower);
            }
        }

        /// <summary>
        /// Before the statement writes the whole file, or deletes it, with <paramref name="bytes"/> its bytes on disk as
        /// the context last read or wrote them (never changed after: kept as they are, not copied). Null when the
        /// context doesn't know them: the file is read.
        /// </summary>
        internal void KeepWhole(string fullPath, byte[] bytes)
        {
            if (bytes == null)
            {
                BeforeWrite(fullPath, 0);
                return;
            }
            lock (gate)
            {
                if (!files.TryGetValue(fullPath, out var before))
                {
                    files[fullPath] = new Before { Existed = true, Length = bytes.Length, SavedFrom = 0, Bytes = bytes, Count = bytes.Length };
                    return;
                }
                if (!before.Existed || before.SavedFrom == 0)
                    return;
                Prepend(before, bytes.AsSpan(0, (int)before.SavedFrom), 0);
            }
        }

        /// <summary>
        /// Before a write that changes the file from <paramref name="from"/> to its end (0: the whole file, or its
        /// delete), when the context holds nothing of the file to note: it is read (a file the statement made is noted
        /// as not there). Throws when the file can't be read (another program holds it): the write isn't made, and the
        /// statement fails with nothing of this file to put back.
        /// </summary>
        internal void BeforeWrite(string fullPath, long from)
        {
            lock (gate)
            {
                if (!files.TryGetValue(fullPath, out var before))
                {
                    if (!File.Exists(fullPath))
                    {
                        files[fullPath] = new Before { Existed = false };
                        return;
                    }
                    using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    long length = fs.Length;
                    long start = Math.Clamp(from, 0, length);
                    byte[] read = Read(fs, start, length - start);
                    files[fullPath] = new Before { Existed = true, Length = length, SavedFrom = start, Bytes = read, Count = read.Length };
                    return;
                }
                if (!before.Existed || from >= before.SavedFrom)
                    return;
                long lower = Math.Max(from, 0);
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    Prepend(before, Read(fs, lower, before.SavedFrom - lower), lower);
            }
        }

        private static void Prepend(Before before, ReadOnlySpan<byte> below, long lower)
        {
            var joined = new byte[below.Length + before.Count];
            below.CopyTo(joined);
            Array.Copy(before.Bytes, 0, joined, below.Length, before.Count);
            if (before.Rented)
                ArrayPool<byte>.Shared.Return(before.Bytes);
            before.Bytes = joined;
            before.Count = joined.Length;
            before.Rented = false;
            before.SavedFrom = lower;
        }

        /// <summary>
        /// The statement has noted the file from <paramref name="offset"/> or lower: a change the context holds of it
        /// from there up is the statement's own, not one from before it still to be written.
        /// </summary>
        internal bool Covers(string fullPath, long offset)
        {
            lock (gate)
            {
                return files.TryGetValue(fullPath, out var before) && (!before.Existed || before.SavedFrom <= offset);
            }
        }

        /// <summary>A write of the statement has changed the file on disk, or may have from here.</summary>
        internal void Changed(string fullPath)
        {
            lock (gate)
            {
                if (files.TryGetValue(fullPath, out var before))
                    before.Changed = true;
            }
        }

        private static byte[] Read(FileStream fs, long offset, long count)
        {
            var bytes = new byte[count];
            fs.Seek(offset, SeekOrigin.Begin);
            fs.ReadExactly(bytes);
            return bytes;
        }

        /// <summary>
        /// Puts every file the statement changed back as it found it: one it made is deleted, one written whole or
        /// deleted is written back whole (as every whole file is: <see cref="IOService.WriteRawBytesAtomic"/>), one
        /// written in place gets its bytes back and its length. A file noted whose write failed before changing it
        /// (another program holds it), or that no write reached, is left alone. Each file is tried; the failures are
        /// thrown together after.
        /// </summary>
        internal void Undo()
        {
            List<Exception> failures = null;
            int putBack = 0;
            lock (gate)
            {
                foreach (var (fullPath, before) in files)
                {
                    if (!before.Changed)
                        continue;
                    try
                    {
                        FaultInjection.Step("undo-file", ++putBack);
                        if (!before.Existed)
                        {
                            if (File.Exists(fullPath))
                                File.Delete(fullPath);
                        }
                        else if (before.SavedFrom == 0)
                        {
                            IOService.WriteRawBytesAtomic(fullPath, before.Bytes, before.Count);
                        }
                        else
                        {
                            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None);
                            fs.Seek(before.SavedFrom, SeekOrigin.Begin);
                            fs.Write(before.Bytes, 0, before.Count);
                            fs.SetLength(before.Length);
                            fs.Flush(flushToDisk: false);
                        }
                    }
                    catch (Exception e)
                    {
                        (failures ??= new List<Exception>()).Add(e);
                    }
                }
            }
            Release();
            if (failures != null)
                throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }

        /// <summary>The statement is over: the copies taken from the pool go back to it, and the record is emptied.</summary>
        internal void Release()
        {
            lock (gate)
            {
                foreach (var before in files.Values)
                {
                    if (before.Rented)
                        ArrayPool<byte>.Shared.Return(before.Bytes);
                    before.Bytes = null;
                    before.Rented = false;
                }
                files.Clear();
            }
        }
    }
}
