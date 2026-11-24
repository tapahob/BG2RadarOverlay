using System;
using System.Runtime.InteropServices;
using WinApiBindings;

namespace BGOverlay
{
    // Common header for EEex-owned buffers: two signature words, version,
    // and low 32 bits of the allocation address.
    public sealed class GameMailbox
    {
        private readonly byte[] magic = new byte[8];
        private readonly uint version;
        private const int ScanChunkBytes = 1 << 20;

        public GameMailbox(uint first, uint second, uint version)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(first), 0, magic, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(second), 0, magic, 4, 4);
            this.version = version;
        }

        public bool Matches(IntPtr address)
        {
            var header = WinAPIBindings.ReadBytes(address, 16);
            for (int i = 0; i < magic.Length; i++)
            {
                if (header[i] != magic[i])
                    return false;
            }

            if (BitConverter.ToUInt32(header, 8) != version)
                return false;

            // The Lua side stamps the low 32 bits of the buffer's own address here, so a
            // coincidental byte match somewhere else in the heap still gets rejected.
            var expectedSelf = (uint)((long)address & 0xFFFFFFFF);
            return BitConverter.ToUInt32(header, 12) == expectedSelf;
        }

        /// <summary>
        /// Walks the game's committed private read/write regions looking for the magic. Probing
        /// from out here is safe - ReadProcessMemory just fails on anything unmapped - which is
        /// exactly why the handshake runs in this direction instead of having the Lua side hunt
        /// for a buffer we allocated.
        /// </summary>
        public IntPtr Find()
        {
            var hProc = Configuration.hProc;
            var mbiSize = (IntPtr)Marshal.SizeOf(typeof(WinAPIBindings.MEMORY_BASIC_INFORMATION));
            var address = IntPtr.Zero;

            while (WinAPIBindings.VirtualQueryEx(hProc, address, out var mbi, mbiSize) != IntPtr.Zero)
            {
                var regionSize = (long)mbi.RegionSize;
                if (regionSize <= 0)
                    break;

                var isCandidate = mbi.State == WinAPIBindings.MEM_COMMIT
                               && mbi.Type == WinAPIBindings.MEM_PRIVATE
                               && mbi.Protect == WinAPIBindings.PAGE_READWRITE;

                if (isCandidate)
                {
                    var found = scanRegion((long)mbi.BaseAddress, regionSize);
                    if (found != IntPtr.Zero)
                        return found;
                }

                var next = (long)mbi.BaseAddress + regionSize;
                if (next <= (long)address)
                    break;
                address = (IntPtr)next;
            }

            return IntPtr.Zero;
        }

        private IntPtr scanRegion(long baseAddress, long size)
        {
            var overlap = magic.Length - 1;
            long offset = 0;

            while (offset < size)
            {
                var toRead = (int)Math.Min(ScanChunkBytes, size - offset);
                var buffer = WinAPIBindings.ReadBytes((IntPtr)(baseAddress + offset), toRead);

                for (int i = 0; i + magic.Length <= toRead; i++)
                {
                    var match = true;
                    for (int j = 0; j < magic.Length; j++)
                    {
                        if (buffer[i + j] != magic[j])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        var candidate = (IntPtr)(baseAddress + offset + i);
                        if (Matches(candidate))
                            return candidate;
                    }
                }

                // Overlap so a mailbox straddling a chunk boundary isn't missed.
                var advance = toRead - overlap;
                offset += advance > 0 ? advance : toRead;
            }

            return IntPtr.Zero;
        }
    }
}
