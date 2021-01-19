using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BGOverlay
{
    public class BIFFReader
    {
        public Dictionary<int, BIFFV1FileEntry> BIFFV1FileEntries = new Dictionary<int, BIFFV1FileEntry>();
        public static Dictionary<string, BIFFReader> Cache = new Dictionary<string, BIFFReader>();
        public string BiffFilename { get; }

        private BIFFReader(string biffFilename = "Default.bif")
        {
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

        public static BIFFReader Get(string bifFilename)
        {
            bifFilename = bifFilename.ToUpper();
            BIFFReader reader;
            if (!Cache.TryGetValue(bifFilename, out reader))
            {
                reader = new BIFFReader(bifFilename);
                Cache[bifFilename] = reader;
            }
            return reader;
        }

        public string Signature { get; }
        public string Version { get; }
        public int FileEntriesCount { get; }
        public int TilesetEntriesCount { get; }
        public int FileEntriesOffset { get; }
    }
}
