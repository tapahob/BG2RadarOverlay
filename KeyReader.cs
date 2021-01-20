using BGOverlay.Resources;
using System.IO;

namespace BGOverlay
{
    public class KeyReader
    {
        private ResourceManager resourceManager;
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
                    resourceManager.BIFEntries.Add(new BIFEntry(i, reader, offset));
                }

                reader.BaseStream.Seek(ResourceEntriesOffset, SeekOrigin.Begin);
                for (int i = 0, offset = ResourceEntriesOffset; i < ResourceEntriesCount; ++i)
                {
                    resourceManager.BIFResourceEntries.Add(new BIFResourceEntry(i, reader, resourceManager));
                }
            }
        }

        public KeyReader(ResourceManager resourceManager)
        {
            this.resourceManager = resourceManager;
        }

        public string Signature { get; }
        public string Version { get; }
        public int BIFEntriesCount { get; }
        public int ResourceEntriesCount { get; }
        public int BIFEntriesOffset { get; }
        public int ResourceEntriesOffset { get; }
    }

}
