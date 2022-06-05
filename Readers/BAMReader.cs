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
                this.Width = reader.ReadInt16();
                this.Height = reader.ReadInt16();
                this.CenterX = reader.ReadInt16();
                this.CenterY = reader.ReadInt16();
                var frameData = reader.ReadInt32();
                this.FrameData = frameData & 0x7fffffff;                
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
                var bifFilePath = bifResourceEntry.BiffEntry.FileName;
                var allEntries = resourceManager.GetBIFFReader(bifFilePath.Substring(bifFilePath.LastIndexOf('/') + 1)).BIFFV1FileEntries;
                var biffFileEntry = allEntries[resourceLocator & 0xfffff];
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
                this.Version = new String(reader.ReadChars(4));                
                if (this.Signature == "BAMC")
                {
                    var size = reader.ReadInt32();
                    var decompressor = new InflaterInputStream(reader.BaseStream);
                    var ms = new MemoryStream();
                    decompressor.CopyTo(ms);
                    using (BinaryReader reader2 = new BinaryReader(ms))
                    {
                        ms.Seek(0, SeekOrigin.Begin);
                        this.Signature = new String(reader2.ReadChars(4)); // BAMC ><
                        this.Version = new String(reader2.ReadChars(4));
                        InitHeader(reader2);
                        LoadResources(0, reader2);
                        LoadImage(0, reader2);
                    }
                    decompressor.Dispose();
                    reader.Dispose();
                    return;
                }

                InitHeader(reader);
                LoadResources(originalOffset, reader);
                LoadImage(originalOffset, reader);                
            }
        }

        private void LoadImage(int originalOffset, BinaryReader reader)
        {
            int[] offsets = { OffsetFrameEntries, OffsetFramePalette, OffsetFrameFrameLookupTable, (int)reader.BaseStream.Length };
            Array.Sort(offsets);
            int idx = Array.BinarySearch(offsets, OffsetFramePalette);
            int numEntries = 256;
            this.BAMPalette = new uint[256];

            if (idx >= 0 && idx + 1 < offsets.Length)
            {
                numEntries = Math.Min(256, (offsets[idx + 1] - offsets[idx]) / 4);
            }
            for (int i = 0; i < 256; ++i)
                BAMPalette[i] = 0xff000000;

            reader.BaseStream.Seek(originalOffset + OffsetFramePalette, SeekOrigin.Begin);
            for (int i = 0; i < numEntries; ++i)
            {
                BAMPalette[i] = reader.ReadUInt32();
            }

            int frameIdx = 0;            
            int srcWidth = Frames[frameIdx].Width;
            int srcHeight = Frames[frameIdx].Height;
            var dstHeight = 32;
            var dstWidth = 32;
            
            bool isCompressed = Frames[frameIdx].Compressed;
            int ofsData = Frames[frameIdx].FrameData;

            int left, top, maxWidth, maxHeight, srcOfs, dstOfs ,count = 0;
            uint color = 0;
            byte pixel = 0;
            
            left = 32;
            if (srcWidth != srcHeight)
                left = 38;
            top = 0;
            maxWidth = (dstWidth < srcWidth + left) ? dstWidth : srcWidth;
            maxHeight = (dstHeight < srcHeight + top) ? dstHeight : srcHeight;
            srcOfs = ofsData;
            dstOfs = top * dstWidth + left;
            reader.BaseStream.Seek(srcOfs, SeekOrigin.Begin);
            var bufferB = new byte[maxWidth * maxHeight];
            var bufferI = new uint[maxWidth * maxHeight];

            try
            {
                for (int y = 0; y < srcHeight; ++y)
                {
                    for (int x = 0; x < srcWidth; x++, dstOfs++)
                    {
                        if (count > 0)
                        {
                            count--;
                            if (x < maxWidth)
                            {
                                bufferB[dstOfs] = pixel;
                                bufferI[dstOfs] = color;
                            }
                        }
                        else
                        {
                            pixel = reader.ReadByte();
                            color = BAMPalette[pixel & 0xff];
                            if (isCompressed && (pixel & 0xff) == ColorIndex)
                            {
                                count = reader.ReadByte() & 0xff;
                            }
                            if (x < maxWidth)
                            {
                                bufferB[dstOfs] = pixel;
                                bufferI[dstOfs] = color;
                            }
                        }
                    }
                    dstOfs += dstWidth - srcWidth;
                }
            }
            catch (Exception ex)
            {
                var breakpoint = 10;
            }

            var img = new Bitmap(maxWidth, maxHeight, PixelFormat.Format8bppIndexed);

            var bits1 = img.LockBits(new Rectangle(0, 0, maxWidth, maxHeight), ImageLockMode.ReadWrite, PixelFormat.Format8bppIndexed);
             Marshal.Copy(bufferB, 0, bits1.Scan0, maxWidth * maxHeight);
            img.UnlockBits(bits1);

            var pal = img.Palette;
            
            for (int i = 0; i < 256; ++i)
                pal.Entries[i] = Color.FromArgb(unchecked((int) BAMPalette[i]));
            img.Palette = pal;
            this.Image = img;
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
            this.CountEntries = reader.ReadInt16();
            this.CountCycles = reader.ReadByte();
            this.ColorIndex = reader.ReadByte() & 0xff;
            this.OffsetFrameEntries = reader.ReadInt32();
            this.OffsetFramePalette = reader.ReadInt32();
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
