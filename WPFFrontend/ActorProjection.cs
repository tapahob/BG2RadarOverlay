using System;
using BGOverlay;
using static WinApiBindings.WinAPIBindings;

namespace WPFFrontend
{
    internal sealed class ActorProjection
    {
        // CInfinity::rViewPort through nCurrentY. nNewX/Y can already describe
        // the next scroll position while the game is still drawing the current one.
        private readonly byte[] camera = new byte[0xC8 - 0x78];
        private readonly byte[] position = new byte[24];

        internal bool TryLocate(BGEntity entity, out IntPtr actor, out int x, out int y, out int width, out int height)
        {
            actor = entity.GetCurrentSpriteAddress();
            return TryLocate(actor, out x, out y, out width, out height);
        }

        internal bool TryLocate(IntPtr actor, out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            if (actor == IntPtr.Zero || !ReadInto(actor + 0xC, position, position.Length)) return false;
            var area = new IntPtr(BitConverter.ToInt64(position, 12));
            if (area == IntPtr.Zero) return false;
            var game = ReadPointer(area + 0x228);
            if (game == IntPtr.Zero) return false;
            int visible = ReadByte(game + 0x6590);
            if (visible >= 12 || ReadPointer(game + 0x6598 + 8 * visible) != area ||
                !ReadInto(area + 0x5C8 + 0x78, camera, camera.Length)) return false;
            x = BitConverter.ToInt32(position, 0) - BitConverter.ToInt32(camera, 0xC0 - 0x78);
            y = BitConverter.ToInt32(position, 4) - BitConverter.ToInt32(camera, 0xC4 - 0x78) - BitConverter.ToInt32(position, 8);
            width = BitConverter.ToInt32(camera, 8) - BitConverter.ToInt32(camera, 0);
            height = BitConverter.ToInt32(camera, 12) - BitConverter.ToInt32(camera, 4);
            if (width <= 0 || height <= 0) return false;
            return true;
        }
    }
}
