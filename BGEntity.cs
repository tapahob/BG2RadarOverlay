using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using WinApiBindings;

namespace BGOverlay
{
    public class BGEntity
    {
        public int Id {get; private set;}
        public int X {get; private set;}
        public int Y {get; private set;}
        public int Type {get; private set;}
        public string Name1 {get; private set;}
        public string Name2 {get; private set;}
        public BGEntity(IntPtr hProc, IntPtr entityIdPtr)
        {
            this.Id = WinAPIBindings.ReadInt32(hProc, entityIdPtr);
            entityIdPtr += 0x4;
            this.Type = WinAPIBindings.ReadByte(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x004 }));
            this.X = WinAPIBindings.ReadInt32(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x008 }));
            this.Y = WinAPIBindings.ReadInt32(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x00C }));
            this.Name1 = WinAPIBindings.ReadString(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x364 }));
            this.Name2 = WinAPIBindings.ReadString(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x3FC }));
        }

        public override string ToString()
        {
            return $"{Id}:\"{Name1}\": X:{X}, Y:{Y}, TYPE: {Type}, {Name2}";
        }
    }
}
