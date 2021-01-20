using BGOverlay.Resources;
using System.Collections.Generic;
using System.IO;

namespace BGOverlay
{
    public class BIFFReader
    {
        public Dictionary<int, BIFFV1FileEntry> BIFFV1FileEntries;
        
        public string BiffFilename { get; }

        public BIFFReader(string biffFilename = "Default.bif")
        {
            BIFFV1FileEntries = new Dictionary<int, BIFFV1FileEntry>();
            this.BiffFilename = biffFilename;
            biffFilename = $"{Configuration.GameFolder}/data/{biffFilename}".Replace('\\', '/');

            using (BinaryReader reader = new BinaryReader(File.OpenRead(biffFilename)))
            {
                this.Signature           = new string(reader.ReadChars(4));
                this.Version             = new string(reader.ReadChars(4));
                this.FileEntriesCount    = reader.ReadInt32();
                this.TilesetEntriesCount = reader.ReadInt32();
                this.FileEntriesOffset   = reader.ReadInt32();

                reader.BaseStream.Seek(FileEntriesOffset, SeekOrigin.Begin);

                for (int i=0; i<FileEntriesCount; ++i)
                {
                    BIFFV1FileEntries.Add(i, new BIFFV1FileEntry(reader));
                }
            }
        }

        public string Signature { get; }
        public string Version { get; }
        public int FileEntriesCount { get; }
        public int TilesetEntriesCount { get; }
        public int FileEntriesOffset { get; }
    }
}
