using BGOverlay.Readers;
using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        internal Dictionary<int, TLKEntry> StringRefs { get; private set; }
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
            StringRefs = new TLKReader(dialogPath(Configuration.GameFolder, Configuration.Locale)).StringRefs;
            new KeyReader(this);
            loadBifs();
            indexPriestBuffs();
        }

        private readonly object localizedReadersGate = new object();

        public async Task<bool> ReloadGameStringsAsync(string locale)
        {
            var gameFolder = Configuration.GameFolder;
            var path = dialogPath(gameFolder, locale);
            var strings = await Task.Run(() => new TLKReader(path).StringRefs);
            if (Instance != this || Configuration.GameFolder != gameFolder)
                return false;

            // Readers cache resolved display strings, including spell names nested in items
            // and item names and localized flags nested in creatures.
            lock (localizedReadersGate)
            {
                StringRefs = strings;
                CREReaderCache.Clear();
                ITMReaderCache.Clear();
                SPLReaderCache.Clear();
                potionSources.Clear();
            }
            return true;
        }

        private static string dialogPath(string gameFolder, string locale) =>
            Path.Combine(gameFolder, "lang", locale, "dialog.tlk");

        private readonly Dictionary<string, string> priestBuffParents =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Spell slots and helper resrefs vary between IWDEE and BG installations with IWD spells.
        // Resolve the installed SPL via SPELL.IDS, then follow its actual cast-spell effects.
        private void indexPriestBuffs()
        {
            priestBuffParents.Clear();
            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "CLERIC_CHANT", "CLERIC_PRAYER", "CLERIC_RECITATION", "CLERIC_RIGHTEOUS_WRATH_OF_THE_FAITHFUL"
            };
            var ids = ReadResource("SPELL.IDS");
            if (ids == null) return;
            foreach (var line in Encoding.ASCII.GetString(ids).Split('\n'))
            {
                var fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || !symbols.Contains(fields[1]) ||
                    !int.TryParse(fields[0], out int slot) || slot < 1000 || slot >= 2000) continue;

                string parent = $"SPPR{slot - 1000:D3}.SPL";
                foreach (var helper in SPLReader.CalledSpells(ReadResource(parent)))
                    priestBuffParents[helper] = parent;
            }
        }

        internal string BuffSpellFilename(string source)
        {
            return priestBuffParents.TryGetValue(source, out var parent) ? parent : source + ".SPL";
        }

        private readonly Dictionary<string, ITMReader> potionSources =
            new Dictionary<string, ITMReader>(StringComparer.OrdinalIgnoreCase);

        internal ITMReader GetPotionSource(string filename)
        {
            lock (localizedReadersGate)
            {
                if (!potionSources.TryGetValue(filename, out var potion))
                {
                    // Inventory lookups can match a shortened resref by suffix. An effect
                    // already carries the full resref, so require an exact resource here.
                    if (BIFResourceEntries.ContainsKey(filename.ToUpperInvariant()) ||
                        File.Exists(Path.Combine(Configuration.GameFolder, "override", filename)))
                    {
                        var item = GetITMReader(filename);
                        if (item?.ItemType == 9) potion = item; // POTION in ITEMCAT.IDS
                    }
                    potionSources[filename] = potion;
                }
                return potion;
            }
        }

        internal byte[] ReadResource(string filename)
        {
            var path = Path.Combine(Configuration.GameFolder, "override", filename);
            try
            {
                if (File.Exists(path)) return File.ReadAllBytes(path);
                if (!BIFResourceEntries.TryGetValue(filename, out var entry)) return null;
                var archive = entry.BiffEntry.BIFFilePath;
                var bif = GetBIFFReader(Path.GetFileName(archive));
                if (bif == null || !bif.BIFFV1FileEntries.TryGetValue(entry.ResourceLocator & 0xfffff, out var file)) return null;
                using (var reader = new BinaryReader(File.OpenRead(archive)))
                {
                    reader.BaseStream.Position = file.Offset;
                    return reader.ReadBytes(file.Size);
                }
            }
            catch (IOException ex)
            {
                Logger.Error($"Could not read {filename}", ex);
                return null;
            }
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
                // The UI resolves active casts while the scanner can resolve effects.
                lock (localizedReadersGate)
                {
                    if (!SPLReaderCache.TryGetValue(splFilename, out reader))
                    {
                        reader = new SPLReader(this, splFilename);
                        SPLReaderCache[splFilename] = reader;
                        Logger.Info($"SPLReader created: {splFilename}");
                    }
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
                lock (BiffReaderCache)
                {
                    if (!BiffReaderCache.TryGetValue(bifFilename, out reader))
                    {
                        reader = new BIFFReader(bifFilename);
                        BiffReaderCache[bifFilename] = reader;
                        Logger.Info($"BIFFReader created: {bifFilename}");
                    }
                }
            } catch (Exception ex)
            {
                Logger.Error($"Could not create an BIFReader: {bifFilename}", ex);
            }
            return reader;
        }

        public CREReader GetCREReader(string creFilename)
        {
            lock (localizedReadersGate)
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
        }

        public ITMReader GetITMReader(string itmFilename)
        {
            lock (localizedReadersGate)
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
        }

        public BAMReader GetBAMReader(string bamFilename)
        {
            if (string.IsNullOrWhiteSpace(bamFilename?.Trim('\0')))
                return null;
            bamFilename = bamFilename.ToUpperInvariant();
            BAMReader reader = null;
            try
            {
                lock (BAMReaderCache)
                {
                    if (!BAMReaderCache.TryGetValue(bamFilename, out reader))
                    {
                        reader = new BAMReader(this, bamFilename);
                        BAMReaderCache[bamFilename] = reader;
                        Logger.Info($"BAMReader created: {bamFilename}");
                    }
                }
            } catch (Exception ex)
            {
                Logger.Error($"Could not create a BAMReader: {bamFilename}", ex);
            }
            
            return reader;
        }

    }
}
