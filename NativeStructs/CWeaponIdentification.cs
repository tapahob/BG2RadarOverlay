using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WinApiBindings;

namespace BGOverlay.NativeStructs
{
    public class CWeaponIdentification
    {
        public CWeaponIdentification(IntPtr addr)
        {
            addr = WinAPIBindings.FindDMAAddy(addr, new int[] { });
            this.Type = WinAPIBindings.ReadUInt16(addr);
            this.Flags = WinAPIBindings.ReadUInt32(addr + 0x04);
            this.FlagMask = WinAPIBindings.ReadUInt32(addr + 0x08);
            this.Attributes = WinAPIBindings.ReadUInt32(addr + 0x0C);
        }

        public UInt16 Type { get; }
        public UInt32 Flags { get; }
        public UInt32 FlagMask { get; }
        public UInt32 Attributes { get; }
    }
}
