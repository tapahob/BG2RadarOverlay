using BGOverlay.Readers;
using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BGOverlay
{
    public class ResourceManager
    {
        public ResourceManager()
        {
            StringRefs         = new Dictionary<int, TLKEntry>();
            BIFEntries         = new List<BIFEntry>();
            BIFResourceEntries = new Dictionary<string, BIFResourceEntry>();
            BiffReaderCache    = new Dictionary<string, BIFFReader>();
            CREReaderCache     = new Dictionary<string, CREReader>();
            ITMReaderCache     = new Dictionary<string, ITMReader>();
            SPLReaderCache     = new Dictionary<string, SPLReader>();
            BAMReaderCache     = new Dictionary<string, BAMReader>();
        }
        public List<BIFResourceEntry> CREResourceEntries => BIFResourceEntries.Values.Where(x => x.Ext == BIFResourceEntry.Extension.CRE).ToList();
        public List<BIFResourceEntry> SPLResourceEntries => BIFResourceEntries.Values.Where(x => x.Ext == BIFResourceEntry.Extension.SPL).ToList();
        public List<BIFResourceEntry> ITMResourceEntries => BIFResourceEntries.Values.Where(x => x.Ext == BIFResourceEntry.Extension.ITM).ToList();

        public Dictionary<int, TLKEntry> StringRefs                     = null;
        public List<BIFEntry> BIFEntries                                = null; 
        public Dictionary<string, BIFResourceEntry> BIFResourceEntries  = null;
        public Dictionary<string, BIFFReader> BiffReaderCache           = null;
        public Dictionary<string, CREReader> CREReaderCache             = null;
        public Dictionary<string, ITMReader> ITMReaderCache             = null;
        public Dictionary<string, SPLReader> SPLReaderCache             = null;
        public Dictionary<string, BAMReader> BAMReaderCache             = null;

        public void Init()
        {
            new TLKReader(this);
            new KeyReader(this);
            loadBifs();
        }


        private void loadBifs()
        {
            var allBifs = new DirectoryInfo($"{Configuration.GameFolder}\\data").GetFiles().Select(x => x.Name);
            foreach (var bifFile in allBifs)
            {
                GetBIFFReader(bifFile);
            }
        }

        public string GetStrRefText(int strRef)
        {
            if (strRef == -1)
            {
                return "-1";
            }
            TLKEntry result;
            if (!StringRefs.TryGetValue(strRef, out result)) {
                return "-1";
            }
            return result.Text;
        }

        public SPLReader GetSPLReader(string splFilename)
        {
            splFilename = splFilename.ToUpper();
            if (!SPLReaderCache.TryGetValue(splFilename, out var reader))
            {
                reader = new SPLReader(this, splFilename);
                SPLReaderCache[splFilename] = reader;
            }
            return reader;
        }

        public BIFFReader GetBIFFReader(string bifFilename)
        {
            bifFilename = bifFilename.ToUpper();
            BIFFReader reader;
            if (!BiffReaderCache.TryGetValue(bifFilename, out reader))
            {
                reader = new BIFFReader(bifFilename);
                BiffReaderCache[bifFilename] = reader;
            }
            return reader;
        }

        public CREReader GetCREReader(string creFilename)
        {
            creFilename = creFilename.ToUpper();
            CREReader reader;
            if (!CREReaderCache.TryGetValue(creFilename, out reader))
            {
                if (creFilename == "<ERROR>.CRE")
                {
                    return null;
                }
                try
                {
                    reader = new CREReader(this, creFilename);
                    if (reader.Version == null)
                    {
                        var key = CREReaderCache.Keys.FirstOrDefault(x => x.EndsWith(creFilename));
                        reader = CREReaderCache[key];
                    }
                    else
                    {
                        CREReaderCache[creFilename] = reader;
                    }
                }
                catch (ArgumentException)
                {
                    reader = null;
                }

            }

            return reader;
        }

        public ITMReader GetITMReader(string itmFilename)
        {
            itmFilename = itmFilename.ToUpper();
            ITMReader reader;
            if (!ITMReaderCache.TryGetValue(itmFilename, out reader))
            {
                if (itmFilename == "<ERROR>.ITM")
                {
                    return null;
                }
                try
                {
                    reader = new ITMReader(this, itmFilename);
                    if (reader.Version == null)
                    {
                        var key = ITMReaderCache.Keys.FirstOrDefault(x => x.EndsWith(itmFilename));
                        reader = ITMReaderCache[key];
                    }
                    else
                    {
                        ITMReaderCache[itmFilename] = reader;
                    }
                }
                catch (ArgumentException)
                {
                    reader = null;
                }

            }

            return reader;
        }

        public BAMReader GetBAMReader(string bamFilename)
        {
            if (bamFilename.Trim('\0') == "")
                return null;
            bamFilename = bamFilename.ToUpper();
            BAMReader reader;
            if (!BAMReaderCache.TryGetValue(bamFilename, out reader))
            {
                reader = new BAMReader(this, bamFilename);
                BAMReaderCache[bamFilename] = reader;
            }
            return reader;
        }

        public void log(string line)
        {
            using(StreamWriter sw = File.AppendText("log.txt"))
            {
                sw.WriteLine(line);
            }
        }

    }
}
