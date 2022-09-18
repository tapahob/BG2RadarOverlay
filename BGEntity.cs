using BGOverlay.NativeStructs;
using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using WinApiBindings;
using static BGOverlay.CREReader;

namespace BGOverlay
{
    public class BGEntity
    {
        public CREReader Reader { get; set; }

        private IntPtr entityIdPtr;
        private ResourceManager resourceManager;

        public int tag { get; set; }
        public List<CGameEffect> TimedEffects { get; private set; }
        public List<Tuple<string, Bitmap, uint>> SpellProtection { get; private set; }
        public bool Loaded { get; private set; }
        public int Id { get; private set; }
        public int X { get; private set; }
        public int Y { get; private set; }
        public byte Type { get; private set; }
        public long RealId { get; private set; }
        public string AreaName { get; private set; }
        public string AreaRef { get; private set; }
        public int MousePosX { get; private set; }
        public int AreaNCharacters { get; private set; }
        public int MousePosY { get; private set; }
        public IntPtr CInfinityPtr { get; private set; }
        public int MousePosX1 { get; private set; }
        public int MousePosY1 { get; private set; }
        public int ViewportHeight { get; private set; }
        public int ViewportWidth { get; private set; }
        public byte Name2Len { get; private set; }
        public string Name2 { get; private set; }
        public string Name1 { get; private set; }
        public string CreResourceFilename { get; private set; }
        public short CurrentHP { get; private set; }
        public CDerivedStats DerivedStats { get; private set; }
        public CDerivedStats DerivedStatsTemp { get; private set; }
        public int THAC0 { get; private set; }

        private IntPtr timedEffectsPointer;
        private IntPtr equipedEffectsPointer;
        private IntPtr curSpellPtr;
        private IntPtr equipmentPtr;

        public int isInvisible { get; private set; }
        public CDerivedStats DerivedStatsBonus { get; private set; }

        public string Race
        {
            get
            {
                if (this.Reader == null || this.Reader.Race != this.RACE)
                {
                    return this.RACE.ToString()[0] + this.RACE.ToString().ToLower().Substring(1).Replace('_', ' ').Replace("alf", "alf-"); 
                }
                return this.Reader.Race.ToString()[0] + this.Reader.Race.ToString().ToLower().Substring(1).Replace('_', ' ').Replace("alf", "alf-");
            }
        }

        public string Class {
            get
            {
                if (this.Reader == null || this.CLASS != this.Reader.Class)
                {
                    return this.CLASS.ToString()[0] + this.CLASS.ToString().ToLower().Substring(1).Replace('_', ' ');
                }
                return (this.Reader.KitInformation != CREReader.KIT.NONE
                    && this.Reader.KitInformation != CREReader.KIT.TRUECLASS)
                        ? this.Reader.KitInformation.ToString().Replace('_', ' ')
                        : this.Reader.Class.ToString()[0] + this.Reader.Class.ToString().ToLower().Substring(1).Replace('_', ' ');
            }
        }

