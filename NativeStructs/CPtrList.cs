using System;
using WinApiBindings;

namespace BGOverlay.NativeStructs
{
    public class CPtrList
    {
        public CPtrList(IntPtr addr)
        {            
            this.Head = new Node(addr + 0x08);
            this.Tail = new Node(addr + 0x10);
            this.Count = WinAPIBindings.ReadInt32(addr + 0x18);
        }

        internal Node Head { get; }
        internal Node Tail { get; }
        internal int Count { get; }
    }

    class Node
    {
        public Node(IntPtr addr)
        {
            this.next = WinAPIBindings.FindDMAAddy(addr, new int[] { 0x0 });
            this.prev = WinAPIBindings.FindDMAAddy(addr, new int[] { 0x8 });
            this.Data = WinAPIBindings.FindDMAAddy(addr, new int[] { 0x10 });
        }

        public Node getNext()
        {
            return new Node(next);
        }

        public Node getPrev()
        {
            return new Node(prev);
        }

        IntPtr next;
        IntPtr prev;
        public IntPtr Data;
    }
}
