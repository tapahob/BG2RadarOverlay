using BGOverlay.Resources;
using System;
using System.Collections.Generic;
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
            CREReaderCache = new Dictionary<string, CREReader>();
        }
        public List<BIFResourceEntry> CREResorceEntries            => BIFResourceEntries.Where(x => x.Ext == BIFResourceEntry.Extension.CRE).ToList();
        public Dictionary<int, TLKEntry> StringRefs                = null;
        public List<BIFEntry> BIFEntries                           = null; 
        public List<BIFResourceEntry> BIFResourceEntries           = null;
        public Dictionary<string, BIFFReader> BiffReaderCache      = null;
        public Dictionary<string, CREReader> CREReaderCache        = null;

        public void Init()
        {
            new TLKReader(this);
            new KeyReader(this);
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
                    if (reader.Version == null) reader = null;
                    CREReaderCache.Add(creFilename, reader);
                }
                catch (ArgumentException ex)
                {
                    //Console.WriteLine(ex.Message);
                    reader = null;
                }

            }
            return reader;
        }
    }
}
