using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static BGOverlay.Resources.BIFResourceEntry;

namespace BGOverlay
{
    public class EFFReader
    {
        public EFFReader(ResourceManager resourceManager, string effFilename)
        {
            effFilename = effFilename.ToUpper();
            var overrideDir = $"{Configuration.GameFolder}\\override";
            //var overrideSPLs = Directory.Exists(overrideDir) ? Directory.GetFiles(overrideDir, "*.SPL").Select(x => x.ToUpper()).ToList() : new List<string>();
            var filename = $"{Configuration.GameFolder}\\override\\{effFilename}".ToUpper();
            var originalOffset = 0;
            if (!new FileInfo(filename).Exists)
            {
                var bifResourceEntry = resourceManager.EFFResourceEntries.FirstOrDefault(x => x.FullName == effFilename);
                if (bifResourceEntry == null)
                {
                    return;
                }
                var resourceLocator  = bifResourceEntry.ResourceLocator;
                var bifFilePath      = bifResourceEntry.BiffEntry.FileName;
                var allEntries       = resourceManager.GetBIFFReader(bifFilePath.Substring(bifFilePath.LastIndexOf('/') + 1)).BIFFV1FileEntries;
                var biffFileEntry    = allEntries[resourceLocator & 0xfffff];
                if (biffFileEntry.Ext != Extension.EFF)
                {
                    throw new Exception();
                }
                filename       = $"{Configuration.GameFolder}\\{bifFilePath}";
                originalOffset = biffFileEntry.Offset;
            }

            using (BinaryReader reader = new BinaryReader(File.OpenRead(filename)))
            {
                reader.BaseStream.Seek(originalOffset + 0x0010, SeekOrigin.Begin);
                this.Type = reader.ReadInt32();
                
                reader.BaseStream.Seek(originalOffset + 0x001C, SeekOrigin.Begin);
                this.Parameter1 = reader.ReadInt32();

                reader.BaseStream.Seek(originalOffset + 0x0020, SeekOrigin.Begin);
                this.Parameter2 = reader.ReadInt32();

                reader.BaseStream.Seek(originalOffset + 0x0028, SeekOrigin.Begin);
                this.Duration = reader.ReadInt32();
            }
        }
        public int Type { get; }
        public int Parameter1 { get; }
        public int Parameter2 { get; }
        public int Duration { get; }

        public override string ToString()
        {
            var durationStr = Duration != 0 ? $" ({Duration}s)" : "";

            var parameter1Str = Parameter1 != 0 ? $": {Parameter1}" : "";

            return $"{(Effect)Type}{durationStr}{parameter1Str}";
        }
    }
}