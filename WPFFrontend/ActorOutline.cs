using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BGOverlay;
using BGOverlay.Resources;
using static WinApiBindings.WinAPIBindings;

namespace WPFFrontend
{
    internal sealed class ActorOutline
    {
        // BG2EE x64 CGameAnimationType subclasses. Each creature's animation INI
        // names the concrete class, including old avatars and multi-part monsters.
        // Field layouts: Bubb13/EEex-Docs, EE Game Structures (x64), CG and CV.
        private sealed record Layout(int Body, int Direction, int MirrorAfter = 0, int Parts = 0,
            int Weapon = 0, int Shield = 0, int Helmet = 0, int DrawWeapons = 0, int DrawHelmet = 0);
        private static readonly Dictionary<string, Layout> Layouts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["effect"] = new(0xCD0, 0xF88, 0xF8A),
            ["monster_quadrant"] = new(0xCD8, 0xD52, 0xD58, 0xD59),
            ["monster_multi"] = new(0xCD8, 0xD42, 0x12F8, 0x12F9),
            ["multi_new"] = new(0xCD8, 0xD2A, 0xD34, 0xD35),
            ["monster_layered_spell"] = new(0xCD8, 0x172A, 0x1758, Weapon: 0x1200, DrawWeapons: 0x172C),
            ["monster_ankheg"] = new(0xCD8, 0x1BAA, 0x1BB0),
            ["town_static"] = new(0xCD0, 0xE4A),
            ["character"] = new(0xD00, 0x240A, 0x2425, Weapon: 0x1360, Shield: 0x1888, Helmet: 0x1DB0,
                DrawWeapons: 0x240C, DrawHelmet: 0x2414),
            ["character_old"] = new(0xCF8, 0x528A, 0x52A5, Weapon: 0x1BE8, Shield: 0x2868, Helmet: 0x34E8,
                DrawWeapons: 0x528C, DrawHelmet: 0x5294),
            ["monster"] = new(0xCD8, 0x123A, 0x1269, Weapon: 0xF88, DrawWeapons: 0x125C),
            ["monster_old"] = new(0xCD8, 0x1202, 0x120D),
            ["monster_layered"] = new(0xCD8, 0x172A, 0x1739, Weapon: 0x1200, DrawWeapons: 0x172C),
            ["monster_large"] = new(0xCD8, 0x1472, 0x1478),
            ["monster_large16"] = new(0xCD8, 0x1472, 0x1478),
            ["ambient_static"] = new(0xCD0, 0xF8A, 0xF94),
            ["ambient"] = new(0xCD8, 0xF92, 0xFA1),
            ["flying"] = new(0xCD0, 0xE4A, 0xE50),
            ["monster_icewind"] = new(0xCD8, 0x4C68, 0x4C79, Weapon: 0x2F10, DrawWeapons: 0x4C6C)
        };

        private readonly SpriteArt art;
        private readonly Dictionary<int, Layout> layoutsById = new();
        private readonly byte[] cellState = new byte[12];
        private readonly ActorProjection projection = new();
        private readonly record struct Layer(string ResRef, int Sequence, int Frame);
        private readonly List<Layer> layers = new(), previous = new();
        private bool previousMirror;
        private Point offset;
        internal BitmapSource Image { get; private set; }
        internal ActorOutline(ResourceManager resources) => art = new SpriteArt(resources);

