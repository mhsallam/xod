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
    }
}
