using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WinApiBindings;

namespace BGOverlay.NativeStructs
{
    public class CGameEffect
    {
        public string Version;

        public Effect EffectId { get; }
        public string Res { get; }
        public string SourceRes { get; }
        public string ScriptName { get; }

        public CGameEffect(IntPtr addr)
        {
            addr = WinAPIBindings.FindDMAAddy(addr, new int[] { 0x04 });
            //addr += 4;
            this.Version = WinAPIBindings.ReadString(addr, 8);
            this.EffectId = (Effect) WinAPIBindings.ReadUInt32(addr + 0x08);
            this.Res = WinAPIBindings.ReadString(addr + 0x28, 8);
            this.SourceRes = WinAPIBindings.ReadString(addr + 0x8C, 8);
            this.ScriptName = WinAPIBindings.ReadString(addr + 0xA0, 32);
        }
    }
}
