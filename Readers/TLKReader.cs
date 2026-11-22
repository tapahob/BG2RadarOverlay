using BGOverlay.Resources;
using System.Collections.Generic;
using System.IO;

namespace BGOverlay
{
    public class TLKReader
    {
        public TLKReader(string tlkFilePath)
        {
            using (BinaryReader reader = new BinaryReader(File.OpenRead(tlkFilePath)))
            {
                this.Signature          = new string(reader.ReadChars(4));
                this.Version            = new string(reader.ReadChars(4));
                this.LanguageId         = reader.ReadInt16();
                this.StrRefCount        = reader.ReadInt32();
                this.OffsetToStringData = reader.ReadInt32();

                if (Signature != "TLK " || Version != "V1  " || StrRefCount < 0
                    || OffsetToStringData < 18 + StrRefCount * 26L
                    || OffsetToStringData > reader.BaseStream.Length)
                    throw new InvalidDataException($"Invalid TLK header: {tlkFilePath}");

                StringRefs = new Dictionary<int, TLKEntry>(StrRefCount);
                for (int i=0; i<StrRefCount; ++i)
                    StringRefs.Add(i, new TLKEntry(reader, OffsetToStringData));
                foreach (var entry in StringRefs.Values)
                    entry.LoadText(reader);
            }
        }

        internal Dictionary<int, TLKEntry> StringRefs { get; }
        public string Signature { get; }
        public string Version { get; }
        public short LanguageId { get; }
        public int StrRefCount { get; }
        public int OffsetToStringData { get; private set; }
    }
}
