using System;
using System.Collections;
using System.Drawing;
using System.IO;

namespace BGOverlay.Resources
{
    public class ItemEffectEntry
    {
        private SecondaryType secondaryType;
        private string displayString = null; 

        public ItemEffectEntry(BinaryReader reader, int offset)
        {
            reader.BaseStream.Seek(offset, SeekOrigin.Begin);
            this.OpCode     = (Effect) reader.ReadInt16();
            this.EffectName = (Effect) OpCode;
            this.TargetType = reader.ReadByte();
            this.Power      = reader.ReadByte();
            this.Parameter1 = reader.ReadInt32();
            this.Parameter2 = reader.ReadInt32();

            reader.BaseStream.Seek(offset + 0x12, SeekOrigin.Begin);
            this.Probability1 = reader.ReadByte();
            this.Probability2 = reader.ReadByte();
            this.Resource     = new string(reader.ReadChars(8));

            reader.BaseStream.Seek(offset + 0xE, SeekOrigin.Begin);
            this.Duration = reader.ReadInt32();

            reader.BaseStream.Seek(offset + 0x24, SeekOrigin.Begin);
            var saveTypeNum = reader.ReadInt32();

            //SaveType = (Save)saveTypeNum;
            SaveType = Save.None;
            if (((saveTypeNum >> 0) & 1) > 0)
                SaveType = Save.Spell;
            if (((saveTypeNum >> 1) & 1) > 0)
                SaveType = Save.Breath;
            if (((saveTypeNum >> 2) & 1) > 0)
                SaveType = Save.Death;
            if (((saveTypeNum >> 3) & 1) > 0)
                SaveType = Save.Wand;
            if (((saveTypeNum >> 4) & 1) > 0)
                SaveType = Save.Polymorph;

            var debug = new BitArray(new int[] { saveTypeNum });
            var bits = new bool[32];
            debug.CopyTo(bits, 0);
            
            reader.BaseStream.Seek(offset + 0x28, SeekOrigin.Begin);
            this.SaveBonus = reader.ReadInt32();

            if (EffectName == Effect.Use_EFF_File)
            {
                var resource        = $"{((Resource.IndexOf("\0") < 0) ? Resource : (Resource.Substring(0, Resource.IndexOf('\0'))))}.EFF";
                this.SubEffect      = ResourceManager.Instance.GetEFFReader(resource);
                this.EffectName     = SubEffect.Type;
                this.SaveType       = SubEffect.SaveType;
                this.SaveBonus      = SubEffect.SaveBonus;
                this.OpCode         = SubEffect.Type;
            }    
            
            if (EffectName.ToString().Contains("Cast_Spell"))
            {
                var resource   = $"{((Resource.IndexOf("\0") < 0) ? Resource : (Resource.Substring(0, Resource.IndexOf('\0'))))}.SPL";
                var spell      = ResourceManager.Instance.GetSPLReader(resource);
                this.Icon      = ResourceManager.Instance.GetBAMReader($"{spell.IconBAM}.BAM")?.Image;
                this.SpellName = spell.Name1 == "-1" ? spell.Name2 : spell.Name1;
                this.SpellName = SpellName == "-1" ? resource : SpellName;
            }

            if (EffectName == Effect.Removal_Remove_Secondary_Type 
                || EffectName == Effect.Removal_Remove_One_Secondary_Type)
            {
                this.secondaryType = (SecondaryType)Parameter2;
                this.displayString = $"Removes {secondaryType.ToString().ToLower().Replace("_", " ")}";
            }
        }

        public enum SecondaryType
        {
            NONE,
            SPELL_PROTECTIONS,
            SPECIFIC_PROTECTIONS,
            ILLUSIONARY_PROTECTIONS,
            MAGIC_ATTACK,
            DIVINATION_ATTACK,
            CONJURATION,
            COMBAT_PROTECTIONS,
            CONTINGENCY,
            BATTLEGROUND,
            OFFENSIVE_DAMAGE,
            DISABLING,
            COMBINATION,
            NON_COMBAT
        }

        [Flags]
        public enum Save
        {
            None      = 0,
            Spell     = 1,
            Breath    = 2,
            Death     = 4,
            Wand      = 8,
            Polymorph = 16,            
        }

        public override string ToString()
        {
            displayString = displayString ?? preprocess(EffectName);
            var result = $"{getProbability()}{displayString}";
            
            if (SpellName != null)
                result += $": \"{SpellName}\"";
            if (SaveType == Save.None)
                result += $", No Save";
            else
                result += $", Save vs {SaveType}";
            if (SaveBonus == 0)
                return result;
            result += SaveBonus > 0 
                ? $" +{SaveBonus}"
                : $" {SaveBonus}";            
            return result;
        }

        private string getProbability()
        {
            var probability = Math.Abs(Probability1 - Probability2);
            return probability != 100 ? $"{probability}% " : "";
        }

        private string preprocess(Effect effectName)
        {
            var result = effectName.ToString()
                .Replace("Stat_", "")
                .Replace("Death_", "")
                .Replace("_", " ")
                .Replace("Removal_", "")
                .Replace("Secondary_Type", "");
            if (effectName.ToString().StartsWith("Stat_"))
            {
                return $"{result} {Parameter1}";
            }
            return result;
        }        

        public Effect OpCode { get; }
        public EFFReader SubEffect { get; private set; }
        public Effect EffectName { get; }
        public byte TargetType { get; private set; }
        public byte Power { get; private set; }
        public int Duration { get; }
        public Save SaveType { get; }
        public int SaveBonus { get; }
        public byte Probability1 { get; private set; }
        public byte Probability2 { get; private set; }
        public string Resource { get; private set; }
        public Bitmap Icon { get; private set; }
        public string SpellName { get; private set; }
        public int Parameter1 { get; private set; }
        public int Parameter2 { get; private set; }
    }

}
