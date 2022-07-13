using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.IO;

namespace BGOverlay
{
    public class BIFFReader
    {
        public Dictionary<int, BIFFV1FileEntry> BIFFV1FileEntries;
        
        public string BiffFilename { get; private set; }

        public BIFFReader(string biffFilename = "Default.bif")
        {
            biffFilename = init(biffFilename);
        }

        private string init(string biffFilename)
        {
            try
            {
                BIFFV1FileEntries = new Dictionary<int, BIFFV1FileEntry>();
                this.BiffFilename = biffFilename;
                biffFilename = $"{Configuration.GameFolder}/data/{biffFilename}".Replace('\\', '/');
                if (!File.Exists(biffFilename))
                {
                    biffFilename = $"{Configuration.GameFolder}/sod-dlc/{biffFilename}".Replace('\\', '/');
                }
                using (BinaryReader reader = new BinaryReader(File.OpenRead(biffFilename)))
                {
                    this.Signature           = new string(reader.ReadChars(4));
                    this.Version             = new string(reader.ReadChars(4));
                    this.FileEntriesCount    = reader.ReadInt32();
                    this.TilesetEntriesCount = reader.ReadInt32();
                    this.FileEntriesOffset   = reader.ReadInt32();

                    reader.BaseStream.Seek(FileEntriesOffset, SeekOrigin.Begin);

                    for (int i = 0; i < FileEntriesCount; ++i)
                    {
                        BIFFV1FileEntries.Add(i, new BIFFV1FileEntry(reader));
                    }
                }
                return biffFilename;
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not find a BIFF {biffFilename}!", ex);
            }
            return null;
        }

        public string Signature { get; private set; }
        public string Version { get; private set; }
        public int FileEntriesCount { get; private set; }
        public int TilesetEntriesCount { get; private set; }
        public int FileEntriesOffset { get; private set; }
    }
}