        public List<string> Protections
        {
            get
            {
                var allEffects = this.Reader?.Effects?
                    .Where(x => x.EffectName != Effect.Text_Protection_from_Display_Specific_String)
                    ?? new List<EffectEntry>();
                var result             = new List<String>();
                var opCodeStrings      = new List<String>();
                var spellStrings       = new List<String>();
                var onHitMeleeStrings  = new List<String>();
                var onHitRangedStrings = new List<String>();

                if (DerivedStats.WeaponImmune.Count > 0)
                {
                    var weaponProtectionFromBuffs = this.TimedEffects.Where(x => x.EffectId == Effect.Protection_from_Weapons);
                    
                    if (weaponProtectionFromBuffs.Any())
                    {
                        // from PFMW
                        if (weaponProtectionFromBuffs.Any(x => x.dWFlags == 1))
                        {
                            var str = $"Protected from Magic";
                            // from normal
                            if (weaponProtectionFromBuffs.Any(x => x.dWFlags == 2))
                            {
                                str += " and Normal";
                            }
                            str += " Weapons";
                             result.Add(str);
                        }
                        else
                        {
                             var val = Math.Max(weaponProtectionFromBuffs.Count(), DerivedStats.WeaponImmune.Count()-1);
                             result.Add($"Requires +{val} weapons to be hit");
                        }
                    } else
                    {
                        result.Add($"Requires +{DerivedStats.WeaponImmune.Count} weapons to be hit");
                    }                                             
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
                if (this.DerivedStats.BackstabImmunity > 0)
                    thiefStr += "Backstab Immunity\t";
                if (this.DerivedStats.SeeInvisible > 0)
                    thiefStr += "See Invisible\t";
                if (this.CritImmune) 
                    thiefStr += "Crit Immune";
                if (thiefStr != "")
                    result.Add(thiefStr);
                string proficiencyStr = "Proficiency: ";
                var allEffectsStrings = new List<string>();
                
                foreach (var item in allEffects)
                {
                    if ($"{item.EffectName}".StartsWith("Graphics")
                        || $"{item.EffectName}".StartsWith("Text")
                        || item.EffectName == Effect.Spell_Effect_NPCBump)
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
                    if (item.EffectName == Effect.Item_Set_Melee_Effect)
                    {
                        var hitEffectName = resourceManager.GetEFFReader($"{item.Resource.Trim('\0')}.EFF".ToUpper()).ToString();
                        if (hitEffectName == "-1")
                        {
                            hitEffectName = resourceManager.GetEFFReader($"{item.Resource.Substring(0, item.Resource.Length - 1).Trim('\0')}.EFF".ToUpper()).ToString();
                            hitEffectName = hitEffectName == "-1" ? item.Resource : hitEffectName;
                        }
                        onHitMeleeStrings.Add(preprocess(hitEffectName));
                        continue;
                    }
                    if (item.EffectName == Effect.Item_Set_Ranged_Effect)
                    {
                        var hitEffectName = resourceManager.GetEFFReader($"{item.Resource.Trim('\0')}.EFF".ToUpper()).ToString();
                        if (hitEffectName == "-1")
                        {
                            hitEffectName = resourceManager.GetEFFReader($"{item.Resource.Substring(0, item.Resource.Length - 1).Trim('\0')}.EFF".ToUpper()).ToString();
                            hitEffectName = hitEffectName == "-1" ? item.Resource : hitEffectName;
                        }
                        onHitRangedStrings.Add(preprocess(hitEffectName));
                        continue;
                    }
                    if (item.EffectName == Effect.Stat_AC_vs_Damage_Type_Modifier)
                    {
                        var amount = item.Param1;
                        var type = item.Param2;

                        if (amount == 0)
                            continue;

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
                        && !(item.EffectName == Effect.Script_Scripting_State_Modifier)
                        && !(item.EffectName == Effect.Apply_Effects_List) //TODO: implement it properly
                        && !(item.EffectName == Effect.HP_Minimum_Limit)
                        && !(item.EffectName == Effect.Protection_Backstab)
                        && !(item.EffectName == Effect.Spell_Effect_Invisible_Detection_by_Script)
                        && !(item.EffectName == Effect.State_Set_State)
                        )
                        allEffectsStrings.Add(preprocess(effectName));
                }
                foreach (var spell in SpellEquipEffects)
                {                    
                    spellStrings.Add(preprocess(spell.Item1));
                }
                spellStrings = spellStrings.Distinct().OrderBy(o => o).ToList();
                if (allEffectsStrings.Any())
                    result.Add(String.Join(", ", allEffectsStrings.Distinct().Where(x => !x.Contains("Resistance") && !x.Contains("Backstab") && !x.Contains("AC") && !x.Contains("Save")).OrderBy(o => o)));

                // seems like these are always covered by "Effect Immunities"
                //if (opCodeStrings.Any())
                //{
                //    result.Add(preprocess("Protection from " + string.Join(", ", opCodeStrings.OrderBy(o => o))));
                //}


                if (onHitMeleeStrings.Any())
                {
                    var onHitMeleeStringsFiltered = onHitMeleeStrings.Where(x =>
                    !x.StartsWith("Text")
                    && !x.StartsWith("Graphics")
                    && !x.Contains("RGB")
                    && !x.StartsWith("Colour")
                    && !x.Contains("Portrait"));
                    
                    result.Add(preprocess("On melee hit: " + string.Join(", ", onHitMeleeStringsFiltered)));
                }

                if (onHitRangedStrings.Any())
                {
                    var onHitRangedStringsFiltered = onHitMeleeStrings.Where(x =>
                    !x.StartsWith("Text")
                    && !x.StartsWith("Graphics")
                    && !x.Contains("RGB")
                    && !x.StartsWith("Colour")
                    && !x.Contains("Portrait"));

                    result.Add(preprocess("On ranged hit: " + string.Join(", ", onHitRangedStringsFiltered)));
                }

                if (spellStrings.Any())
                {
                    result.Add(preprocess("Immune to spells: " + string.Join(", ", spellStrings.OrderBy(o => o))));
                }

                if (!proficiencyStr.EndsWith(": "))
                    result.Add(proficiencyStr);
                
                var inMemoryProtections = DerivedStats.EffectImmunes.Select(y => y.EffectId.ToString()).Where(x =>
                !x.StartsWith("Text")
                && !x.StartsWith("Graphics")
                && !x.Contains("RGB")
                && !x.StartsWith("Colour")).Select(z => preprocess(z)).Distinct().ToList();                
                if (inMemoryProtections.Any())
                    result.Add("Effect immunities: " + string.Join(", ", inMemoryProtections.OrderBy(o => o)));
                var moreSpellImmunities = DerivedStats.SpellImmunities;
                return result;
            }
        }

        public byte EnemyAlly { get; private set; }
        public RACE RACE { get; private set; }
        public CLASS CLASS { get; private set; }

        private IntPtr cInfGamePtr;

        public uint GameTime { get; private set; }
        public string Attacks { get; private set; }
        public List<CGameEffect> EquipedEffects { get; private set; }
        public List<Tuple<string, Bitmap, uint>> SpellEquipEffects { get; private set; }

        public string HPString { get { return $"{this.CurrentHP}/{this.DerivedStatsTemp.MaxHP}"; } }

        public bool CritImmune { get; private set; }

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
            init(resourceManager, entityIdPtr);
        }

