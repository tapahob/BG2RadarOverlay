using BGOverlay.NativeStructs;
using BGOverlay.Resources;
using NLog.LayoutRenderers;
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

        /// <summary>
        /// True when this slot's target is not a creature at all: the struct wasn't readable, or
        /// its type tag says it is something else entirely. That is a verdict about the address,
        /// so the scan can stop re-asking about it every tick.
        ///
        /// Deliberately not set for the other ways a build gives up - no resource name yet, a
        /// position still at its default. Those describe a creature the game hasn't finished
        /// setting up, which is exactly the case that must be retried on the next pass rather
        /// than remembered: a creature a viewer just paid to summon starts out looking like one.
        /// </summary>
        public bool NotACreature { get; private set; }
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
        public string CreResourceFilename { get; set; }
        public string cleanResourceFilename { get; set; }
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
        public string ClassString
        {
            get
            {
                if (this.Kit != CREReader.KIT.NONE && this.Kit != CREReader.KIT.TRUECLASS)
                {
                    if (RadarLocalization.TryGet($"Kit_{this.Kit}", out var localizedKit))
                        return localizedKit;
                    return this.Kit.ToString().Replace('_', ' ');
                }
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
        public KIT Kit { get; private set; }

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

        public BGEntity(ResourceManager resourceManager, IntPtr entityIdPtr, IntPtr knownBase)
        {
            init(resourceManager, entityIdPtr, knownBase);
        }

        /// <summary>
        /// A helper function that initializes the properties of a BGEntity object.
        /// </summary>
        /// <param name="resourceManager"></param>
        /// <param name="entityIdPtr"></param>
        /// <param name="knownBase">
        /// The creature's base address, where the caller already has it - the entity scan reads
        /// the whole slot array into a local buffer, so the pointer this would otherwise chase is
        /// sitting in memory it already owns. IntPtr.Zero to chase it here instead.
        /// </param>
        private void init(ResourceManager resourceManager, IntPtr entityIdPtr, IntPtr knownBase)
        {
            this.entityIdPtr     = entityIdPtr;
            this.resourceManager = resourceManager;
            this.Loaded          = false;
            this.SpellProtection = new List<Tuple<string, Bitmap, uint>>();
            try
            {
                // Most of the scanned slot range is not the entity array at all, so most calls
                // reach this method only to be thrown away by the Type check below. That reject
                // used to cost four syscalls - two to chase and read Id, two more for Type - and
                // with tens of thousands of them per tick it was the entire cost of the scan.
                //
                // Now: the caller hands over the base address it already has, one block read
                // copies the head of CGameAIBase, and Type is answered from that buffer. A
                // rejected slot costs one syscall, and the fields below come out of the same
                // copy rather than chasing the same pointer another twenty times.
                var entityBase = knownBase != IntPtr.Zero
                    ? knownBase
                    : WinAPIBindings.ReadPointer(entityIdPtr);

                if (entityBase == IntPtr.Zero)
                {
                    this.NotACreature = true;
                    return;
                }

                // 1020 bytes CGameAIBase
                var window = _entityWindow ?? (_entityWindow = new byte[EntityWindowSize]);
                if (!WinAPIBindings.ReadInto(entityBase, window, EntityWindowSize))
                {
                    this.NotACreature = true;
                    return;
                }

                this.Type = window[OffType];

                if (Type != 49)
                {
                    this.NotACreature = true;
                    return;
                }

                this.Id = BitConverter.ToInt32(window, OffId);

                this.X = BitConverter.ToInt32(window, OffX);
                this.Y = BitConverter.ToInt32(window, OffY);

                if (X < 0 || Y < 0)
                    return;

                var rawCreResRef = WinAPIBindings.ReadResRef(IntPtr.Add(entityBase, 0x540));
                // The struct offset for this field is reverse-engineered and can be a byte
                // or two off, silently dropping leading characters with no garbage left
                // behind to detect. Resolving against the authoritative CRE list parsed
                // from the KEY/BIFF index (populated at startup, independent of what's
                // already been lazily cached) recovers the real, complete name instead of
                // leaving callers to work around a truncated one via EndsWith.
                var resolvedCreName = rawCreResRef.Length > 0
                    ? ResourceManager.Instance.CREResourceEntries.Where(x => x.FullName.EndsWith($"{rawCreResRef.ToUpper()}.CRE")).OrderBy(x=>x.FullName.Length)?.FirstOrDefault()?.FullName
                    : null;
                this.CreResourceFilename = resolvedCreName ?? (rawCreResRef + ".CRE");

                IntPtr cGameAreaPtr = IntPtr.Add(entityBase, OffAreaPtr);
                this.EnemyAlly      = window[OffEnemyAlly];
                this.RACE           = (RACE)window[OffRace];
                this.CLASS          = (CLASS)window[OffClass];
                // try to get kit
                ushort mageSpecUpper = BitConverter.ToUInt16(window, OffKitUpper);
                ushort mageSpec = BitConverter.ToUInt16(window, OffKitLower);
                this.Kit = (CREReader.KIT)((mageSpec << 16) | mageSpecUpper);
                var kit5DebugStr = ((mageSpec << 16) | mageSpecUpper).ToString("X8");


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
                this.Name2                 = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(IntPtr.Add(entityBase, 0x3928), 0x0), 64);
                this.Name1                 = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(IntPtr.Add(entityBase, 0x30), 0x0), 8);
                this.CurrentHP             = BitConverter.ToInt16(window, OffCurrentHP);
                this.timedEffectsPointer   = IntPtr.Add(entityBase, 0x4A00);
                this.equipedEffectsPointer = IntPtr.Add(entityBase, 0x49B0);
                this.curSpellPtr           = entityIdPtr + 0x4AE0; //TODO: Current spell being cast? should be pretty cool
                this.equipmentPtr          = IntPtr.Add(entityBase, 0xFC0);
                this.isInvisible           = WinAPIBindings.ReadInt32(IntPtr.Add(entityBase, 0x4928));
                this.Loaded                = true;

                if (Configuration.DebugMode)
                {                    
                   this.Name2 += $" [{this.CreResourceFilename}]";
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
                // One syscall for the creature's base address, one for the block of struct that
                // holds everything below. This used to be a FindDMAAddy + scalar read per field -
                // two syscalls each, eighteen fields, for every creature on every tick - and with
                // a few hundred actors loaded that arithmetic is the whole cost of the scan.
                var entityBase = WinAPIBindings.ReadPointer(entityIdPtr);
                if (entityBase == IntPtr.Zero)
                    return null;

                var window = _entityWindow ?? (_entityWindow = new byte[EntityWindowSize]);
                if (!WinAPIBindings.ReadInto(entityBase, window, EntityWindowSize))
                    return null;

                if (BitConverter.ToInt32(window, OffId) != this.Id)
                    return null;

                var copy = new BGEntity(this);

                copy.X = BitConverter.ToInt32(window, OffX);
                copy.Y = BitConverter.ToInt32(window, OffY);

                if (copy.X < 0 || copy.Y < 0)
                    return null;

                copy.EnemyAlly       = window[OffEnemyAlly];
                copy.RACE            = (RACE)window[OffRace];
                copy.CLASS           = (CLASS)window[OffClass];
                ushort mageSpecUpper = BitConverter.ToUInt16(window, OffKitUpper);
                ushort mageSpec      = BitConverter.ToUInt16(window, OffKitLower);
                copy.Kit             = (CREReader.KIT)((mageSpec << 16) | mageSpecUpper);
                copy.CurrentHP       = BitConverter.ToInt16(window, OffCurrentHP);

                // Far enough past the rest of the struct that pulling the gap along with it would
                // cost more than this one extra read saves.
                copy.isInvisible = WinAPIBindings.ReadInt32(IntPtr.Add(entityBase, OffIsInvisible));

                // Mouse position, viewport and game time belong to the area and the game, not to
                // this creature: every actor in the same area was reading identical values out of
                // the same two structs, once each, every tick. Read once per tick instead - keyed
                // by pointer, so actors in different areas still get their own.
                copy.GameTime = gameTimeFor(cInfGamePtr);

                var area            = areaViewFor(pointerAt(window, OffAreaPtr));
                copy.MousePosX      = area.MousePosX;
                copy.MousePosY      = area.MousePosY;
                copy.MousePosX1     = area.MousePosX1;
                copy.MousePosY1     = area.MousePosY1;
                copy.ViewportHeight = area.ViewportHeight;
                copy.ViewportWidth  = area.ViewportWidth;

                copy.Loaded = true;
                return copy;
            }
            catch (Exception ex)
            {
                Logger.Error("Error during BGEntity refresh!", ex);
                return null;
            }
        }

        #region Per-tick read path

        // Offsets into CGameAIBase, exactly as the per-field reads above used to spell them -
        // the block read changes how many syscalls fetch these bytes, never which bytes.
        private const int OffType        = 0x08;
        private const int OffX           = 0x0C;
        private const int OffY           = 0x0C + 4;
        private const int OffAreaPtr     = 0x18;
        private const int OffEnemyAlly   = 0x38;
        private const int OffRace        = 0x3A;
        private const int OffClass       = 0x3B;
        private const int OffId          = 0x48;
        private const int OffCurrentHP   = 0x560 + 0x1C;
        private const int OffKitUpper    = 0x560 + 0x23C;
        private const int OffKitLower    = 0x560 + 0x23E;
        private const int OffIsInvisible = 0x4928;

        /// <summary>Enough of CGameAIBase to cover every offset above except OffIsInvisible.</summary>
        private const int EntityWindowSize = OffKitLower + 2;

        // Offsets into CGameArea, and the slice of it worth copying in one go.
        private const int OffMousePosX     = 0x254;
        private const int OffMousePosY     = 0x254 + 4;
        private const int OffMousePosX1    = 0x5C8 + 0x60;
        private const int OffMousePosY1    = 0x5C8 + 0x60 + 0x4;
        private const int OffViewportWidth = 0x5C8 + 0x78 + 0x8;
        private const int OffViewportHeight= 0x5C8 + 0x78 + 0xC;
        private const int AreaWindowStart  = OffMousePosX;
        private const int AreaWindowSize   = OffViewportHeight + 4 - AreaWindowStart;

        private const int OffGameTime = 0x3FA0;

        /// <summary>The area-wide values every creature in one area shares.</summary>
        private struct AreaView
        {
            public int MousePosX, MousePosY, MousePosX1, MousePosY1, ViewportWidth, ViewportHeight;
        }

        // Thread-static and reused: the scan runs on one dedicated thread, and a fresh two-kilobyte
        // array per creature per tick would hand the GC more garbage than the syscalls saved here.
        [ThreadStatic] private static byte[] _entityWindow;
        [ThreadStatic] private static byte[] _areaWindow;

        // Null except on the thread that called BeginTick, which is deliberate: a caller that
        // isn't driving the scan (the UI thread reading a right-clicked creature, say) gets a
        // straight-through read rather than a cached value from whenever the loop last ticked.
        [ThreadStatic] private static Dictionary<long, AreaView> _areaCache;
        [ThreadStatic] private static Dictionary<long, uint> _gameTimeCache;

        /// <summary>
        /// Opens a tick's worth of caching on the calling thread. ProcessHacker.MainLoop calls
        /// this at the top of every pass; nothing else should, since holding these across ticks
        /// would freeze the mouse cursor and the clock.
        /// </summary>
        public static void BeginTick()
        {
            if (_areaCache == null)
                _areaCache = new Dictionary<long, AreaView>();
            else
                _areaCache.Clear();

            if (_gameTimeCache == null)
                _gameTimeCache = new Dictionary<long, uint>();
            else
                _gameTimeCache.Clear();
        }

        private static IntPtr pointerAt(byte[] buffer, int offset)
        {
            return IntPtr.Size == 4
                ? new IntPtr(BitConverter.ToInt32(buffer, offset))
                : new IntPtr(BitConverter.ToInt64(buffer, offset));
        }

        private static AreaView areaViewFor(IntPtr areaBase)
        {
            var key = areaBase.ToInt64();

            AreaView cached;
            if (_areaCache != null && _areaCache.TryGetValue(key, out cached))
                return cached;

            var buffer = _areaWindow ?? (_areaWindow = new byte[AreaWindowSize]);
            var view   = new AreaView();

            if (WinAPIBindings.ReadInto(IntPtr.Add(areaBase, AreaWindowStart), buffer, AreaWindowSize))
            {
                view.MousePosX      = BitConverter.ToInt32(buffer, OffMousePosX      - AreaWindowStart);
                view.MousePosY      = BitConverter.ToInt32(buffer, OffMousePosY      - AreaWindowStart);
                view.MousePosX1     = BitConverter.ToInt32(buffer, OffMousePosX1     - AreaWindowStart);
                view.MousePosY1     = BitConverter.ToInt32(buffer, OffMousePosY1     - AreaWindowStart);
                view.ViewportWidth  = BitConverter.ToInt32(buffer, OffViewportWidth  - AreaWindowStart);
                view.ViewportHeight = BitConverter.ToInt32(buffer, OffViewportHeight - AreaWindowStart);
            }

            if (_areaCache != null)
                _areaCache[key] = view;

            return view;
        }

        private static uint gameTimeFor(IntPtr cInfGamePtr)
        {
            var key = cInfGamePtr.ToInt64();

            uint cached;
            if (_gameTimeCache != null && _gameTimeCache.TryGetValue(key, out cached))
                return cached;

            var value = WinAPIBindings.ReadUInt32(WinAPIBindings.FindDMAAddy(cInfGamePtr, OffGameTime));

            if (_gameTimeCache != null)
                _gameTimeCache[key] = value;

            return value;
        }

        #endregion

        /// <summary>
        /// Reads this creature's class level straight from memory, returning it instead of
        /// storing it. LoadDerivedStats() would be the obvious way to get at this, but that
        /// mutates the entity, and this is called from the relay client's thread while the UI
        /// thread may be reading the same instance - see the copy-constructor's note about why
        /// these objects are never mutated after being handed out.
        ///
        /// Multiclass characters get the highest of their class levels, as the best single
        /// stand-in for "how dangerous a fight should be".
        /// </summary>
        public int ReadClassLevel()
        {
            try
            {
                var stats = new CDerivedStats(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x1DC8 }));
                return Math.Max(stats.Level1, Math.Max(stats.Level2, stats.Level3));
            }
            catch (Exception ex)
            {
                Logger.Error("Could not read class level!", ex);
                return 0;
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
            this.GameTime = gameTimeFor(cInfGamePtr);
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
