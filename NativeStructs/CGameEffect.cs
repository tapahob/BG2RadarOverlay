using BGOverlay.Resources;
using System;
using System.Drawing;
using System.Text;
using WinApiBindings;

namespace BGOverlay.NativeStructs
{
    public class CGameEffect
    {
        public string Version { get; }
        public Effect EffectId { get; }
        public string Res { get; }
        public string Res2 { get; private set; }
        public string Res3 { get; private set; }
        public string SourceRes { get; }
        private readonly uint sourceType;
        private readonly IntPtr address;
        public string ScriptName { get; }
        public int Done { get; private set; }
        public uint DurationTemp { get; private set; }
        public uint Duration { get; private set; }
        public uint DurationType { get; private set; }
        public int EffectAmount { get; private set; }
        public int EffectAmount2 { get; private set; }
        public int EffectAmount3 { get; private set; }
        public int EffectAmount4 { get; private set; }
        public int EffectAmount5 { get; private set; }
        public uint dWFlags { get; private set; }
        public Bitmap Icon { get; private set; }

        public CGameEffect(IntPtr addr)
        {
            address = WinAPIBindings.ReadPointer(addr);
            addr = address + 8;
            this.Version       = WinAPIBindings.ReadString(addr, 8);
            this.EffectId      = (Effect) WinAPIBindings.ReadUInt32(addr + 0x08);
            this.Res           = Encoding.UTF8.GetString(WinAPIBindings.ReadBytes(addr + 0x28, 8));
            this.Res2          = WinAPIBindings.ReadString(addr + 0x68, 8);
            this.Res3          = WinAPIBindings.ReadString(addr + 0x70, 8);
            this.sourceType    = WinAPIBindings.ReadUInt32(addr + 0x88);
            this.SourceRes     = WinAPIBindings.ReadString(addr + 0x8C, 8);
            this.ScriptName    = WinAPIBindings.ReadString(addr + 0xA0, 32);
            this.Done          = WinAPIBindings.ReadInt32(addr + 0x110);
            this.DurationTemp  = WinAPIBindings.ReadUInt32(addr + 0x118);
            this.Duration      = WinAPIBindings.ReadUInt32(addr + 0x20);
            this.DurationType  = WinAPIBindings.ReadUInt32(addr + 0x1C);
            this.EffectAmount  = WinAPIBindings.ReadInt32(addr + 0x14);
            this.EffectAmount2 = WinAPIBindings.ReadInt32(addr + 0x58);
            this.EffectAmount3 = WinAPIBindings.ReadInt32(addr + 0x5C);
            this.EffectAmount4 = WinAPIBindings.ReadInt32(addr + 0x60);
            this.EffectAmount5 = WinAPIBindings.ReadInt32(addr + 0x64);
            this.dWFlags       = WinAPIBindings.ReadUInt32(addr + 0x18);
        }

        public Tuple<string, Bitmap, uint> getSpellName(ResourceManager resourceManager)
        {
            var res = Res.TrimEnd('\0');
            if (res.Length == 0 || res == "<ERROR>") return null;
            var filename = res + ".SPL";
            var spell = resourceManager.GetSPLReader(filename);
            var name = spell?.Name1;
            if (string.IsNullOrWhiteSpace(name) || name == "-1") return null;
            Icon = resourceManager.GetSpellImmunityIcon(spell);
            if (Configuration.DebugMode) name += " " + filename;
            return Tuple.Create(name, Icon, DurationType == 1 ? uint.MaxValue : Duration);
        }

        internal (string Key, string Name, Bitmap Icon, uint Expires)? ReadBuff(ResourceManager resourceManager)
        {
            if (address == IntPtr.Zero) return null;
            bool fromItem = sourceType == 2;
            if (fromItem)
            {
                // A healing potion can leave a short animation or a delayed sound after the heal.
                uint timing = DurationType & 0xff;
                if ((timing != 0 && timing != 10) || Duration == 0 ||
                    EffectId == Effect.Creature_RGB_color_fade || EffectId == Effect.Text_Display_String)
                    return null;
            }

            string filename = null;
            string name = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(SourceRes) && SourceRes != "<ERROR>")
                {
                    if (fromItem)
                    {
                        filename = SourceRes + ".ITM";
                        var item = resourceManager.GetEffectSourceItem(filename);
                        if (item != null && item.ItemType != 9) return null; // Only potions.
                        name = item?.ResolvedName;
                        Icon = item?.Icon;
                    }
                    else
                    {
                        filename = resourceManager.BuffSpellFilename(SourceRes);
                        var spell = resourceManager.GetSPLReader(filename);
                        name = BGEntity.DisplayName(spell?.Name1) ?? BGEntity.DisplayName(spell?.Name2);
                        Icon = spell?.CastingIcon;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not read buff resource {filename}", ex);
            }

            name = BGEntity.DisplayName(name);
            var label = name ?? "<NO IDEA>";
            if (Configuration.DebugMode && filename != null) label += " " + filename;
            // Known aliases share a label. Missing labels must never merge unrelated effects.
            string key;
            if (name != null) key = "name:" + label;
            else if (filename != null) key = "resource:" + filename.ToUpperInvariant();
            else key = "effect:" + address;
            return (key, label, Icon, DurationType == 1 ? uint.MaxValue : Duration);
        }

        public override string ToString()
        {
            return this.EffectId.ToString();
        }
    }
}
