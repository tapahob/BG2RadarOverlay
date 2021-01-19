using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BGOverlay
{
    public class KeyReader
    {
        public static List<BIFEntry> BIFEntries = new List<BIFEntry>();
        public static List<BIFResourceEntry> BIFResourceEntries = new List<BIFResourceEntry>();
        public static List<BIFResourceEntry> CREResorceEntries => BIFResourceEntries.Where(x => x.Ext == BIFResourceEntry.Extension.CRE).ToList();
        public KeyReader(string keyFilename = "chitin.key")
        {
            keyFilename = $"{Configuration.GameFolder}\\{keyFilename}";
            using (BinaryReader reader = new BinaryReader(File.Open(keyFilename, FileMode.Open)))
            {
                this.Signature             = new string(reader.ReadChars(4));
                this.Version               = new string(reader.ReadChars(4));
                this.BIFEntriesCount       = reader.ReadInt32();
                this.ResourceEntriesCount  = reader.ReadInt32();
                this.BIFEntriesOffset      = reader.ReadInt32();
                this.ResourceEntriesOffset = reader.ReadInt32();

                reader.BaseStream.Seek(BIFEntriesOffset, SeekOrigin.Begin);
                for (int i=0, offset=BIFEntriesOffset; i<BIFEntriesCount; ++i)
                {
                    BIFEntries.Add(new BIFEntry(i, reader, offset));
                }

                reader.BaseStream.Seek(ResourceEntriesOffset, SeekOrigin.Begin);
                for (int i = 0, offset = ResourceEntriesOffset; i < ResourceEntriesCount; ++i)
                {
                    BIFResourceEntries.Add(new BIFResourceEntry(i, reader));
                }
            }
        }

        public string Signature { get; }
        public string Version { get; }
        public int BIFEntriesCount { get; }
        public int ResourceEntriesCount { get; }
        public int BIFEntriesOffset { get; }
        public int ResourceEntriesOffset { get; }
    }

}
