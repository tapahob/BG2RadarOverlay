using System;
using WinApiBindings;

namespace BGOverlay.NativeStructs
{
    public class CImmunitySpell
    {
        public string CResRef { get; set; }
        public uint Error { get; set; }
        public int Item { get; set; }

        public CImmunitySpell(IntPtr addr)
        {
            addr = WinAPIBindings.FindDMAAddy(addr);
            this.CResRef = WinAPIBindings.ReadString(addr, 8);
            this.Error = WinAPIBindings.ReadUInt32(addr + 0x08);
            this.Item = WinAPIBindings.ReadInt32(addr + 0x0C);
        }
    }
}
