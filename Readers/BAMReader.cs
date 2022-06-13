using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using static BGOverlay.Resources.BIFResourceEntry;

namespace BGOverlay.Readers
{
    public class BAMReader
    {
        public class FrameEntry
        {
            public FrameEntry(BinaryReader reader)
            {
                this.Width      = reader.ReadInt16();
                this.Height     = reader.ReadInt16();
                this.CenterX    = reader.ReadInt16();
                this.CenterY    = reader.ReadInt16();
                var frameData   = reader.ReadInt32();
                this.FrameData  = frameData & 0x7fffffff;                
                this.Compressed = (frameData & 0x80000000) == 0;
            }

            public short Width { get; private set; }
            public short Height { get; private set; }
            public short CenterX { get; private set; }
            public short CenterY { get; private set; }
            public int FrameData { get; private set; }
            public bool Compressed { get; private set; }
        }

        public class CycleEntry
        {
            public CycleEntry(BinaryReader reader)
            {
                this.FrameIndexCount = reader.ReadInt16();
                this.FirstLookupIndex = reader.ReadInt16();
            }

            public short FrameIndexCount { get; private set; }
            public short FirstLookupIndex { get; private set; }
        }

        public List<FrameEntry> Frames = new List<FrameEntry>();
        public List<CycleEntry> Cycles = new List<CycleEntry>();

        public BAMReader(ResourceManager resourceManager, string bamFilename)
        {
            bamFilename = bamFilename.ToUpper() + ".BAM";
            this.FileName = bamFilename;
            var filename = $"{Configuration.GameFolder}\\override\\{bamFilename}".ToUpper();
            var originalOffset = 0;
            if (!new FileInfo(filename).Exists)
            {
                if (!resourceManager.BIFResourceEntries.TryGetValue(bamFilename, out var bifResourceEntry))
                {
                    return;
                }
                var resourceLocator = bifResourceEntry.ResourceLocator;
                var bifFilePath     = bifResourceEntry.BiffEntry.FileName;
                var allEntries      = resourceManager.GetBIFFReader(bifFilePath.Substring(bifFilePath.LastIndexOf('/') + 1)).BIFFV1FileEntries;
                var biffFileEntry   = allEntries[resourceLocator & 0xfffff];
                if (biffFileEntry.Ext != Extension.BAM)
                {
                    throw new Exception();
                }
                filename = $"{Configuration.GameFolder}\\{bifFilePath}";
                originalOffset = biffFileEntry.Offset;
            }

            using (BinaryReader reader = new BinaryReader(File.OpenRead(filename)))
            {
                reader.BaseStream.Seek(originalOffset, SeekOrigin.Begin);
                this.Signature = new String(reader.ReadChars(4)); // BAMC ><
                this.Version   = new String(reader.ReadChars(4));                
                if (this.Signature == "BAMC")
                {
                    var size = reader.ReadInt32();
                    var decompressor = new InflaterInputStream(reader.BaseStream);
                    var ms = new MemoryStream();
                    decompressor.CopyTo(ms);
                    using (BinaryReader reader2 = new BinaryReader(ms))
                    {
                        ms.Seek(0, SeekOrigin.Begin);
                        LoadImage(0, reader2);
                    }
                    decompressor.Dispose();
                    reader.Dispose();
                    return;
                }
                reader.BaseStream.Seek(originalOffset, SeekOrigin.Begin);
                LoadImage(originalOffset, reader);                
            }
        }