        private void init(ResourceManager resourceManager, IntPtr entityIdPtr)
        {
            this.entityIdPtr     = entityIdPtr;
            this.resourceManager = resourceManager;
            this.Loaded          = false;
            try
            {
                // 1020 bytes CGameAIBase
                this.Id   = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x48 }));
                this.Type = WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x8 }));

                if (Type != 49)
                    return;

                this.X = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0xC }));
                this.Y = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0xC + 4 }));
                
                if (X < 0 || Y < 0)
                    return;

                this.CreResourceFilename = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x540 }), 8).Trim('*') + ".CRE";

                IntPtr cGameAreaPtr = WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x18 });
                this.EnemyAlly      = WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x38 }));
                this.RACE           = (RACE)WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x3A }));
                this.CLASS          = (CLASS)WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x3B }));

                if (this.CreResourceFilename == ".CRE")
                    return;
                this.cInfGamePtr = WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x228 });
                this.updateTime();
                this.AreaName              = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x0 }), 8);
                this.MousePosX             = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x254 }));
                this.MousePosY             = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x254 + 4 }));
                this.CInfinityPtr          = cGameAreaPtr + 0x5C8;
                this.MousePosX1            = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x5C8 + 0x60 }));
                this.MousePosY1            = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x5C8 + 0x60 + 0x4 }));
                this.ViewportHeight        = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x5C8 + 0x78 + 0xC }));
                this.ViewportWidth         = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x5C8 + 0x78 + 0x8 }));
                this.Name2                 = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x3928, 0x0 }), 64);
                this.Name1                 = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x30, 0x0 }), 8);
                this.CurrentHP             = WinAPIBindings.ReadInt16(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x560 + 0x1C }));
                this.timedEffectsPointer   = WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x4A00 });
                this.equipedEffectsPointer = WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x49B0 });
                this.curSpellPtr           = entityIdPtr + 0x4AE0; //TODO: Current spell being cast? should be pretty cool
                this.equipmentPtr          = WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0xFC0 });
                this.isInvisible           = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x4928 }));
                this.Loaded                = true;
            }
            catch (Exception ex)
            {
                Logger.Error("Error during BGEntity creation!", ex);
            }
            
        }

        public void LoadCREResource()
        {
            this.Reader = resourceManager.GetCREReader(CreResourceFilename.ToUpper());
            if (Reader == null || Reader.Class == CREReader.CLASS.ERROR)
            {
                if (resourceManager.CREReaderCache.ContainsKey(CreResourceFilename.ToUpper()))
                    this.Reader = resourceManager.CREReaderCache[CreResourceFilename.ToUpper()];
            }
        }
        
        private void updateTime()
        {
            this.GameTime = WinAPIBindings.ReadUInt32(WinAPIBindings.FindDMAAddy(cInfGamePtr, new int[] { 0x3FA0 }));
        }

        public void LoadDerivedStats()
        {
            this.DerivedStats      = new CDerivedStats(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x1120 }));
            this.DerivedStatsBonus = new CDerivedStats(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x2A70 }));
            this.DerivedStatsTemp  = new CDerivedStats(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x1DC8 }));
            this.THAC0             = DerivedStats.THAC0 - DerivedStatsTemp.HitBonus - DerivedStats.THAC0BonusRight;
            
            this.calcAPR();
            this.loadWeaponStats();
        }
        private void loadWeaponStats()
        {
            var selectedWeapon = WinAPIBindings.ReadByte(equipmentPtr + 0x138);
            var tempItem       = new CItem(equipmentPtr + 0x140);
            var lst            = new List<CItem>();
            this.CritImmune    = false;
            for (int i=0; i<40; ++i)
            {
                var currentItem = new CItem(equipmentPtr + 8 * i);
                lst.Add(currentItem);
                if (currentItem.resRef == "<ERROR>" || i > 9)
                    continue;
                var read = resourceManager.GetITMReader($"{currentItem.resRef}.ITM");
                this.CritImmune = this.CritImmune 
                    || (i == 6 && ((read.Flags & 0x2000000) == 0))
                    || i != 6 && ((read.Flags & 0x2000000) != 0);                
            }

            var ITMRes = lst[selectedWeapon];
            var reader = resourceManager.GetITMReader($"{ITMRes.resRef}.ITM");
            this.CritImmune = this.CritImmune || ((reader.Flags & 0x2000000) != 0);
            if (reader != null)
            {
                if (this.Reader == null)
                    this.Reader = new CREReader();
                this.Reader.Enchantment        = reader.Enchantment;
                this.Reader.EquippedWeaponIcon = reader.Icon;
                this.Reader.EquippedWeaponName = reader.IdentifiedName;
                this.Reader.ItemEffects.Clear();
                reader.Effects.FindAll(itemEffect => !ITMReader.ExcludedItemEffectOpcodes.Contains((Effect)itemEffect.OpCode))
                    .ForEach(itemEffect => Reader.ItemEffects.Add(itemEffect));
            }
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
            var intPtr          = this.equipedEffectsPointer;
            this.EquipedEffects = new List<CGameEffect>();
            var list            = new CPtrList(intPtr);
            var count           = list.Count;
            if (count > 300)
                return;
            var node = list.Head;
            for (int i = 0; i < count; ++i)
            {
                this.EquipedEffects.Add(new CGameEffect(node.Data));
                node = node.getNext();
            }
            this.EquipedEffects = this.EquipedEffects.Where(x =>
            x != null && !x.ToString().StartsWith("Graphics")
                && !x.ToString().StartsWith("Script")
                && !x.ToString().EndsWith("Sound_Effect")
                && !x.ToString().StartsWith("Text_")
                && !x.ToString().StartsWith("State_"))
                .ToList();

            this.SpellEquipEffects = this.EquipedEffects.Select(x => x.getSpellName(resourceManager)).Distinct()
                .Where(x => x != null && x.Item1 != null).ToList();
        }

        public void loadTimedEffects()
        {
            this.updateTime();
            var intPtr        = this.timedEffectsPointer;
            this.TimedEffects = new List<CGameEffect>();
            var list          = new CPtrList(intPtr);
            var count         = list.Count;
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
            
            this.SpellProtection = TimedEffects.Select(x => x.getSpellName(resourceManager, true)).Distinct()
                .Where(x => x != null && x.Item1 != "-1" && x.Item1 != null && !x.Item1.StartsWith("Extra")).ToList();            
        }

        public override string ToString()
        {
            return additionalInfo();
        }

        private string additionalInfo()
        {            
            return $"{this.Name2} HP:{CurrentHP}";
        }

        public static Dictionary<ushort, string> EnemyAllyDict = new Dictionary<ushort, string>
        {
            { 0, "Anyone" },
            { 1, "Inanimate" },
            { 2, "Regular party members" },
            { 3, "Familiars" },
            { 4, "Ally" },
            { 128, "Neutral" }, 
        };
    }
}
