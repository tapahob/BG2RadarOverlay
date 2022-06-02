using BGOverlay.NativeStructs;
using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using WinApiBindings;
namespace BGOverlay
{
    public class BGEntity
    {
        public CREReader Reader { get; set; }

        private IntPtr entityIdPtr;
        private ResourceManager resourceManager;

        public int tag { get; set; }
        public List<CGameEffect> TimedEffects { get; private set; }
        public List<Tuple<string, string, uint>> SpellProtection { get; private set; }
        public bool Loaded { get; }
        public int Id { get; private set; }
        public int X { get; private set; }
        public int Y { get; private set; }
        public byte Type { get; private set; }
        public long RealId { get; }
        public string AreaName { get; }
        public string AreaRef { get; }
        public int MousePosX { get; }
        public int AreaNCharacters { get; }
        public int MousePosY { get; }
        public IntPtr CInfinityPtr { get; private set; }
        public int MousePosX1 { get; }
        public int MousePosY1 { get; }
        public byte Name2Len { get; }
        public string Name2 { get; }
        public string Name1 { get; private set; }
        public string CreResourceFilename { get; private set; }

        public byte CurrentHP { get; private set; }
        public CDerivedStats DerivedStats { get; private set; }
        public CDerivedStats DerivedStatsTemp { get; private set; }
        public int THAC0 { get; private set; }

        private IntPtr timedEffectsPointer;
        private IntPtr equipedEffectsPointer;
        private IntPtr curSpellPtr;

        public CDerivedStats DerivedStatsBonus { get; private set; }

        public String Class {
            get
            {
                return (this.Reader.KitInformation != CREReader.KIT.NONE
                    && this.Reader.KitInformation != CREReader.KIT.TRUECLASS)
                    ? this.Reader.KitInformation.ToString().Replace('_', ' ')
                    : this.Reader.Class.ToString().Replace('_', ' ');
            }
        }

        public List<string> Protections
        {
            get
            {
                var allEffects = this.Reader.Effects.Where(x => x.EffectName != Effect.Text_Protection_from_Display_Specific_String);
                var result = new List<String>();
                var opCodeStrings = new List<String>();
                var spellStrings = new List<String>();
                if (DerivedStatsTemp.WeaponImmune.Count > 0)
                {
                    result.Add($"Requires +{DerivedStatsTemp.WeaponImmune.Count} weapons to be hit");
                }
                for (int i = 9; i > 0; --i)
                {
                    if (DerivedStatsTemp.spellImmuneLevel[i] > 0)
                    {
                        result.Add($"Immune to spells up to level {i}");
                        break;
                    }
                }
                var thiefStr = "";
                if (this.DerivedStatsTemp.BackstabImmunity > 0)
                    thiefStr += "Backstab Immunity\t";
                if (this.DerivedStatsTemp.SeeInvisible > 0)
                    thiefStr += "See Invisible";
                if (thiefStr != "")
                    result.Add(thiefStr);
                string proficiencyStr = "Proficiency: ";
                foreach (var item in allEffects)
                {
                    if ($"{item.EffectName}".StartsWith("Graphics")
                        || $"{item.EffectName}".StartsWith("Text"))
                    {
                        continue;
                    }
                    if (item.EffectName == Effect.Protection_from_Opcode)
                    {
                        opCodeStrings.Add(preprocess($"{(Effect)item.Param2}"));
                        continue;
                    }
                    if (item.EffectName == Effect.Spell_Protection_from_Spell)
                    {
                        var spellName = resourceManager.GetSPLReader($"{item.Resource.Trim('\0')}.SPL".ToUpper()).Name1;
                        if (spellName == "-1")
                        {
                            spellName = resourceManager.GetSPLReader($"{item.Resource.Substring(0, item.Resource.Length - 1).Trim('\0')}.SPL".ToUpper()).Name1;
                            spellName = spellName == "-1" ? item.Resource : spellName;
                        }
                        spellStrings.Add(preprocess(spellName));
                        continue;
                    }
                    if (item.EffectName == Effect.Stat_Proficiency_Modifier)
                    {
                        var amount = item.Param1;
                        var type = (Proficiency)item.Param2;
                        proficiencyStr += $"{type.ToString().Replace("_", " ")} +{amount} ";
                        continue;
                    }
                    if (item.EffectName == Effect.Stat_AC_vs_Damage_Type_Modifier)
                    {
                        var amount = item.Param1;
                        var type = item.Param2;

                        switch (type)
                        {
                            case 0:
                                result.Add($"AC Bonus +{amount}");
                                break;
                            case 1:
                                result.Add($"AC vs Crushing +{amount}");
                                break;
                            case 2:
                                result.Add($"AC vs Missile +{amount}");
                                break;
                            case 4:
                                result.Add($"AC vs Piercing +{amount}");
                                break;
                            case 8:
                                result.Add($"AC vs Slashing +{amount}");
                                break;
                            case 16:
                                result.Add($"Set AC to {amount}");
                                break;
                        }
                        continue;
                    }
                    if (item.EffectName == Effect.Stat_THAC0_Modifier)
                    {
                        var amount = item.Param1;
                        switch (item.Param2)
                        {
                            case 0:
                                result.Add($"THAC0 +{amount}");
                                break;
                            case 1:
                                result.Add($"Set THAC0 to {amount}");
                                break;
                            case 2:
                                result.Add($"THAC0 +{amount}%");
                                break;
                        }
                        continue;
                    }
                    var effectName = item.EffectName.ToString();
                    if (!effectName.StartsWith("Colour") 
                        && !effectName.StartsWith("Protection_Backstab")
                        && !effectName.EndsWith("by_Script"))
                        result.Add(preprocess(effectName));
                }
                //if (opCodeStrings.Any())
                //{
                //    result.Add(preprocess("Protection from " + string.Join(", ", opCodeStrings)));
                //}
                if (spellStrings.Any())
                {
                    result.Add(preprocess("Immune to spells: " + string.Join(", ", spellStrings)));
                }
                if (!proficiencyStr.EndsWith(": "))
                    result.Add(proficiencyStr);
                var inMemoryProtections = DerivedStatsTemp.EffectImmunes.Select(y => y.EffectId.ToString()).Where(x =>
                !x.StartsWith("Text")
                && !x.StartsWith("Graphics")
                && !x.Contains("RGB")
                && !x.StartsWith("Colour")).Select(z => preprocess(z)).Distinct().ToList();                
                if (inMemoryProtections.Any())
                    result.Add("Effect immunities: " + string.Join(", ", inMemoryProtections));
                var moreSpellImmunities = DerivedStatsTemp.SpellImmunities;


                return result;
            }
        }

