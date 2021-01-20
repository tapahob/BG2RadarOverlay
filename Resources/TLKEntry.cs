using System.IO;
using System.Text;

namespace BGOverlay.Resources
{
    public class TLKEntry
    {
        public TLKEntry(BinaryReader reader, int offsetToStringData)
        {
            this.BitField           = reader.ReadInt16();
            this.Sound              = reader.ReadChars(8);
            this.Volume             = reader.ReadInt32();
            this.Pitch              = reader.ReadInt32();
            this.Offset             = reader.ReadInt32();
            this.StrLength          = reader.ReadInt32();
            this.Text               = "No text found";
            this.offsetToStringData = offsetToStringData;
        }

        public override string ToString()
        {
            return Text;
        }

        public void LoadText(BinaryReader reader)
        {
            reader.BaseStream.Seek(offsetToStringData + Offset, SeekOrigin.Begin);
            Text = Encoding.UTF8.GetString(reader.ReadBytes(StrLength));
        }

        public short BitField { get; }
        public char[] Sound { get; }
        public int Volume { get; }
        public int Pitch { get; }
        public int Offset { get; }
        public int StrLength { get; }
        public string Text { get; private set; }

        private int offsetToStringData;
    }
}
