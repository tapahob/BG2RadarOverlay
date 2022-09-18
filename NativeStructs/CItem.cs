using System;
using System.Linq;
using System.Text;
using WinApiBindings;

namespace BGOverlay.NativeStructs
{
    public class CItem
    {
        public CItem(IntPtr intPtr)
        {
            var itemPtr     = WinAPIBindings.FindDMAAddy(intPtr);
            var item2Ptr    = WinAPIBindings.FindDMAAddy(itemPtr, new int[] { 0x8 });
            this.resRef     = WinAPIBindings.ReadString(item2Ptr + 8, 8);
        }

        public string resRef { get; private set; }
    }
}
