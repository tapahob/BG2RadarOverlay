using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BGOverlay
{
    public class TLKReader
    {
        public static Dictionary<int, TLKEntry> StringRefs = new Dictionary<int, TLKEntry>();

        public TLKReader(string locale = "ru_RU")
        {
            string tlkFilePath = $"{Configuration.GameFolder}/lang/{locale}/dialogf.tlk";

            

            using (BinaryReader reader = new BinaryReader(File.OpenRead(tlkFilePath)))
            {
                this.Signature          = new string(reader.ReadChars(4));
                this.Version            = new string(reader.ReadChars(4));
                this.LanguageId         = reader.ReadInt16();
                this.StrRefCount        = reader.ReadInt32();
                OffsetToStringData      = reader.ReadInt32();

                reader.BaseStream.Seek(18, SeekOrigin.Begin);
                for (int i=0; i<StrRefCount; ++i)
                {
                    var entry = new TLKEntry(reader);
                    //entry.LoadText(reader);
                    StringRefs[i] = entry;
                }
                StringRefs.Values.ToList().ForEach(x => x.LoadText(reader));
                int z = 0;
            }
        }

        public string Signature { get; }
        public string Version { get; }
        public short LanguageId { get; }
        public int StrRefCount { get; }
        public static int OffsetToStringData { get; private set; }
    }
}
