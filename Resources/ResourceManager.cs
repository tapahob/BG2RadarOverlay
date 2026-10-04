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
            EFFReaderCache     = new Dictionary<string, EFFReader>();
            BAMReaderCache     = new Dictionary<string, BAMReader>();
        }

        public static ResourceManager Instance { get; private set; }

        public List<BIFResourceEntry> CREResourceEntries => BIFResourceEntries.Values.Where(x => x.Ext == BIFResourceEntry.Extension.CRE).ToList();
        public List<BIFResourceEntry> SPLResourceEntries => BIFResourceEntries.Values.Where(x => x.Ext == BIFResourceEntry.Extension.SPL).ToList();
        public List<BIFResourceEntry> EFFResourceEntries => BIFResourceEntries.Values.Where(x => x.Ext == BIFResourceEntry.Extension.EFF).ToList();
        public List<BIFResourceEntry> ITMResourceEntries => BIFResourceEntries.Values.Where(x => x.Ext == BIFResourceEntry.Extension.ITM).ToList();

        public Dictionary<int, TLKEntry> StringRefs                     = null;
        public List<BIFEntry> BIFEntries                                = null; 
        public Dictionary<string, BIFResourceEntry> BIFResourceEntries  = null;
        public Dictionary<string, BIFFReader> BiffReaderCache           = null;
        public Dictionary<string, CREReader> CREReaderCache             = null;
        public Dictionary<string, ITMReader> ITMReaderCache             = null;
        public Dictionary<string, SPLReader> SPLReaderCache             = null;
        public Dictionary<string, EFFReader> EFFReaderCache             = null;
        public Dictionary<string, BAMReader> BAMReaderCache             = null;

        // The KEY/BIF index is built once at startup, but override/ is not in it at all - a
        // creature a mod dropped in there is perfectly spawnable and would otherwise read as
        // missing. Listed lazily and re-listed occasionally, since a folder someone is modding
        // can gain files while the game is open.
        private HashSet<string> overrideCreatures;
        private DateTime overrideCreaturesListedUtc = DateTime.MinValue;
        private static readonly TimeSpan OverrideListingTtl = TimeSpan.FromSeconds(30);
        private readonly object overrideGate = new object();

        /// <summary>
        /// Whether a creature ResRef names something this installation can actually spawn.
        ///
        /// Used to keep packs that reference creatures nobody has out of what viewers are offered
        /// and out of what a random summon can pick: a viewer spending points on an ambush that
        /// cannot happen is the one outcome worth ruling out up front.
        ///
        /// Unknown reads as "exists". Before the game is hooked there is no index to consult, and
        /// hiding every pack at that point would be far more wrong than letting one through.
        /// </summary>
        public bool CreatureExists(string resref)
        {
            if (string.IsNullOrWhiteSpace(resref))
                return false;

            var name = resref.Trim().ToUpperInvariant();

            if (BIFResourceEntries != null && BIFResourceEntries.ContainsKey(name + ".CRE"))
                return true;

            foreach (var inOverride in overrideCreatureNames())
            {
                if (inOverride == name)
                    return true;
            }

            return false;
        }

        private HashSet<string> overrideCreatureNames()
        {
            lock (overrideGate)
            {
                if (overrideCreatures != null && DateTime.UtcNow - overrideCreaturesListedUtc < OverrideListingTtl)
                    return overrideCreatures;

                var found = new HashSet<string>();
                try
                {
                    var folder = string.IsNullOrEmpty(Configuration.GameFolder)
                        ? null
                        : Path.Combine(Configuration.GameFolder, "override");

                    if (folder != null && Directory.Exists(folder))
                    {
                        foreach (var path in Directory.GetFiles(folder, "*.cre"))
                            found.Add(Path.GetFileNameWithoutExtension(path).ToUpperInvariant());
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Could not list override creatures", ex);
                }

                overrideCreatures = found;
                overrideCreaturesListedUtc = DateTime.UtcNow;
                return overrideCreatures;
            }
        }

        public void Init()
        {
            ResourceManager.Instance = this;
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
                return "<NO IDEA>";
            }
            return result.Text;
        }

        public SPLReader GetSPLReader(string splFilename)
        {
            splFilename = splFilename.ToUpperInvariant();
            SPLReader reader = null;
            try
            {
                if (!SPLReaderCache.TryGetValue(splFilename, out reader))
                {
                    reader = new SPLReader(this, splFilename);
                    SPLReaderCache[splFilename] = reader;
                    Logger.Info($"SPLReader created: {splFilename}");
                }
            } catch (Exception ex)
            {
                Logger.Error($"Could not create an SPLReader: {splFilename}", ex);
            }
            
            return reader;
        }

        public EFFReader GetEFFReader(string effFilename)
        {
            effFilename = effFilename.ToUpperInvariant();
            EFFReader reader = null;
            try
            {
                if (!EFFReaderCache.TryGetValue(effFilename, out reader))
                {
                    reader = new EFFReader(this, effFilename);
                    EFFReaderCache[effFilename] = reader;
                    Logger.Info($"EFFReader created: {effFilename}");
                }
            } catch (Exception ex)
            {
                Logger.Error($"Could not create an EFFReader: {effFilename}", ex);
            }
            return reader;
        }

        public BIFFReader GetBIFFReader(string bifFilename)
        {
            bifFilename = bifFilename.ToUpperInvariant();
            BIFFReader reader = null;
            try
            {
                if (!BiffReaderCache.TryGetValue(bifFilename, out reader))
                {
                    reader = new BIFFReader(bifFilename);
                    BiffReaderCache[bifFilename] = reader;
                    Logger.Info($"BIFFReader created: {bifFilename}");
                }
            } catch (Exception ex)
            {
                Logger.Error($"Could not create an BIFReader: {bifFilename}", ex);
            }
            return reader;
        }

        public CREReader GetCREReader(string creFilename)
        {
            creFilename = creFilename.ToUpperInvariant();
            CREReader reader;
            // Cache entries are always stored under this exact normalized filename (see
            // BIFResourceEntry.LoadCREFiles), so try the O(1) lookup first and only fall
            // back to the O(n) suffix scan when that misses.
            if (!CREReaderCache.TryGetValue(creFilename, out reader))
            {
                var key = CREReaderCache.Keys.FirstOrDefault(x => x.EndsWith(creFilename));
                if (key != null)
                {
                    reader = CREReaderCache[key];
                    return reader;
                }

                if (creFilename == "<ERROR>.CRE")
                {
                    return null;
                }
                try
                {
                    reader = new CREReader(this, creFilename);
                    if (reader.Version == null)
                    {
                        key = CREReaderCache.Keys.FirstOrDefault(x => x.EndsWith(creFilename)) ?? creFilename;
                        reader = CREReaderCache[key];
                        Logger.Info($"CREReader created: {creFilename}");
                    }
                    else
                    {
                        CREReaderCache[creFilename] = reader;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Could not create a CREReader: {creFilename}", ex);
                    reader = null;
                }

            }

            return reader;
        }

        public ITMReader GetITMReader(string itmFilename)
        {
            itmFilename = itmFilename.ToUpperInvariant();
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
                        Logger.Info($"ITMReader created: {itmFilename}");
                    }
                    else
                    {
                        ITMReaderCache[itmFilename] = reader;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Could not create an ITMReader: {itmFilename}", ex);
                    reader = null;
                }

            }

            return reader;
        }

        public BAMReader GetBAMReader(string bamFilename)
        {
            if (bamFilename.Trim('\0') == "")
                return null;
            bamFilename = bamFilename.ToUpperInvariant();
            BAMReader reader = null;
            try
            {
                if (!BAMReaderCache.TryGetValue(bamFilename, out reader))
                {
                    reader = new BAMReader(this, bamFilename);
                    BAMReaderCache[bamFilename] = reader;
                    Logger.Info($"BAMReader created: {bamFilename}");
                }
            } catch (Exception ex)
            {
                Logger.Error($"Could not create a BAMReader: {bamFilename}", ex);
            }
            
            return reader;
        }

    }
}