        public byte EnemyAlly { get; private set; }

        private IntPtr cInfGamePtr;

        public uint GameTime { get; private set; }
        public string Attacks { get; private set; }
        public List<CGameEffect> EquipedEffects { get; private set; }
        public List<Tuple<string, string, uint>> SpellEquipEffects { get; private set; }

        public string HPString { get { return $"{this.CurrentHP}/{this.DerivedStatsTemp.MaxHP}"; } }

        private static List<string> filter = new List<string>()
        {
            "State_",
            "Stat_",
            "Death_",
            "Spell_Effect_",
            "Spell_",
        };

        private string preprocess(string str)
        {
            if (str == null) { return "null"; }
            foreach (var pattern in filter)
            {
                str = str.Replace(pattern, "");
            }
            return str.Replace("_"," ");
        }

        public BGEntity(ResourceManager resourceManager, IntPtr entityIdPtr)
        {
            this.entityIdPtr = entityIdPtr;
            this.resourceManager = resourceManager;
            this.Loaded = false;
            // 1020 bytes CGameAIBase
            this.Id = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x48 }));
            this.Type = WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x8 }));
            if (Type != 49)
                return;
            this.X = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0xC }));
            this.Y = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0xC + 4 }));
            if (X < 0 || Y < 0)
                return;
            this.CreResourceFilename = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x540 }), 8).Trim('*') + ".CRE";
            if (this.CreResourceFilename == ".CRE") 
                return;
            IntPtr cGameAreaPtr = WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x18 });
            this.EnemyAlly = WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x38 }));            
            this.cInfGamePtr = WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x228 });
            this.updateTime();
            this.AreaName = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x0 }), 8);
            this.MousePosX = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x254 }));
            this.MousePosY = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x254 + 4 }));
            this.CInfinityPtr = cGameAreaPtr + 0x5C8;            
            this.MousePosX1 = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x5C8 + 0x60 }));
            this.MousePosY1 = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x5C8 + 0x60 + 0x4 }));
            this.Name2 = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x3928, 0x0}), 64);
            this.Name1 = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x30, 0x0 }), 8);            
            this.CurrentHP = WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x560 + 0x1C }));
            this.timedEffectsPointer = WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x4A00 });
            this.equipedEffectsPointer = WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x49B0 });
            
            this.curSpellPtr = entityIdPtr + 0x4AE0; //TODO: Current spell being cast? should be pretty cool

            if (Type == 49)
            {
                this.Reader = resourceManager.GetCREReader(CreResourceFilename.ToUpper());
                if (Reader == null || Reader.Class == CREReader.CLASS.ERROR)
                {
                    if (resourceManager.CREReaderCache.ContainsKey(CreResourceFilename.ToUpper()))
                        this.Reader = resourceManager.CREReaderCache[CreResourceFilename.ToUpper()];
                    else return;
                }
            }
            this.Loaded = true;
        }

        private void updateTime()
        {
            this.GameTime = WinAPIBindings.ReadUInt32(WinAPIBindings.FindDMAAddy(cInfGamePtr, new int[] { 0x3FA0 }));
        }

        public void LoadDerivedStats()
        {
            this.DerivedStats = new CDerivedStats(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x1120 }));
            this.DerivedStatsBonus = new CDerivedStats(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x2A70 }));
            this.DerivedStatsTemp = new CDerivedStats(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x1DC8 }));
            this.THAC0 = DerivedStats.THAC0 - DerivedStatsTemp.HitBonus - DerivedStats.THAC0BonusRight;
            this.calcAPR();
        }

        private void calcAPR()
        {
            this.loadEquipedEffects();

            string formatStr;
            var apr = this.DerivedStats.NumberOfAttacks;
            int aprDisplayNum = apr;
            
            if ((DerivedStatsTemp.GeneralState * 0x8000) == 0 
                || (this.EquipedEffects.Any(x => x.EffectId == Effect.Stat_Attacks_Per_Round_Modifier && x.dWFlags == 3)))
            {
                // normal apr
                if (apr < 6)
                {
                    formatStr = "{0}";
                }
                else
                {
                    formatStr = "{0}/2";
                    aprDisplayNum = aprDisplayNum * 2 - 11;
                }
            }
            else
            {
                //hasted
                formatStr = "{0}";
                if (apr < 6)
                {
                    aprDisplayNum = aprDisplayNum * 2;
                }
                else
                {
                    aprDisplayNum = aprDisplayNum * 2 - 11;
                }
            }
            this.Attacks = string.Format(formatStr, aprDisplayNum);
        }

        private void loadEquipedEffects()
        {
            var intPtr = this.equipedEffectsPointer;
            this.EquipedEffects = new List<CGameEffect>();
            var list = new CPtrList(intPtr);
            var count = list.Count;
            if (count > 300)
                return;
            var node = list.Head;
            for (int i = 0; i < count; ++i)
            {
                this.EquipedEffects.Add(new CGameEffect(node.Data));
                node = node.getNext();
            }
            this.SpellEquipEffects = this.EquipedEffects.Where(x => !x.ToString().StartsWith("Graphics")
                && !x.ToString().StartsWith("Script")
                && !x.ToString().EndsWith("Sound_Effect")
                && x.SourceRes != "<ERROR>").Select(x => x.getSpellName(resourceManager)).Distinct()
                .Where(x => x.Item1 != null).ToList();            
        }

        public void loadTimedEffects()
        {
            this.updateTime();
            var intPtr = this.timedEffectsPointer;
            this.TimedEffects = new List<CGameEffect>();
            var list = new CPtrList(intPtr);
            var count = list.Count;
            if (count > 300)
                return;
            var node = list.Head;
            for (int i = 0; i < count; ++i)
            {
                this.TimedEffects.Add(new CGameEffect(node.Data));
                node = node.getNext();
            }
            this.TimedEffects = this.TimedEffects.Where(x => !x.ToString().StartsWith("Graphics")
                && !x.ToString().StartsWith("Script")
                && !x.ToString().EndsWith("Sound_Effect")
                && x.SourceRes != "<ERROR>").ToList();
            this.SpellProtection = TimedEffects.Select(x => x.getSpellName(resourceManager)).Distinct()
                .Where(x => x.Item1 != null && !x.Item1.StartsWith("Extra")).ToList();            
            var dif = this.SpellProtection.Where(x => !spellProtectionIcons.Contains(x.Item2)).Distinct().ToList();
            var breakpoint = 0;
        }

        public IList<string> spellProtectionIcons = new List<string>()
        {
            "SPWI108C",
            "SPWI114C",
            "SPWI201C",
            "SPWI212C",
            "SPWI219C",
            "SPWI310C",
            "SPWI311C",
            "SPWI319C",
            "SPWI320C",
            "SPWI403C",
            "SPWI405C",
            "SPWI406C",
            "SPWI408C",
            "SPWI414C",
            "SPWI418C",
            "SPWI512C",
            "SPWI522C",
            "SPWI590C",
            "SPWI591C",
            "SPWI592C",
            "SPWI593C",
            "SPWI594C",
            "SPWI595C",
            "SPWI596C",
            "SPWI597C",
            "SPWI602C",
            "SPWI606C",
            "SPWI611C",
            "SPWI618C",
            "SPWI701C",
            "SPWI702C",
            "SPWI708C",
            "SPWI801C",
            "SPWI902C",
            "SPWI907C",
        };

        public override string ToString()
        {
            return additionalInfo();
        }

        private string additionalInfo()
        {
            if (Reader == null)
            {
                return "NO .CRE INFO";
            }
            if (Reader.ShortName == "<NO TEXT>")
            {
                throw new Exception();
            }
            return $"{this.Name2} HP:{CurrentHP}";
        }
    }
}
