using BGOverlay.Resources;
using System;
using System.Drawing;
using WinApiBindings;

namespace BGOverlay.NativeStructs
{
    public class CGameEffect
    {
        public string Version { get; }
        public Effect EffectId { get; }
        public string Res { get; }
        public string SourceRes { get; }
        public string ScriptName { get; }
        public int Done { get; private set; }
        public uint DurationTemp { get; private set; }
        public uint Duration { get; private set; }
        public uint DurationType { get; private set; }
        public int EffectAmount { get; private set; }
        public uint dWFlags { get; private set; }
        public Bitmap Icon { get; private set; }

        public CGameEffect(IntPtr addr)
        {
            addr = WinAPIBindings.FindDMAAddy(addr, new int[] { 0x8 });
            //addr += 4;
            this.Version = WinAPIBindings.ReadString(addr, 8);
            this.EffectId = (Effect) WinAPIBindings.ReadUInt32(addr + 0x08);
            this.Res = WinAPIBindings.ReadString(addr + 0x28, 8);
            this.SourceRes = WinAPIBindings.ReadString(addr + 0x8C, 8);
            this.ScriptName = WinAPIBindings.ReadString(addr + 0xA0, 32);
            this.Done = WinAPIBindings.ReadInt32(addr + 0x110);
            this.DurationTemp = WinAPIBindings.ReadUInt32(addr + 0x118);
            this.Duration = WinAPIBindings.ReadUInt32(addr + 0x20);
            this.DurationType = WinAPIBindings.ReadUInt32(addr + 0x1C);
            this.EffectAmount = WinAPIBindings.ReadInt32(addr + 0x14);            
            this.dWFlags = WinAPIBindings.ReadUInt32(addr + 0x18);
        }

        public Tuple<string, Bitmap, uint> getSpellName(ResourceManager resourceManager)
        {
            if (this.SourceRes != "<ERROR>")
            {
                var splReader = resourceManager.GetSPLReader($"{this.SourceRes.Trim('\0')}.SPL".ToUpper());
                if (splReader == null)
                    return null;
                var spellName = splReader?.Name1;
                //if (spellName == "-1")
                //{
                //    splReader = resourceManager.GetSPLReader($"{this.SourceRes.Substring(0, this.SourceRes.Length - 1).Trim('\0')}.SPL".ToUpper());
                //    spellName = splReader.Name1;
                //    spellName = spellName == "-1" ? this.SourceRes : spellName;
                //}                
                if (splReader.Name1 != "-1" && splReader.IconBAM != null)
                    this.Icon = resourceManager.GetBAMReader(splReader.IconBAM)?.Image;
                return new Tuple<string, Bitmap, uint>(spellName, this.Icon, Duration);
            }
            return null;
        }

        public override string ToString()
        {
            return this.EffectId.ToString();
        }
    }
}
