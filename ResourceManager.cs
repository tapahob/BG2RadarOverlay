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
            BIFResourceEntries = new List<BIFResourceEntry>();
            BiffReaderCache    = new Dictionary<string, BIFFReader>();
            CREReaderCache     = new Dictionary<string, CREReader>();
            SPLReaderCache     = new Dictionary<string, SPLReader>();
        }
        public List<BIFResourceEntry> CREResourceEntries            => BIFResourceEntries.Where(x => x.Ext == BIFResourceEntry.Extension.CRE).ToList();
        public List<BIFResourceEntry> SPLResourceEntries            => BIFResourceEntries.Where(x => x.Ext == BIFResourceEntry.Extension.SPL).ToList();

        public Dictionary<int, TLKEntry> StringRefs                 = null;
        public List<BIFEntry> BIFEntries                            = null; 
        public List<BIFResourceEntry> BIFResourceEntries            = null;
        public Dictionary<string, BIFFReader> BiffReaderCache       = null;
        public Dictionary<string, CREReader> CREReaderCache         = null;
        public Dictionary<string, SPLReader> SPLReaderCache         = null;

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
            SPLReader reader;
            if (!SPLReaderCache.TryGetValue(splFilename, out reader))
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
                }
                catch (ArgumentException)
                {
                    reader = null;
                }

            }

            return reader;
        }
    }
}
