using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using BGOverlay.Resources;

namespace BGOverlay
{
    public class SPLReader
    {
        private readonly Lazy<Bitmap> castingIcon;

        public SPLReader(ResourceManager resourceManager, string splFilename)
        {
            var spell = resourceManager.ReadResource(splFilename.ToUpperInvariant());
            if (spell == null || spell.Length < 0x72) return;
            Name1 = resourceManager.GetStrRefText(BitConverter.ToInt32(spell, 0x08));
            Name2 = resourceManager.GetStrRefText(BitConverter.ToInt32(spell, 0x0C));
            IconBAM = Encoding.ASCII.GetString(spell, 0x3A, 8).TrimEnd('\0');
            SpellDescription = resourceManager.GetStrRefText(BitConverter.ToInt32(spell, 0x50));

            var abilityIcon = ReadAbilityIcon(spell);
            castingIcon = new Lazy<Bitmap>(() => resourceManager.GetBAMReader(IconBAM)?.Image
                ?? resourceManager.GetBAMReader(abilityIcon)?.Image);
        }

        internal Bitmap CastingIcon => castingIcon?.Value;

        private static string ReadAbilityIcon(byte[] spell)
        {
            int offset = BitConverter.ToInt32(spell, 0x64);
            int count = BitConverter.ToUInt16(spell, 0x68);
            const int abilitySize = 40;
            if (offset < 0x72 || offset > spell.Length - count * abilitySize) return null;

            for (int i = 0; i < count; i++)
            {
                var icon = Encoding.ASCII.GetString(spell, offset + i * abilitySize + 4, 8).TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(icon)) return icon;
            }
            return null;
        }

        internal static IEnumerable<string> CalledSpells(byte[] spell)
        {
            if (spell == null || spell.Length < 0x72) yield break;
            int abilities = BitConverter.ToInt32(spell, 0x64);
            int count = BitConverter.ToUInt16(spell, 0x68);
            int effects = BitConverter.ToInt32(spell, 0x6A);
            if (abilities < 0 || effects < 0 || abilities > spell.Length - count * 40) yield break;
            for (int i = 0; i < count; i++)
            {
                int ability = abilities + i * 40;
                int length = BitConverter.ToUInt16(spell, ability + 0x1E);
                int first = BitConverter.ToUInt16(spell, ability + 0x20);
                if (effects > spell.Length - (first + length) * 48) continue;
                for (int j = first; j < first + length; j++)
                {
                    int offset = effects + j * 48;
                    var opcode = (Effect)BitConverter.ToUInt16(spell, offset);
                    if (opcode == Effect.Cast_Spell_at_Creature || opcode == Effect.Apply_Effects_List)
                    {
                        string helper = Encoding.ASCII.GetString(spell, offset + 0x14, 8).TrimEnd('\0');
                        if (helper.Length > 0) yield return helper;
                    }
                }
            }
        }

        public string Name1 { get; }
        public string Name2 { get; }
        public string IconBAM { get; }
        public string SpellDescription { get; }
    }
}