        private List<Bitmap> HandleBam(BinaryReader br)
        {
            var signature = string.Join("", br.ReadChars(4));
            var version = string.Join("", br.ReadChars(4));

            if (version == "V1  " && signature == "BAM ")
            {
                var frameEntries = new List<BamFrameEntryBinary>();
                var cycles = new List<BamCycleEntryBinary>();
                var palette = new List<RGBA>();
                var frameLookups = new List<ushort>();
                var frames = new List<Bitmap>();

                // header
                var frameEntryCount = br.ReadInt16();
                var cycleCount = br.ReadByte();
                var rleColourIndex = br.ReadByte();
                var frameEntryOffset = br.ReadInt32();
                var paletteOffset = br.ReadInt32();
                var frameLookupTableOffset = br.ReadInt32();
                var frameLookupTableCount = 0;

                br.BaseStream.Seek(frameEntryOffset, SeekOrigin.Begin);
                for (int i = 0; i < frameEntryCount; i++)
                {
                    var width = br.ReadInt16();
                    var height = br.ReadInt16();
                    var xCentre = br.ReadInt16();
                    var yCentre = br.ReadInt16();
                    var frameDataOffset = br.ReadInt32();

                    var frameEntry = new BamFrameEntryBinary
                    {
                        Width = width,
                        Height = height,
                        XCentre = xCentre,
                        YCentre = yCentre,
                        FrameDataOffset = frameDataOffset
                    };

                    frameEntries.Add(frameEntry);
                }

                // We're now in the right position to read cycles
                for (int i = 0; i < cycleCount; i++)
                {

                    var frameIndexCount = br.ReadInt16();
                    var frameIndexOffset = br.ReadInt16();

                    var cycle = new BamCycleEntryBinary()
                    {
                        FrameIndexCount = frameIndexCount,
                        FrameIndexOffset = frameIndexOffset
                    };

                    // We need to track the highest frame lookup index referenced, as this info isn't stored in the BAM file directly
                    if (frameIndexCount + frameIndexOffset > frameLookupTableCount)
                    {
                        frameLookupTableCount = frameIndexCount + frameIndexOffset;
                    }

                    cycles.Add(cycle);
                }

                br.BaseStream.Seek(paletteOffset, SeekOrigin.Begin);
                for (int i = 0; i < 256; i++)
                {
                    var blue = br.ReadByte();
                    var green = br.ReadByte();
                    var red = br.ReadByte();
                    var alpha = br.ReadByte();

                    var paletteEntry = new RGBA() { Red = red, Green = green, Blue = blue, Alpha = alpha };

                    palette.Add(paletteEntry);
                }

                palette[rleColourIndex] = new RGBA { Red = 0, Green = 0, Blue = 0, Alpha = 255 };

                br.BaseStream.Seek(frameLookupTableOffset, SeekOrigin.Begin);
                for (int i = 0; i < frameLookupTableCount; i++)
                {
                    var flt = br.ReadUInt16();
                    frameLookups.Add(flt);
                }

                var result = new List<Bitmap>();
                for (int i = 0; i < frameEntries.Count; i++)
                {
                    br.BaseStream.Seek(frameEntries[i].FrameDataOffset & 0x7FFFFFFF, SeekOrigin.Begin);
                    ulong pixelCount = (ulong)(frameEntries[i].Height * frameEntries[i].Width);
                    var rleCompressed = (frameEntries[i].FrameDataOffset & 0x80000000) == 0;
                    var pixels = new byte[pixelCount];

                    br.BaseStream.Read(pixels, 0, (int)pixelCount);
                    if (rleCompressed)
                    {
                        // RLE encoding: 
                        // If the byte is the transparent index, read the next byte (x) and output (x+1) copies of the transarent colour
                        // If the byte is not the transparent index, it represents itself
                        // e.g. for a transparent colour of 0 the values 1203 would indicate
                        // 1x colour 1, 1x colour 2, 4x transparent colour

                        var decodedPixels = new List<byte>();
                        for (int m = 0; m < pixels.Length; m++)
                        {
                            // We need to stop when we've read/calculated all the pixels required to make this frame
                            if (decodedPixels.Count == (int)pixelCount)
                            {
                                pixels = decodedPixels.ToArray();
                                break;
                            }

                            if (pixels[m] == rleColourIndex)
                            {
                                var rlePixelCount = 1;

                                if (m + 1 < pixels.Length)
                                {
                                    rlePixelCount = pixels[m + 1] + 1;
                                }
                                for (var runLength = 0; runLength < rlePixelCount; runLength++)
                                {
                                    decodedPixels.Add(rleColourIndex);
                                }
                                m++;
                            }
                            else
                            {
                                decodedPixels.Add(pixels[m]);
                            }
                        }

                        // We need to stop when we've read/calculated all the pixels required to make this frame
                        if (decodedPixels.Count == pixels.Length)
                        {
                            pixels = decodedPixels.ToArray();
                        }
                        else
                        {
                            // We read the decoded data into the expected location (pixels) byte by byte as 
                            // copying the entire thing over results in an exception
                            for (int idx = 0; idx < decodedPixels.Count; idx++)
                            {
                                pixels[idx] = decodedPixels[idx];
                            }
                        }
                    }

                    var data = new List<RGBA>();
                    foreach (var p in pixels)
                    {
                        data.Add(palette[p]);
                    }

                    var size = frameEntries[i].Width * 4 * frameEntries[i].Height;
                    var xdata = new byte[size];
                    var cnt = 0;
                    for (int y = 0; y < frameEntries[i].Height; y++)
                    {
                        for (int x = 0; x < frameEntries[i].Width; x++)
                        {
                            var alpha = data[(y * frameEntries[i].Width) + x].Alpha;
                            xdata[cnt] = data[(y * frameEntries[i].Width) + x].Blue;
                            xdata[cnt + 1] = data[(y * frameEntries[i].Width) + x].Green;
                            xdata[cnt + 2] = data[(y * frameEntries[i].Width) + x].Red;
                            xdata[cnt + 3] = alpha == (byte)0 ? (byte)255 : alpha;
                            cnt += 4;
                        }
                    }

                    var img = new Bitmap(frameEntries[i].Width, frameEntries[i].Height, frameEntries[i].Width * 4, PixelFormat.Format32bppArgb, Marshal.UnsafeAddrOfPinnedArrayElement(xdata, 0));
                    img.MakeTransparent();
                    frames.Add(img);
                }


                foreach (var cycle in cycles)
                {
                    for (int frameIndexCount = 0; frameIndexCount < cycle.FrameIndexCount; frameIndexCount++)
                    {
                        var frameLookupIndex = cycle.FrameIndexOffset + frameIndexCount;
                        var frameIndex = frameLookups[frameLookupIndex];
                        var frame = frames[frameIndex];
                        var bitmap = new Bitmap(frame);
                        result.Add(bitmap);
                    }
                }

                return result;
            }

            return new List<Bitmap>() { new Bitmap(1, 1) };
        }

