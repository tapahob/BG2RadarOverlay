using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BGOverlay
{
    // reference: https://gibberlings3.github.io/iesdp/file_formats/ie_formats/cre_v1.htm
    public class ITMReader
    {
        public static string gameDirectory = Configuration.GameFolder;
        private ResourceManager resourceManager;

        public ITMReader(ResourceManager resourceManager, String itmFilename = "GHAST1.ITM", int originOffset = 0, string biffArchivePath = "")
        {
            this.resourceManager = resourceManager;
            var filename         = itmFilename;
            var overrideItmFilename = $"{gameDirectory}\\override\\{itmFilename}";
            if (File.Exists(overrideItmFilename))
            {
                originOffset    = 0;
                biffArchivePath = "";
                filename        = overrideItmFilename;
            } 
            else
            {   
                var lastTry = Directory.Exists($"{gameDirectory}\\override") 
                    ? Directory.GetFiles($"{gameDirectory}\\override").Select(x=>x.ToUpper()).FirstOrDefault(x => x.EndsWith(itmFilename))
                    : null;
                if (lastTry == null)
                {
                    filename = biffArchivePath;
                    if (biffArchivePath.Equals(""))
                    {
                        var test2 = resourceManager.ITMResourceEntries.FirstOrDefault(x => x.FullName.EndsWith(itmFilename));
                        if (test2 != null)
                        {
                            test2.LoadCREFiles();
                            return;
                        }  
                    }
                } else
                {
                    filename = lastTry;
                }

            }
            using (BinaryReader reader = new BinaryReader(File.OpenRead(filename)))
            {
                reader.BaseStream.Seek(originOffset, SeekOrigin.Begin);
                this.Signature = new string(reader.ReadChars(4));
                this.Version = new string(reader.ReadChars(4));
                resourceManager.StringRefs.TryGetValue(reader.ReadInt32(), out var text);
                this.GeneralName = text?.Text;
                resourceManager.StringRefs.TryGetValue(reader.ReadInt32(), out text);
                this.IdentifiedName = text?.Text;
            }
        }
        public string Signature { get; private set; }
        public string Version { get; private set; }
        public string GeneralName { get; private set; }
        public string IdentifiedName { get; private set; }


    }
}