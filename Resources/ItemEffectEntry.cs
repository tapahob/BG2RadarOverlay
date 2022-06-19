using System;
using System.IO;
using System.Text;

namespace BGOverlay.Resources
{
    public class ItemEffectEntry
    {
        public ItemEffectEntry(BinaryReader reader, int offset)
        {
            reader.BaseStream.Seek(offset, SeekOrigin.Begin);
            this.OpCode = reader.ReadInt16();
            this.EffectName = (Effect) OpCode;

            reader.BaseStream.Seek(offset + 0xE, SeekOrigin.Begin);
            this.Duration = reader.ReadInt32(); ;

            reader.BaseStream.Seek(offset + 0x24, SeekOrigin.Begin);
            var saveTypeNum = reader.ReadInt32(); ;
            SaveType = (Save)saveTypeNum;

            reader.BaseStream.Seek(offset + 0x28, SeekOrigin.Begin);
            this.SaveBonus = reader.ReadInt32();
        }

        public enum Save
        {
            Spell = 0,
            Breath_Weapon = 1,
            Paralyze_Poison_Death = 2,
            Rod_Staff_Wand = 3,
            Petrify_Polymorph = 4
        }

        public override string ToString()
        {
            return $"{EffectName} ({Duration}): {SaveType} ({SaveBonus})";
        }

        public int OpCode { get; }
        public Effect EffectName { get; }
        public int Duration { get; }
        public Save SaveType { get; }
        public int SaveBonus { get; }
    }

}
