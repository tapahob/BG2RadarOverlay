using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BGOverlay;
using Microsoft.Win32.SafeHandles;
using System.Windows.Input;

namespace WPFFrontend
{
    // The artwork and fingertip hotspots come from this installation's CURSORS.BAM.
    // The game uses a copy of the USER cursor handle, leaving ownership here.
    internal sealed class GameHandCursor : IDisposable
    {
        private sealed record Frame(IntPtr Cursor, BitmapSource Image, System.Drawing.Point Hotspot);
        private readonly Dictionary<int, Frame> _frames = new();
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private Frame _current;
        public BitmapSource Image => _current?.Image;
        public Cursor PointingCursor { get; private set; }
        public IntPtr PointingHandle => _frames.TryGetValue(0, out var hand) ? hand.Cursor : IntPtr.Zero;

        public GameHandCursor(int pid, ResourceManager resources)
        {
            _map = MemoryMappedFile.CreateOrOpen($@"Local\BG2Radar.Pointer.1.{pid}", 16);
            _view = _map.CreateViewAccessor();
            SetFrame(-1);
            var atlas = resources.GetBAMReader("CURSORS");
            if (atlas == null) return;
            foreach (var (id, source) in atlas.CursorFrames)
            {
                using var artwork = (System.Drawing.Bitmap)source.Image.Clone();
                clearPointingGloveGap(id, artwork);
                IntPtr icon = artwork.GetHicon();
                try
                {
                    if (!GetIconInfo(icon, out var info)) continue;
                    try
                    {
                        info.Icon = false;
                        info.X = (uint)Math.Clamp(source.Hotspot.X, 0, source.Image.Width - 1);
                        info.Y = (uint)Math.Clamp(source.Hotspot.Y, 0, source.Image.Height - 1);
                        var cursor = CreateIconIndirect(ref info);
                        if (cursor == IntPtr.Zero) continue;
                        var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        image.Freeze();
                        _frames[id] = new Frame(cursor, image, source.Hotspot);
                        if (id == 0)
                            PointingCursor = CursorInteropHelper.Create(new SafeFileHandle(cursor, ownsHandle: false));
                    }
                    finally { DeleteObject(info.Mask); DeleteObject(info.Color); }
                }
                finally { DestroyIcon(icon); }
            }
        }

        // The stock pointing gloves paint the gap between finger and thumb in
        // opaque shadow colours. Cut only this part of these two silhouettes.
        private static void clearPointingGloveGap(int id, System.Drawing.Bitmap bitmap)
        {
            System.Drawing.Point[] gap;
            if (id == 0 && bitmap.Width == 46 && bitmap.Height == 38
                && bitmap.GetPixel(10, 11).ToArgb() == unchecked((int)0xFF181008))
                gap = new System.Drawing.Point[] { new(3, 6), new(6, 6), new(14, 16), new(14, 17), new(9, 16), new(7, 14) };
            else if (id == 1 && bitmap.Width == 49 && bitmap.Height == 33
                && bitmap.GetPixel(13, 10).ToArgb() == unchecked((int)0xFF181008))
                gap = new System.Drawing.Point[] { new(7, 7), new(9, 7), new(16, 14), new(15, 15), new(12, 15), new(10, 12) };
            else return;

            using var g = System.Drawing.Graphics.FromImage(bitmap);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.FillPolygon(System.Drawing.Brushes.Transparent, gap);
        }

        public void SetFrame(int id)
        {
            _frames.TryGetValue(id, out _current);
            _view.Write(0, _current?.Cursor.ToInt64() ?? 0L);
            _view.Write(8, id < 0 ? 0L : Environment.TickCount64);
        }

        public bool UseSoftwareCursor
        {
            get
            {
                var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
                return _current != null && GetCursorInfo(ref info) && (info.Flags & 1) == 0;
            }
        }

        public Rect AreaAt(Point p, DpiScale dpi) => new Rect(
            p.X - _current.Hotspot.X / dpi.DpiScaleX, p.Y - _current.Hotspot.Y / dpi.DpiScaleY,
            Image.PixelWidth / dpi.DpiScaleX, Image.PixelHeight / dpi.DpiScaleY);

        public void Dispose()
        {
            SetFrame(-1);
            _view.Dispose();
            _map.Dispose();
            PointingCursor?.Dispose();
            foreach (var frame in _frames.Values) DestroyCursor(frame.Cursor);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo { public bool Icon; public uint X, Y; public IntPtr Mask, Color; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo { public int Size, Flags; public IntPtr Cursor; public System.Drawing.Point Screen; }

        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr cursor);
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    }
}
