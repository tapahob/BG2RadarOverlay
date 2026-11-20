using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using BGOverlay;
using BGOverlay.Resources;

namespace WPFFrontend
{
    // Only the current frame's opacity is needed. Loading every frame as a GDI bitmap
    // (as for inventory icons) is expensive for walking cycles and segmented dragons.
    internal sealed class SpriteArt
    {
        internal sealed record Mask(int Width, int Height, int CenterX, int CenterY, byte[] Pixels);
        private readonly ResourceManager resources;
        private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        private int cachedBytes;
        private const int MaxBytes = 32 * 1024 * 1024;

        internal SpriteArt(ResourceManager resources) => this.resources = resources;

        internal byte[] Read(string name)
        {
            if (files.TryGetValue(name, out var bytes)) return bytes;
            try
            {
                var path = Path.Combine(Configuration.GameFolder, "override", name);
                if (File.Exists(path))
                {
                    using var f = File.OpenRead(path);
                    bytes = ReadBytes(f, f.Length);
                }
                else
                {
                    // The KEY index in this project calls the PVRZ type "PVR".
                    var key = name.EndsWith(".PVRZ") ? Path.ChangeExtension(name, "PVR") : name;
                    if (resources.BIFResourceEntries.TryGetValue(key, out var entry))
                    {
                        var archive = entry.BiffEntry.FileName.Replace('/', Path.DirectorySeparatorChar);
                        var index = resources.GetBIFFReader(Path.GetFileName(archive)).BIFFV1FileEntries;
                        if (index.TryGetValue(entry.ResourceLocator & 0xfffff, out var file))
                        {
                            using var f = File.OpenRead(Path.Combine(Configuration.GameFolder, archive));
                            f.Position = file.Offset;
                            bytes = ReadBytes(f, file.Size);
                        }
                    }
                }
                if (bytes != null && bytes.AsSpan().StartsWith("BAMCV1  "u8)) bytes = Inflate(bytes, 8);
                else if (bytes != null && name.EndsWith(".PVRZ")) bytes = Inflate(bytes, 0);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Logger.Debug($"Could not read outline art {name}: {ex.Message}");
                bytes = null;
            }
            if (files.Count >= 128 || cachedBytes + (bytes?.Length ?? 0) > MaxBytes)
            {
                files.Clear();
                cachedBytes = 0;
            }
            files[name] = bytes;
            cachedBytes += bytes?.Length ?? 0;
            return bytes;
        }

        private static byte[] ReadBytes(Stream stream, long size)
        {
            if (size <= 0 || size > MaxBytes) throw new InvalidDataException("Invalid sprite resource size.");
            var bytes = new byte[(int)size];
            stream.ReadExactly(bytes);
            return bytes;
        }

        private static byte[] Inflate(byte[] bytes, int sizeAt)
        {
            if (bytes.Length < sizeAt + 6) throw new InvalidDataException("Truncated sprite resource.");
            using var raw = new MemoryStream(bytes, sizeAt + 4, bytes.Length - sizeAt - 4);
            using var zlib = new ZLibStream(raw, CompressionMode.Decompress);
            return ReadBytes(zlib, I32(bytes, sizeAt));
        }

