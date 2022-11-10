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
            addr = WinAPIBindings.FindDMAAddy(addr, new int[] { 0x8 });
            //addr += 4;
            this.Version       = WinAPIBindings.ReadString(addr, 8);
            this.EffectId      = (Effect) WinAPIBindings.ReadUInt32(addr + 0x08);
            this.Res           = Encoding.UTF8.GetString(WinAPIBindings.ReadBytes(addr + 0x28, 8));
            this.Res2          = WinAPIBindings.ReadString(addr + 0x68, 8);
            this.Res3          = WinAPIBindings.ReadString(addr + 0x70, 8);
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

        public Tuple<string, Bitmap, uint> getSpellName(ResourceManager resourceManager, bool buffs = false)
        {
            var res = buffs ? SourceRes : Res;
            if (res != "<ERROR>")
            {
                var splReader = resourceManager.GetSPLReader($"{res.Trim('\0')}.SPL".ToUpper());
                if (splReader == null)
                    return null;
                var spellName = splReader?.Name1;
                if (splReader.Name1 == null || splReader.Name1 == "-1")
                    return null;
                if (splReader.Name1 != "-1" && splReader.IconBAM != null)
                    this.Icon = resourceManager.GetBAMReader(splReader.IconBAM)?.Image;
                if (Configuration.DebugMode)
                {
                    spellName += $" {res.Trim('\0')}.SPL";
                }
                return new Tuple<string, Bitmap, uint>(spellName, this.Icon, DurationType == 1 ? uint.MaxValue : Duration);
            }
            return null;
        }

        public override string ToString()
        {
            return this.EffectId.ToString();
        }
    }
}
