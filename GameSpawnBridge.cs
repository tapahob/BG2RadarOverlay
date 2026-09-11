using System;
using System.Runtime.InteropServices;
using System.Text;
using WinApiBindings;

namespace BGOverlay
{
    /// <summary>
    /// Asks the game to spawn a creature, by posting a request into the mailbox that
    /// EEexMod/M_BG2RDR.lua allocates inside the game process - the overlay half of the
    /// Twitch summon feature. See that Lua file for the struct layout and the reasoning.
    ///
    /// This deliberately never calls into the engine. It writes 20 bytes into a buffer the
    /// game's own Lua owns and polls; everything that actually touches game state runs on the
    /// game's thread, in Lua. Requires EEex plus that mod installed - without them there is no
    /// mailbox to find and every request fails with a readable error.
    /// </summary>
    public sealed class GameSpawnBridge
    {
        public static GameSpawnBridge Instance { get; } = new GameSpawnBridge();

        // Must stay in sync with M_BG2RDR.lua.
        //
        // The magic is a pair of numbers, not a string, because an ASCII magic self-matched:
        // the literal lived in M_BG2RDR.lua, Lua interned it null-terminated, and this scan
        // "found" that string constant in the heap and wrote a request into Lua's string table.
        // These byte runs appear nowhere in the mod's loaded source. OffsetSelf is the
        // belt-and-braces half - a false match would have to also contain its own address.
        private static readonly byte[] Magic = magicBytes(0x9E3779B9, 0x7F4A7C15);
        private const int OffsetVersion = 0x08;
        private const int OffsetSelf    = 0x0C;
        private const int OffsetFlag    = 0x10;
        private const int OffsetResRef  = 0x14;
        private const int ResRefSize    = 16;
        private const uint LayoutVersion = 2;
        private const uint FlagPending   = 1;

        private static byte[] magicBytes(uint first, uint second)
        {
            var bytes = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(first), 0, bytes, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(second), 0, bytes, 4, 4);
            return bytes;
        }

        private const int ScanChunkBytes = 1 << 20;

        private readonly object gate = new object();
        private IntPtr mailbox = IntPtr.Zero;

        private GameSpawnBridge() { }

        /// <summary>
        /// Posts a spawn request. Returns false (with a reason) rather than throwing, since
        /// every failure here is an expected operational state - EEex not installed, no game
        /// loaded, or the previous request not consumed yet - not a bug.
        /// </summary>
        public bool TrySpawn(string resref, out string error)
        {
            lock (gate)
            {
                if (!isValidResRef(resref))
                {
                    error = "Invalid creature ResRef (expected 1-8 characters, A-Z 0-9 _).";
                    return false;
                }

                if (!ensureMailbox())
                {
                    error = "Could not find the in-game bridge. Is EEex installed with M_BG2RDR.lua in override, and a game loaded?";
                    return false;
                }

                if (WinAPIBindings.ReadUInt32(mailbox + OffsetFlag) != 0)
                {
                    error = "The previous summon hasn't been picked up by the game yet.";
                    return false;
                }

                var payload = new byte[ResRefSize];
                var upper = resref.ToUpperInvariant();
                Encoding.ASCII.GetBytes(upper, 0, upper.Length, payload, 0);

                if (!WinAPIBindings.WriteBytes(mailbox + OffsetResRef, payload))
                {
                    error = "Could not write the summon request into the game.";
                    return false;
                }

                // Flag last. The Lua side reads a set flag as "the ResRef next to me is
                // complete and valid", so the payload has to already be in place.
                if (!WinAPIBindings.WriteBytes(mailbox + OffsetFlag, BitConverter.GetBytes(FlagPending)))
                {
                    error = "Could not signal the summon request to the game.";
                    return false;
                }

                error = null;
                return true;
            }
        }

        private static bool isValidResRef(string resref)
        {
            if (string.IsNullOrEmpty(resref) || resref.Length > 8)
                return false;

            foreach (var c in resref)
            {
                var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                      || (c >= '0' && c <= '9') || c == '_';
                if (!ok)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The mailbox is re-allocated by the Lua side on every game load, so a cached address
        /// is only trusted while the magic is still sitting at it; otherwise re-scan.
        /// </summary>
        private bool ensureMailbox()
        {
            if (mailbox != IntPtr.Zero && magicMatches(mailbox))
                return true;

            mailbox = findMailbox();
            return mailbox != IntPtr.Zero;
        }

        private static bool magicMatches(IntPtr address)
        {
            var header = WinAPIBindings.ReadBytes(address, Magic.Length);
            for (int i = 0; i < Magic.Length; i++)
            {
                if (header[i] != Magic[i])
                    return false;
            }

            if (WinAPIBindings.ReadUInt32(address + OffsetVersion) != LayoutVersion)
                return false;

            // The Lua side stamps the low 32 bits of the buffer's own address here, so a
            // coincidental byte match somewhere else in the heap still gets rejected.
            var expectedSelf = (uint)((long)address & 0xFFFFFFFF);
            return WinAPIBindings.ReadUInt32(address + OffsetSelf) == expectedSelf;
        }

        /// <summary>
        /// Walks the game's committed private read/write regions looking for the magic. Probing
        /// from out here is safe - ReadProcessMemory just fails on anything unmapped - which is
        /// exactly why the handshake runs in this direction instead of having the Lua side hunt
        /// for a buffer we allocated.
        /// </summary>
        private static IntPtr findMailbox()
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

        private static IntPtr scanRegion(long baseAddress, long size)
        {
            var overlap = Magic.Length - 1;
            long offset = 0;

            while (offset < size)
            {
                var toRead = (int)Math.Min(ScanChunkBytes, size - offset);
                var buffer = WinAPIBindings.ReadBytes((IntPtr)(baseAddress + offset), toRead);

                for (int i = 0; i + Magic.Length <= toRead; i++)
                {
                    var match = true;
                    for (int j = 0; j < Magic.Length; j++)
                    {
                        if (buffer[i + j] != Magic[j])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        var candidate = (IntPtr)(baseAddress + offset + i);
                        if (magicMatches(candidate))
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
