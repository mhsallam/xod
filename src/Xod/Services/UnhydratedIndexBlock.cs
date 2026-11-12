namespace Xod.Services
{
    internal sealed class UnhydratedIndexBlock
    {
        public byte[] RawBytes;
        /// <summary>When set, the block lives at Offset..Offset+ByteLength in this file and is read on
        /// demand. Loading the index only walks the property headers instead of reading the whole file.</summary>
        public string FilePath;
        public int Offset;
        public int ByteLength;
        public int EntryCount;
        public string MinKey;
        public string MaxKey;
        /// <summary>The table the block belongs to.</summary>
        public System.Type Type;
        /// <summary>The generation of the snapshot the block was found in (marker -5): a read from the file checks
        /// it, since another context may have replaced the file meanwhile (ISS-042).</summary>
        public long Generation;
    }
}