        private class BamFrameEntryBinary
        {
            public Int16 Width;
            public Int16 Height;
            public Int16 XCentre;
            public Int16 YCentre;
            public Int32 FrameDataOffset; // 0-30 - offset, 31 - IsNotRLECompressed
        }

        private class BamCycleEntryBinary
        {
            public Int16 FrameIndexCount;
            public Int16 FrameIndexOffset; // Index into FrameLookupTable
        }

        private class RGBA
        {
            public byte Red { get; set; }
            public byte Green { get; set; }
            public byte Blue { get; set; }
            public byte Alpha { get; set; }
        }

        private void LoadImage(int originalOffset, BinaryReader reader)
        {
            this.Image = HandleBam(reader)[0];
        }

        private void LoadResources(int originalOffset, BinaryReader reader)
        {
            reader.BaseStream.Seek(originalOffset + OffsetFrameEntries, SeekOrigin.Begin);
            for (int i = 0; i < CountEntries; ++i)
            {
                Frames.Add(new FrameEntry(reader));
            }
            for (int i = 0; i < CountCycles; ++i)
            {
                Cycles.Add(new CycleEntry(reader));
            }
        }

        private void InitHeader(BinaryReader reader)
        {
            this.CountEntries                = reader.ReadInt16();
            this.CountCycles                 = reader.ReadByte();
            this.ColorIndex                  = reader.ReadByte() & 0xff;
            this.OffsetFrameEntries          = reader.ReadInt32();
            this.OffsetFramePalette          = reader.ReadInt32();
            this.OffsetFrameFrameLookupTable = reader.ReadInt32();
        }

        public string Signature { get; private set; }
        public string Version { get; private set; }
        public short CountEntries { get; private set; }
        public byte CountCycles { get; private set; }
        public int ColorIndex { get; private set; }
        public int OffsetFrameEntries { get; private set; }
        public int OffsetFramePalette { get; private set; }
        public int OffsetFrameFrameLookupTable { get; private set; }
        public uint[] BAMPalette { get; private set; }
        public Bitmap Image { get; private set; }
        public string FileName { get; }
    }
}