        internal Mask Frame(string resref, int sequence, int frame)
        {
            var bam = Read(resref + ".BAM");
            if (bam == null || bam.Length < 32) return null;
            bool v2 = bam.AsSpan().StartsWith("BAM V2  "u8);
            if (!v2 && !bam.AsSpan().StartsWith("BAM V1  "u8)) return null;
            int frames = v2 ? I32(bam, 8) : U16(bam, 8);
            int frameTable = I32(bam, v2 ? 20 : 12);
            int cycles = v2 ? I32(bam, 12) : bam[10];
            if ((uint)sequence >= cycles) return null;
            int cycleTable = v2 ? I32(bam, 24) : checked(frameTable + frames * 12);
            int cycle = checked(cycleTable + sequence * 4);
            int count = U16(bam, cycle);
            if (count == 0) return null;
            int entry = U16(bam, cycle + 2) + Math.Clamp(frame, 0, count - 1);
            if (!v2) entry = U16(bam, checked(I32(bam, 20) + 2 * entry));
            if ((uint)entry >= frames) return null;
            int offset = checked(frameTable + entry * 12);
            int width = U16(bam, offset), height = U16(bam, offset + 2);
            if (width == 0 || height == 0 || width > 1024 || height > 1024) return null;
            var mask = new Mask(width, height, I16(bam, offset + 4), I16(bam, offset + 6), new byte[width * height]);
            if (v2) ReadBlocks(bam, offset, mask);
            else ReadIndexed(bam, offset, mask);
            return mask;
        }

        private static void ReadIndexed(byte[] bam, int offset, Mask mask)
        {
            int flags = I32(bam, offset + 8);
            int src = flags & int.MaxValue;
            byte clear = bam[11];
            // Palette entry 1 is the ground shadow in creature BAMs.
            for (int dest = 0; dest < mask.Pixels.Length;)
            {
                byte value = bam[src++];
                if (value == clear && flags >= 0) dest += bam[src++] + 1;
                else mask.Pixels[dest++] = (byte)(value == clear || value == 1 ? 0 : 1);
            }
        }

        private void ReadBlocks(byte[] bam, int offset, Mask mask)
        {
            int start = U16(bam, offset + 8), count = U16(bam, offset + 10);
            int table = I32(bam, 28);
            for (int i = start; i < start + count; i++)
            {
                int block = checked(table + i * 28);
                var page = Read($"MOS{I32(bam, block):D4}.PVRZ");
                if (page == null || page.Length < 52 || I32(page, 0) != 0x03525650) continue;
                int format = I32(page, 8);
                if (format != 7 && format != 11) continue; // Desktop EE uses BC1 and BC3.
                int sx = I32(bam, block + 4), sy = I32(bam, block + 8);
                int w = I32(bam, block + 12), h = I32(bam, block + 16);
                int dx = I32(bam, block + 20), dy = I32(bam, block + 24);
                int pw = I32(page, 28), ph = I32(page, 24);
                if (sx < 0 || sy < 0 || dx < 0 || dy < 0 || w <= 0 || h <= 0 ||
                    w > mask.Width - dx || h > mask.Height - dy || w > pw - sx || h > ph - sy) continue;
                int data = checked(52 + I32(page, 48));
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int px = sx + x, py = sy + y;
                    int b = checked(data + ((py / 4) * ((pw + 3) / 4) + px / 4) * (format == 7 ? 8 : 16));
                    int pixel = (py % 4) * 4 + px % 4;
                    bool solid = format == 7
                        ? U16(page, b) > U16(page, b + 2) || (I32(page, b + 4) >> (pixel * 2) & 3) != 3
                        : Bc3Alpha(page, b, pixel) >= 128;
                    if (solid) mask.Pixels[(dy + y) * mask.Width + dx + x] = 1;
                }
            }
        }

        private static int Bc3Alpha(byte[] page, int block, int pixel)
        {
            int a = page[block], b = page[block + 1];
            int index = (int)(BitConverter.ToUInt64(page, block) >> (16 + 3 * pixel) & 7);
            if (index == 0) return a;
            if (index == 1) return b;
            if (a > b) return ((8 - index) * a + (index - 1) * b) / 7;
            return index < 6 ? ((6 - index) * a + (index - 1) * b) / 5 : (index == 6 ? 0 : 255);
        }

        private static int U16(byte[] bytes, int offset) => BitConverter.ToUInt16(bytes, offset);
        private static int I16(byte[] bytes, int offset) => BitConverter.ToInt16(bytes, offset);
        private static int I32(byte[] bytes, int offset) => BitConverter.ToInt32(bytes, offset);
    }
}
