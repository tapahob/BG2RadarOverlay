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
            this.Duration = reader.ReadInt32();

            reader.BaseStream.Seek(offset + 0x24, SeekOrigin.Begin);
            var saveTypeNum = reader.ReadInt32();
            SaveType = Save.None;
            if ((saveTypeNum & (1 << 0)) > 0)
                SaveType = Save.Spell;
            if ((saveTypeNum & (1 << 1)) > 0)
                SaveType = Save.Breath;
            if ((saveTypeNum & (1 << 2)) > 0)
                SaveType = Save.Death;
            if ((saveTypeNum & (1 << 3)) > 0)
                SaveType = Save.Wand;
            if ((saveTypeNum & (1 << 4)) > 0)
                SaveType = Save.Polymorph;
            
            reader.BaseStream.Seek(offset + 0x28, SeekOrigin.Begin);
            this.SaveBonus = reader.ReadInt32();
        }

        public enum Save
        {
            None,
            Spell,
            Breath,
            Death,
            Wand,
            Polymorph
        }

        public override string ToString()
        {
            var result = $"{preprocess(EffectName)}, Save vs {SaveType}";
            if (SaveBonus == 0)
                return result;
            result += SaveBonus > 0 
                ? $" +{SaveBonus}"
                : $" {SaveBonus}";            
            return result;
        }

        private string preprocess(Effect effectName)
        {
            return effectName.ToString().Replace("Death_", "").Replace("_", " ");
        }        

        public int OpCode { get; }
        public Effect EffectName { get; }
        public int Duration { get; }
        public Save SaveType { get; }
        public int SaveBonus { get; }
    }

}