        internal Rect Locate(BGEntity entity, Size canvas)
        {
            if (entity == null || canvas.Width <= 0 || canvas.Height <= 0) return Rect.Empty;
            if (!projection.TryLocate(entity, out var actor, out int x, out int y, out int width, out int height))
                return Rect.Empty;
            var animation = ReadPointer(actor + 0x3C40);
            if (animation == IntPtr.Zero) return Rect.Empty;
            int id = ReadUInt16(animation + 8);
            if (!layoutsById.TryGetValue(id, out var layout))
            {
                var ini = art.Read($"{id:X4}.INI");
                if (ini != null)
                    foreach (var line in Encoding.ASCII.GetString(ini).Split('\n'))
                    {
                        var section = line.Trim();
                        if (section.StartsWith('[') && section.EndsWith(']') &&
                            Layouts.TryGetValue(section[1..^1], out layout)) break;
                    }
                layoutsById[id] = layout;
            }
            if (layout == null) return Rect.Empty;
            var body = ReadPointer(animation + layout.Body);
            if (body == IntPtr.Zero) return Rect.Empty;
            layers.Clear();
            int parts = layout.Parts == 0 ? 1 : ReadByte(animation + layout.Parts);
            if (parts < 1 || parts > 16) return Rect.Empty;
            for (int i = 0; i < parts; i++) AddCell(body + i * 0x138);
            if (layers.Count == 0) return Rect.Empty;
            if (layout.Weapon != 0 && ReadInt32(animation + layout.DrawWeapons) != 0)
            {
                AddCell(ReadPointer(animation + layout.Weapon));
                if (layout.Shield != 0) AddCell(ReadPointer(animation + layout.Shield));
            }
            if (layout.Helmet != 0 && ReadInt32(animation + layout.DrawHelmet) != 0)
                AddCell(ReadPointer(animation + layout.Helmet));
            bool mirror = layout.MirrorAfter != 0 && ReadUInt16(animation + layout.Direction) > ReadByte(animation + layout.MirrorAfter)
                && body == ReadPointer(animation + layout.Body + 8); // Extended eastern BAMs are already oriented.
            if (mirror != previousMirror || !previous.SequenceEqual(layers))
            {
                previous.Clear();
                previous.AddRange(layers);
                previousMirror = mirror;
                Image = null;
                try
                {
                    var masks = layers.Select(l => art.Frame(l.ResRef, l.Sequence, l.Frame)).Where(m => m != null).ToArray();
                    if (masks.Length > 0) Image = Trace(masks, mirror, out offset);
                }
                catch (Exception ex) when (ex is ArgumentException or OverflowException or IndexOutOfRangeException)
                {
                    Logger.Debug($"Invalid outline frame in {layers[0].ResRef}: {ex.Message}");
                }
            }
            if (Image == null) return Rect.Empty;
            double sx = canvas.Width / width, sy = canvas.Height / height;
            return new Rect((x + offset.X) * sx, (y + offset.Y) * sy, Image.PixelWidth * sx, Image.PixelHeight * sy);
        }

        private void AddCell(IntPtr cell)
        {
            // CVidCell carries its resource name even while fog or invisibility
            // prevents the engine from submitting the sprite to the renderer.
            if (cell == IntPtr.Zero || !ReadInto(cell + 0x110, cellState, cellState.Length)) return;
            int length = 0;
            while (length < 8 && cellState[length] != 0)
            {
                char ch = (char)cellState[length++];
                if (ch < ' ' || ch > '~' || "<>:\"/\\|?*".Contains(ch)) return;
            }
            if (length == 0) return;
            layers.Add(new Layer(Encoding.ASCII.GetString(cellState, 0, length).ToUpperInvariant(),
                BitConverter.ToUInt16(cellState, 10), BitConverter.ToInt16(cellState, 8)));
        }

        private static BitmapSource Trace(SpriteArt.Mask[] masks, bool mirror, out Point offset)
        {
            const int pad = 2;
            int left = masks.Min(m => -m.CenterX) - pad, top = masks.Min(m => -m.CenterY) - pad;
            int w = masks.Max(m => m.Width - m.CenterX) + pad - left;
            int h = masks.Max(m => m.Height - m.CenterY) + pad - top;
            offset = new Point(mirror ? 1 - left - w : left, top);
            if (w > 2048 || h > 2048) return null;
            var solid = new byte[w * h];
            foreach (var m in masks)
            {
                for (int y = 0; y < m.Height; y++)
                for (int x = 0; x < m.Width; x++)
                    if (m.Pixels[y * m.Width + x] != 0)
                        solid[(y - m.CenterY - top) * w + x - m.CenterX - left] = 1;
            }
            // Trace the union so the body and its equipment share one edge. Leave
            // the interior transparent, including on creatures the game has hidden.
            var pixels = new uint[w * h];
            for (int y = pad; y < h - pad; y++)
            for (int x = pad; x < w - pad; x++)
            {
                int i = y * w + x;
                if (solid[i] == 0 || (solid[i-1] & solid[i+1] & solid[i-w] & solid[i+w]) != 0) continue;
                for (int dy = -pad; dy <= pad; dy++)
                for (int dx = -pad; dx <= pad; dx++)
                {
                    int distance = dx * dx + dy * dy;
                    if (distance > 4 || solid[i + dy * w + dx] != 0) continue;
                    int px = mirror ? w - 1 - x - dx : x + dx;
                    ref uint color = ref pixels[(y + dy) * w + px];
                    if (distance <= 2) color = 0xFFFFD875;
                    else if (color == 0) color = 0xDB49341D;
                }
            }
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
