using System;
using System.IO;
using System.Linq;

namespace BGOverlay.Resources
{
    public class BIFResourceEntry
    {
        public enum Extension
        {
            ERR = 0x000,
            BMP = 0x001,
            MVE = 0x002,
            WAV = 0x004,
            WFX = 0x005,
            PLT = 0x006,
            TGA = 0x3b8,
            BAM = 0x3e8,
            WED = 0x3e9,
            CHU = 0x3ea,
            TIS = 0x3eb,
            MOS = 0x3ec,
            ITM = 0x3ed,
            SPL = 0x3ee,
            BCS = 0x3ef,
            IDS = 0x3f0,
            CRE = 0x3f1,
            ARE = 0x3f2,
            DLG = 0x3f3,
            TYPE_2DA = 0x3f4,
            GAM = 0x3f5,
            STO = 0x3f6,
            WMP = 0x3f7,
            EFF = 0x3f8,
            BS = 0x3f9,
            CHR = 0x3fa,
            VVC = 0x3fb,
            VEF = 0x3fc,
            PRO = 0x3fd,
            BIO = 0x3fe,
            WBM = 0x3ff,
            FNT = 0x400,
            GUI = 0x402,
            SQL = 0x403,
            PVR = 0x404,
            GLS = 0x405,
            TOT = 0x406,
            TOH = 0x407,
            MEN = 0x408,
            LUA = 0x409,
            TTF = 0x40a,
            PNG = 0x40b,
            BAH = 0x44c,
            INI = 0x802,
            SRC = 0x803,
            MAZ = 0x804,
            MUS = 0xffe,
            ACM = 0xfff
        }

        public BIFResourceEntry(int index, BinaryReader reader, ResourceManager resourceManager)
        {
            this.Index           = index;
            this.ResourceName    = new string(reader.ReadChars(8)).Trim('\0').ToUpper();
            this.ResourceType    = reader.ReadInt16() & 0xffff;
            this.ResourceLocator = reader.ReadInt32();
            this.Ext             = (Extension)ResourceType;
            this.BiffEntry       = resourceManager.BIFEntries.FirstOrDefault(x => x.Index == ((ResourceLocator >> 20) & 0xfff));
            this.FullName        = $"{ResourceName}.{Ext.ToString().Substring(Ext.ToString().Length-3, 3)}".ToUpper();
            this.resourceManager = resourceManager;
        }

        public void LoadCREFiles()
        {
            if (Ext == Extension.CRE)
            {
                var bifArchive    = BiffEntry.FileName.Substring(BiffEntry.FileName.LastIndexOf('/') + 1);
                var allEntries    = resourceManager.GetBIFFReader(bifArchive).BIFFV1FileEntries;
                var biffFileEntry = allEntries[ResourceLocator & 0xfffff];
                if (biffFileEntry.Ext != Extension.CRE)
                {
                    throw new Exception();
                }
                this.resourceManager.CREReaderCache[$"{ResourceName}.CRE"] = new CREReader(this.resourceManager, $"{ResourceName}.CRE", biffFileEntry.Offset, BiffEntry.BIFFilePath);
            }
        }

        public override string ToString()
        {
            return $"{FullName} : {BiffEntry}";
        }

        public int Index { get; }
        public string ResourceName { get; }
        public int ResourceType { get; }
        public int ResourceLocator { get; }

        public Extension Ext { get; }
        public BIFEntry BiffEntry { get; }
        public string FullName { get; }

        private ResourceManager resourceManager;
    }
}
