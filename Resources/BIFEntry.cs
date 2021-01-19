using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BGOverlay.Resources
{
    public class BIFEntry
    {
        public BIFEntry(int index, BinaryReader reader, int offset)
        {
            this.Index          = index;
            this.FileSize       = reader.ReadInt32();
            this.FilenameOffset = reader.ReadInt32();
            var filenameLength  = reader.ReadInt16();
            this.Location       = reader.ReadInt16() & 0xffff;

            var currentPosition = reader.BaseStream.Position;

            reader.BaseStream.Seek(FilenameOffset, SeekOrigin.Begin);
            this.FileName = new string(reader.ReadChars(filenameLength-1));
            reader.BaseStream.Seek(currentPosition, SeekOrigin.Begin);

            this.BIFFilePath = $"{Configuration.GameFolder}/{FileName}".Replace('\\', '/');
        }

        public override string ToString()
        {
            return this.FileName;
        }

        public int Index { get; }
        public int FileSize { get; }
        public int FilenameOffset { get; }
        public int Location { get; }
        public string FileName { get; }
        public string BIFFilePath { get; }
    }
}
