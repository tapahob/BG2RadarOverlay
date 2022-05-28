using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using WinApiBindings;
namespace BGOverlay
{
    public class BGEntity
    {
        public CREReader Reader { get; set; }
        private ResourceManager resourceManager;

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
        public int MousePosX1 { get; }
        public int MousePosY1 { get; }
        public byte Name2Len { get; }
        public string Name2 { get; }
        public string Name1 { get; private set; }
        public string CreResourceFilename { get; private set; }

        public byte CurrentHP { get; private set; }
        public CDerivedStats DerivedStats { get; }
        public CDerivedStats DerivedStatsTemp { get; }
        public CDerivedStats DerivedStatsBonus { get; }

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
                for (int i = 9; i>0; --i)
                {
                    if(DerivedStatsTemp.spellImmuneLevel[i] > 0)
                    {
                        result.Add($"Immune to spells up to level {i}");
                        break;
                    }
                }
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
                            spellName = resourceManager.GetSPLReader($"{item.Resource.Substring(0, item.Resource.Length-1).Trim('\0')}.SPL".ToUpper()).Name1;
                            spellName = spellName == "-1" ? item.Resource : spellName;
                        }
                        spellStrings.Add(preprocess(spellName));
                        continue;
                    }
                    if (item.EffectName == Effect.Stat_Proficiency_Modifier)
                    {
                        var amount = item.Param1;
                        var type = (Proficiency)item.Param2;
                        result.Add($"Proficiency {type.ToString().Replace("_"," ")} +{amount}");
                        continue;
                    }
                    if (item.EffectName == Effect.Stat_AC_vs_Damage_Type_Modifier)
                    {
                        var amount = item.Param1;
                        var type = item.Param2;

                        switch(type)
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
                    result.Add(preprocess(item.EffectName.ToString()));
                }
                if (opCodeStrings.Any())
                {
                    result.Add(preprocess("Protection from " + string.Join(", ", opCodeStrings)));
                }
                if (spellStrings.Any())
                {
                    result.Add(preprocess("Spell protections: " + string.Join(", ", spellStrings)));
                }
                return result;
            }
        }

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

        public BGEntity()
        {

        }
        public BGEntity(ResourceManager resourceManager, IntPtr entityIdPtr)
        {
            this.resourceManager = resourceManager;
            this.Loaded = false;
            // 1020 bytes CGameAIBase
            this.Id                            = WinAPIBindings.ReadInt32(entityIdPtr);
            //entityIdPtr                       += 0x4;
            this.Type                          = WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x004 }));
            if (Type != 49)
                return;
            this.X                             = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x008 }));
            this.Y                             = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x00C }));
            if (X < 0 || Y < 0) 
                return;
            IntPtr cGameAreaPtr                = WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x14 });
            this.RealId                        = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x34 }));
            this.AreaName                      = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x0 }), 8);
            this.AreaRef                       = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x1E4 }), 8);
            this.MousePosX                     = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x22C }));
            this.MousePosY                     = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0x22C + 4 }));
            this.MousePosX1                    = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0xA84 }));
            this.MousePosY1                    = WinAPIBindings.ReadInt32(WinAPIBindings.FindDMAAddy(cGameAreaPtr, new int[] { 0xA84 + 4 }));
            this.Name2                         = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x28A8, 0}), 64);
            this.Name1                         = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x364 }), 8);
            this.CreResourceFilename           = WinAPIBindings.ReadString(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x3FC }), 8).Trim('*') + ".CRE";
            if (this.CreResourceFilename == ".CRE") return;
            this.CurrentHP                     = WinAPIBindings.ReadByte(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x438 }));
            //this.DerivedStats                = new CDerivedStats(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0xB30 }));
            //this.DerivedStatsTemp            = new CDerivedStats(entityIdPtr + 0x1454 );
            this.DerivedStatsTemp              = new CDerivedStats(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x1454 }));

            //this.DerivedStatsBonus = new CDerivedStats(WinAPIBindings.FindDMAAddy(entityIdPtr, new int[] { 0x1D78 }));
            if (Type == 49)
            {
                this.Reader = resourceManager.GetCREReader(CreResourceFilename.ToUpper());
                if (Reader.Class == CREReader.CLASS.ERROR)
                {
                    this.Reader = resourceManager.CREReaderCache[CreResourceFilename.ToUpper()];
                }
            }
            this.Loaded = true;
        }

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
