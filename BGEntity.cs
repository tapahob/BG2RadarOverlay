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

        /// <summary>
        /// Returns a string representation of a RACE enum value.
        /// </summary>
        public string Race
        {
            get
            {
                var race = (this.Reader == null || this.Reader.Race != this.RACE) ? this.RACE : this.Reader.Race;
                if (RadarLocalization.TryGet($"Race_{race}", out var localized))
                    return localized;

                return race.ToString()[0] + race.ToString().ToLower().Substring(1).Replace('_', ' ').Replace("alf", "alf-");
            }
        }

        /// <summary>
        /// Returns a string representation of a CLASS enum value.
        /// </summary>
        public string Class
        {
            get
            {
                if (this.Reader == null || this.CLASS != this.Reader.Class)
                {
                    if (RadarLocalization.TryGet($"Class_{this.CLASS}", out var localizedClass1))
                        return localizedClass1;

                    return this.CLASS.ToString()[0] + this.CLASS.ToString().ToLower().Substring(1).Replace('_', ' ');
                }
                if (this.Reader.KitInformation != CREReader.KIT.NONE
                    && this.Reader.KitInformation != CREReader.KIT.TRUECLASS)
                {
                    if (RadarLocalization.TryGet($"Kit_{this.Reader.KitInformation}", out var localizedKit))
                        return localizedKit;

                    return this.Reader.KitInformation.ToString().Replace('_', ' ');
                }
                if (RadarLocalization.TryGet($"Class_{this.Reader.Class}", out var localizedClass2))
                    return localizedClass2;

                return this.Reader.Class.ToString()[0] + this.Reader.Class.ToString().ToLower().Substring(1).Replace('_', ' ');
            }
        }

        private List<object> _protectionsCache;

        /// <summary>
        /// A list of protections and immunities to render. Most entries are plain strings;
        /// the "Immune to spells" entry (when present) is a <see cref="SpellImmunityLine"/>
        /// instead, so the UI can show each spell's icon next to its name.
        /// Recomputed once per LoadDerivedStats() call (i.e. once per refresh tick) rather
        /// than on every access, since building it walks several effect lists with LINQ.
        /// </summary>
        public List<object> Protections => _protectionsCache ?? (_protectionsCache = computeProtections());

        /// <summary>
        /// Inventory items this creature is carrying that can be stolen via pickpocket -
        /// CREReader.Pockets already filters out items with the Unstealable flag set.
        /// Must stay a property (not a plain field) - EnemyControl.xaml's
        /// "{Binding Pockets}" ItemsSource can't resolve a field, so the ListView would
        /// silently stay empty even though pocketsSection.Visibility (a direct code-behind
        /// field read) correctly turns on.
        /// </summary>
        public List<PocketItemEntry> Pockets { get; set; } = new List<PocketItemEntry>();

        private List<object> computeProtections()
        {
            {
                var allEffects = this.Reader?.Effects?
                    .Where(x => x.EffectName != Effect.Text_Protection_from_Display_Specific_String)
                    ?? new List<EffectEntry>();
                var result             = new List<object>();
                var opCodeStrings      = new List<String>();
                var spellStrings       = new List<SpellIconEntry>();
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
                            // from normal
                            var alsoNormal = weaponProtectionFromBuffs.Any(x => x.dWFlags == 2);
                            result.Add(RadarLocalization.Get(alsoNormal
                                ? "Str_ProtectedFromMagicAndNormalWeapons"
                                : "Str_ProtectedFromMagicWeapons"));
                        }
                    } else
                    {
                        uint requiredEnhancementToHit = 0;
                        var protectedFromNormal = DerivedStats.WeaponImmune.Any(x => x.Flags == 0 && x.FlagMask == 0x40);
                        if (protectedFromNormal)
                            requiredEnhancementToHit = 1;
                        var enhancementProtection = DerivedStats.WeaponImmune.Where(x => x.Flags == 0 && x.FlagMask == 0).OrderByDescending(x=>x.Attributes).FirstOrDefault();
                        if (enhancementProtection != null)
                            requiredEnhancementToHit = enhancementProtection.Attributes + 1;
                        result.Add(string.Format(RadarLocalization.Get("Str_RequiresEnhancementToHit"), requiredEnhancementToHit));
                    }
                }
                for (int i = 9; i > 0; --i)
                {
                    if (DerivedStatsTemp.spellImmuneLevel[i] > 0)
                    {
                        result.Add(string.Format(RadarLocalization.Get("Str_ImmuneToSpellsUpToLevel"), i));
                        break;
                    }
                }
                var thiefStr = "";
                if (this.DerivedStats.BackstabImmunity > 0)
                    thiefStr += RadarLocalization.Get("Str_BackstabImmunity") + "\t";
                if (this.DerivedStats.SeeInvisible > 0)
                    thiefStr += RadarLocalization.Get("Str_SeeInvisible") + "\t";
                if (this.CritImmune)
                    thiefStr += RadarLocalization.Get("Str_CritImmune");
                if (thiefStr != "")
                    result.Add(thiefStr);
                var proficiencyParts = new List<string>();
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
                        var splReader = resourceManager.GetSPLReader($"{item.Resource.Trim('\0')}.SPL".ToUpper());
                        var spellName = splReader.Name1;
                        if (spellName == "-1")
                        {
                            splReader = resourceManager.GetSPLReader($"{item.Resource.Substring(0, item.Resource.Length - 1).Trim('\0')}.SPL".ToUpper());
                            spellName = splReader.Name1;
                            spellName = spellName == "-1" ? item.Resource : spellName;
                        }
                        var icon = splReader?.IconBAM != null ? resourceManager.GetBAMReader(splReader.IconBAM)?.Image : null;
                        spellStrings.Add(new SpellIconEntry { Name = preprocess(spellName), Icon = icon });
                        continue;
                    }
                    if (item.EffectName == Effect.Stat_Proficiency_Modifier)
                    {
                        var amount = item.Param1;
                        var type = (Proficiency)item.Param2;
                        proficiencyParts.Add($"{localizeProficiency(type)} +{amount}");
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
                                result.Add(string.Format(RadarLocalization.Get("Str_ACBonus"), amount));
                                break;
                            case 1:
                                result.Add(string.Format(RadarLocalization.Get("Str_ACvsCrushing"), amount));
                                break;
                            case 2:
                                result.Add(string.Format(RadarLocalization.Get("Str_ACvsMissile"), amount));
                                break;
                            case 4:
                                result.Add(string.Format(RadarLocalization.Get("Str_ACvsPiercing"), amount));
                                break;
                            case 8:
                                result.Add(string.Format(RadarLocalization.Get("Str_ACvsSlashing"), amount));
                                break;
                            case 16:
                                result.Add(string.Format(RadarLocalization.Get("Str_SetACTo"), amount));
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
                                result.Add(string.Format(RadarLocalization.Get("Str_THAC0Bonus"), amount));
                                break;
                            case 1:
                                result.Add(string.Format(RadarLocalization.Get("Str_SetTHAC0To"), amount));
                                break;
                            case 2:
                                result.Add(string.Format(RadarLocalization.Get("Str_THAC0Percent"), amount));
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
                        // These are excluded here (on the raw English enum name, so the check
                        // stays correct regardless of locale) because they're already shown via
                        // their own dedicated lines/labels elsewhere (Res* labels, AC/Save
                        // modifier lines) - keeping them out of this generic bucket too would
                        // just duplicate them.
                        && !effectName.Contains("Resistance")
                        && !effectName.Contains("Backstab")
                        && !effectName.Contains("AC")
                        && !effectName.Contains("Save")
                        )
                        allEffectsStrings.Add(localizeEffect(item.EffectName));
                }
                foreach (var spell in SpellEquipEffects)
                {
                    spellStrings.Add(new SpellIconEntry { Name = preprocess(spell.Item1), Icon = spell.Item2 });
                }
                // Distinct-by-name (keeping the first icon seen for a given name), same as the
                // plain string.Distinct() this replaced.
                spellStrings = spellStrings.GroupBy(s => s.Name).Select(g => g.First()).OrderBy(s => s.Name).ToList();
                if (spellStrings.Any())
                    spellStrings[spellStrings.Count - 1].IsLast = true;
                if (allEffectsStrings.Any())
                    // The Resistance/Backstab/AC/Save exclusion already happened above, against
                    // the raw (locale-independent) enum name - this list holds only localized
                    // display text now, so it's just deduped and sorted here.
                    result.Add(String.Join(", ", allEffectsStrings.Distinct().OrderBy(o => o)));

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

                    result.Add(string.Format(RadarLocalization.Get("Str_OnMeleeHit"), string.Join(", ", onHitMeleeStringsFiltered)));
                }

                if (onHitRangedStrings.Any())
                {
                    var onHitRangedStringsFiltered = onHitMeleeStrings.Where(x =>
                    !x.StartsWith("Text")
                    && !x.StartsWith("Graphics")
                    && !x.Contains("RGB")
                    && !x.StartsWith("Colour")
                    && !x.Contains("Portrait"));

                    result.Add(string.Format(RadarLocalization.Get("Str_OnRangedHit"), string.Join(", ", onHitRangedStringsFiltered)));
                }

                if (spellStrings.Any())
                {
                    // Kept as a small structured entry (label + per-spell icon/name pairs)
                    // instead of one joined string, so the UI can show each spell's icon.
                    result.Add(new SpellImmunityLine
                    {
                        Label  = RadarLocalization.Get("Str_ImmuneToSpellsList"),
                        Spells = spellStrings
                    });
                }

                if (proficiencyParts.Any())
                    // Kept as a small structured entry (label + a list of name-only "icon"
                    // entries with no actual icon) instead of one joined string, by analogy
                    // with the "Immune to spells" line, so the UI can lay it out in columns.
                    result.Add(new SpellImmunityLine
                    {
                        Label  = RadarLocalization.Get("Str_Proficiency"),
                        Spells = proficiencyParts.Select(p => new SpellIconEntry { Name = p, Icon = null }).ToList()
                    });

                var inMemoryProtections = DerivedStats.EffectImmunes.Where(y =>
                {
                    // Filtered against the raw (locale-independent) enum name, same as before.
                    var x = y.EffectId.ToString();
                    return !x.StartsWith("Text")
                        && !x.StartsWith("Graphics")
                        && !x.Contains("RGB")
                        && !x.StartsWith("Colour");
                }).Select(y => localizeEffect(y.EffectId)).Distinct().OrderBy(o => o).ToList();
                if (inMemoryProtections.Any())
                    result.Add(new SpellImmunityLine
                    {
                        Label  = RadarLocalization.Get("Str_EffectImmunities"),
                        Spells = inMemoryProtections.Select(name => new SpellIconEntry { Name = name, Icon = null }).ToList()
                    });
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

        /// <summary>
        /// A helper function that modifies a string by removing pre-defined filters and underscores.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        private string preprocess(string str)
        {
            if (str == null) { return "null"; }
            foreach (var pattern in filter)
            {
                str = str.Replace(pattern, "");
            }
            return str.Replace("_"," ");
        }

        /// <summary>
        /// A display name for an Effect enum value, from the locale's "Effect_&lt;EnumName&gt;"
        /// key when the current locale has one, falling back to the existing
        /// underscore-stripping heuristic (<see cref="preprocess"/>) for any value that hasn't
        /// been translated yet - so an as-yet-unmapped or partially-translated locale still
        /// shows readable (if English-shaped) text instead of nothing.
        /// </summary>
        private string localizeEffect(Effect effect)
        {
            return RadarLocalization.TryGet($"Effect_{effect}", out var localized)
                ? localized
                : preprocess(effect.ToString());
        }

        /// <summary>
        /// A display name for a Proficiency enum value, from the locale's
        /// "Proficiency_&lt;EnumName&gt;" key when the current locale has one, falling back to
        /// the same underscore-to-space heuristic this used before Proficiency had its own
        /// locale keys.
        /// </summary>
        private string localizeProficiency(Proficiency proficiency)
        {
            return RadarLocalization.TryGet($"Proficiency_{proficiency}", out var localized)
                ? localized
                : proficiency.ToString().Replace("_", " ");
        }

        public BGEntity(ResourceManager resourceManager, IntPtr entityIdPtr)
        {
            init(resourceManager, entityIdPtr);
        }

        /// <summary>
        /// A helper function that initializes the properties of a BGEntity object.
        /// </summary>
        /// <param name="resourceManager"></param>
        /// <param name="entityIdPtr"></param>
        private void init(ResourceManager resourceManager, IntPtr entityIdPtr)
        {
            this.entityIdPtr     = entityIdPtr;
            this.resourceManager = resourceManager;
            this.Loaded          = false;
            this.SpellProtection = new List<Tuple<string, Bitmap, uint>>();
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

                if (Configuration.DebugMode)
                {
                    var processedCreName = ResourceManager.Instance.CREReaderCache.Keys.FirstOrDefault(x => x.EndsWith(CreResourceFilename.ToUpper())) ?? CreResourceFilename;
                    this.Name2 += $" [{processedCreName}]";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error during BGEntity creation!", ex);
            }

        }

        /// <summary>
        /// Copy-constructor used by <see cref="RefreshedCopy"/> - copies only the fields
        /// that <see cref="init"/> resolves via multi-hop pointer chases and string reads
        /// and that stay constant for as long as the same creature occupies this slot
        /// (name, resource file, list pointers, ...). Dynamic fields are left at their
        /// default value; the caller fills them in via freshly read data.
        ///
        /// This object is never mutated after being handed out (every tick produces a new
        /// instance instead) - mutating fields in place here would race with the UI thread
        /// reading this same object (e.g. right-click hit testing against ProcessHacker's
        /// published entity list) while a later tick's scan runs on the background thread.
        /// </summary>
        private BGEntity(BGEntity template)
        {
            this.entityIdPtr     = template.entityIdPtr;
            this.resourceManager = template.resourceManager;
            this.SpellProtection = new List<Tuple<string, Bitmap, uint>>();
            this.Loaded          = false;

            this.Id                    = template.Id;
            this.Type                  = template.Type;
            this.CreResourceFilename   = template.CreResourceFilename;
            this.cInfGamePtr           = template.cInfGamePtr;
            this.AreaName              = template.AreaName;
            this.CInfinityPtr          = template.CInfinityPtr;
            this.Name2                 = template.Name2;
            this.Name1                 = template.Name1;
            this.timedEffectsPointer   = template.timedEffectsPointer;
            this.equipedEffectsPointer = template.equipedEffectsPointer;
            this.curSpellPtr           = template.curSpellPtr;
            this.equipmentPtr          = template.equipmentPtr;
        }

        /// <summary>
        /// Builds a new, independent BGEntity for a creature already known to occupy this
        /// slot, reusing this instance's cached static fields and re-reading only the ones
        /// that can legitimately change from tick to tick (position, HP, disposition,
        /// buffs/viewport state) - skipping the multi-hop pointer chases and string reads
        /// that <see cref="init"/> already resolved.
        ///
        /// The scan that finds slots re-derives the entity pointer from slot position alone,
        /// so a slot can end up holding a different creature between ticks (the previous one
        /// died/left and a new one was placed there). That is detected by re-checking the
        /// CGameAIBase id field before trusting anything else; on a mismatch (or any other
        /// invalid read) this returns null so the caller falls back to a full <see cref="init"/>
        /// instead of showing data that actually belongs to a different creature.
        /// </summary>
        public BGEntity RefreshedCopy()
        {
            try
            {
                var currentId = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x48 }));
                if (currentId != this.Id)
                    return null;

                var copy = new BGEntity(this);

                copy.X = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0xC }));
                copy.Y = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0xC + 4 }));

                if (copy.X < 0 || copy.Y < 0)
                    return null;

                IntPtr cGameAreaPtr = WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x18 });
                copy.EnemyAlly = WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x38 }));
                copy.RACE      = (RACE)WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x3A }));
                copy.CLASS     = (CLASS)WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x3B }));

                copy.updateTime();
                copy.MousePosX      = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x254 }));
                copy.MousePosY      = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x254 + 4 }));
                copy.MousePosX1     = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x5C8 + 0x60 }));
                copy.MousePosY1     = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x5C8 + 0x60 + 0x4 }));
                copy.ViewportHeight = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x5C8 + 0x78 + 0xC }));
                copy.ViewportWidth  = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x5C8 + 0x78 + 0x8 }));
                copy.CurrentHP      = WinAPIBindings.ReadInt16(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x560 + 0x1C }));
                copy.isInvisible    = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x4928 }));

                copy.Loaded = true;
                return copy;
            }
            catch (Exception ex)
            {
                Logger.Error("Error during BGEntity refresh!", ex);
                return null;
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
            // Protections depends on DerivedStats/TimedEffects/EquipedEffects, all of which
            // this call (re)loads below - drop the cache so the next access recomputes once.
            this._protectionsCache = null;
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
            var lst            = new List<Tuple<CItem, ITMReader>>();
            //var itmReaders   = new List<ITMReader>();
            this.CritImmune    = false;
            for (int i=0; i<40; ++i)
            {                
                var currentItem = new CItem(equipmentPtr + 8 * i);
                var currentPair = new Tuple<CItem, ITMReader>(currentItem, null);
                lst.Add(currentPair);
                if (currentItem.resRef == "<ERROR>")
                    continue;
                var read = resourceManager.GetITMReader($"{currentItem.resRef}.ITM");
                lst[i] = new Tuple<CItem, ITMReader>(currentItem, read);
                if (i > 9)
                    continue;
                this.CritImmune = this.CritImmune
                    || (i == 6 && ((read.Flags & 0x2000000) == 0))
                    || i != 6 && ((read.Flags & 0x2000000) != 0);
            }

            this.Pockets = this.Reader?.Pockets ?? new List<PocketItemEntry>();
            
            var ITMRes = lst[selectedWeapon].Item1;
            var reader = lst[selectedWeapon].Item2;
            
            var rtPockets = lst
                .Where(x => x.Item2 != null && x.Item2 != reader && (this.Pockets.Any(y => y.ITMReader == x.Item2)))
                .Select(x => x.Item2)
                .GroupBy(x => x)
                .Select(x => new PocketItemEntry{ ITMReader = x.Key, Count = x.Count(), Icon = x.Key.Icon, Name = x.Key.IdentifiedName, FlagsText = "" }).ToList();
            this.Pockets = rtPockets;
            
            this.CritImmune = this.CritImmune || ((reader.Flags & 0x2000000) != 0);
            if (reader != null)
            {
                if (this.Reader == null)
                    this.Reader = new CREReader();
                this.Reader.Enchantment        = reader.Enchantment;
                this.Reader.EquippedWeaponIcon = reader.Icon;
                this.Reader.EquippedWeaponName = reader.IdentifiedName;
                this.Reader.WeaponDamageType   = reader.DamageType.ToString().Replace("_"," "); 
                
                if (Configuration.DebugMode)
                {
                    this.Reader.EquippedWeaponName += $" [{ITMRes.resRef}.ITM]";
                }
                this.Reader.ItemEffects.Clear();
                reader.Effects.FindAll(itemEffect => !ITMReader.ExcludedItemEffectOpcodes.Contains(itemEffect.OpCode))
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
            if (count > 900)
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
                && x.SourceRes != "CDHLYSY2"
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
