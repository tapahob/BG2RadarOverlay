using System;
using System.Collections.Generic;
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
    /// This deliberately never calls into the engine. It writes a few dozen bytes into a buffer the
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
        private const int OffsetAmount  = 0x24;
        private const int OffsetMessage = 0x28;
        private const int ResRefSize    = 16;
        private const int MessageSize   = 96;
        private const uint LayoutVersion = 4;
        private const uint FlagPending   = 1;

        /// <summary>
        /// Longest viewer message that reaches the game, in characters. One byte short of the
        /// mailbox field so there is always room for the terminating null the Lua side reads up
        /// to. The extension caps its input at 80, but that is presentation - a message arriving
        /// over the relay is whatever someone chose to POST, so it gets cut here too.
        /// </summary>
        public const int MaxMessageLength = MessageSize - 1;

        /// <summary>
        /// Mirrors MAX_AMOUNT in M_BG2RDR.lua. Clamped on both sides so neither a typo here nor
        /// a stale mod file can lock the game up spawning thousands of creatures.
        /// </summary>
        public const int MaxAmount = 20;

        private const int MaxQueued = 32;

        // ~3s at the default 300ms tick: long enough not to fire on the normal one-tick gap
        // between writing a request and the game picking it up.
        private const int StalledTicksBeforeWarning = 10;

        private int stalledTicks;

        /// <summary>
        /// True while a request has been written but the game hasn't consumed it for a while -
        /// in practice, the game is paused. Surfaced in the options UI so a summon that goes
        /// nowhere doesn't look like a broken bridge.
        /// </summary>
        public bool IsStalled => stalledTicks >= StalledTicksBeforeWarning;

        /// <summary>How many entries are still waiting to be handed to the game.</summary>
        public int QueueLength
        {
            get { lock (gate) { return pending.Count; } }
        }

        private static byte[] magicBytes(uint first, uint second)
        {
            var bytes = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(first), 0, bytes, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(second), 0, bytes, 4, 4);
            return bytes;
        }

        private const int ScanChunkBytes = 1 << 20;

        private readonly object gate = new object();
        private readonly Queue<QueuedSpawn> pending = new Queue<QueuedSpawn>();
        private IntPtr mailbox = IntPtr.Zero;

        /// <summary>
        /// A queued entry plus the viewer message that should accompany it. The message lives
        /// here rather than on <see cref="SpawnEntry"/> because it belongs to the request, not to
        /// the creature: a pack of three entries is one viewer saying one thing, so only the
        /// first entry carries the text and the rest spawn silently.
        /// </summary>
        private struct QueuedSpawn
        {
            public SpawnEntry Entry;
            public string Message;
        }

        private GameSpawnBridge() { }

        /// <summary>
        /// Queues entries to be spawned. The mailbox only holds one request at a time - the Lua
        /// side consumes it on its next tick - so a multi-creature pack can't be written in one
        /// go; <see cref="Pump"/> feeds them in as the game takes them.
        /// </summary>
        public void Enqueue(IEnumerable<SpawnEntry> entries)
        {
            Enqueue(entries, null);
        }

        /// <param name="message">
        /// Optional viewer text, displayed in the game's message log alongside the first entry.
        /// </param>
        public void Enqueue(IEnumerable<SpawnEntry> entries, string message)
        {
            var text = SanitizeMessage(message);

            lock (gate)
            {
                foreach (var entry in entries)
                {
                    // Bounded so a burst of redemptions can't build a backlog that keeps
                    // spawning long after the viewers who triggered it have moved on.
                    if (pending.Count >= MaxQueued)
                        break;
                    pending.Enqueue(new QueuedSpawn { Entry = entry, Message = text });
                    text = null;
                }
            }
        }

        /// <summary>
        /// Reduces viewer text to what can safely be handed to the game: printable ASCII, single
        /// spaces, length-capped. Anything a viewer types arrives here as-is, so this is the
        /// enforcement point - the extension's own trimming is cosmetic and the relay can be
        /// POSTed to directly. Non-ASCII is dropped rather than transliterated because the engine
        /// renders its message log from a single-byte codepage.
        /// </summary>
        public static string SanitizeMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return null;

            var sb = new StringBuilder(MaxMessageLength);
            var lastWasSpace = true;

            foreach (var c in message)
            {
                var isSpace = c == ' ' || c == '\t' || c == '\r' || c == '\n';
                if (isSpace)
                {
                    if (!lastWasSpace && sb.Length < MaxMessageLength)
                        sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                if (c < 32 || c > 126)
                    continue;

                if (sb.Length >= MaxMessageLength)
                    break;

                sb.Append(c);
                lastWasSpace = false;
            }

            var result = sb.ToString().TrimEnd();
            return result.Length == 0 ? null : result;
        }

        /// <summary>
        /// Hands the next queued entry to the game if it's ready for one. Called every
        /// ProcessHacker tick; a no-op when there's nothing queued.
        /// </summary>
        public void Pump()
        {
            QueuedSpawn next;
            lock (gate)
            {
                if (pending.Count == 0)
                    return;
                next = pending.Peek();
            }

            if (TrySpawn(next.Entry.ResRef, next.Entry.Amount, next.Message, out var error))
            {
                lock (gate)
                {
                    if (pending.Count > 0)
                        pending.Dequeue();
                }
                stalledTicks = 0;
                return;
            }

            // "Still pending" just means the game hasn't picked the last one up yet - keep the
            // entry and retry next tick. Anything else (no game, bad ResRef) won't fix itself,
            // so drop it rather than retrying forever.
            if (!error.StartsWith("The previous summon", StringComparison.Ordinal))
            {
                lock (gate)
                {
                    if (pending.Count > 0)
                        pending.Dequeue();
                }
                stalledTicks = 0;
                Logger.Info($"Dropped queued summon '{next.Entry.ResRef}' x{next.Entry.Amount}: {error}");
                return;
            }

            // The game not consuming requests is overwhelmingly "the game is paused", since the
            // Lua side polls from an AI hook. Retrying in silence made that indistinguishable
            // from the bridge being broken, so say so once instead of never.
            stalledTicks++;
            if (stalledTicks == StalledTicksBeforeWarning)
                Logger.Info($"Summon queue stalled on '{next.Entry.ResRef}' - the game hasn't consumed the request. Is it paused?");
        }

        /// <summary>
        /// Posts a spawn request. Returns false (with a reason) rather than throwing, since
        /// every failure here is an expected operational state - EEex not installed, no game
        /// loaded, or the previous request not consumed yet - not a bug.
        /// </summary>
        public bool TrySpawn(string resref, out string error)
        {
            return TrySpawn(resref, 1, null, out error);
        }

        public bool TrySpawn(string resref, int amount, out string error)
        {
            return TrySpawn(resref, amount, null, out error);
        }

        public bool TrySpawn(string resref, int amount, string message, out string error)
        {
            lock (gate)
            {
                if (!isValidResRef(resref))
                {
                    error = "Invalid creature ResRef (expected 1-8 characters, A-Z 0-9 _).";
                    return false;
                }

                if (amount < 1 || amount > MaxAmount)
                {
                    error = $"Invalid amount (expected 1-{MaxAmount}).";
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

                if (!WinAPIBindings.WriteBytes(mailbox + OffsetAmount, BitConverter.GetBytes((uint)amount)))
                {
                    error = "Could not write the summon amount into the game.";
                    return false;
                }

                // Written in full every time, not just when there's a message: the field would
                // otherwise still hold the previous viewer's text, and the next silent summon
                // would print it again.
                var messageBytes = new byte[MessageSize];
                var text = SanitizeMessage(message);
                if (!string.IsNullOrEmpty(text))
                    Encoding.ASCII.GetBytes(text, 0, text.Length, messageBytes, 0);

                if (!WinAPIBindings.WriteBytes(mailbox + OffsetMessage, messageBytes))
                {
                    error = "Could not write the summon message into the game.";
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
